// Built-in preset library facade. The presets themselves are data now: plain
// JSON files in a folder (see BuiltinPresetStore), not C# string consts. This
// type just resolves which folder to load and exposes the loaded set under the
// same API the rest of the plugin already uses (BuiltinPresetJsons /
// GameDefaultBindings / IsBuiltin), so callers didn't change when the storage
// moved out of code.
//
// Folder resolution:
//   1. Settings.BuiltinPresetsFolder, if set and it exists (lets a user point
//      at a moved folder or a shared "preset pack").
//   2. Otherwise the shipped default next to the plugin DLL: <dll>\TrueforceForAll-Presets.
//
// The plugin calls Initialize(...) early in Init (after settings load) so the
// folder override is honored. Anything that touches the API before that
// triggers a lazy load from the default folder.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TrueforceForAll.Plugin
{
    internal static class BuiltinPresets
    {
        /// <summary>Root folder under SimHub's PluginsData/Common that holds
        /// every Trueforce For All file (factory built-ins, user content,
        /// drop-in inbox). One on-disk root, two role subfolders.</summary>
        public const string RootFolderName     = "TrueforceForAll";
        /// <summary>Subfolder under the root that holds the factory built-ins
        /// shipped by the installer.</summary>
        public const string FactorySubfolderName = "factory";

        private static BuiltinPresetStore _store;

        private static BuiltinPresetStore Store
        {
            get
            {
                if (_store == null) _store = BuiltinPresetStore.LoadFromFolder(DefaultFolder);
                return _store;
            }
        }

        /// <summary>Factory built-in folder: SimHub's PluginsData/Common is
        /// user-writable (works for DEV authoring without admin) but the
        /// installer drops the factory set there at install time. Same
        /// subfolder layout as the user folder.</summary>
        public static string DefaultFolder
        {
            get
            {
                return Path.Combine(TfPaths.CommonRoot, RootFolderName, FactorySubfolderName);
            }
        }

        /// <summary>The folder actually loaded (after Initialize). Surfaced so
        /// the UI can show / open / repair it.</summary>
        public static string CurrentFolder => Store.FolderPath;

        public static bool Loaded => Store.Loaded;

        /// <summary>Resolve and load the built-in folder. <paramref
        /// name="folderOverride"/> wins when non-empty and present, else the
        /// shipped default is used. Idempotent; call again after the user
        /// changes the folder.</summary>
        public static void Initialize(string folderOverride)
        {
            string folder = (!string.IsNullOrWhiteSpace(folderOverride) && Directory.Exists(folderOverride))
                ? folderOverride
                : DefaultFolder;
            _store = BuiltinPresetStore.LoadFromFolder(folder);
        }

        /// <summary>Reload the current folder from disk (after an export /
        /// import that changed the files).</summary>
        public static void Reload()
        {
            _store = BuiltinPresetStore.LoadFromFolder(_store?.FolderPath ?? DefaultFolder);
        }

        /// <summary>Per-game default preset name, bound automatically as the
        /// game's default if no user-chosen default exists yet. SimHub
        /// GameName -> built-in preset name.</summary>
        public static IReadOnlyDictionary<string, string> GameDefaultBindings => Store.GameDefaults;

        /// <summary>Built-in preset name -> serialized GameSettingsSnapshot
        /// JSON, deserialized the same way as user presets.</summary>
        public static IReadOnlyDictionary<string, string> BuiltinPresetJsons => Store.PresetJsons;

        /// <summary>Car preset key -> serialized CarPresetFile JSON. Fed to
        /// CarPresetStore.InstallOrUpdateBuiltinCarPresets. See BuiltinCarPresets.</summary>
        public static IReadOnlyDictionary<string, string> CarPresetJsons => Store.CarPresetJsons;

        /// <summary>carId -> built-in default preset name (car-defaults.json).
        /// Seeds Settings.CarDefaults when the user hasn't chosen one.</summary>
        public static IReadOnlyDictionary<string, string> CarDefaultBindings => Store.CarDefaults;

        public static bool IsBuiltin(string presetName)
            => !string.IsNullOrEmpty(presetName) && Store.PresetJsons.ContainsKey(presetName);

        /// <summary>Relabel the structural " (default)" suffix to
        /// " (built-in)" for display. Built-ins are STORED with " (default)"
        /// (the marker IsBuiltin, refresh-on-load and export stripping key
        /// off), but every UI surface shows " (built-in)" so the word
        /// "default" only ever means the per-game auto-load binding. Display
        /// only; never feed the result back into preset lookups.</summary>
        public static string ToDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string oldSuffix = " (default)";
            const string newSuffix = " (built-in)";
            return name.EndsWith(oldSuffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - oldSuffix.Length) + newSuffix
                : name;
        }

        /// <summary>Validate the loaded built-ins (parse + missing-section
        /// check) against the given top-level section names. For the dev panel.</summary>
        public static List<string> Validate(IEnumerable<string> expectedSections)
            => Store.Validate(expectedSections);
    }
}
