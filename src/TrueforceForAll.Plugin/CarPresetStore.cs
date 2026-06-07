// User car preset file store.
//
// Layout, mirroring the built-in car folder (BuiltinPresets cars/ subfolder):
//   <user library root>/cars/<GameName>/<carId>/<PresetName>.json
// (e.g. PluginsData/Common/TrueforceForAll-Library/cars/AssettoCorsa/ks_audi_r8/Stock setup.json)
//
// Each file holds a CarPresetFile (CarId, PresetName, IsBuiltin=false,
// GameName, Override). Identical on-disk shape to the built-in car files so
// import/export round-trips work without translation.
//
// Built-in car presets live in the built-in folder, not here, and are loaded
// from BuiltinPresets.CarPresetJsons at runtime. This store is the USER side
// only; the runtime merges both sources into the in-memory map.
//
// Filename charset is sanitised (Windows-invalid chars are replaced with '_'),
// so a carId or preset name with a colon or slash still produces a valid path.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    /// <summary>One car preset's worth of data, surfaced to public callers
    /// (UI dropdown, plugin queries). Public because TrueforcePlugin exposes
    /// it from public APIs; the underlying CarPresetStore stays internal.</summary>
    public sealed class CarPresetEntry
    {
        public string CarId       { get; set; }
        public string PresetName  { get; set; }
        public string GameName    { get; set; }
        public bool   IsBuiltin   { get; set; }
        // Optional pack/sharing metadata, carried from the on-disk
        // CarPresetFile when present (set on imported pack files). Left null
        // for built-ins and locally-saved presets. Used by the Preset
        // Manager's Source column to attribute a car preset to its pack
        // and by the export flow to preserve per-row credit when assembling
        // a curator's pack (Description + AuthorVersion round-trip too).
        public string PackName      { get; set; }
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string AuthorVersion { get; set; }
        public CarOverride Override { get; set; }
    }

    internal sealed class CarPresetStore
    {
        public const string CarsSubfolderName = "cars";
        public const string FileExtension     = ".json";

        // JSON deserializer settings capped at depth 64 to prevent unbounded
        // recursion / DoS through a deeply nested malicious preset JSON.
        internal static readonly JsonSerializerSettings SafeJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 64
        };

        // Lazy: the user library folder path can be set/changed after the
        // store is constructed, so look it up on each access via the provider.
        private readonly Func<string> _rootFolderProvider;
        private readonly Action<string> _log;

        public CarPresetStore(Func<string> rootFolderProvider, Action<string> log = null)
        {
            _rootFolderProvider = rootFolderProvider
                ?? throw new ArgumentNullException(nameof(rootFolderProvider));
            _log = log;
        }

        /// <summary>Where car files live: <user library root>/cars.</summary>
        public string FolderPath => Path.Combine(_rootFolderProvider() ?? "", CarsSubfolderName);

        // Strip Windows-invalid chars but keep readability (spaces, parens stay).
        private static string SafeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private string PathFor(string gameName, string carId, string presetName)
        {
            string game = SafeFile(string.IsNullOrEmpty(gameName) ? "Unknown" : gameName);
            return Path.Combine(FolderPath, game, SafeFile(carId), SafeFile(presetName) + FileExtension);
        }

        /// <summary>Resolve the on-disk path for (carId, presetName), walking
        /// the cars/ tree when the game folder isn't known. Returns null if no
        /// matching file exists. Public so the Pack Manager can hash + delete
        /// car entries whose owning game wasn't captured in the pack record.</summary>
        public string FindCarFile(string carId, string presetName)
        {
            string root = FolderPath;
            if (!Directory.Exists(root)) return null;
            string carDir = SafeFile(carId);
            string fileName = SafeFile(presetName) + FileExtension;
            foreach (var gameDir in Directory.GetDirectories(root))
            {
                string candidate = Path.Combine(gameDir, carDir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Walks the folder tree and returns every user car preset
        /// indexed by carId then presetName. Skips malformed files with a log
        /// line. Built-in cars live in the built-in folder, not here; callers
        /// merge them in from BuiltinPresets.CarPresetJsons.</summary>
        public Dictionary<string, Dictionary<string, CarPresetEntry>> LoadAll()
        {
            var result = new Dictionary<string, Dictionary<string, CarPresetEntry>>(StringComparer.Ordinal);
            try
            {
                string root = FolderPath;
                if (!Directory.Exists(root)) return result;
                foreach (var path in Directory.GetFiles(root, "*" + FileExtension, SearchOption.AllDirectories))
                {
                    try
                    {
                        var f = JsonConvert.DeserializeObject<CarPresetFile>(File.ReadAllText(path), SafeJsonSettings);
                        if (f == null || string.IsNullOrEmpty(f.CarId) || f.Override == null) continue;

                        string presetName = string.IsNullOrEmpty(f.PresetName) ? f.CarId : f.PresetName;
                        var entry = new CarPresetEntry
                        {
                            CarId         = f.CarId,
                            PresetName    = presetName,
                            GameName      = f.GameName ?? "",
                            IsBuiltin     = false, // user-library files are user; built-ins come from the built-in folder
                            PackName      = f.PackName,
                            Author        = f.Author,
                            Description   = f.Description,
                            AuthorVersion = f.AuthorVersion,
                            Override      = f.Override,
                        };
                        if (!result.TryGetValue(f.CarId, out var perCar))
                        {
                            perCar = new Dictionary<string, CarPresetEntry>(StringComparer.Ordinal);
                            result[f.CarId] = perCar;
                        }
                        perCar[presetName] = entry;
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"[Trueforce] Skipping malformed car preset '{Path.GetFileName(path)}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] LoadAll car presets failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>Writes a car preset file for (carId, presetName). Empty
        /// overrides are deleted instead of written. Throws nothing; logs on
        /// I/O failure. <paramref name="isBuiltin"/> is accepted for API
        /// compat but ignored: user-library writes are always non-builtin.
        /// The optional pack/sharing metadata (packName, author, description,
        /// authorVersion) is persisted when provided; non-import callers leave
        /// it null and the fields stay blank (no behavior change for them).
        ///
        /// <paramref name="defaultAuthor"/> is the persistent author the
        /// caller would like stamped on a new save when no per-call author
        /// was supplied. The preserve-rule: per-field, an explicit non-null
        /// argument wins; otherwise the existing on-disk file's value is
        /// preserved; otherwise the defaultAuthor seeds Author only. This
        /// makes a user re-saving a pack-imported preset keep the pack
        /// attribution AND a user saving fresh stamp their own author, with
        /// no per-call-site lookup churn.</summary>
        public void Save(string carId, string presetName, string gameName, CarOverride ovr, bool isBuiltin = false,
            string packName = null, string author = null, string description = null, string authorVersion = null,
            string defaultAuthor = null)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return;
            if (ovr == null || ovr.IsEmpty)
            {
                Delete(carId, presetName);
                return;
            }
            try
            {
                var path = PathFor(gameName, carId, presetName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // Preserve existing-on-disk attribution when the caller
                // didn't pass a value for that specific field. Read the
                // current file once if it exists; if it doesn't (fresh
                // save), the defaultAuthor seeds Author.
                string existingPackName = null, existingAuthor = null, existingDesc = null, existingVer = null;
                if (File.Exists(path))
                {
                    try
                    {
                        var prev = JsonConvert.DeserializeObject<CarPresetFile>(File.ReadAllText(path), SafeJsonSettings);
                        if (prev != null)
                        {
                            existingPackName = NullIfBlank(prev.PackName);
                            existingAuthor   = NullIfBlank(prev.Author);
                            existingDesc     = NullIfBlank(prev.Description);
                            existingVer      = NullIfBlank(prev.AuthorVersion);
                        }
                    }
                    catch { /* malformed prior file; treat as no prior attribution */ }
                }

                string finalAuthor =
                    author != null   ? author
                    : existingAuthor != null ? existingAuthor
                    : NullIfBlank(defaultAuthor);

                var f = new CarPresetFile
                {
                    GameName      = gameName ?? "",
                    CarId         = carId,
                    PresetName    = presetName,
                    IsBuiltin     = false,
                    PackName      = packName      ?? existingPackName,
                    Author        = finalAuthor,
                    Description   = description   ?? existingDesc,
                    AuthorVersion = authorVersion ?? existingVer,
                    Override      = ovr,
                };
                AtomicWriteAllText(path, JsonConvert.SerializeObject(f, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Save car preset '{carId}/{presetName}' failed: {ex.Message}");
            }
        }

        /// <summary>Deletes a car preset file. No-op if it doesn't exist.
        /// Walks the tree to find the file when game isn't passed.</summary>
        public void Delete(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return;
            try
            {
                string path = FindCarFile(carId, presetName);
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                    // Best-effort prune of the now-empty car dir.
                    var carDir = Path.GetDirectoryName(path);
                    try
                    {
                        if (Directory.Exists(carDir)
                            && Directory.GetFileSystemEntries(carDir).Length == 0)
                            Directory.Delete(carDir);
                    }
                    catch { /* leave the dir if it won't delete */ }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Delete car preset '{carId}/{presetName}' failed: {ex.Message}");
            }
        }

        /// <summary>True iff a file already exists for (carId, presetName) in
        /// some game subfolder.</summary>
        public bool Exists(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return false;
            return FindCarFile(carId, presetName) != null;
        }

        // Write through a temp file + rename so a crash mid-write doesn't
        // leave a corrupt file. Same pattern as the rest of the persistence layer.
        private static void AtomicWriteAllText(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
    }
}
