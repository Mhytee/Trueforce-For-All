// Phase 2 backup/sync, milestone M1: the portable projection of TrueforceSettings.
//
// A backup must reproduce a user's setup on a SECOND PC. The live settings blob
// mixes three kinds of field:
//   1. Portable  - the user's actual choices (effects, feel, presets, car facts).
//                  These travel.
//   2. Machine   - things physically tied to THIS PC (USB bus pins, absolute
//                  paths, ring sizes, the bind address). Restoring these onto PC2
//                  breaks its FFB capture, so they NEVER travel.
//   3. Excluded  - portable in principle but unsafe to re-apply (one-time
//                  migration latches would skip migrations PC2 still needs) or
//                  valueless to carry (nag/learned/UI state, runtime caches
//                  rebuilt from the on-disk preset library).
//
// Design rule (see docs/preset-sharing-design.md, Phase 2): the projection is an
// explicit ALLOWLIST, not "serialize everything minus a blocklist." A newly added
// field is therefore NOT backed up until someone classifies it, so a new machine
// field can never silently travel and brick PC2. FindUnclassifiedFields() is the
// guard that fails loudly the moment a field is added without a classification.
//
// Restore MERGES the portable subset onto PC2's live settings (Populate only
// writes the keys present in the projection), so PC2 keeps its own machine fields.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>Builds and restores the portable subset of <see cref="TrueforceSettings"/>
    /// for cloud backup/sync. Pure data + JSON; no network, no file I/O (the preset
    /// library files are bundled by a separate step that owns the folder API).</summary>
    public static class BackupProjection
    {
        public const int SchemaVersion = 1;

        // ---- Classification of every top-level TrueforceSettings property ----
        // Keep these in sync with the field list; FindUnclassifiedFields() enforces
        // that every property lands in exactly one bucket (Forza is the lone Partial).

        /// <summary>Travels to PC2. The default for a setting that is a genuine user choice.</summary>
        public static readonly HashSet<string> Portable = new HashSet<string>(StringComparer.Ordinal)
        {
            // Feature toggles the user opted into.
            "PluginEnabled", "MairaFfbPassthrough", "ModeBRevLightsEnabled", "ShowFeedbackBox",
            "ShowAchievementCelebrations",
            // Wheel-base OLED. Travels with the rev-light toggle for the same
            // reason: it is a preference about how the wheel presents, and the
            // wheel goes to the second PC with the driver.
            "ModeBOledEnabled", "OledScreen", "OledUseMph",
            "OledCustomLayout", "OledCustomSlots", "OledCustomTexts",
            "OledShiftFlash", "OledShiftFlashStyle", "OledLapResult",
            "OledGreetingEnabled", "OledGreetingText", "OledWriteIntervalMs",
            "CommunityEnabled", "UseCommunityCarFacts", "AutoUpdateDownloadedPresets",
            "AutoSubmitCarFacts", "CarFactsConsentAsked", "CarFactsAnonId",
            "MotdLevel", "ShowEffectsTabShareButtons",
            "UpdateCheckIntervalHours", "BetaUpdatesEnabled",
            "DashRevStripOutsideIn", "DashRememberLastTab", "DashDefaultTab",
            "DashDriveSlots", "DashDriveTwoRows", "DashFlagsEnabled",
            // Per-game Drive layouts travel with the shared one: same kind of
            // choice, and the games they are keyed to are the same games on
            // the second PC.
            "DashDriveSlotsPerGame", "DashDriveSlotsByGame",
            // Learned per-game telemetry capability. Travels for the same
            // reason CarGripCalibration does: it is knowledge that costs seat
            // time to rebuild, and it is about the games, not this PC.
            "DashDriveSeen", "DashDriveDrivenSec",
            "DashRevStripCentered", "DashDrivePedals",
            "DashSpotterEnabled",
            "DashIdleEnabled", "DashIdleDelaySeconds", "DashIdleStyle",
            "DashIdleDriverName", "DashIdleNumber", "DashIdleColor", "DashIdleNameAbove", "DashIdleFont", "DashTheme",
            "DashTabOrder", "DashTabsDisabled",
            // Earned access-code unlocks (not machine-bound; the user unlocked them).
            "RpmLedUnlocked", "ShowManualOverrideUi", "ExperimentalFfbCapture",
            "ExperimentalDriverIntercept", "DriverTestingUnlocked",
            "DevModeUnlocked", "ImportPreviewBypass", "OledIgnoreModeBGate",
            // Global feel / FFB shaping.
            "MasterGain", "MasterGainStep", "FfbScale", "FfbInvertSign",
            "FfbSmoothTimeConstantMs", "FfbSpikeTamingEnabled", "FfbSpikeUseSlewLimiter",
            "FfbSpikeMaxLsbPerMs", "FfbPeakSoftLimitLsb",
            "StationarySpringEnabled", "StationarySpringStrength", "StationarySpringCutoffKmh",
            "ClassicSpringEmulationEnabled",
            "SpringModeTerrainEnabled", "SpringModeTerrainGain",
            "DuckingEnabled", "DuckDepth", "DuckAttackMs", "DuckReleaseMs",
            "DuckFrequencyAware",
            // Telemetry based FFB (Mode B): all global feel choices, same
            // category as FfbScale. CarGripCalibration travels too: learned
            // per-car state, but re-learning costs seat time per car (the
            // CarFacts rationale, not the GamesWithRedline one).
            "ModeBGameEnabled", "ModeBSatGain", "ModeBRiseGamma", "ModeBPeakUtil",
            "ModeBDropFloor", "ModeBEmaMs", "ModeBSign", "ModeBDamper",
            "ModeBCenter", "ModeBLatGain", "ModeBDirSoft",
            "ModeBCompressor", "ModeBSuspensionLoad", "ModeBEarlyTorquePeak",
            "ModeBRoadKick", "ModeBRoadKickGain",
            "ModeBReversalDamp", "ModeBReversalDampGain",
            "ModeBPhaseLead", "ModeBPhaseLeadMs",
            "ModeBGripAutoCal", "ModeBFrictionCircle", "ModeBLockupRecoverMs",
            "ModeBLockupPoint", "ModeBLongitudinalGripLearn", "ModeBGripTrim",
            "ModeBLateralDemand", "ModeBAutoStrength",
            "ModeBMinForce", "ModeBCenterPd", "ModeBCenterLeadMs",
            "CarGripCalibration",
            // Per-effect settings blocks (all taste).
            "AudioCapture", "EnginePulse", "RoadBumps", "TractionLoss", "GearShift",
            "AbsClick", "PitLimiter", "Drs", "Collision", "RevLimiter", "Airborne",
            "AxleSlip", "KerbThump", "LockupJudder",
            // Per-game/car data + the custom-engine library (lives in settings, not files).
            "GameEnabled", "AudioCaptureExeOverrides", "CarFacts", "CarFactsSelection",
            "CustomEngines", "SharingAuthor",
            // Active-slot download tracking. Travels with the preset files it tracks.
            // POST-RESTORE the caller must re-mount slots so this re-references the
            // active slot (see ApplySettings remarks).
            "DownloadedCommunityPresets",
        };

        /// <summary>Physically tied to this PC. Never travels (would break PC2).</summary>
        public static readonly HashSet<string> MachineLocal = new HashSet<string>(StringComparer.Ordinal)
        {
            // USB bus topology / pinned device identity (per machine + per port).
            "UsbPcapCmdPathOverride", "ManualUsbPcapInterface", "ManualUsbPcapDeviceAddress",
            "ManualUsbPcapVid", "ManualUsbPcapPid",
            // Absolute filesystem paths (SimHub may be installed elsewhere on PC2).
            "BuiltinPresetsFolder", "UserImportsFolder", "UserLibraryFolder",
            // Ring sizes are a property of this machine's CPU/scheduler.
            "Performance",
            // Identity / security: the auth session and the install-local slot keying.
            "AuthSession", "UserSlots", "ActiveSlotKey", "LegacyDataOwnerEmail",
            // "Remember my email" prefill: a per-PC sign-in convenience, not a portable choice.
            "RememberSignInEmail", "LastSignInEmail",
            // Last wheel detected on this PC (Account session list display); per-PC hardware.
            "LastUsedWheel",
            // TF4ALL Telemetry game-mod install state: the mod lives in THIS
            // PC's Farming Simulator folder, and consent was given here.
            "FsModInstallDeclined", "FsModInstalledVersion",
            // One-shot wheel-defaults latch for the per-wheel Mode B defaults;
            // per-PC hardware state like LastUsedWheel.
            "WheelDefaultsApplied",
            // Rebuildable local cache.
            "CarCylinderCache", "CarCylinderCacheVersion",
            // Community-fact cache: re-fetchable from the backend; would only bloat
            // the backup envelope and could ship one PC's stale snapshot to another.
            "CommunityFactCache",
            // Cloud-backup sync bookkeeping (per-PC sync point; never itself backed up).
            "BackupLastSyncedRevision", "BackupLastSyncedEnvelopeJson",
            // Auto-sync opt-in is per-PC (a restored second PC must not auto-push unprompted).
            "AutoSyncBackupEnabled",
            // DEV-only supporter-badge display override (never backed up; display-only).
            "DevSupporterBadgeOverride",
            // DEV-only "show secret achievements" toggle (display-only; per-PC).
            "DevShowAllAchievements",
        };

        /// <summary>Excluded for SAFETY (re-applying breaks PC2) or no value, NOT because
        /// machine-bound. Migration latches, nag/learned/UI state, and the runtime caches
        /// that are rebuilt from the on-disk preset library on the next Init.</summary>
        public static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            // One-time migration latches: carrying "already migrated = true" onto an
            // older plugin on PC2 would skip migrations it still needs.
            "CarFactsMigratedV1", "FeedbackBoxDefaultedOn", "ManualOverrideClearedV0_1_22",
            "PresetsMigratedV2", "CarsMigratedV2", "LegacyBuiltinsCleanedV1",
            "FoldersRestructuredV3", "UserSlotsMigratedV1", "SlotsKeyedByUserIdV1", "GamesWithRedlineRevalidated",
            "CarPresetOrdinalNamesMigratedV1", "CarPresetOrdinalNamesMigratedV2", "ForzaCarIdsNormalizedV1",
            "CommunityDefaultOnRepitchedV1", "EngineChoiceMovedToCarFactsV1", "EngineOnlyOverridesPrunedV1",
            // Backend config (release bakes constants; a dev override must not travel).
            "CommunityBackendUrl", "CommunityBackendAnonKey",
            // Nag / learned / diagnostic state (re-learns or re-shows harmlessly on PC2).
            "HasSeenNetworkedWelcome", "WelcomeDeclineCount", "WelcomeNextShowAt",
            "IRacingTrueforceNoticeDismissed", "HasSeenModeBIntro",
            "LastVoteNudgeUtc", "ConsecutiveVoteNudgeDismissals", "SeenEffects",
            "NewEffectViewCount", "NewEffectBadgeUnseenBaseline",
            "LastSeenVersion", "ActiveStreamingSeconds", "ShareCtaDismissed",
            // MOTD client state: re-fetchable cache + transient per-message dismiss bookkeeping.
            "MotdCache", "MotdDismissedIds", "MotdPoolDismissedOn", "MotdRecurringDismissedOcc",
            // MOTD audience / nag pacing: contribution-recency timestamps + nag cooldown.
            // Nag/learned state; re-learns harmlessly on a second PC.
            "LastSharedPresetOn", "LastVotedOn", "LastSubmittedFactOn", "MotdLastNagOn",
            "ExperimentalSuccessReportDismissed", "LogUsbBytesEnabled", "StopStreamOnPause",
            // Per-account achievement-celebration baseline + notify-dot (re-seed on PC2).
            "AchievementBaseline", "AchievementUnseen",
            // Preset-manager UI layout.
            "ManagerCommunityForCars", "ManageGamesSort", "ManageCarsSort", "ManageCustomsSort",
            "ManageGamesColumns", "ManageCarsColumns", "ManageCustomsColumns",
            // Remote-dash last-open-tab bookkeeping (the remember/default PREFS travel;
            // where the dash happened to sit on PC1 is transient UI state).
            "DashLastTab",
            // Runtime caches of the on-disk library: back up the FILES, not these dicts.
            // They are rebuilt from user/games + user/cars on the next Init.
            "Presets", "GameDefaults", "CarDefaults", "CarOverrides", "GamePresets",
            // Learned-per-machine redline set (re-learns from telemetry on PC2).
            "GamesWithRedline",
            // Beta-build auto-enroll latch: tied to the build installed on THIS
            // PC. Traveling would pre-latch (or wrongly un-latch) the other PC's
            // own auto-enroll decision for whatever build IT runs.
            "BetaAutoEnrolledVersion",
            // Cross-wheel FFB sync policy + "apply anyway" retention. The policy
            // is a per-PC decision tied to THIS device's wheel; the stash holds
            // ANOTHER wheel's withheld tuning until the user acts. Neither
            // travels: the policy would mis-govern the other device, and the
            // stash would re-introduce the cross-wheel bleed the gate prevents.
            "CrossWheelFfbMode", "PendingCrossWheelFfb", "PendingCrossWheelFfbSource",
        };

        // Forza is the one PARTIAL field: the listener preference (Enabled + Port)
        // travels, but its network addresses (BindAddress/Forward*) are machine-local.
        // A PC1 BindAddress that does not exist on PC2 would fail the UDP bind and
        // silently kill Forza telemetry, so Forza is split at field level.
        public const string PartialForza = "Forza";
        // ForwardGapBridge travels: it is a preference (mask replay gaps on the
        // forwarded copy), not a machine address like BindAddress/Forward*.
        public static readonly string[] ForzaPortableFields = { "Enabled", "Port", "ForwardGapBridge" };

        /// <summary>The wheel-specific FFB tuning keys (Mode B feel + learned
        /// grip calibration): a subset of Portable that ApplySettings WITHHOLDS
        /// when the cross-wheel policy is not Always and the backup came from a
        /// different wheel model. They still travel in the envelope; the gate
        /// only decides whether they are written onto THIS PC. Kept in sync with
        /// the ModeB* / CarGripCalibration entries in Portable (a self-test
        /// asserts the subset relationship).</summary>
        public static readonly HashSet<string> FfbWheelSpecific = new HashSet<string>(StringComparer.Ordinal)
        {
            "ModeBGameEnabled", "ModeBSatGain", "ModeBRiseGamma", "ModeBPeakUtil",
            "ModeBDropFloor", "ModeBEmaMs", "ModeBSign", "ModeBDamper",
            "ModeBCenter", "ModeBLatGain", "ModeBDirSoft",
            "ModeBCompressor", "ModeBSuspensionLoad", "ModeBEarlyTorquePeak",
            "ModeBRoadKick", "ModeBRoadKickGain",
            "ModeBReversalDamp", "ModeBReversalDampGain",
            "ModeBPhaseLead", "ModeBPhaseLeadMs",
            "ModeBGripAutoCal", "ModeBFrictionCircle", "ModeBLockupRecoverMs",
            "ModeBLockupPoint", "ModeBLongitudinalGripLearn", "ModeBGripTrim",
            "ModeBLateralDemand", "ModeBAutoStrength",
            "ModeBMinForce", "ModeBCenterPd", "ModeBCenterLeadMs",
            "CarGripCalibration",
        };

        /// <summary>True only when both wheel labels are known AND name a
        /// different chassis. Unknown on either side (a pre-gate backup, or a PC
        /// with no wheel detected yet) is NOT "different", so FFB applies: we
        /// never withhold tuning we can't prove is for the wrong wheel. Compared
        /// on the short chassis label (LastUsedWheel, e.g. "G PRO"), so console
        /// transport is already out of the picture.</summary>
        public static bool WheelModelsDiffer(string sourceWheel, string currentWheel)
        {
            if (string.IsNullOrWhiteSpace(sourceWheel) || string.IsNullOrWhiteSpace(currentWheel))
                return false;
            return !string.Equals(sourceWheel.Trim(), currentWheel.Trim(),
                                  StringComparison.OrdinalIgnoreCase);
        }

        private static JsonSerializer CreateSerializer()
        {
            // Replace (not the default Auto/merge) so that on restore each portable
            // collection/object property is rebuilt wholesale from the backup rather
            // than merged into PC2's existing value. Without this, List<T> properties
            // (CustomEngines, ...) would APPEND on Populate and duplicate every entry.
            return new JsonSerializer { ObjectCreationHandling = ObjectCreationHandling.Replace };
        }

        /// <summary>Project the portable subset of <paramref name="settings"/> into a
        /// backup envelope. Caller fills <see cref="BackupEnvelope.Library"/> with the
        /// on-disk preset files in a later step.</summary>
        public static BackupEnvelope Build(TrueforceSettings settings, string deviceLabel, DateTime createdUtc)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var full = JObject.FromObject(settings, CreateSerializer());

            var portable = new JObject();
            foreach (var key in Portable)
            {
                var tok = full[key];
                if (tok != null) portable[key] = tok;
            }

            JObject forza = null;
            if (full["Forza"] is JObject f)
            {
                forza = new JObject();
                foreach (var sub in ForzaPortableFields)
                    if (f[sub] != null) forza[sub] = f[sub];
            }

            return new BackupEnvelope
            {
                SchemaVersion = SchemaVersion,
                CreatedUtc = createdUtc.ToString("o"),
                DeviceLabel = deviceLabel ?? string.Empty,
                // Stamp the wheel this FFB tuning was built on so the receiving
                // PC can gate it (cross-wheel FFB). Null when no wheel is known.
                SourceWheelModel = string.IsNullOrWhiteSpace(settings.LastUsedWheel)
                    ? null : settings.LastUsedWheel.Trim(),
                Settings = portable,
                Forza = forza,
                Library = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase),
            };
        }

        /// <summary>Merge the envelope's portable settings onto a live settings object.
        /// Only the keys present in the projection are written; every machine-local and
        /// excluded field on <paramref name="live"/> is left untouched.</summary>
        /// <remarks>POST-CONDITION: the caller must re-mount user slots after this so
        /// <c>DownloadedCommunityPresets</c> re-references the active slot (this method
        /// replaces the dictionary reference). The on-disk preset library is restored by
        /// the separate library step, not here.</remarks>
        public static BackupApplyResult ApplySettings(BackupEnvelope env, TrueforceSettings live)
        {
            var result = new BackupApplyResult();
            if (env == null || live == null) return result;

            if (env.Settings != null)
            {
                // Defensive: only apply keys STILL classified Portable. Guards against an
                // older backup carrying a field that has since been reclassified
                // machine-local/excluded (it must not be written onto this PC).
                var filtered = new JObject();
                foreach (var prop in env.Settings.Properties())
                    if (Portable.Contains(prop.Name)) filtered[prop.Name] = prop.Value;

                // Cross-wheel FFB gate: withhold the wheel-specific FFB tuning
                // (Mode B + learned grip) unless the policy is Always AND this
                // backup was built on a different wheel model. The keys still
                // arrived in the envelope; we just don't write them onto a PC
                // running a different wheel. The withheld subset is handed back
                // so the caller can stash it for an "apply anyway" prompt (Ask)
                // or discard it silently (Never); ApplySettings withholds the
                // same either way, the caller distinguishes on the mode.
                if (live.CrossWheelFfbMode != CrossWheelFfbMode.Always
                    && WheelModelsDiffer(env.SourceWheelModel, live.LastUsedWheel))
                {
                    var skipped = new JObject();
                    foreach (var key in FfbWheelSpecific)
                    {
                        var tok = filtered[key];
                        if (tok != null) { skipped[key] = tok; filtered.Remove(key); }
                    }
                    if (skipped.Count > 0)
                    {
                        result.FfbGated    = true;
                        result.SkippedFfb  = skipped;
                        result.SourceWheel = env.SourceWheelModel;
                    }
                }
                // Atomicity: Populate writes property-by-property, so a malformed or
                // cross-version leaf (a since-retyped field, or a tampered cloud blob)
                // throws MID-stream and leaves `live` half-applied. Validate on a
                // throwaway first; only touch live if the dry run succeeds, so a bad
                // envelope abandons the settings restore cleanly instead of corrupting
                // half of it. (Same defense the Forza block below already documents.)
                bool valid;
                try
                {
                    using (var probe = filtered.CreateReader())
                        CreateSerializer().Populate(probe, new TrueforceSettings());
                    valid = true;
                }
                catch
                {
                    valid = false;
                }
                if (valid)
                    using (var reader = filtered.CreateReader())
                        CreateSerializer().Populate(reader, live);
            }

            // Forza: merge only the two portable fields onto the existing instance so
            // PC2's bind/forward addresses survive.
            if (env.Forza != null && live.Forza != null)
            {
                // Guarded: a malformed/old envelope (e.g. these stored as strings)
                // must not throw out of here. The settings Populate above has
                // already run, so an uncaught cast would leave a half-applied
                // restore. On failure, leave live.Forza at its current values.
                try
                {
                    var en = env.Forza["Enabled"];
                    if (en != null) live.Forza.Enabled = en.Value<bool>();
                    var pt = env.Forza["Port"];
                    if (pt != null) live.Forza.Port = pt.Value<int>();
                }
                catch { }
            }

            return result;
        }

        /// <summary>Cross-wheel FFB gate for the MANUAL import path. The cloud restore
        /// gets this gate inside <see cref="ApplySettings"/>; a file import replaces
        /// settings wholesale, so it calls this afterward to get identical behavior.
        /// If the imported file was built on a different wheel model (and the policy is
        /// not Always), the wheel-specific FFB tuning (Mode B + learned grip) is restored
        /// to this PC's pre-import values on <paramref name="live"/>, and the imported
        /// values are handed back so the caller can stash them for an "apply anyway?"
        /// prompt (Ask) or drop them silently (Never). <paramref name="localBefore"/> is
        /// the settings snapshot from before the import (this PC's real FFB + wheel).</summary>
        public static BackupApplyResult GateImportedCrossWheelFfb(
            TrueforceSettings importedFile, TrueforceSettings localBefore, TrueforceSettings live)
        {
            var result = new BackupApplyResult();
            if (importedFile == null || localBefore == null || live == null) return result;
            if (live.CrossWheelFfbMode == CrossWheelFfbMode.Always) return result;
            if (!WheelModelsDiffer(importedFile.LastUsedWheel, localBefore.LastUsedWheel)) return result;

            var ser         = CreateSerializer();
            var importedJson = JObject.FromObject(importedFile, ser);  // the file's (foreign-wheel) FFB
            var localJson    = JObject.FromObject(localBefore, ser);   // this PC's FFB
            var restore = new JObject();
            var skipped = new JObject();
            foreach (var key in FfbWheelSpecific)
            {
                var imp = importedJson[key];
                if (imp != null) skipped[key] = imp;
                var loc = localJson[key];
                if (loc != null) restore[key] = loc;
            }
            if (restore.Count > 0)
                using (var r = restore.CreateReader())
                    ser.Populate(r, live);   // put this PC's FFB tuning back onto live
            if (skipped.Count > 0)
            {
                result.FfbGated    = true;
                result.SkippedFfb  = skipped;
                result.SourceWheel = string.IsNullOrWhiteSpace(importedFile.LastUsedWheel)
                    ? null : importedFile.LastUsedWheel;
            }
            return result;
        }

        /// <summary>Guard: every public read/write property of TrueforceSettings must be
        /// classified into exactly one bucket. Returns the unclassified property names
        /// (empty = healthy). Surfaced as a DEV-panel self-test so adding a settings field
        /// without classifying it fails loudly instead of silently dropping from backups.</summary>
        public static IReadOnlyList<string> FindUnclassifiedFields()
        {
            var classified = new HashSet<string>(StringComparer.Ordinal);
            classified.UnionWith(Portable);
            classified.UnionWith(MachineLocal);
            classified.UnionWith(Excluded);
            classified.Add(PartialForza);

            var missing = new List<string>();
            foreach (var p in typeof(TrueforceSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (!classified.Contains(p.Name)) missing.Add(p.Name);
            }
            return missing;
        }

        /// <summary>Guard: a property classified into more than one bucket (drift after a
        /// copy/paste edit). Returns the offending names (empty = healthy).</summary>
        public static IReadOnlyList<string> FindDoubleClassifiedFields()
        {
            return Portable.Concat(MachineLocal).Concat(Excluded).Concat(new[] { PartialForza })
                .GroupBy(x => x, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
        }
    }

    /// <summary>Outcome of <see cref="BackupProjection.ApplySettings"/>. Tells the
    /// caller whether the cross-wheel FFB gate withheld the wheel-specific tuning
    /// so it can stash the skipped keys and prompt "apply anyway".</summary>
    public sealed class BackupApplyResult
    {
        /// <summary>True when the FFB / Mode B keys were withheld because the
        /// backup was tuned on a different wheel model and the gate is on.</summary>
        public bool FfbGated { get; set; }

        /// <summary>The withheld FFB keys (Mode B + grip cal) as a JObject, for
        /// the "apply anyway" retention. Null unless <see cref="FfbGated"/>.</summary>
        public JObject SkippedFfb { get; set; }

        /// <summary>The wheel model the withheld tuning was built on (the
        /// envelope's SourceWheelModel). Null unless <see cref="FfbGated"/>.</summary>
        public string SourceWheel { get; set; }
    }

    /// <summary>Serializable backup payload. Tiny enough (presets are 0.3-2.5 KB JSON,
    /// the whole envelope ~1-3 MB) to store as one JSON object, no zip needed.</summary>
    public sealed class BackupEnvelope
    {
        public int SchemaVersion { get; set; }
        public string CreatedUtc { get; set; }
        public string DeviceLabel { get; set; }

        /// <summary>Short wheel-chassis label the FFB / Mode B tuning in this
        /// backup was built on ("G PRO" / "RS50" / "G923"), taken from
        /// LastUsedWheel at build time. Null when no wheel was known. Consumed by
        /// ApplySettings' cross-wheel FFB gate; older envelopes without it read
        /// as "unknown" and never gate.</summary>
        public string SourceWheelModel { get; set; }

        /// <summary>Portable top-level settings keys (NO Forza, NO machine/excluded fields).</summary>
        public JObject Settings { get; set; }

        /// <summary>Forza's portable fields only ({Enabled, Port}), or null.</summary>
        public JObject Forza { get; set; }

        /// <summary>Preset-library files: relative path under the user library root ->
        /// file text + last-modified time. Filled by the library-bundling step;
        /// ModifiedUtc powers the newest-wins merge.</summary>
        public Dictionary<string, BackupFile> Library { get; set; }
    }

    /// <summary>One backed-up preset-library file: verbatim text plus the last-write
    /// time captured at bundle. ModifiedUtc is preserved across restore (the file's
    /// last-write-time is re-stamped) so newest-wins comparisons stay meaningful after
    /// a sync, instead of a restored-but-unedited file looking newer than its origin.</summary>
    public sealed class BackupFile
    {
        public string   Text        { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }
}
