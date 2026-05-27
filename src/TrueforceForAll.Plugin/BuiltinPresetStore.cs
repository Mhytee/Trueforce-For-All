// File-based built-in preset store. Built-in presets ship as plain JSON
// files in a folder (default: <plugin dll dir>\TrueforceForAll-Presets) plus a
// manifest.json describing the set and the per-game default bindings. This
// replaces the old C# string consts: presets are now data, not code, so they
// can be exported / imported / reseeded / repaired without a recompile.
//
// Folder layout (manifest schema 2):
//   manifest.json   - { schema:2,
//                       games:[{name,file}],          // game presets (GameSettingsSnapshot)
//                       gameDefaults:{game:name},      // auto-bind map
//                       cars:[{key,file}] }            // car presets (CarPresetFile)
//   games/<Preset Name>.json
//   cars/<carKey>.json
//
// Schema 1 (games-only, flat) is still read: a "presets" array maps the same
// as "games". A folder without a manifest is usable as a simple game "preset
// pack": every root-level *.json loads as a game preset named by its file,
// with no game-default bindings or cars. Lets a user point at a shared pack.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class BuiltinPresetStore
    {
        // Preset display name -> raw JSON text (a GameSettingsSnapshot).
        public Dictionary<string, string> PresetJsons { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // SimHub GameName -> built-in preset name to auto-bind as that game's default.
        public Dictionary<string, string> GameDefaults { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Car preset key (informational) -> raw JSON text (a CarPresetFile).
        // Fed verbatim into CarPresetStore.InstallOrUpdateBuiltinCarPresets.
        public Dictionary<string, string> CarPresetJsons { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // The folder this store was loaded from (may not exist, in which case
        // it loaded nothing). Surfaced so the UI can show / open / repair it.
        public string FolderPath { get; private set; }

        public bool HasManifest { get; private set; }
        public bool Loaded => PresetJsons.Count > 0 || CarPresetJsons.Count > 0;

        public const string ManifestFileName = "manifest.json";

        /// <summary>Load every built-in from <paramref name="folder"/>. Never
        /// throws: a missing folder or unreadable file just yields an emptier
        /// store and a logged warning, so a bad install degrades instead of
        /// crashing the plugin. Callers check <see cref="Loaded"/>.</summary>
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

                string manifestPath = Path.Combine(folder, ManifestFileName);
                if (File.Exists(manifestPath))
                {
                    store.HasManifest = true;
                    store.LoadViaManifest(manifestPath, folder);
                }
                else
                {
                    store.LoadLooseJson(folder);
                }

                SimHub.Logging.Current.Info($"[Trueforce] Loaded {store.PresetJsons.Count} game + {store.CarPresetJsons.Count} car built-in preset(s) from '{folder}' (manifest: {store.HasManifest}).");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Failed loading built-in presets from '{folder}': {ex.Message}");
            }
            return store;
        }

        private void LoadViaManifest(string manifestPath, string folder)
        {
            var root = JObject.Parse(File.ReadAllText(manifestPath));

            // Game presets: "games" (schema 2) or "presets" (schema 1).
            var games = (root["games"] as JArray) ?? (root["presets"] as JArray);
            if (games != null)
            {
                foreach (var p in games)
                {
                    string name = (string)p["name"];
                    string file = (string)p["file"];
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file)) continue;
                    string path = Path.Combine(folder, file);
                    if (!File.Exists(path))
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Built-in game preset '{name}' references missing file '{file}'.");
                        continue;
                    }
                    PresetJsons[name] = File.ReadAllText(path);
                }
            }

            var defaults = root["gameDefaults"] as JObject;
            if (defaults != null)
                foreach (var kv in defaults)
                    if (kv.Value != null) GameDefaults[kv.Key] = (string)kv.Value;

            // Car presets (schema 2). Keyed for log readability; the
            // authoritative carId/name live inside each file.
            var cars = root["cars"] as JArray;
            if (cars != null)
            {
                foreach (var c in cars)
                {
                    string file = (string)c["file"];
                    if (string.IsNullOrEmpty(file)) continue;
                    string key = (string)c["key"] ?? Path.GetFileNameWithoutExtension(file);
                    string path = Path.Combine(folder, file);
                    if (!File.Exists(path))
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Built-in car preset '{key}' references missing file '{file}'.");
                        continue;
                    }
                    CarPresetJsons[key] = File.ReadAllText(path);
                }
            }
        }

        // Manifest-less folder: treat each *.json (except the manifest) as a
        // preset named by its filename. No game-default bindings.
        private void LoadLooseJson(string folder)
        {
            foreach (var path in Directory.GetFiles(folder, "*.json"))
            {
                string file = Path.GetFileName(path);
                if (string.Equals(file, ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name)) continue;
                PresetJsons[name] = File.ReadAllText(path);
            }
        }

        /// <summary>Round-trip each built-in JSON to flag problems: ones that
        /// fail to parse, and ones missing a top-level effect section that the
        /// current snapshot shape defines (e.g. a preset shipped before an
        /// effect was added). Returns human-readable lines for the dev panel.</summary>
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
