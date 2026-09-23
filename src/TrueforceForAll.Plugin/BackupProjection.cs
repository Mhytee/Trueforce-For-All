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
// Two consumers, and they differ. The CLOUD path PROJECTS: Build reads Portable and
// nothing else, so 2 and 3 are indistinguishable to it. The FILE/ZIP path SNAPSHOTS:
// it replaces the settings object wholesale and then restores bucket 2 from this PC,
// which makes the 2-vs-3 line load-bearing in exactly one direction. A name is
// MachineLocal if another install must never hand it to this one; it is Excluded if
// it merely should not be projected onto a different PC. Getting that backwards is
// how a settings file came to carry another machine's backend endpoint, and getting
// it backwards the other way would make an old settings file import as an empty
// library (the legacy-preset rescue reads its latches off the file).
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
            // MasterMode sits beside the bool it replaced: the two are written
            // together and must never land on PC2 disagreeing about whether the
            // plugin is on.
            "PluginEnabled", "MasterMode", "ModeBRevLightsEnabled", "CarRevLightEffect", "CarLightPattern", "ShowFeedbackBox",
            // What the rim LEDs do when the revs cannot drive them. Travels
            // with the rev-light toggle for the same reason: it is a preference
            // about how the wheel presents, and the wheel goes to the second PC
            // with the driver. The sensitivity is in dB below full scale, which
            // is a property of the material being played rather than of one
            // machine's sound card.
            "IdleLedMode", "NoRevLedMode", "LedSweepPeriodMs",
            // iRacing reshape strength. Travels: it is normalized against the
            // sim's own max force, so it carries no wheel-specific meaning.
            "IRacingForceGain", "IRacingUse360Hz",
            "IRacingForceMode", "IRacingPredictGain",
            // iRacing soft lock. Travels: normalised to the car's own lock,
            // nothing wheel-specific in it.
            "IRacingSoftLockEnabled", "IRacingSoftLockStrength",
            // iRacing kerb strike softening. Travels: it reads the sim's own
            // shock speeds and torque, nothing wheel-specific in it.
            "IRacingKerbSofteningEnabled", "IRacingKerbSoftening", "IRacingKerbSensitivity",
            // RaceRoom shared-memory FFB route (dev). Travels: it is a
            // preference about which force source to trust, not a machine fact.
            "R3ESharedMemoryFfb", "R3EAutoStrength", "R3EStrengthByCar",
            "R3EStationaryDamper", "R3EStationaryDamperStrength", "R3EStationaryDamperFadeKmh",
            "R3ESmoothingMs",
            // Le Mans Ultimate handover. Travels for the same reason, and the
            // full scale describes the sim's torque, not this PC.
            "LmuSharedMemoryFfb", "LmuFullScaleNm",
            // Wheel full-scale in Nm. Travels with the other feel settings: it
            // describes the WHEEL, and the wheel goes to the second PC with the
            // driver, same reasoning as the rev-light and OLED preferences.
            "IRacingMaxForceNmOverride",
            // Per-car force scale. Travels for the same reason CarGripCalibration
            // does: it costs seat time per car to rebuild, and it describes the
            // cars rather than this PC.
            "IRacingMaxForcePerCar", "IRacingMaxForceByCar",
            "ShowAchievementCelebrations",
            // Has ever backed the project: a fact about the person, not the machine,
            // so it travels and a supporter is never re-asked on a second PC.
            "HasEverSupported",
            // Wheel-base OLED. Travels with the rev-light toggle for the same
            // reason: it is a preference about how the wheel presents, and the
            // wheel goes to the second PC with the driver.
            "ModeBOledEnabled", "OledScreen", "OledUseMph",
            "OledCustomLayout", "OledCustomSlots", "OledCustomTexts",
            "OledShiftFlash", "OledShiftFlashStyle", "OledLapResult",
            "OledGreetingEnabled", "OledGreetingText", "OledWriteIntervalMs",
            "CommunityEnabled", "UseCommunityCarFacts", "AutoUpdateDownloadedPresets",
            "AutoSubmitCarFacts", "CarFactsConsentAsked", "CarFactsAnonId",
            // The usage-stats opt-out travels: it is a privacy choice, and it should
            // follow the person to a second PC. AnalyticsAnonId deliberately does
            // NOT (see Excluded): a backup is stored under the user's account, so
            // carrying the id there would put account -> anon-id in the backend and
            // make the telemetry joinable to a real identity.
            "ShareUsageStats",
            "MotdLevel", "ShowEffectsTabShareButtons", "ShowPerGearRedlineEditor",
            "UpdateCheckIntervalHours", "BetaUpdatesEnabled",
            "DashRevStripOutsideIn", "DashRevStripAuto",
            "DashRememberLastTab", "DashDefaultTab",
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
            "DashSpotterEnabled", "DashIncidentsEnabled",
            "DashIdleEnabled", "DashIdleDelaySeconds", "DashIdleStyle",
            "DashIdleDriverName", "DashIdleNumber", "DashIdleColor", "DashIdleNameAbove", "DashIdleFont", "DashTheme",
            "DashTabOrder", "DashTabsDisabled",
            // Earned access-code unlocks (not machine-bound; the user unlocked them).
            "RpmLedUnlocked", "ShowManualOverrideUi", "ExperimentalFfbCapture",
            "ExperimentalDriverIntercept", "DriverTestingUnlocked",
            "DevModeUnlocked", "ImportPreviewBypass", "OledIgnoreModeBGate", "F8IgnoreQuietGate",
            "FxBenchUnlocked",
            "LightsyncTabUnlocked", "LovelyCarDataEnabled", "LovelyFixedStripOptOut",
            "LightsyncDynamicSlot",
            "AlwaysRememberCarPattern",
            // LED color trim. Portable because it describes the WHEEL, which
            // travels to the second PC with its own LED binning intact, same
            // reasoning as the rev-light and OLED preferences. Also listed in
            // FfbWheelSpecific, so restoring a G PRO's trim onto a G923 with a
            // different LED package is withheld rather than applied.
            "LedTrimR", "LedTrimG", "LedTrimB",
            // Tap-free AC FFB toggle: a preference about how force is sourced,
            // not a machine fact (shared memory exists wherever AC does).
            "AcShmFfbEnabled", "CspBridgeFfbEnabled", "CspBridgeFfbField", "CspBridgeMaxNm",
            // Global feel / FFB shaping.
            "MasterGain", "MasterGainStep", "FfbScale", "FfbInvertSign",
            "FfbSmoothTimeConstantMs", "FfbSpikeTamingEnabled", "FfbSpikeUseSlewLimiter",
            "FfbSpikeMaxLsbPerMs", "FfbPeakSoftLimitLsb", "FfbSpikeTransientThresholdLsb",
            // Condition-render tuning (FXTEST bench): describes the wheel's
            // feel and travels with it, same category as FfbScale.
            "FfbConditionDamperGain", "FfbConditionSignInverted", "FfbConditionLpfHz",
            "FfbConditionDamperLpfHz", "FfbConditionSpringLpfHz", "FfbConditionFrictionLpfHz", "FfbConditionInertiaLpfHz",
            "FfbConditionSpringGain", "FfbConditionFrictionGain",
            "FfbConditionInertiaGain", "FfbConditionPeriodicGain", "FfbConditionRampGain",
            "FfbConditionInertiaCoasts", "FfbConditionMeasuredAtScale",
            "FfbConditionInertiaAsDamping",
            "StationarySpringEnabled", "StationarySpringStrength", "StationarySpringCutoffKmh",
            "StationarySpringByGame",
            "ClassicSpringEmulationEnabled",
            "SpringModeTerrainEnabled", "SpringModeTerrainGain",
            "SpringModeCenterGain", "SpringModeCenterFirmness", "SpringModeSpeedEffect",
            "SpringModeStrength", "SpringModeMinForce",
            "SpringModeDragEnabled", "SpringModeDragGain", "SpringModeDragStrainFraction",
            "SpringModeChassisWeightEnabled", "SpringModeChassisWeightGain",
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
            "AxleSlip", "KerbThump", "LockupJudder", "ImplementThud",
            // Per-game/car data + the custom-engine library (lives in settings, not files).
            // (MenuHaptics and MinForcePercent used to be listed here. They are properties of
            // ArcadeSettings, never of TrueforceSettings, so the names matched nothing and the
            // two real settings travelled nowhere; they now sit in ArcadePortableFields.)
            "GameEnabled", "GameModes", "AudioCaptureExeOverrides", "CarFacts", "CarFactsSelection",
            "CustomEngines", "SharingAuthor",
            // Active-slot download tracking. Travels with the preset files it tracks.
            // POST-RESTORE the caller must re-mount slots so this re-references the
            // active slot (see ApplySettings remarks).
            "DownloadedCommunityPresets",
        };

        /// <summary>Per-INSTALL. Never travels, and never arrives: this is the bucket the
        /// file/zip import restores from the pre-import settings, so a name belongs here if
        /// another install must never hand it to this one. That covers the literally
        /// machine-bound things (USB pins, absolute paths, ring sizes) and also the
        /// per-install FACTS that are not hardware: this install's identity and sign-in, its
        /// backend endpoint, the gates it volunteered to test behind, and its analytics id.
        ///
        /// MachineLocal and Excluded are indistinguishable to the CLOUD path (Build copies
        /// only Portable), so the split between them is decided entirely by the file path:
        /// MachineLocal is kept from this PC, Excluded is taken from the file.</summary>
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
            // TF4ALL Enhanced Telemetry game-mod install state: the mod lives in THIS
            // PC's Farming Simulator folders, and consent was given here.
            "FsModInstallDeclined", "FsModInstalledVersions",
            // CSP bridge script install state: the script lives in THIS PC's
            // Assetto Corsa folder and consent was given here.
            "CspBridgeInstallDeclined", "CspBridgeDisplacedRecorded", "CspBridgeDisplacedSection",
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
            // ---- Per-INSTALL, moved here from Excluded ----
            // These were Excluded, which is enough for the CLOUD path (Build only
            // reads Portable, so Excluded and MachineLocal are indistinguishable
            // there) but NOT for the file/zip path, which replaces the settings
            // object wholesale and then restores only this bucket. Each of them
            // was either already hand-copied back by ImportSettings or is a thing
            // another install must never hand us. Classifying them here is what
            // lets that hand-written block go away and stay gone.
            //
            // Backend config: release bakes the constants, but every client reads
            // these LIVE per request, so an imported file would re-point sign-in,
            // community and the backup upload (which carries the user's token) at
            // whatever host the file names, for the rest of the session.
            "CommunityBackendUrl", "CommunityBackendAnonKey",
            // Testing gates. Not earned unlocks: they say "THIS PC volunteered to
            // retest a known troublesome path". A file from a PC that did must not
            // switch them on for a PC that did not. Same reasoning as the arcade
            // master switch, which is withheld for the same reason one level down
            // in ArcadeMachineLocalFields.
            "StationarySpringUnlocked", "ClassicConditionEmulationEnabled",
            // Beta auto-enroll latch: tied to the BUILD installed on this PC.
            // PreBetaBackup normalizes this very field out of its own snapshot for
            // the same reason.
            "BetaAutoEnrolledVersion",
            // Usage-analytics identity + bookkeeping. The anon id must not travel
            // at all (a backup is stored under the account, so carrying it would
            // record account -> anon-id and make the telemetry joinable to a real
            // identity); the day stamp, settings hash and undrained queues are the
            // per-install bookkeeping that goes with it. PC2 mints its own.
            "AnalyticsAnonId", "LastTelemetryPingDay", "LastTelemetrySettingsHash",
            "TelemetryGameDays", "TelemetryGamePresetHashes",
            // Cross-wheel FFB policy + the "apply anyway" stash. The policy governs
            // THIS device's wheel, and the stash holds ANOTHER wheel's withheld
            // tuning until the user acts, so neither may arrive from elsewhere.
            "CrossWheelFfbMode", "PendingCrossWheelFfb", "PendingCrossWheelFfbSource",
        };

        /// <summary>Kept out of the CLOUD envelope for safety (re-applying breaks PC2) or
        /// for want of value, but DELIBERATELY still carried by a settings file or backup
        /// zip, which is a wholesale snapshot of one install rather than a projection onto
        /// another. Migration latches, nag/learned/UI state, and the runtime caches rebuilt
        /// from the on-disk preset library on the next Init.
        ///
        /// The latches and the legacy Presets/GameDefaults caches MUST keep travelling on
        /// the file path: ImportSettings reads them off the imported file to decide whether
        /// to run the legacy-preset rescue, and a zip restore lays down the very files those
        /// latches describe. Preserving them from the local PC instead would make an old
        /// settings file import as an empty library. Anything here that another install must
        /// genuinely never hand us belongs in MachineLocal, not in this bucket.</summary>
        public static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            // One-time migration latches: carrying "already migrated = true" onto an
            // older plugin on PC2 would skip migrations it still needs.
            "CarFactsMigratedV1", "FeedbackBoxDefaultedOn", "ManualOverrideClearedV0_1_22",
            "PresetsMigratedV2", "CarsMigratedV2", "LegacyBuiltinsCleanedV1",
            "FoldersRestructuredV3", "UserSlotsMigratedV1", "SlotsKeyedByUserIdV1", "GamesWithRedlineRevalidated",
            "CarPresetOrdinalNamesMigratedV1", "CarPresetOrdinalNamesMigratedV2", "ForzaCarIdsNormalizedV1",
            "CommunityDefaultOnRepitchedV1", "EngineChoiceMovedToCarFactsV1", "EngineOnlyOverridesPrunedV1",
            "MasterModeMigratedV1", "FfbConditionInertiaSpecMigrated", "FfbConditionDefaultsGeneration",
            // (Backend config and the two testing gates moved to MachineLocal: being
            // Excluded kept them out of the cloud envelope but let a file import adopt
            // them wholesale, which is the one path where it matters.)
            // Nag / learned / diagnostic state (re-learns or re-shows harmlessly on PC2).
            "HasSeenNetworkedWelcome", "WelcomeDeclineCount", "WelcomeNextShowAt",
            "IRacingTrueforceNoticeDismissed", "R3ETrueforceNoticeDismissed", "LmuTrueforceNoticeDismissed", "StandDownNoticeDismissedGames", "MairaTapNoticeDismissed", "HasSeenModeBIntro", "GameModeMapMigratedV1",
            "LastVoteNudgeUtc", "ConsecutiveVoteNudgeDismissals", "SeenEffects",
            "NewEffectViewCount", "NewEffectBadgeUnseenBaseline",
            "LastSeenVersion", "ActiveStreamingSeconds", "ShareCtaDismissed", "LightsyncCycleHintDismissed", "HasSeenLightsyncIntro",
            // (The usage-analytics id and its ping bookkeeping moved to MachineLocal.
            // The privacy reason for keeping the anon id off a backup is unchanged -
            // a backup is stored under the account, so carrying it would record
            // account -> anon-id - and MachineLocal withholds it from the envelope
            // exactly as Excluded did, while ALSO stopping a settings file from
            // handing this install another install's identity.)
            // Migration latch: PC2 needs to run its own, so this must not travel.
            "LightsyncReleasedMigrated",
            // MOTD client state: re-fetchable cache + transient per-message dismiss bookkeeping.
            "MotdCache", "MotdDismissedIds", "MotdPoolDismissedOn", "MotdRecurringDismissedOcc",
            // MOTD audience / nag pacing: contribution-recency timestamps + nag cooldown.
            // Nag/learned state; re-learns harmlessly on a second PC.
            "LastSharedPresetOn", "LastVotedOn", "LastSubmittedFactOn", "MotdLastNagOn",
            "MotdLastSupportNagOn",
            // Support-prompt pacing. HasEverSupported is deliberately NOT here: it
            // travels, so a supporter restoring onto a second PC is not asked again.
            "SupportPromptCount", "SupportPromptDeclineCount", "SupportPromptLastUtc",
            "ExperimentalSuccessReportDismissed", "LogUsbBytesEnabled", "StopStreamOnPause",
            "StopStreamOnPauseMigrated", "StopStreamOnPauseDefaultOffMigrated",
            "ReleaseForceOnFocusLoss", "AutoReEnumerateOnBlindCapture",
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
            // (The beta auto-enroll latch and the cross-wheel FFB trio moved to
            // MachineLocal for the file-import reason described there.)
        };

        // Forza is the one PARTIAL field: the listener preference (Enabled + Port)
        // travels, but its network addresses (BindAddress/Forward*) are machine-local.
        // A PC1 BindAddress that does not exist on PC2 would fail the UDP bind and
        // silently kill Forza telemetry, so Forza is split at field level.
        public const string PartialForza = "Forza";
        // ForwardGapBridge travels: it is a preference (mask replay gaps on the
        // forwarded copy), not a machine address like BindAddress/Forward*.
        public static readonly string[] ForzaPortableFields = { "Enabled", "Port", "ForwardGapBridge" };
        // The other half, named explicitly rather than implied by "everything else". A
        // PARTIAL split has two buckets and the file path needs the NOT-portable one by
        // name, the same way the top level needs MachineLocal. BindAddress is the one that
        // actually breaks capture (ParseIpOrAny only falls back to Any for an UNPARSEABLE
        // string, so a valid-but-foreign NIC address parses, the bind throws, and Forza
        // telemetry dies silently); the forward target is listed with it because it is the
        // same kind of fact, an address on one network.
        public static readonly string[] ForzaMachineLocalFields =
        {
            "BindAddress", "ForwardEnabled", "ForwardHost", "ForwardPort",
        };

        // Arcade is the second PARTIAL field, and for the same reason as Forza. Which arcade dumps
        // are installed and what their executables are called is a property of THIS machine, and
        // carrying a PC1 path onto PC2 would point the plugin at a game that is not there. But the
        // Initial D 8 leaderboard preferences underneath are taste, not machine facts: whether the
        // boards are filled at all, which pool each of the two boards shows, and whether finished
        // runs are sent. Those are exactly the kind of choice the backup exists to carry, and they
        // sat behind the whole-object machine-local classification only because that classification
        // predates them and its justification speaks only about dumps and exe names.
        public const string PartialArcade = "Arcade";

        // Id8SubmitNoticeShown is deliberately NOT here. It records that the one-time disclosure
        // about publishing a username has been displayed ON THIS PC, and submission is refused
        // until it has been. Carrying it would mean a restore onto a new machine silently skips
        // the notice and starts publishing, which is the one thing the latch exists to prevent.
        //
        // MenuHaptics, MinForcePercent and Tuning are here because they are the same kind of
        // thing as the leaderboard preferences: per-cabinet FEEL, keyed by game identity, with
        // no machine fact in them. MenuHaptics and MinForcePercent were written into the
        // top-level Portable set by the commit that created them, where they matched no
        // property and therefore travelled nowhere; this is where they were meant to go.
        public static readonly string[] ArcadePortableFields =
        {
            "Id8LeaderboardsEnabled", "Id8OnlineBoardSource", "Id8ShopBoardSource",
            "Id8SubmitTimesEnabled", "Id8LadderClimbEnabled",
            "MenuHaptics", "MinForcePercent", "Tuning",
        };

        // The machine-local half of the Arcade split, named for the same reason as Forza's.
        // Enabled is the shelved-feature master switch and its own doc says a restore must not
        // silently re-enable the arcade path on a PC that never asked; Games / TeknoParrotPath /
        // ModInstalledVersions describe which dumps exist on THIS disk and where; PublisherMapName
        // names one install's cabinet; Id8SubmitNoticeShown is the disclosure latch above.
        public static readonly string[] ArcadeMachineLocalFields =
        {
            "Enabled", "Games", "TeknoParrotPath", "ModInstalledVersions",
            "PublisherMapName", "Id8SubmitNoticeShown",
        };

        /// <summary>The wheel-specific tuning keys (Mode B feel, learned grip
        /// calibration, and the LED color trim): a subset of Portable that
        /// ApplySettings WITHHOLDS when the cross-wheel policy is not Always and
        /// the backup came from a different wheel model. They still travel in
        /// the envelope; the gate only decides whether they are written onto
        /// THIS PC. Kept in sync with the ModeB* / CarGripCalibration / LedTrim*
        /// entries in Portable (a self-test asserts the subset relationship).
        /// LedTrim* being null (never chosen) travels harmlessly either way,
        /// since it resolves from the shipped table on whatever wheel it lands.
        ///
        /// The LED trim is here for the same reason as the FFB tuning but a
        /// blunter one: it compensates the relative brightness of one wheel's
        /// LED dies, so writing a G PRO's numbers onto a G923 would miscolor a
        /// package it was never measured against.</summary>
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
            // Spring mode is Mode B's Farming Simulator half and is tuned per
            // wheel the same way: ApplyWheelDefaults gives the G923 its own
            // SpringModeMinForce (0.15, for the belt friction that eats FS's
            // light spring) where every other wheel ships 0. Without these
            // names the gate let a G923 backup write that floor onto a G PRO.
            "SpringModeCenterGain", "SpringModeCenterFirmness", "SpringModeSpeedEffect",
            "SpringModeStrength", "SpringModeMinForce",
            "SpringModeTerrainEnabled", "SpringModeTerrainGain",
            "SpringModeDragEnabled", "SpringModeDragGain", "SpringModeDragStrainFraction",
            "SpringModeChassisWeightEnabled", "SpringModeChassisWeightGain",
            "CarGripCalibration",
            "LedTrimR", "LedTrimG", "LedTrimB",
            // The condition engine's gains and filters. Same argument as the LED trim,
            // stated by the generation-1 migration itself: "there is one right tuning per
            // wheel, and the bench exists to find it, not to keep a tune of one's own".
            // They are bench facts about a chassis, not preferences, so a G PRO's numbers
            // landing on a G923 is the cross-wheel bleed this gate exists to stop.
            // FfbConditionSignInverted and FfbConditionInertiaAsDamping are deliberately
            // absent: that migration calls the direction flip and the inertia mode wheel
            // FACTS rather than tuning, and they are already resolved per wheel.
            "FfbConditionDamperGain", "FfbConditionSpringGain", "FfbConditionFrictionGain",
            "FfbConditionInertiaGain", "FfbConditionPeriodicGain", "FfbConditionRampGain",
            "FfbConditionLpfHz", "FfbConditionDamperLpfHz", "FfbConditionSpringLpfHz",
            "FfbConditionFrictionLpfHz", "FfbConditionInertiaLpfHz",
            "FfbConditionMeasuredAtScale",
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

        /// <summary>Does one wheel-specific key already hold <paramref name="incoming"/> on the
        /// live settings? Reads THAT ONE PROPERTY by reflection rather than serializing the whole
        /// settings graph.
        ///
        /// That distinction is load-bearing, not tidiness. A whole-graph JObject.FromObject walks
        /// CarFacts, Presets, GamePresets, GamesWithRedline and TelemetryGameDays, all of which the
        /// SimHub data thread mutates under _carFactsLock, so doing it here would throw "collection
        /// was modified" straight out of a live cloud restore. Every other whole-graph serialize in
        /// the plugin (PersistSettingsCore, BuildEnvelopeLocked, the import-path gate) holds that
        /// lock; ApplyRestoredEnvelopeCore does not, and this is called from there. Reading a single
        /// scalar cannot tear.
        ///
        /// A property that cannot be read or compared counts as DIFFERENT, which is the safe answer:
        /// it withholds the incoming value and tells the user about it, rather than silently writing
        /// another wheel's number.</summary>
        private static bool MatchesLive(TrueforceSettings live, string key, JToken incoming)
        {
            try
            {
                var p = typeof(TrueforceSettings).GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanRead) return false;
                var current = p.GetValue(live);
                if (current == null) return incoming == null || incoming.Type == JTokenType.Null;
                return JToken.DeepEquals(incoming, JToken.FromObject(current, CreateSerializer()));
            }
            catch { return false; }
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

            JObject arcade = null;
            if (full[PartialArcade] is JObject a)
            {
                arcade = new JObject();
                foreach (var sub in ArcadePortableFields)
                    if (a[sub] != null) arcade[sub] = a[sub];
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
                SourceLatches = BuildSourceLatches(full),
                Settings = portable,
                Forza = forza,
                Arcade = arcade,
                Library = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase),
            };
        }

        /// <summary>Migration latches that are Excluded (so they never travel as SETTINGS)
        /// but whose SUBJECT is Portable (so the data they fix up does). That combination is
        /// a trap: a restore lands the source's unmigrated values behind a latch the
        /// receiving PC has already stamped, and the migration never runs against them
        /// again. Carrying the latch VALUE as envelope provenance - not as a setting - lets
        /// the restore notice and re-run, without breaking the rule that a latch must not be
        /// written onto PC2 wholesale.
        ///
        /// Both members are the condition engine's: the generation counter resets every gain
        /// and filter, and the inertia-spec latch repairs the inertia gain and its mode. Add
        /// to this list whenever a new latch governs Portable data; leaving one off is silent
        /// (the data simply never gets re-migrated), which is what the self-test covers.</summary>
        public static readonly string[] LatchesGoverningPortableData =
        {
            "FfbConditionDefaultsGeneration", "FfbConditionInertiaSpecMigrated",
        };

        private static JObject BuildSourceLatches(JObject full)
        {
            var latches = new JObject();
            foreach (var name in LatchesGoverningPortableData)
            {
                var tok = full[name];
                if (tok != null) latches[name] = tok;
            }
            return latches.Count > 0 ? latches : null;
        }

        /// <summary>True when this envelope states its latch provenance at all. An envelope
        /// built before SourceLatches existed says NOTHING about what its source had migrated,
        /// and "nothing" must not be read as "migrated nothing".
        ///
        /// The difference is destructive. The condition-generation re-run rewrites every gain
        /// and filter to shipped defaults, and those fields hold real bench tuning, so reading
        /// an absent block as generation 0 would wipe the tuning out of any backup made before
        /// this provenance existed. Every build that HAS a generation counter was already
        /// driven to the current generation by its own Init, so silence almost always means
        /// "same as us". Callers re-arm only when the envelope actually states a lower
        /// value.</summary>
        public static bool HasLatchProvenance(BackupEnvelope env) => env?.SourceLatches != null;

        /// <summary>Read one latch out of an envelope's provenance. Returns
        /// <paramref name="whenAbsent"/> when the envelope does not state this latch; callers
        /// that would act destructively on the answer must check
        /// <see cref="HasLatchProvenance"/> first rather than leaning on that default.</summary>
        public static int SourceLatch(BackupEnvelope env, string name, int whenAbsent = 0)
        {
            var tok = env?.SourceLatches?[name];
            if (tok == null) return whenAbsent;
            try
            {
                if (tok.Type == JTokenType.Boolean) return tok.Value<bool>() ? 1 : 0;
                return tok.Value<int>();
            }
            catch { return whenAbsent; }
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
                    // Withhold on DIFFERENCE, not on presence. Build emits every
                    // wheel-specific key on every push, so a presence test reports
                    // "tuning withheld" on each sync between two wheels even when
                    // both PCs hold identical values, and the Ask policy turns each
                    // of those into a fresh stash and an "apply anyway" notice. What
                    // the user is being asked about is a CHANGE to their feel, so a
                    // key equal to what this PC already has is not one.
                    var skipped = new JObject();
                    foreach (var key in FfbWheelSpecific)
                    {
                        var tok = filtered[key];
                        if (tok == null) continue;
                        // Always remove: the foreign value must not be written even
                        // when it matches, since "matches" is why there is nothing to
                        // ask about, not a reason to take the other wheel's copy.
                        filtered.Remove(key);
                        if (!MatchesLive(live, key, tok)) skipped[key] = tok;
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

            // Forza: merge only the portable fields onto the existing instance so PC2's
            // bind/forward addresses survive. Driven off ForzaPortableFields through the
            // serializer, exactly like the Arcade block below and for the reason that block
            // already gives: this was two hand-written casts, and it rotted the moment a
            // third field was added. ForwardGapBridge joined the list, Build started
            // bundling it, and nothing here ever wrote it, so turning the gap bridge off on
            // PC1 silently left it on everywhere else.
            if (env.Forza != null && live.Forza != null)
            {
                // Guarded: a malformed/old envelope (e.g. these stored as strings)
                // must not throw out of here. The settings Populate above has
                // already run, so an uncaught cast would leave a half-applied
                // restore. On failure, leave live.Forza at its current values.
                try
                {
                    var filteredForza = new JObject();
                    foreach (var sub in ForzaPortableFields)
                        if (env.Forza[sub] != null) filteredForza[sub] = env.Forza[sub];
                    if (filteredForza.Count > 0)
                        using (var reader = filteredForza.CreateReader())
                            CreateSerializer().Populate(reader, live.Forza);
                }
                catch { }
            }

            // Arcade: merge only the leaderboard preferences, so PC2's installed dumps, its exe
            // names and its own disclosure latch survive. Populated through the serializer rather
            // than field by field because two of the four are enums, and a hand-written cast would
            // be the thing that rots when a fifth is added.
            if (env.Arcade != null && live.Arcade != null)
            {
                try
                {
                    var filteredArcade = new JObject();
                    foreach (var sub in ArcadePortableFields)
                        if (env.Arcade[sub] != null) filteredArcade[sub] = env.Arcade[sub];
                    if (filteredArcade.Count > 0)
                        using (var reader = filteredArcade.CreateReader())
                            CreateSerializer().Populate(reader, live.Arcade);
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
        /// the settings snapshot from before the import (this PC's real FFB + wheel).
        ///
        /// <paramref name="sourceWheel"/> is the wheel label read out of the FILE, and it is
        /// a parameter rather than something read back off <paramref name="importedFile"/>
        /// because that is what made this gate dead code for its whole life. On the import
        /// path `importedFile` and `live` are the SAME object (ImportSettings publishes the
        /// deserialized file as Settings), and LastUsedWheel is MachineLocal, so by the time
        /// the gate ran the file's wheel had already been overwritten with this PC's and the
        /// comparison was this PC against itself: never different, never gated. Passing the
        /// label in forces the caller to capture it before it can be laundered.</summary>
        public static BackupApplyResult GateImportedCrossWheelFfb(
            TrueforceSettings importedFile, TrueforceSettings localBefore, TrueforceSettings live,
            string sourceWheel)
        {
            var result = new BackupApplyResult();
            if (importedFile == null || localBefore == null || live == null) return result;
            if (live.CrossWheelFfbMode == CrossWheelFfbMode.Always) return result;
            if (!WheelModelsDiffer(sourceWheel, localBefore.LastUsedWheel)) return result;

            var ser         = CreateSerializer();
            var importedJson = JObject.FromObject(importedFile, ser);  // the file's (foreign-wheel) FFB
            var localJson    = JObject.FromObject(localBefore, ser);   // this PC's FFB
            var restore = new JObject();
            var skipped = new JObject();
            foreach (var key in FfbWheelSpecific)
            {
                var imp = importedJson[key];
                var loc = localJson[key];
                // Report only what actually CHANGES, same rule as the cloud gate: a
                // foreign value equal to this PC's is not a change to ask about.
                if (imp != null && !JToken.DeepEquals(imp, loc)) skipped[key] = imp;
                if (loc != null) restore[key] = loc;
            }
            if (restore.Count > 0)
                using (var r = restore.CreateReader())
                    ser.Populate(r, live);   // put this PC's FFB tuning back onto live
            if (skipped.Count > 0)
            {
                result.FfbGated    = true;
                result.SkippedFfb  = skipped;
                result.SourceWheel = string.IsNullOrWhiteSpace(sourceWheel) ? null : sourceWheel;
            }
            return result;
        }

        /// <summary>Restore the machine-local HALF of the two PARTIAL objects after a
        /// wholesale settings replace, the way <c>PreserveMachineLocalSettings</c> restores
        /// the machine-local bucket at the top level. The cloud path never needs this (Build
        /// only ever bundles the portable fields), but a settings file or backup zip carries
        /// whole objects, so without this a PC1 bind address, a PC1 TeknoParrot path and the
        /// arcade master switch all land on PC2 - the exact failure the field-level split was
        /// introduced to prevent, going in through the other door.
        ///
        /// Driven off the *MachineLocalFields arrays so it cannot drift from the split, and
        /// deliberately NOT written as "everything that is not portable": ArcadeSettings also
        /// holds per-cabinet feel that only lives in settings, and blanket-preserving it would
        /// wipe an arcade user's tuning on the same-PC restore the feature exists for.</summary>
        public static void PreserveMachineLocalPartials(TrueforceSettings from, TrueforceSettings to)
        {
            if (from == null || to == null) return;
            CopyFields(from.Forza,  to.Forza,  ForzaMachineLocalFields);
            CopyFields(from.Arcade, to.Arcade, ArcadeMachineLocalFields);
        }

        private static void CopyFields(object from, object to, string[] names)
        {
            if (from == null || to == null) return;
            var t = from.GetType();
            foreach (var name in names)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanRead || !p.CanWrite) continue;
                try { p.SetValue(to, p.GetValue(from)); } catch { /* best-effort per field */ }
            }
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
            classified.Add(PartialArcade);

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
            return Portable.Concat(MachineLocal).Concat(Excluded).Concat(new[] { PartialForza, PartialArcade })
                .GroupBy(x => x, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
        }

        /// <summary>Guard: the MIRROR of <see cref="FindUnclassifiedFields"/>. That one walks
        /// properties and asks whether each is classified; this walks the classifications and
        /// asks whether each names a real property. Nothing checked that direction, which is
        /// how "MenuHaptics" and "MinForcePercent" sat in Portable matching nothing: the names
        /// belong to ArcadeSettings, so Build skipped them silently and two real settings were
        /// backed up by no path at all while the list said otherwise.
        ///
        /// Covers the partial field arrays too, which were validated against nothing.
        /// Returns "Bucket.Name" for each offender (empty = healthy).</summary>
        public static IReadOnlyList<string> FindStaleClassifications()
        {
            var stale = new List<string>();
            CollectStale(stale, "Portable",     typeof(TrueforceSettings), Portable);
            CollectStale(stale, "MachineLocal", typeof(TrueforceSettings), MachineLocal);
            CollectStale(stale, "Excluded",     typeof(TrueforceSettings), Excluded);
            CollectStale(stale, "LatchesGoverningPortableData", typeof(TrueforceSettings), LatchesGoverningPortableData);
            // FfbWheelSpecific is the list where a wrong name is most invisible: its entries are
            // only ever used as lookups into other JObjects, so a stale one gates nothing and
            // says nothing. A name here that is not also Portable is equally dead, because the
            // gate only ever sees keys that survived the Portable filter.
            CollectStale(stale, "FfbWheelSpecific", typeof(TrueforceSettings), FfbWheelSpecific);
            foreach (var name in FfbWheelSpecific)
                if (!Portable.Contains(name))
                    stale.Add("FfbWheelSpecific." + name + " (not Portable, so it can never be withheld)");
            CollectStale(stale, "ForzaPortableFields",      typeof(ForzaSettings),  ForzaPortableFields);
            CollectStale(stale, "ForzaMachineLocalFields",  typeof(ForzaSettings),  ForzaMachineLocalFields);
            CollectStale(stale, "ArcadePortableFields",     typeof(ArcadeSettings), ArcadePortableFields);
            CollectStale(stale, "ArcadeMachineLocalFields", typeof(ArcadeSettings), ArcadeMachineLocalFields);
            return stale;
        }

        private static void CollectStale(List<string> into, string bucket, Type owner, IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                var p = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanRead || !p.CanWrite) into.Add(bucket + "." + name);
            }
        }

        /// <summary>Guard: the partial objects get the same completeness promise as the top
        /// level. Every read/write property of ForzaSettings and ArcadeSettings must be either
        /// portable or machine-local, so a field added to one of them cannot default into
        /// "travels on a file import but not in the cloud", which is precisely the state the
        /// whole Arcade object was in. Returns "Type.Name" for each unclassified property.</summary>
        public static IReadOnlyList<string> FindUnclassifiedPartialFields()
        {
            var missing = new List<string>();
            CollectUnclassified(missing, typeof(ForzaSettings),  ForzaPortableFields,  ForzaMachineLocalFields);
            CollectUnclassified(missing, typeof(ArcadeSettings), ArcadePortableFields, ArcadeMachineLocalFields);
            return missing;
        }

        private static void CollectUnclassified(List<string> into, Type owner, string[] portable, string[] machine)
        {
            var known = new HashSet<string>(portable, StringComparer.Ordinal);
            known.UnionWith(machine);
            foreach (var p in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (!known.Contains(p.Name)) into.Add(owner.Name + "." + p.Name);
            }
        }

        /// <summary>Guard: no <see cref="BackupEnvelope"/> member is silently dropped when an
        /// envelope is rebuilt (the merge paths construct a fresh one by hand). Returns the
        /// names present on <paramref name="from"/> but null/absent on <paramref name="to"/>,
        /// ignoring <c>Library</c>, which a merge legitimately rebuilds. This is the guard
        /// that would have caught SourceWheelModel and Arcade going missing through Merge and
        /// quietly disabling the cross-wheel gate on every merged backup.</summary>
        public static IReadOnlyList<string> FindDroppedEnvelopeFields(BackupEnvelope from, BackupEnvelope to)
        {
            var dropped = new List<string>();
            if (from == null || to == null) return dropped;
            foreach (var p in typeof(BackupEnvelope).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.Name == "Library" || p.Name == "CreatedUtc" || p.Name == "DeviceLabel") continue;
                object a = null, b = null;
                try { a = p.GetValue(from); b = p.GetValue(to); } catch { continue; }
                if (a != null && b == null) dropped.Add(p.Name);
            }
            return dropped;
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

        /// <summary>Provenance, not settings: the value each latch in
        /// <see cref="BackupProjection.LatchesGoverningPortableData"/> held on the source PC.
        /// Lets the receiving PC notice that this payload predates a data migration it has
        /// already stamped, and re-run that migration against the restored values instead of
        /// leaving them permanently unmigrated behind its own latch. Null on envelopes built
        /// before this existed, which reads as "assume it had migrated nothing" - safe,
        /// because that can only cause a re-run, never a skip.</summary>
        public JObject SourceLatches { get; set; }

        /// <summary>Portable top-level settings keys (NO Forza, NO machine/excluded fields).</summary>
        public JObject Settings { get; set; }

        /// <summary>Forza's portable fields only (BackupProjection.ForzaPortableFields), or
        /// null. Named by that list rather than spelled out here, because spelling them out
        /// is how the restore came to be missing one.</summary>
        public JObject Forza { get; set; }

        /// <summary>Arcade's portable fields only (the Initial D 8 leaderboard preferences), or
        /// null. Absent from every envelope written before this existed, which restores as "leave
        /// this PC's arcade settings alone" and is the right behaviour for an old backup.</summary>
        public JObject Arcade { get; set; }

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
