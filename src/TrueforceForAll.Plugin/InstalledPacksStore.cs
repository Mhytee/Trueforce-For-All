// Sidecar registry of community packs the user has imported.
//
// Lives at the USER library root as installed-packs.json (alongside
// game-defaults.json / car-defaults.json, NOT inside games/ or cars/). One
// record per ImportPack call, listing the pack's identity (PackName, Author,
// AuthorVersion, Description, ImportedAt) and the actual on-disk names that
// import wrote (game preset names and car carId+presetName pairs).
//
// Purpose: preserve pack identity that the on-disk game preset (a bare
// GameSettingsSnapshot) and car files don't otherwise carry, so a later
// "delete pack / filter by pack / set pack as default" feature has something
// to key off, and so the Preset Manager's Source column can attribute a row
// to its pack.
//
// Read side is lazy + tolerant: a missing or malformed file yields an empty
// registry. Write side appends one record and rewrites the whole file via a
// temp-file + rename (same atomic pattern as CarPresetStore.AtomicWriteAllText).

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    /// <summary>One imported pack's identity plus the entries it wrote.</summary>
    public sealed class InstalledPack
    {
        public string PackName      { get; set; }
        public string Author        { get; set; }
        public string AuthorVersion { get; set; }
        public string Description   { get; set; }
        public DateTime ImportedAt  { get; set; }
        public List<InstalledPackEntry> Entries { get; set; } = new List<InstalledPackEntry>();
    }

    /// <summary>One preset written by an import. For a game preset, Kind is
    /// "game" and Name is the actual (unique) preset name written. For a car
    /// preset, Kind is "car" and CarId + PresetName are the actual values
    /// written.</summary>
    public sealed class InstalledPackEntry
    {
        public const string KindGame = "game";
        public const string KindCar  = "car";

        public string Kind       { get; set; }
        public string Name       { get; set; } // game preset name (Kind == "game")
        public string CarId      { get; set; } // car id        (Kind == "car")
        public string PresetName { get; set; } // car preset    (Kind == "car")
    }

    /// <summary>Top-level shape of installed-packs.json.</summary>
    public sealed class InstalledPacksFile
    {
        public const string FileType = "trueforce-installed-packs";
        public string Type    { get; set; } = FileType;
        public int    Version { get; set; } = 1;
        public List<InstalledPack> Packs { get; set; } = new List<InstalledPack>();
    }

    internal sealed class InstalledPacksStore
    {
        public const string FileName = "installed-packs.json";

        // Lazy: the user library folder path can be set/changed after the
        // store is constructed, so look it up on each access via the provider.
        private readonly Func<string> _rootFolderProvider;
        private readonly Action<string> _log;

        // Loaded lazily and cached. Reset to null to force a reload (not
        // currently needed; appends keep the cache in sync).
        private InstalledPacksFile _cache;

        public InstalledPacksStore(Func<string> rootFolderProvider, Action<string> log = null)
        {
            _rootFolderProvider = rootFolderProvider
                ?? throw new ArgumentNullException(nameof(rootFolderProvider));
            _log = log;
        }

        private string FilePath => Path.Combine(_rootFolderProvider() ?? "", FileName);

        /// <summary>Read the registry from disk (cached). A missing or
        /// malformed file yields an empty registry rather than throwing.</summary>
        public InstalledPacksFile Load()
        {
            if (_cache != null) return _cache;
            var file = new InstalledPacksFile();
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var parsed = JsonConvert.DeserializeObject<InstalledPacksFile>(File.ReadAllText(path));
                    if (parsed != null)
                    {
                        if (parsed.Packs == null) parsed.Packs = new List<InstalledPack>();
                        file = parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Reading installed-packs.json failed: {ex.Message}");
            }
            _cache = file;
            return _cache;
        }

        /// <summary>Append one pack record and persist. No-op-safe: a null or
        /// entry-less pack is ignored so we don't write empty records.</summary>
        public void AddPack(InstalledPack pack)
        {
            if (pack == null || pack.Entries == null || pack.Entries.Count == 0) return;
            var file = Load();
            file.Packs.Add(pack);
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicWriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Writing installed-packs.json failed: {ex.Message}");
            }
        }

        /// <summary>Find the pack that contains a game preset by its written
        /// name. Returns null if no pack claims it. First match wins.</summary>
        public InstalledPack FindPackForGame(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return null;
            foreach (var pack in Load().Packs)
            {
                if (pack?.Entries == null) continue;
                foreach (var e in pack.Entries)
                {
                    if (e != null
                        && string.Equals(e.Kind, InstalledPackEntry.KindGame, StringComparison.Ordinal)
                        && string.Equals(e.Name, presetName, StringComparison.Ordinal))
                        return pack;
                }
            }
            return null;
        }

        /// <summary>Find the pack that contains a car preset by carId +
        /// presetName. Returns null if no pack claims it. First match wins.</summary>
        public InstalledPack FindPackForCar(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return null;
            foreach (var pack in Load().Packs)
            {
                if (pack?.Entries == null) continue;
                foreach (var e in pack.Entries)
                {
                    if (e != null
                        && string.Equals(e.Kind, InstalledPackEntry.KindCar, StringComparison.Ordinal)
                        && string.Equals(e.CarId, carId, StringComparison.Ordinal)
                        && string.Equals(e.PresetName, presetName, StringComparison.Ordinal))
                        return pack;
                }
            }
            return null;
        }

        // Write through a temp file + rename so a crash mid-write doesn't
        // leave a corrupt file. Same pattern as CarPresetStore.AtomicWriteAllText.
        private static void AtomicWriteAllText(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
    }
}
