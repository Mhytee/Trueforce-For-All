// Writes built-in preset files + keeps manifest.json in sync. The read side
// is BuiltinPresetStore; this is the dev-tooling write side (Export-as-built-in
// / Export-all). Kept separate so normal runtime never touches the folder.
//
// All writes target the live built-in folder (BuiltinPresets.CurrentFolder).
// After a batch of writes the caller calls BuiltinPresets.Reload() so the
// in-memory store reflects the new files.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal static class BuiltinPresetWriter
    {
        // Strip characters Windows forbids in file names; keep the rest
        // (spaces / parens are fine and keep the name readable).
        private static string SafeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "preset";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Trim();
        }

        private static JObject LoadOrNewManifest(string folder)
        {
            string path = Path.Combine(folder, BuiltinPresetStore.ManifestFileName);
            if (File.Exists(path))
            {
                try { return JObject.Parse(File.ReadAllText(path)); }
                catch { /* fall through to a fresh manifest */ }
            }
            return new JObject
            {
                ["schema"] = 2,
                ["games"] = new JArray(),
                ["gameDefaults"] = new JObject(),
                ["cars"] = new JArray(),
            };
        }

        private static void SaveManifest(string folder, JObject manifest)
        {
            File.WriteAllText(Path.Combine(folder, BuiltinPresetStore.ManifestFileName),
                manifest.ToString(Formatting.Indented));
        }

        // Upsert a {keyField:keyValue, file:relPath} entry into a manifest array.
        private static void Upsert(JObject manifest, string arrayName, string keyField, string keyValue, string relPath)
        {
            var arr = manifest[arrayName] as JArray;
            if (arr == null) { arr = new JArray(); manifest[arrayName] = arr; }
            var existing = arr.FirstOrDefault(t => (string)t[keyField] == keyValue);
            if (existing != null) { existing["file"] = relPath; }
            else arr.Add(new JObject { [keyField] = keyValue, ["file"] = relPath });
        }

        /// <summary>Write a game preset (GameSettingsSnapshot JSON) into the
        /// folder's games/ subfolder and register it in the manifest. Pass
        /// pretty-printed JSON. Returns the written relative path.</summary>
        public static string WriteGame(string folder, string name, string snapshotJson)
        {
            Directory.CreateDirectory(Path.Combine(folder, "games"));
            string rel = "games/" + SafeFile(name) + ".json";
            File.WriteAllText(Path.Combine(folder, rel), snapshotJson);
            var m = LoadOrNewManifest(folder);
            Upsert(m, "games", "name", name, rel);
            SaveManifest(folder, m);
            return rel;
        }

        /// <summary>Write a car preset (CarPresetFile JSON) into the folder's
        /// cars/ subfolder and register it in the manifest under the given
        /// key. Returns the written relative path.</summary>
        public static string WriteCar(string folder, string key, string carPresetJson)
        {
            Directory.CreateDirectory(Path.Combine(folder, "cars"));
            string rel = "cars/" + SafeFile(key) + ".json";
            File.WriteAllText(Path.Combine(folder, rel), carPresetJson);
            var m = LoadOrNewManifest(folder);
            Upsert(m, "cars", "key", key, rel);
            SaveManifest(folder, m);
            return rel;
        }

        /// <summary>Set a game-default binding (GameName -> built-in preset name)
        /// in the manifest. No-op-safe.</summary>
        public static void SetGameDefault(string folder, string gameName, string presetName)
        {
            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(presetName)) return;
            var m = LoadOrNewManifest(folder);
            var gd = m["gameDefaults"] as JObject;
            if (gd == null) { gd = new JObject(); m["gameDefaults"] = gd; }
            gd[gameName] = presetName;
            SaveManifest(folder, m);
        }
    }
}
