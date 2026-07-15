// Pre-beta safety net. When a stable install takes its first step onto the
// beta channel through the in-app updater, we snapshot the user's presets and
// settings BEFORE the beta build ever runs (beta migrations are one-way; a
// later stable build may not read beta-format files). If the user then takes
// the switch-back offer to return to the main release, the update modal
// offers to put this snapshot back, so they land on stable exactly where
// they left it.
//
// The plugin's binaries are deliberately NOT part of the snapshot: the
// switch-back offer already downloads the newest stable installer from
// GitHub, so only the data needs a local copy.
//
// On-disk layout, sibling to the data it mirrors so it survives plugin
// updates and reinstalls:
//   <SimHub>\PluginsData\Common\TrueforceForAll-PreBetaBackup\
//     manifest.json                          fromVersion + UTC timestamp
//     TrueforcePlugin.GeneralSettings.json   settings snapshot
//     data\...                               TrueforceForAll root minus factory
// The factory subfolder is skipped: it is installer-owned and the stable
// installer lays down its own set.

using System;
using System.IO;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    internal sealed class PreBetaBackupManifest
    {
        public string   FromVersion { get; set; }
        public DateTime TakenUtc    { get; set; }
    }

    internal static class PreBetaBackup
    {
        internal const string FolderName = "TrueforceForAll-PreBetaBackup";
        private  const string ManifestName  = "manifest.json";
        private  const string DataSubfolder = "data";

        private static string Root     => Path.Combine(TfPaths.CommonRoot, FolderName);
        private static string DataRoot => Path.Combine(TfPaths.CommonRoot, BuiltinPresets.RootFolderName);

        private static void Log(string msg)
        {
            try { SimHub.Logging.Current.Info("[TF4ALL] " + msg); } catch { }
        }

        /// <summary>Take (replace) the snapshot. Builds into a temp sibling and
        /// swaps it in only once complete, so a failure mid-copy never destroys
        /// an older still-valid backup and never leaves a readable half-backup
        /// (the manifest is written last, and TryRead keys off it). Returns
        /// false on any failure; the caller decides whether to proceed.</summary>
        internal static bool TryTake(string fromVersion)
        {
            string tmp = Root + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(tmp);
                if (File.Exists(TfPaths.GeneralSettingsFile))
                {
                    string snap = Path.Combine(tmp, Path.GetFileName(TfPaths.GeneralSettingsFile));
                    File.Copy(TfPaths.GeneralSettingsFile, snap, true);
                    // Normalize the snapshot to stable-channel state: the
                    // crossing itself flipped the beta opt-in on moments
                    // earlier, and restoring that verbatim would immediately
                    // re-offer the beta the user just left. JObject edit (not
                    // a TrueforceSettings round-trip) so every other field
                    // passes through byte-faithful.
                    try
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(snap));
                        jo["BetaUpdatesEnabled"]      = false;
                        jo["BetaAutoEnrolledVersion"] = null;
                        File.WriteAllText(snap, jo.ToString(Formatting.Indented));
                    }
                    catch { /* keep the raw copy; worst case the restored install re-offers the beta */ }
                }
                if (Directory.Exists(DataRoot))
                    CopyTree(DataRoot, Path.Combine(tmp, DataSubfolder),
                             skipTopLevel: BuiltinPresets.FactorySubfolderName);
                var manifest = new PreBetaBackupManifest
                {
                    FromVersion = fromVersion,
                    TakenUtc    = DateTime.UtcNow,
                };
                File.WriteAllText(Path.Combine(tmp, ManifestName),
                                  JsonConvert.SerializeObject(manifest, Formatting.Indented));
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
                Directory.Move(tmp, Root);
                Log($"Pre-beta backup taken (v{fromVersion}) at {Root}");
                return true;
            }
            catch (Exception ex)
            {
                Log("Pre-beta backup FAILED: " + ex.Message);
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                return false;
            }
        }

        /// <summary>The manifest of the stored snapshot, or null when no
        /// complete backup exists (missing, unreadable, or half-written).</summary>
        internal static PreBetaBackupManifest TryRead()
        {
            try
            {
                string path = Path.Combine(Root, ManifestName);
                if (!File.Exists(path)) return null;
                var m = JsonConvert.DeserializeObject<PreBetaBackupManifest>(File.ReadAllText(path));
                return string.IsNullOrEmpty(m?.FromVersion) ? null : m;
            }
            catch { return null; }
        }

        /// <summary>True when the snapshot can be restored under the offered
        /// stable release: forward migrations let any same-or-newer stable read
        /// it. Stable never regresses, so this is normally always true; the
        /// check guards a manually copied-in backup from a newer install.</summary>
        internal static bool IsCompatibleWith(string targetVersionTag, PreBetaBackupManifest manifest)
        {
            var from   = ParseVersion3(manifest?.FromVersion);
            var target = ParseVersion3(targetVersionTag);
            return from != null && target != null && target >= from;
        }

        /// <summary>Replace the live presets and settings with the snapshot.
        /// Destructive by design (the user confirmed); the replaced live state
        /// is parked in ONE undo slot (TrueforceForAll-PreRestore.bak, replaced
        /// on the next restore) for manual recovery. Parking moves the live
        /// entries out first, so the snapshot copies into a clean root with no
        /// beta-era leftovers merged in. Factory built-ins are untouched
        /// (installer-owned). Throws on failure; the caller surfaces it and
        /// the undo slot holds whatever was parked before the throw.</summary>
        internal static void Restore()
        {
            string undo = Path.Combine(TfPaths.CommonRoot, "TrueforceForAll-PreRestore.bak");
            try { if (Directory.Exists(undo)) Directory.Delete(undo, true); } catch { }
            Directory.CreateDirectory(undo);

            if (File.Exists(TfPaths.GeneralSettingsFile))
                File.Copy(TfPaths.GeneralSettingsFile,
                          Path.Combine(undo, Path.GetFileName(TfPaths.GeneralSettingsFile)), true);
            if (Directory.Exists(DataRoot))
                MoveTopLevel(DataRoot, Path.Combine(undo, DataSubfolder),
                             skip: BuiltinPresets.FactorySubfolderName);

            string settingsSnapshot = Path.Combine(Root, Path.GetFileName(TfPaths.GeneralSettingsFile));
            if (File.Exists(settingsSnapshot))
                File.Copy(settingsSnapshot, TfPaths.GeneralSettingsFile, true);

            string dataSnapshot = Path.Combine(Root, DataSubfolder);
            if (Directory.Exists(dataSnapshot))
                CopyTree(dataSnapshot, DataRoot, skipTopLevel: null);

            Log($"Pre-beta backup restored; the replaced beta-era data is parked at {undo}");
        }

        // Recursive copy. skipTopLevel names ONE first-level subfolder to leave
        // out (the installer-owned factory set); nested levels copy everything.
        private static void CopyTree(string from, string to, string skipTopLevel)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from))
            {
                string name = Path.GetFileName(dir);
                if (skipTopLevel != null
                    && string.Equals(name, skipTopLevel, StringComparison.OrdinalIgnoreCase)) continue;
                CopyTree(dir, Path.Combine(to, name), null);
            }
        }

        // Move every first-level entry of `from` (except `skip`) into `to`,
        // which the caller just created empty, so no destination collisions.
        private static void MoveTopLevel(string from, string to, string skip)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                string dest = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }
            foreach (var dir in Directory.GetDirectories(from))
            {
                string name = Path.GetFileName(dir);
                if (skip != null
                    && string.Equals(name, skip, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.Move(dir, Path.Combine(to, name));
            }
        }

        // "v0.2.1" / "0.2.1" -> a 3-component Version, or null. Mirrors the
        // updater's 3-part comparison (assembly versions carry a 4th part).
        private static Version ParseVersion3(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            if (!Version.TryParse(text, out var v)) return null;
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }
    }
}
