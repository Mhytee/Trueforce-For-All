// Pre-beta safety net, stable-build half. When this (stable) install takes
// its first step onto the beta channel through the in-app updater, we
// snapshot the user's presets and settings BEFORE the beta build ever runs
// (beta migrations are one-way; this stable build could not read the
// migrated files back). The beta build's switch-back flow owns the restore
// side; this build only ever produces the snapshot.
//
// The plugin's binaries are deliberately NOT part of the snapshot: the beta
// build's switch-back offer downloads the newest stable installer from
// GitHub, so only the data needs a local copy.
//
// On-disk layout, shared with the beta builds ("common-v1" in the manifest):
//   <SimHub>\PluginsData\Common\TrueforceForAll-PreBetaBackup\
//     manifest.json                          fromVersion + UTC stamp + layout
//     TrueforcePlugin.GeneralSettings.json   settings snapshot
//     data\...                               plugin-owned folders, Common-relative
// data\ holds Common-relative copies, so one backup format serves every
// plugin generation regardless of its disk layout. The 0.2.x root's factory
// subfolder is installer-owned and skipped (absent on this generation, but
// the skip keeps the copier correct if it ever appears).

using System;
using System.IO;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    internal sealed class PreBetaBackupManifest
    {
        public string   FromVersion { get; set; }
        public DateTime TakenUtc    { get; set; }
        // Backup format marker; see the header comment. "common-v1" = data\
        // mirrors paths relative to PluginsData\Common.
        public string   Layout      { get; set; }
    }

    internal static class PreBetaBackup
    {
        internal const string FolderName   = "TrueforceForAll-PreBetaBackup";
        internal const string CommonLayout = "common-v1";
        private  const string ManifestName  = "manifest.json";
        private  const string DataSubfolder = "data";

        private static string CommonRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "PluginsData", "Common");
        private static string SettingsFile =>
            Path.Combine(CommonRoot, "TrueforcePlugin.GeneralSettings.json");
        private static string Root => Path.Combine(CommonRoot, FolderName);

        // Every data folder the plugin owns under PluginsData\Common, across
        // generations, so the snapshot covers whichever layout is live.
        private static readonly string[] OwnedDataFolders =
        {
            "TrueforceForAll",           // 0.2.x root (not present on this generation)
            "TrueforceForAll-Library",   // car-preset library
            "TrueforceCars",             // pre-rebrand legacy car presets
        };

        private static void Log(string msg)
        {
            try { SimHub.Logging.Current.Info("[Trueforce] " + msg); } catch { }
        }

        /// <summary>Take (replace) the snapshot. Builds into a temp sibling and
        /// swaps it in only once complete, so a failure mid-copy never destroys
        /// an older still-valid backup and never leaves a readable half-backup
        /// (the manifest is written last, and the beta build's reader keys off
        /// it). Returns false on any failure; the caller decides whether to
        /// proceed with the update.</summary>
        internal static bool TryTake(string fromVersion)
        {
            string tmp = Root + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(tmp);
                if (File.Exists(SettingsFile))
                {
                    string snap = Path.Combine(tmp, Path.GetFileName(SettingsFile));
                    File.Copy(SettingsFile, snap, true);
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
                foreach (var name in OwnedDataFolders)
                {
                    string live = Path.Combine(CommonRoot, name);
                    if (!Directory.Exists(live)) continue;
                    CopyTree(live, Path.Combine(tmp, DataSubfolder, name),
                             skipTopLevel: name == "TrueforceForAll" ? "factory" : null);
                }
                var manifest = new PreBetaBackupManifest
                {
                    FromVersion = fromVersion,
                    TakenUtc    = DateTime.UtcNow,
                    Layout      = CommonLayout,
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
    }
}
