// User preset library facade. The user's own (non-builtin) presets live as
// data files in the same folder shape as the built-in folder, just under a
// different, user-writable location:
//   <SimHub>\PluginsData\Common\TrueforceForAll-Library\
//     game-defaults.json
//     games/<Preset Name>.json
//     car-defaults.json
//     cars/<GameName>/<carId>/<PresetName>.json
//
// The runtime Settings.Presets / GameDefaults / CarDefaults dicts are rebuilt
// from `built-in folder + user library` on every Init: user files first, then
// built-ins overwrite by name (so a built-in always wins a name collision).
// The dicts inside GeneralSettings.json become a legacy carry-over; a one-time
// migration writes their contents into this folder on first launch after the
// upgrade and clears them.
//
// Backed by the same BuiltinPresetStore loader as built-ins, so the loading
// semantics (directory scan, JObject content parsing for cars, etc.) match
// exactly. BuiltinPresetWriter handles writes.

using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TrueforceForAll.Plugin
{
    internal static class UserPresets
    {
        /// <summary>Subfolder under the shared TrueforceForAll root that
        /// holds the user's own content (presets, defaults, drop-in inbox).</summary>
        public const string UserSubfolderName = "user";

        private static BuiltinPresetStore _store;

        private static BuiltinPresetStore Store
        {
            get
            {
                if (_store == null) _store = BuiltinPresetStore.LoadFromFolder(DefaultFolder);
                return _store;
            }
        }

        /// <summary>Default user folder, under SimHub's PluginsData/Common
        /// alongside the factory folder (same TrueforceForAll root). Writable
        /// without admin so save / delete / rename always work.</summary>
        public static string DefaultFolder
        {
            get
            {
                string baseDir = System.AppDomain.CurrentDomain.BaseDirectory ?? "";
                return Path.Combine(baseDir, "PluginsData", "Common",
                    BuiltinPresets.RootFolderName, UserSubfolderName);
            }
        }

        /// <summary>The folder actually loaded (after Initialize).</summary>
        public static string CurrentFolder => Store.FolderPath;

        public static bool Loaded => Store.Loaded;

        /// <summary>Resolve and load the user-library folder. Override wins
        /// when non-empty and present; else the default under PluginsData/Common.
        /// Idempotent; call again after the user changes the folder.</summary>
        public static void Initialize(string folderOverride)
        {
            string folder = (!string.IsNullOrWhiteSpace(folderOverride) && Directory.Exists(folderOverride))
                ? folderOverride
                : DefaultFolder;
            // Ensure it exists on first run so writes succeed without ceremony.
            try { Directory.CreateDirectory(folder); } catch { /* best-effort */ }
            _store = BuiltinPresetStore.LoadFromFolder(folder);
        }

        /// <summary>Reload after a write that changed the files.</summary>
        public static void Reload()
        {
            _store = BuiltinPresetStore.LoadFromFolder(_store?.FolderPath ?? DefaultFolder);
        }

        /// <summary>User game presets: display name -> raw GameSettingsSnapshot JSON.</summary>
        public static IReadOnlyDictionary<string, string> PresetJsons => Store.PresetJsons;

        /// <summary>User-set per-game default preset name (game-defaults.json).</summary>
        public static IReadOnlyDictionary<string, string> GameDefaults => Store.GameDefaults;

        /// <summary>User car presets: "<carId>/<presetName>" -> raw CarPresetFile JSON.</summary>
        public static IReadOnlyDictionary<string, string> CarPresetJsons => Store.CarPresetJsons;

        /// <summary>User-set per-car default preset name (car-defaults.json).</summary>
        public static IReadOnlyDictionary<string, string> CarDefaults => Store.CarDefaults;

        /// <summary>True if the named preset exists as a user file.</summary>
        public static bool HasGamePreset(string name)
            => !string.IsNullOrEmpty(name) && Store.PresetJsons.ContainsKey(name);
    }
}
