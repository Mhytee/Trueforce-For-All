// Writes built-in preset files into the folder (directory-scan layout; no
// manifest to keep in sync). The read side is BuiltinPresetStore. This is the
// dev-tooling / DEV-mode write side so normal runtime never touches the folder.
//
// After a batch of writes the caller calls BuiltinPresets.Reload() so the
// in-memory store reflects the new files.

using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal static class BuiltinPresetWriter
    {
        // Strip characters Windows forbids in file names; keep the rest
        // (spaces / parens stay, so names remain readable).
        private static string SafeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "preset";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Trim();
        }

        /// <summary>Write a game preset (GameSettingsSnapshot JSON) to
        /// games/&lt;name&gt;.json. The preset name is taken from the filename
        /// on load, so it round-trips. Returns the relative path.</summary>
        public static string WriteGame(string folder, string name, string snapshotJson)
        {
            Directory.CreateDirectory(Path.Combine(folder, "games"));
            string rel = "games/" + SafeFile(name) + ".json";
            File.WriteAllText(Path.Combine(folder, rel), snapshotJson);
            return rel;
        }

        /// <summary>Write a car preset (CarPresetFile JSON) to
        /// cars/&lt;GameName&gt;/&lt;carId&gt;.json. Returns the relative path.</summary>
        public static string WriteCar(string folder, string gameName, string carId, string carPresetJson)
        {
            string dir = Path.Combine(folder, "cars", SafeFile(string.IsNullOrEmpty(gameName) ? "Unknown" : gameName));
            Directory.CreateDirectory(dir);
            string rel = "cars/" + SafeFile(string.IsNullOrEmpty(gameName) ? "Unknown" : gameName) + "/" + SafeFile(carId) + ".json";
            File.WriteAllText(Path.Combine(folder, rel), carPresetJson);
            return rel;
        }

        /// <summary>Set a game-default binding (GameName -> built-in preset
        /// name) in game-defaults.json. No-op-safe.</summary>
        public static void SetGameDefault(string folder, string gameName, string presetName)
        {
            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(presetName)) return;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, BuiltinPresetStore.GameDefaultsFileName);
            JObject o;
            try { o = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject(); }
            catch { o = new JObject(); }
            o[gameName] = presetName;
            File.WriteAllText(path, o.ToString(Formatting.Indented));
        }
    }
}
