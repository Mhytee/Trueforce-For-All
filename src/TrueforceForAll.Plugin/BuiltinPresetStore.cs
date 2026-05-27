// File-based built-in preset store. Built-in presets ship as plain JSON files
// in a folder (default: <plugin dll dir>\TrueforceForAll-Presets) and are
// discovered by directory scan, so dropping a file in makes it a built-in with
// no manifest to maintain. This replaces the old C# string consts: presets are
// data now, exportable / importable / reseedable / repairable without a
// recompile.
//
// Folder layout:
//   game-defaults.json            - { "<GameName>": "<game preset name>", ... }
//   games/<Preset Name>.json      - one GameSettingsSnapshot per game built-in
//                                    (name comes from the filename; a preset can
//                                    be the default for several games, so games
//                                    stay flat, not per-game).
//   cars/<GameName>/<carId>.json  - one CarPresetFile per car built-in, grouped
//                                    by the game the car belongs to.
//
// A folder pointed at a simple flat pack (game *.json at the root) still loads:
// root-level *.json are read as game presets too.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class BuiltinPresetStore
    {
        // Game preset display name -> raw JSON text (a GameSettingsSnapshot).
        public Dictionary<string, string> PresetJsons { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // SimHub GameName -> built-in preset name to auto-bind as that game's default.
        public Dictionary<string, string> GameDefaults { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Car preset key ("<carId>/<presetName>", informational) -> raw JSON
        // text (a CarPresetFile). Fed verbatim into CarPresetStore.InstallOrUpdate.
        // Multiple presets per car are supported (distinct keys).
        public Dictionary<string, string> CarPresetJsons { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // carId -> built-in default preset name (from car-defaults.json).
        // Seeds Settings.CarDefaults when the user hasn't chosen one.
        public Dictionary<string, string> CarDefaults { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public string FolderPath { get; private set; }
        public bool Loaded => PresetJsons.Count > 0 || CarPresetJsons.Count > 0;

        public const string GameDefaultsFileName = "game-defaults.json";
        public const string CarDefaultsFileName  = "car-defaults.json";

        /// <summary>Load every built-in from <paramref name="folder"/> by
        /// directory scan. Never throws: a missing folder or unreadable file
        /// just yields an emptier store and a logged warning.</summary>
        public static BuiltinPresetStore LoadFromFolder(string folder)
        {
            var store = new BuiltinPresetStore { FolderPath = folder };
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Built-in preset folder not found: '{folder}'. No built-ins loaded (use Repair / set the folder).");
                    return store;
                }

                store.LoadGameDefaults(folder);
                store.LoadGamePresets(folder);
                store.LoadCarPresets(folder);
                store.LoadCarDefaults(folder);

                SimHub.Logging.Current.Info($"[Trueforce] Loaded {store.PresetJsons.Count} game + {store.CarPresetJsons.Count} car built-in preset(s) from '{folder}'.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Failed loading built-in presets from '{folder}': {ex.Message}");
            }
            return store;
        }

        private void LoadGameDefaults(string folder)
        {
            string path = Path.Combine(folder, GameDefaultsFileName);
            if (!File.Exists(path)) return;
            try
            {
                var o = JObject.Parse(File.ReadAllText(path));
                foreach (var kv in o)
                    if (kv.Value != null) GameDefaults[kv.Key] = (string)kv.Value;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Bad {GameDefaultsFileName}: {ex.Message}");
            }
        }

        // Game presets: games/*.json (name = filename). Also accepts root-level
        // *.json so a plain flat pack works when the folder is pointed there.
        private void LoadGamePresets(string folder)
        {
            foreach (var dir in new[] { Path.Combine(folder, "games"), folder })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var path in Directory.GetFiles(dir, "*.json"))
                {
                    string file = Path.GetFileName(path);
                    if (string.Equals(file, GameDefaultsFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrEmpty(name) && !PresetJsons.ContainsKey(name))
                        PresetJsons[name] = File.ReadAllText(path);
                }
            }
        }

        // Car presets: cars/<GameName>/<carId>/<PresetName>.json (recursive).
        // Multiple presets per car. carId/presetName come from the file content
        // (authoritative), so the key is robust to folder/file naming.
        private void LoadCarPresets(string folder)
        {
            string carsRoot = Path.Combine(folder, "cars");
            if (!Directory.Exists(carsRoot)) return;
            foreach (var path in Directory.GetFiles(carsRoot, "*.json", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path);
                string carId, preset;
                try
                {
                    var o = JObject.Parse(text);
                    carId  = (string)o["CarId"];
                    preset = (string)o["PresetName"];
                }
                catch { continue; }   // skip a malformed car file
                if (string.IsNullOrEmpty(carId)) continue;
                string key = carId + "/" + (preset ?? Path.GetFileNameWithoutExtension(path));
                CarPresetJsons[key] = text;
            }
        }

        private void LoadCarDefaults(string folder)
        {
            string path = Path.Combine(folder, CarDefaultsFileName);
            if (!File.Exists(path)) return;
            try
            {
                var o = JObject.Parse(File.ReadAllText(path));
                foreach (var kv in o)
                    if (kv.Value != null) CarDefaults[kv.Key] = (string)kv.Value;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Bad {CarDefaultsFileName}: {ex.Message}");
            }
        }

        /// <summary>Round-trip each game built-in JSON to flag problems: ones
        /// that fail to parse, and ones missing a top-level effect section the
        /// current snapshot shape defines. Returns lines for the dev panel.</summary>
        public List<string> Validate(IEnumerable<string> expectedSections)
        {
            var lines = new List<string>();
            var expected = expectedSections?.ToList() ?? new List<string>();
            foreach (var kv in PresetJsons.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var o = JObject.Parse(kv.Value);
                    var missing = expected.Where(s => o[s] == null).ToList();
                    lines.Add(missing.Count == 0
                        ? $"OK    {kv.Key}"
                        : $"STALE {kv.Key} - missing: {string.Join(", ", missing)}");
                }
                catch (Exception ex)
                {
                    lines.Add($"FAIL  {kv.Key} - parse error: {ex.Message}");
                }
            }
            return lines;
        }
    }
}
