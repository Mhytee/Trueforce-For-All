// File-based built-in preset store. Built-in presets ship as plain JSON
// files in a folder (default: <plugin dll dir>\TrueforceBuiltins) plus a
// manifest.json describing the set and the per-game default bindings. This
// replaces the old C# string consts: presets are now data, not code, so they
// can be exported / imported / reseeded / repaired without a recompile.
//
// Folder layout:
//   manifest.json                 - { schema, presets:[{name,file}], gameDefaults:{game:name} }
//   <Preset Name>.json            - one GameSettingsSnapshot per built-in
//
// A folder without a manifest is still usable as a "preset pack": every
// *.json (other than manifest.json) loads as a preset named by its file, with
// no game-default bindings. Lets a user point the plugin at a shared pack.

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

        // The folder this store was loaded from (may not exist, in which case
        // it loaded nothing). Surfaced so the UI can show / open / repair it.
        public string FolderPath { get; private set; }

        public bool HasManifest { get; private set; }
        public bool Loaded => PresetJsons.Count > 0;

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

                SimHub.Logging.Current.Info($"[Trueforce] Loaded {store.PresetJsons.Count} built-in preset(s) from '{folder}' (manifest: {store.HasManifest}).");
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

            var presets = root["presets"] as JArray;
            if (presets != null)
            {
                foreach (var p in presets)
                {
                    string name = (string)p["name"];
                    string file = (string)p["file"];
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file)) continue;
                    string path = Path.Combine(folder, file);
                    if (!File.Exists(path))
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Built-in '{name}' references missing file '{file}'.");
                        continue;
                    }
                    PresetJsons[name] = File.ReadAllText(path);
                }
            }

            var defaults = root["gameDefaults"] as JObject;
            if (defaults != null)
                foreach (var kv in defaults)
                    if (kv.Value != null) GameDefaults[kv.Key] = (string)kv.Value;
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
