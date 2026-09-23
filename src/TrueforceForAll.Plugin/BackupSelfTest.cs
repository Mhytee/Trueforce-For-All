// Phase 2 backup/sync, M1 verification. Exercises the full backup round-trip on a
// representative settings object + a sample preset folder, asserting:
//   - every portable field survives Build -> Apply byte-equal (no data loss),
//   - machine-local fields on the TARGET are NOT clobbered by the restore,
//   - Forza's portable split travels but its bind address stays local,
//   - the preset library files round-trip verbatim, with the import inbox and
//     hidden working dirs correctly skipped,
//   - the classification audit is clean (every field classified, none twice, and
//     every classified NAME resolves to a real property, which is the direction
//     that let two Portable entries match nothing for a month),
//   - a MERGED envelope keeps its provenance. Both merge paths used to hand-build
//     an envelope and set 6 of its 8 members, dropping the wheel stamp, which
//     silently disarmed the cross-wheel FFB gate on every merged backup,
//   - the cross-wheel gate withholds on a real DIFFERENCE rather than on the mere
//     presence of a key, so syncing two wheels does not raise a prompt per sync,
//   - the file-import path keeps this PC's half of the two partial objects.
//
// Pure logic (no SimHub, no network). Run it from the DEV panel, or standalone via
// the SelfTestHarness console. Returns (ok, report-lines).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    public static class BackupSelfTest
    {
        public static (bool ok, List<string> lines) RunRoundTrip()
        {
            var lines = new List<string>();
            bool ok = true;
            void Check(string name, bool cond)
            {
                lines.Add((cond ? "PASS  " : "FAIL  ") + name);
                if (!cond) ok = false;
            }

            // ---- Classification audit -------------------------------------
            var unclassified = BackupProjection.FindUnclassifiedFields();
            var doubled = BackupProjection.FindDoubleClassifiedFields();
            Check($"every settings field classified (unclassified: {unclassified.Count})", unclassified.Count == 0);
            if (unclassified.Count > 0) lines.Add("        -> " + string.Join(", ", unclassified));
            Check($"no field classified twice (doubled: {doubled.Count})", doubled.Count == 0);
            if (doubled.Count > 0) lines.Add("        -> " + string.Join(", ", doubled));

            // ---- Settings round-trip --------------------------------------
            // SOURCE: a user's setup with non-default portable values + distinctive
            // machine-local values.
            var src = new TrueforceSettings();
            src.MasterGain = 0.789f;
            src.MasterGainStep = 0.123f;       // the field the owner moved to portable
            src.FfbScale = 0.42f;
            src.SharingAuthor = "Mhytee";
            src.DevModeUnlocked = true;         // earned unlock -> portable
            src.ExperimentalFfbCapture = true;
            src.GameEnabled["AssettoCorsa"] = false;
            src.GameEnabled["FH6"] = true;
            src.MasterMode = TrueforceMasterMode.LightsyncOnly;   // portable: a real choice
            src.MasterModeMigratedV1 = true;                      // excluded: a migration latch
            src.AudioCaptureExeOverrides["FH6"] = "forza";
            src.CarFacts["AssettoCorsa/ks_x"] = new CarFactsBundle { CarName = "Test Car" };
            src.UsbPcapCmdPathOverride = "SOURCE_USB";   // machine-local -> must NOT travel
            src.Performance.TfRingSize = 32;             // machine-local
            src.AuthSession = new CommunityAuthSession { Email = "source@x.com" }; // must NOT travel
            src.Forza.Enabled = false;          // portable
            src.Forza.Port = 9999;              // portable
            src.Forza.BindAddress = "1.2.3.4";  // machine-local -> must NOT travel

            // TARGET: a DIFFERENT PC with its own machine-local config + stale portable values.
            var target = new TrueforceSettings();
            target.MasterGain = 0.111f;
            target.SharingAuthor = "OtherUser";
            target.UsbPcapCmdPathOverride = "TARGET_USB";
            target.Performance.TfRingSize = 16;
            target.AuthSession = new CommunityAuthSession { Email = "target@x.com" };
            target.Forza.BindAddress = "10.0.0.9";
            target.Forza.Port = 5300;

            var envelope = BackupProjection.Build(src, "PC1", DateTime.UtcNow);
            BackupProjection.ApplySettings(envelope, target);

            // Portable values copied onto the target.
            Check("portable: MasterGain copied", Math.Abs(target.MasterGain - 0.789f) < 1e-6);
            Check("portable: MasterGainStep copied", Math.Abs(target.MasterGainStep - 0.123f) < 1e-6);
            Check("portable: SharingAuthor copied", target.SharingAuthor == "Mhytee");
            Check("portable: unlock (DevModeUnlocked) copied", target.DevModeUnlocked);
            Check("portable: GameEnabled copied", target.GameEnabled.TryGetValue("AssettoCorsa", out var ae) && !ae
                                                  && target.GameEnabled.TryGetValue("FH6", out var fe) && fe);
            Check("portable: AudioCaptureExeOverrides copied",
                target.AudioCaptureExeOverrides.TryGetValue("FH6", out var exe) && exe == "forza");
            Check("portable: CarFacts copied",
                target.CarFacts.TryGetValue("AssettoCorsa/ks_x", out var cf) && cf?.CarName == "Test Car");
            Check("portable: MasterMode copied", target.MasterMode == TrueforceMasterMode.LightsyncOnly);
            Check("excluded: MasterModeMigratedV1 not carried", !target.MasterModeMigratedV1);
            Check("portable: Forza.Enabled copied", target.Forza.Enabled == false);
            Check("portable: Forza.Port copied", target.Forza.Port == 9999);

            // Machine-local values on the target NOT clobbered.
            Check("machine-local kept: UsbPcapCmdPathOverride", target.UsbPcapCmdPathOverride == "TARGET_USB");
            Check("machine-local kept: Performance.TfRingSize", target.Performance.TfRingSize == 16);
            Check("machine-local kept: AuthSession", target.AuthSession?.Email == "target@x.com");
            Check("machine-local kept: Forza.BindAddress", target.Forza.BindAddress == "10.0.0.9");

            // Holistic: re-projecting the restored target reproduces the backup
            // exactly -> every portable key round-tripped with no loss.
            var reproject = BackupProjection.Build(target, "PC2", DateTime.UtcNow);
            Check("portable projection round-trips byte-equal",
                JToken.DeepEquals(envelope.Settings, reproject.Settings));
            Check("Forza split round-trips byte-equal",
                JToken.DeepEquals(envelope.Forza, reproject.Forza));

            // ---- Preset library round-trip --------------------------------
            string srcDir = Path.Combine(Path.GetTempPath(), "tfbk-src-" + Guid.NewGuid().ToString("N"));
            string dstDir = Path.Combine(Path.GetTempPath(), "tfbk-dst-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteFile(srcDir, "game-defaults.json", "{\"AssettoCorsa\":\"MyTune\"}");
                WriteFile(srcDir, "car-defaults.json", "{\"Forza_1234\":\"CarTune\"}");
                WriteFile(srcDir, "games/MyTune.json", "{\"MasterGain\":0.8}");
                WriteFile(srcDir, "cars/FH6/Forza_1234/CarTune.json",
                    "{\"Type\":\"trueforce-car-preset\",\"GameName\":\"FH6\",\"CarId\":\"Forza_1234\",\"PresetName\":\"CarTune\"}");
                WriteFile(srcDir, "import/pending.json", "{\"x\":1}");        // skipped (inbox)
                WriteFile(srcDir, ".cleanup-1/old.json", "{\"y\":2}");        // skipped (hidden)

                var bundle = BackupLibrary.Bundle(srcDir);
                Check("library: real files bundled (4)", bundle.Count == 4);
                Check("library: defaults files bundled",
                    bundle.ContainsKey("game-defaults.json") && bundle.ContainsKey("car-defaults.json"));
                Check("library: game folder + content preserved",
                    bundle.TryGetValue("cars/FH6/Forza_1234/CarTune.json", out var carFile)
                    && carFile.Text.Contains("\"GameName\":\"FH6\""));
                Check("library: import inbox skipped",
                    !bundle.ContainsKey("import/pending.json"));
                Check("library: hidden working dir skipped",
                    !bundle.ContainsKey(".cleanup-1/old.json"));

                BackupLibrary.Restore(dstDir, bundle);
                var rebundle = BackupLibrary.Bundle(dstDir);
                bool same = rebundle.Count == bundle.Count;
                foreach (var kv in bundle)
                    same &= rebundle.TryGetValue(kv.Key, out var v) && v != null && v.Text == kv.Value.Text;
                Check("library: restore reproduces every file byte-for-byte", same);

                // Restore is additive: a pre-existing dst file not in the bundle survives.
                WriteFile(dstDir, "games/LocalOnly.json", "{\"local\":true}");
                BackupLibrary.Restore(dstDir, bundle);
                Check("library: restore is additive (keeps target-only presets)",
                    File.Exists(Path.Combine(dstDir, "games", "LocalOnly.json")));
            }
            finally
            {
                TryDelete(srcDir);
                TryDelete(dstDir);
            }

            // ---- Per-file mtime preserved through restore (newest-wins basis) ----
            string mtSrc = Path.Combine(Path.GetTempPath(), "tfbk-mt-" + Guid.NewGuid().ToString("N"));
            string mtDst = Path.Combine(Path.GetTempPath(), "tfbk-mtd-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteFile(mtSrc, "games/Stamped.json", "{\"x\":1}");
                var past = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Path.Combine(mtSrc, "games", "Stamped.json"), past);
                var mb = BackupLibrary.Bundle(mtSrc);
                Check("mtime: captured at bundle",
                    mb.TryGetValue("games/Stamped.json", out var sf)
                    && Math.Abs((sf.ModifiedUtc - past).TotalSeconds) < 2);
                BackupLibrary.Restore(mtDst, mb);
                var restoredMt = File.GetLastWriteTimeUtc(Path.Combine(mtDst, "games", "Stamped.json"));
                Check("mtime: preserved through restore (no false-newer)",
                    Math.Abs((restoredMt - past).TotalSeconds) < 2);
            }
            finally { TryDelete(mtSrc); TryDelete(mtDst); }

            // ---- Newest-wins merge (union + newer file wins on a clash) ----
            var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var localLib = new System.Collections.Generic.Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase)
            {
                ["games/A.json"] = new BackupFile { Text = "local-A-new", ModifiedUtc = t2 },
                ["games/B.json"] = new BackupFile { Text = "local-B",     ModifiedUtc = t1 },
            };
            var cloudLib = new System.Collections.Generic.Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase)
            {
                ["games/A.json"] = new BackupFile { Text = "cloud-A-old", ModifiedUtc = t1 },
                ["games/C.json"] = new BackupFile { Text = "cloud-C",     ModifiedUtc = t2 },
            };
            var merged = BackupLibrary.MergeNewestWins(localLib, cloudLib);
            Check("merge: union keeps all paths (3)", merged.Count == 3);
            Check("merge: newest wins on clash (local newer)",
                merged.TryGetValue("games/A.json", out var ma) && ma.Text == "local-A-new");
            Check("merge: local-only kept", merged.ContainsKey("games/B.json"));
            Check("merge: cloud-only kept", merged.ContainsKey("games/C.json"));

            // ---- BackupService codec + merge (settings side-pick, null fallback) ----
            string svcDir = Path.Combine(Path.GetTempPath(), "tfbk-svc-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteFile(svcDir, "games/SvcTune.json", "{\"MasterGain\":0.5}");
                var svcSrc = new TrueforceSettings { MasterGain = 0.654f, SharingAuthor = "Svc" };
                var built = BackupService.BuildEnvelope(svcSrc, svcDir, "PCX", new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
                string js = BackupService.Serialize(built);
                var parsed = BackupService.Parse(js);
                Check("codec: serialize->parse keeps library",
                    parsed?.Library != null && parsed.Library.ContainsKey("games/SvcTune.json"));
                // Round-trip via APPLY (the real use): serialize -> parse -> Populate.
                // (Don't DeepEquals the JObjects: FromObject emits floats, JSON re-parses
                // as doubles, so 0.654f != 0.654d at the token level though Populate coerces.)
                var applied = new TrueforceSettings();
                if (parsed != null) BackupProjection.ApplySettings(parsed, applied);
                Check("codec: serialize->parse->apply restores a settings value",
                    parsed?.Settings != null && Math.Abs(applied.MasterGain - 0.654f) < 1e-4);
                Check("codec: BackupFile.ModifiedUtc survives JSON round-trip",
                    parsed.Library.TryGetValue("games/SvcTune.json", out var pf) && pf.ModifiedUtc.Year > 2000);

                var cloudFull = BackupService.BuildEnvelope(new TrueforceSettings { MasterGain = 0.111f }, svcDir, "Cloud", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var mLocal2 = BackupService.Merge(built, cloudFull, keepCloudSettings: false, "PCX", new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
                var mCloud2 = BackupService.Merge(built, cloudFull, keepCloudSettings: true, "PCX", new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
                Check("merge: keepCloudSettings=false uses local settings", JToken.DeepEquals(mLocal2.Settings, built.Settings));
                Check("merge: keepCloudSettings=true uses cloud settings", JToken.DeepEquals(mCloud2.Settings, cloudFull.Settings));

                var cloudNull = new BackupEnvelope { SchemaVersion = 1, Settings = null, Forza = null, Library = new System.Collections.Generic.Dictionary<string, BackupFile>() };
                var mFallback = BackupService.Merge(built, cloudNull, keepCloudSettings: true, "PCX", new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
                Check("merge: null cloud settings falls back to local (no settings dropped)",
                    mFallback.Settings != null && JToken.DeepEquals(mFallback.Settings, built.Settings));
            }
            finally { TryDelete(svcDir); }

            // ---- ApplySettings ignores reclassified (non-Portable) keys ----
            var reclassEnv = BackupProjection.Build(new TrueforceSettings { MasterGain = 0.42f }, "PC", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            reclassEnv.Settings["UsbPcapCmdPathOverride"] = JToken.FromObject("OLD_USB_FROM_BACKUP"); // simulate an old backup carrying a since-machine-local field
            var reclassTarget = new TrueforceSettings { UsbPcapCmdPathOverride = "TARGET_USB", MasterGain = 0.1f };
            BackupProjection.ApplySettings(reclassEnv, reclassTarget);
            Check("reclassification guard: machine-local key in envelope NOT applied",
                reclassTarget.UsbPcapCmdPathOverride == "TARGET_USB");
            Check("reclassification guard: portable key still applied",
                Math.Abs(reclassTarget.MasterGain - 0.42f) < 1e-6);

            // ---- Cross-wheel FFB gate ----
            var wheelDate = new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc);

            // FfbWheelSpecific must stay a subset of Portable, or the gate would
            // strip keys ApplySettings never writes (a no-op) or miss ones it does.
            bool ffbSubset = true;
            foreach (var k in BackupProjection.FfbWheelSpecific)
                if (!BackupProjection.Portable.Contains(k)) ffbSubset = false;
            Check("cross-wheel gate: FfbWheelSpecific is a subset of Portable", ffbSubset);

            // Build stamps SourceWheelModel from LastUsedWheel.
            var srcGpro = BackupProjection.Build(
                new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.77f, MasterGain = 0.44f },
                "PC", wheelDate);
            Check("cross-wheel gate: Build stamps SourceWheelModel", srcGpro.SourceWheelModel == "G PRO");

            // Matching wheel (Ask): FFB applies, not gated.
            var tgtMatch = new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.10f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var rMatch = BackupProjection.ApplySettings(srcGpro, tgtMatch);
            Check("cross-wheel gate: matching wheel applies FFB",
                Math.Abs(tgtMatch.ModeBSatGain - 0.77f) < 1e-6 && !rMatch.FfbGated);

            // Mismatch + Ask: Mode B withheld, non-FFB still applied, result reports the stash.
            var tgtMis = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.10f, MasterGain = 0.10f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var rMis = BackupProjection.ApplySettings(srcGpro, tgtMis);
            Check("cross-wheel gate: mismatch + Ask withholds Mode B", Math.Abs(tgtMis.ModeBSatGain - 0.10f) < 1e-6);
            Check("cross-wheel gate: mismatch + Ask still applies non-FFB", Math.Abs(tgtMis.MasterGain - 0.44f) < 1e-6);
            Check("cross-wheel gate: mismatch reports FfbGated + source + stash",
                rMis.FfbGated && rMis.SourceWheel == "G PRO"
                && rMis.SkippedFfb != null && rMis.SkippedFfb["ModeBSatGain"] != null);

            // Mismatch + Never: withheld too (the plugin decides silent vs notice; ApplySettings withholds either way).
            var tgtNever = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.10f, CrossWheelFfbMode = CrossWheelFfbMode.Never };
            var rNever = BackupProjection.ApplySettings(srcGpro, tgtNever);
            Check("cross-wheel gate: mismatch + Never withholds Mode B",
                Math.Abs(tgtNever.ModeBSatGain - 0.10f) < 1e-6 && rNever.FfbGated);

            // Mismatch + Always: FFB applies across wheels.
            var tgtAlways = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.10f, CrossWheelFfbMode = CrossWheelFfbMode.Always };
            var rAlways = BackupProjection.ApplySettings(srcGpro, tgtAlways);
            Check("cross-wheel gate: Always applies FFB across wheels",
                Math.Abs(tgtAlways.ModeBSatGain - 0.77f) < 1e-6 && !rAlways.FfbGated);

            // Unknown source (no wheel known at build): never gates, even with Ask.
            var srcUnknown = BackupProjection.Build(
                new TrueforceSettings { LastUsedWheel = "", ModeBSatGain = 0.77f }, "PC", wheelDate);
            var tgtUnknown = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.10f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var rUnknown = BackupProjection.ApplySettings(srcUnknown, tgtUnknown);
            Check("cross-wheel gate: unknown source applies FFB (never gates)",
                srcUnknown.SourceWheelModel == null
                && Math.Abs(tgtUnknown.ModeBSatGain - 0.77f) < 1e-6 && !rUnknown.FfbGated);

            // Manual-import gate: same behavior as the cloud gate, computed on live
            // settings. `live` simulates the post-replace state (imported FFB, this
            // PC's wheel + policy). importedFile = the file, localBefore = this PC pre-import.
            var impFile        = new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.77f };
            var impLocalBefore = new TrueforceSettings { LastUsedWheel = "G923",  ModeBSatGain = 0.10f };
            var impLiveMis = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.77f, MasterGain = 0.44f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var impMis = BackupProjection.GateImportedCrossWheelFfb(impFile, impLocalBefore, impLiveMis, "G PRO");
            Check("import gate: mismatch restores this PC's Mode B", Math.Abs(impLiveMis.ModeBSatGain - 0.10f) < 1e-6);
            Check("import gate: mismatch leaves non-FFB alone", Math.Abs(impLiveMis.MasterGain - 0.44f) < 1e-6);
            Check("import gate: mismatch reports FfbGated + source + stashed foreign value",
                impMis.FfbGated && impMis.SourceWheel == "G PRO"
                && impMis.SkippedFfb?["ModeBSatGain"] != null
                && Math.Abs(impMis.SkippedFfb["ModeBSatGain"].Value<float>() - 0.77f) < 1e-6);

            var impLiveMatch = new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.77f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var impMatch = BackupProjection.GateImportedCrossWheelFfb(
                new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.77f },
                new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.10f },
                impLiveMatch, "G PRO");
            Check("import gate: matching wheel keeps imported FFB (no gate)",
                Math.Abs(impLiveMatch.ModeBSatGain - 0.77f) < 1e-6 && !impMatch.FfbGated);

            var impLiveAlways = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.77f, CrossWheelFfbMode = CrossWheelFfbMode.Always };
            var impAlways = BackupProjection.GateImportedCrossWheelFfb(impFile, impLocalBefore, impLiveAlways, "G PRO");
            Check("import gate: Always keeps imported FFB across wheels",
                Math.Abs(impLiveAlways.ModeBSatGain - 0.77f) < 1e-6 && !impAlways.FfbGated);

            // The import gate's source wheel is a PARAMETER, and this is why. On the real
            // import path `importedFile` and `live` are the same object and LastUsedWheel is
            // MachineLocal, so by the time the gate ran the file's wheel had been replaced by
            // this PC's. Passing the laundered label must produce no gate, which is exactly
            // the dead-code state the parameter exists to make impossible to reach by
            // accident: if someone "simplifies" the signature back, this check goes red.
            var impLiveLaundered = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.77f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var impLaundered = BackupProjection.GateImportedCrossWheelFfb(
                impFile, impLocalBefore, impLiveLaundered, "G923");   // the laundered label
            Check("import gate: a laundered source wheel does NOT gate (regression marker)",
                !impLaundered.FfbGated);

            // Value-aware gating: the foreign wheel's value is identical to this PC's, so
            // there is nothing to ask about. Before this, Build emitted every wheel-specific
            // key on every push and the gate fired on mere presence, so each sync between two
            // wheels raised a fresh "apply anyway" prompt about values that never changed.
            var srcSame = BackupProjection.Build(
                new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.55f, MasterGain = 0.9f },
                "PC", wheelDate);
            var tgtSame = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.55f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var rSame = BackupProjection.ApplySettings(srcSame, tgtSame);
            Check("cross-wheel gate: identical values across wheels do not raise a prompt", !rSame.FfbGated);
            Check("cross-wheel gate: identical values still not adopted from the other wheel",
                Math.Abs(tgtSame.ModeBSatGain - 0.55f) < 1e-6);
            Check("cross-wheel gate: non-FFB still applied alongside", Math.Abs(tgtSame.MasterGain - 0.9f) < 1e-6);

            // ...but a genuine difference must still be caught (guard against the value-aware
            // check turning into a blanket false negative).
            var tgtDiff = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.11f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var rDiff = BackupProjection.ApplySettings(srcSame, tgtDiff);
            Check("cross-wheel gate: a real difference is still withheld and reported",
                rDiff.FfbGated && Math.Abs(tgtDiff.ModeBSatGain - 0.11f) < 1e-6);

            // The condition-engine gains are wheel-specific (one right tuning per wheel, found
            // at the bench), so they ride the same gate as Mode B and the LED trim.
            Check("cross-wheel gate: condition gains are in the wheel-specific set",
                BackupProjection.FfbWheelSpecific.Contains("FfbConditionDamperGain")
                && BackupProjection.FfbWheelSpecific.Contains("FfbConditionSpringGain")
                && BackupProjection.FfbWheelSpecific.Contains("FfbConditionInertiaGain"));
            var srcCond = BackupProjection.Build(
                new TrueforceSettings { LastUsedWheel = "G PRO", FfbConditionDamperGain = 0.66f }, "PC", wheelDate);
            var tgtCond = new TrueforceSettings { LastUsedWheel = "G923", FfbConditionDamperGain = 0.22f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            BackupProjection.ApplySettings(srcCond, tgtCond);
            Check("cross-wheel gate: condition gains withheld across wheels",
                Math.Abs(tgtCond.FfbConditionDamperGain - 0.22f) < 1e-6);

            // ---- Merge carries provenance (findings 1 + 2) ----------------
            // The bug: both Merge overloads hand-built an envelope and set 6 of its 8 members,
            // so every merged backup lost SourceWheelModel and Arcade. A null wheel never
            // gates, so one merge silently disarmed the cross-wheel gate for BOTH devices
            // until somebody pushed a fresh envelope.
            var gproEnv = BackupProjection.Build(
                new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.77f, MasterGain = 0.5f },
                "PC1", wheelDate);
            var g923Env = BackupProjection.Build(
                new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.22f, MasterGain = 0.6f },
                "PC2", wheelDate);

            var sidePickLocal = BackupService.Merge(gproEnv, g923Env, keepCloudSettings: false, "PC1", wheelDate);
            var sidePickCloud = BackupService.Merge(gproEnv, g923Env, keepCloudSettings: true, "PC1", wheelDate);
            Check("merge (side-pick): keeps the settings side's wheel stamp",
                sidePickLocal.SourceWheelModel == "G PRO" && sidePickCloud.SourceWheelModel == "G923");
            Check("merge (side-pick): carries Arcade", sidePickLocal.Arcade != null);
            Check("merge (side-pick): carries latch provenance", sidePickLocal.SourceLatches != null);
            Check("merge (side-pick): drops no envelope member",
                BackupProjection.FindDroppedEnvelopeFields(gproEnv, sidePickLocal).Count == 0);

            // A merged envelope must still gate on the other wheel. This is the end-to-end
            // statement of the bug: build -> merge -> apply on a different wheel.
            var tgtAfterMerge = new TrueforceSettings { LastUsedWheel = "G923", ModeBSatGain = 0.22f, CrossWheelFfbMode = CrossWheelFfbMode.Ask };
            var rAfterMerge = BackupProjection.ApplySettings(sidePickLocal, tgtAfterMerge);
            Check("merge (side-pick): the merged envelope still gates on the other wheel",
                rAfterMerge.FfbGated && Math.Abs(tgtAfterMerge.ModeBSatGain - 0.22f) < 1e-6);

            // 3-way merge across two wheels: the wheel-specific keys must resolve to LOCAL, so
            // the shared copy is one wheel's tuning rather than a blend of two. A blend would
            // be laundered as native by the next ordinary push from either PC.
            var merged3 = BackupService.Merge(gproEnv, g923Env, null, null, null, null, "PC1", wheelDate);
            Check("merge (3-way): wheel-specific keys resolve to the local side",
                merged3.Settings?["ModeBSatGain"] != null
                && Math.Abs(merged3.Settings["ModeBSatGain"].Value<float>() - 0.77f) < 1e-6);
            Check("merge (3-way): stamped with the local wheel", merged3.SourceWheelModel == "G PRO");
            Check("merge (3-way): drops no envelope member",
                BackupProjection.FindDroppedEnvelopeFields(gproEnv, merged3).Count == 0);

            // Same wheel on both sides: no resolution, ordinary field merge still applies the
            // cloud's changed leaf (guard against the cross-wheel rule over-reaching).
            var g923Env2 = BackupProjection.Build(
                new TrueforceSettings { LastUsedWheel = "G PRO", ModeBSatGain = 0.99f }, "PC2", wheelDate);
            var merged3Same = BackupService.Merge(gproEnv, g923Env2, null, null, null, null, "PC1", wheelDate);
            Check("merge (3-way): same wheel still field-merges normally",
                merged3Same.SourceWheelModel == "G PRO" && merged3Same.Settings?["ModeBSatGain"] != null);

            // ---- Latch provenance (finding 7) -----------------------------
            // Hardening, not a live bug: no released build puts FfbCondition names in an
            // envelope at all. It bites on the next generation bump, when a payload built
            // before the reset lands on a PC that already stamped the latch.
            var genEnv = BackupProjection.Build(
                new TrueforceSettings { FfbConditionDefaultsGeneration = 0, FfbConditionDamperGain = 0.33f },
                "PC", wheelDate);
            Check("latch provenance: the generation is stamped into the envelope",
                BackupProjection.SourceLatch(genEnv, "FfbConditionDefaultsGeneration", 9) == 0);
            Check("latch provenance: the generation is NOT a portable setting",
                genEnv.Settings?["FfbConditionDefaultsGeneration"] == null);
            // Absent provenance means UNKNOWN, not "migrated nothing". Reading it as 0 would
            // re-run the generation reset against a backup made before this existed and wipe
            // real bench tuning, so callers ask HasLatchProvenance first.
            Check("latch provenance: an envelope without the block states nothing",
                !BackupProjection.HasLatchProvenance(new BackupEnvelope())
                && BackupProjection.HasLatchProvenance(genEnv));
            var genHigh = BackupProjection.Build(
                new TrueforceSettings { FfbConditionDefaultsGeneration = 1 }, "PC", wheelDate);
            var mergedLatch = BackupService.Merge(genHigh, genEnv, null, null, null, null, "PC", wheelDate);
            Check("latch provenance: a merge takes the LOWER generation of the two sides",
                BackupProjection.SourceLatch(mergedLatch, "FfbConditionDefaultsGeneration", 9) == 0);
            Check("latch provenance: a merge with one side silent claims nothing",
                BackupService.Merge(genHigh, new BackupEnvelope { Settings = new JObject() },
                    null, null, null, null, "PC", wheelDate).SourceLatches == null);

            // ---- Forza's third portable field (restore rot) ---------------
            // ForwardGapBridge joined ForzaPortableFields and Build started bundling it, but
            // the restore hand-wrote only Enabled and Port, so turning the gap bridge off on
            // PC1 left it on everywhere else. The old "round-trips byte-equal" check could not
            // see it: both sides sat at the default.
            var fzSrc = new TrueforceSettings();
            fzSrc.Forza.Enabled = true;
            fzSrc.Forza.Port = 5300;
            fzSrc.Forza.ForwardGapBridge = false;          // the non-default the user chose
            fzSrc.Forza.BindAddress = "192.168.1.50";      // machine-local, must not travel
            var fzEnv = BackupProjection.Build(fzSrc, "PC1", wheelDate);
            var fzTgt = new TrueforceSettings();
            fzTgt.Forza.BindAddress = "0.0.0.0";
            BackupProjection.ApplySettings(fzEnv, fzTgt);
            Check("forza: every portable field restores, including ForwardGapBridge",
                fzTgt.Forza.ForwardGapBridge == false && fzTgt.Forza.Port == 5300);
            Check("forza: the bind address stays this PC's", fzTgt.Forza.BindAddress == "0.0.0.0");

            // ---- Arcade taste travels, arcade machine facts do not --------
            var acSrc = new TrueforceSettings();
            acSrc.Arcade.Enabled = true;                   // shelved-feature gate: must NOT travel
            acSrc.Arcade.MenuHaptics = true;               // taste: must travel
            acSrc.Arcade.MinForcePercent = 25;             // taste: must travel
            acSrc.Arcade.TeknoParrotPath = @"D:\PC1\TP";   // machine fact: must NOT travel
            var acEnv = BackupProjection.Build(acSrc, "PC1", wheelDate);
            var acTgt = new TrueforceSettings();
            BackupProjection.ApplySettings(acEnv, acTgt);
            Check("arcade: menu haptics + force floor now travel (they matched no property before)",
                acTgt.Arcade.MenuHaptics && acTgt.Arcade.MinForcePercent == 25);
            Check("arcade: the shelved-feature master switch does not travel", !acTgt.Arcade.Enabled);
            Check("arcade: the TeknoParrot path does not travel",
                string.IsNullOrEmpty(acTgt.Arcade.TeknoParrotPath));

            // The machine-local half of the partials, restored after a WHOLESALE replace.
            // This is the file/zip path, which the cloud projection never exercises.
            var partialLocal = new TrueforceSettings();
            partialLocal.Forza.BindAddress   = "10.0.0.9";
            partialLocal.Arcade.Enabled      = false;
            partialLocal.Arcade.TeknoParrotPath = @"C:\PC2\TP";
            var partialImported = new TrueforceSettings();
            partialImported.Forza.BindAddress   = "192.168.1.50";   // PC1's NIC, not on PC2
            partialImported.Forza.Port          = 5301;             // portable: keep the file's
            partialImported.Arcade.Enabled      = true;             // PC1 had arcade unlocked
            partialImported.Arcade.MenuHaptics  = true;             // taste: keep the file's
            partialImported.Arcade.TeknoParrotPath = @"D:\PC1\TP";
            BackupProjection.PreserveMachineLocalPartials(partialLocal, partialImported);
            Check("import partials: the file's bind address is replaced by this PC's",
                partialImported.Forza.BindAddress == "10.0.0.9");
            Check("import partials: the file's portable Forza fields survive",
                partialImported.Forza.Port == 5301);
            Check("import partials: an imported file cannot unlock the shelved arcade path",
                !partialImported.Arcade.Enabled);
            Check("import partials: this PC's TeknoParrot path survives",
                partialImported.Arcade.TeknoParrotPath == @"C:\PC2\TP");
            Check("import partials: the file's arcade taste survives", partialImported.Arcade.MenuHaptics);

            // ---- Classification guards (finding 5) ------------------------
            var stale = BackupProjection.FindStaleClassifications();
            Check($"every classified name resolves to a real property (stale: {stale.Count})", stale.Count == 0);
            if (stale.Count > 0) lines.Add("        -> " + string.Join(", ", stale));
            var partialUnclassified = BackupProjection.FindUnclassifiedPartialFields();
            Check($"every Forza/Arcade field is classified (unclassified: {partialUnclassified.Count})",
                partialUnclassified.Count == 0);
            if (partialUnclassified.Count > 0) lines.Add("        -> " + string.Join(", ", partialUnclassified));
            Check("the wheel-specific set is still a subset of Portable",
                BackupProjection.FfbWheelSpecific.All(k => BackupProjection.Portable.Contains(k)));

            lines.Add(ok ? "ALL PASS" : "FAILURES PRESENT");
            return (ok, lines);
        }

        private static void WriteFile(string root, string rel, string text)
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
