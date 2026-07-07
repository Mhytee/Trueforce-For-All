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
using System.Security.Cryptography;
using System.Text;
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
        // Server uuid when this pack came from the Community browser (null for
        // a .tfpack imported from disk). Keys AddOrMergePack so a repeat or
        // partial community download folds into the existing record instead of
        // spawning a duplicate row. Nullable so older records keep loading.
        public string CommunitySourceId { get; set; }
        public List<InstalledPackEntry> Entries { get; set; } = new List<InstalledPackEntry>();
    }

    /// <summary>One preset written by an import. For a game preset, Kind is
    /// "game" and Name is the actual (unique) preset name written. For a car
    /// preset, Kind is "car" and CarId + PresetName are the actual values
    /// written. GameName is the car preset's owning game folder (cars are stored
    /// under cars/&lt;game&gt;/&lt;carId&gt;/), captured so removal can find the
    /// file without enumerating. BaselineHash is the hash of the JSON written at
    /// install time; removal compares it to the file's current hash to decide
    /// whether the user edited the entry (different hash → keep, same hash →
    /// safe to delete). DefaultForGames is the optional list of game keys the
    /// pack intends this preset to be the default for; populated from the pack
    /// manifest when present and consumed by "Set pack as defaults". All three
    /// new fields are nullable so older installed-packs.json records (written
    /// before this work) keep loading; missing BaselineHash is treated as
    /// "potentially edited" so removal won't blow away data we don't recognize.</summary>
    public sealed class InstalledPackEntry
    {
        public const string KindGame   = "game";
        public const string KindCar    = "car";
        public const string KindEngine = "engine";

        public string Kind         { get; set; }
        public string Name         { get; set; } // game preset name (Kind == "game")
        public string CarId        { get; set; } // car id        (Kind == "car")
        public string PresetName   { get; set; } // car preset    (Kind == "car")
        public string GameName     { get; set; } // car preset's owning game folder
        public string EngineId     { get; set; } // custom-engine library Id (Kind == "engine")
        public string BaselineHash { get; set; } // SHA1 of JSON we wrote at install
        public List<string> DefaultForGames { get; set; } // optional set-as-default hint (game keys)
    }

    /// <summary>Result of "Set pack as defaults", aggregated across every
    /// entry in the pack. The Pack Manager renders this as the post-action
    /// toast. Overwritten counts cover the case where an existing default
    /// pointed somewhere else and we replaced it; GamePresetsSkipped covers
    /// the data-model gap (pre-this-work imports + manifests without
    /// DefaultForGames) where we can't tell which game(s) a preset belongs
    /// to.</summary>
    public sealed class SetDefaultsSummary
    {
        public int GameDefaultsSet         { get; set; }
        public int CarDefaultsSet          { get; set; }
        public int GameDefaultsOverwritten { get; set; }
        public int CarDefaultsOverwritten  { get; set; }
        public int GamePresetsSkipped      { get; set; }
        // Entries left alone because the game/car already had a default and the
        // user chose "Skip existing" instead of overwriting.
        public int GameDefaultsSkippedConflict { get; set; }
        public int CarDefaultsSkippedConflict  { get; set; }
    }

    // How SetPackAsDefaults treats an entry whose game/car already has a default:
    // OverwriteAll replaces it; SkipConflicts keeps the existing default and only
    // fills in the ones the user hasn't bound yet.
    public enum SetDefaultsConflictPolicy { OverwriteAll, SkipConflicts }

    /// <summary>Read-only count of what SetPackAsDefaults would do, so the UI can
    /// warn before overwriting: Fresh = no existing default, Conflict = would replace one.</summary>
    public sealed class SetDefaultsPreview
    {
        public int FreshCount    { get; set; }
        public int ConflictCount { get; set; }
    }

    /// <summary>Result of destructive "Remove pack". Deleted counts the
    /// entries whose on-disk file matched its install-time BaselineHash (so
    /// the user hadn't touched it). Kept covers entries with a hash mismatch
    /// OR no recorded BaselineHash (pre-this-work packs are conservatively
    /// kept so we never destroy data we can't reason about).</summary>
    public sealed class RemovePackSummary
    {
        public int EntriesDeleted { get; set; }
        public int EntriesKept    { get; set; }
    }

    /// <summary>Caller-supplied policy for RemovePack. Defaults are the safe,
    /// non-interactive choice; the inline Packs grid overrides them from its
    /// keep/remove prompts.</summary>
    public sealed class RemovePackOptions
    {
        /// <summary>Delete entries even when the user edited them since
        /// download. Default false (edited entries are preserved).</summary>
        public bool RemoveEditedEntries { get; set; } = false;

        /// <summary>Delete this pack's custom engines even when other installed
        /// packs or presets outside this pack still reference them (the dangling
        /// refs then fall back to Auto). Default false (shared engines are
        /// kept).</summary>
        public bool DeleteSharedEngines { get; set; } = false;
    }

    /// <summary>One custom engine bundled by a pack that is also referenced from
    /// OUTSIDE that pack, so deleting it on pack removal would affect something
    /// else. Drives the "keep or delete shared engines?" prompt.</summary>
    public sealed class SharedEngineRef
    {
        public string EngineId       { get; set; }
        public string EngineName     { get; set; }
        public int    OtherPackCount    { get; set; } // other installed packs listing it
        public int    OutsidePresetRefs { get; set; } // presets/overrides outside this pack using it
    }

    /// <summary>What removing a pack would touch, computed before any deletion
    /// so the UI can prompt. EditedEntryCount counts provably-edited entries
    /// (baseline hash present and current content differs).</summary>
    public sealed class PackRemovalImpact
    {
        public int EditedEntryCount { get; set; }
        public List<SharedEngineRef> SharedEngines { get; set; } = new List<SharedEngineRef>();
        public bool HasSharedEngines => SharedEngines != null && SharedEngines.Count > 0;
    }

    /// <summary>How many places reference a given custom engine, for the
    /// usage-aware delete warning. Counts are distinct presets/overrides.</summary>
    public sealed class EngineUsage
    {
        public int  GamePresetCount { get; set; }
        public int  CarPresetCount  { get; set; }
        public bool GlobalDefault   { get; set; }
        public bool ActiveConfig    { get; set; }
        public int  TotalPresetRefs => GamePresetCount + CarPresetCount + (GlobalDefault ? 1 : 0);
        public bool AnyReference    => TotalPresetRefs > 0 || ActiveConfig;
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

        // JSON deserializer settings capped at depth 64 to prevent unbounded
        // recursion / DoS through a deeply nested malicious installed-packs file.
        internal static readonly JsonSerializerSettings SafeJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 64
        };

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
                    var parsed = JsonConvert.DeserializeObject<InstalledPacksFile>(File.ReadAllText(path), SafeJsonSettings);
                    if (parsed != null)
                    {
                        if (parsed.Packs == null) parsed.Packs = new List<InstalledPack>();
                        file = parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Reading installed-packs.json failed: {ex.Message}");
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
            SaveToDisk(file);
        }

        /// <summary>Add a community-downloaded pack, or fold its entries into an
        /// existing record with the same CommunitySourceId. Used by the Community
        /// browser's pack download so a full re-download (all entries deduped →
        /// empty pack → no-op) or a partial download (only the newly-taken
        /// entries) doesn't pile up duplicate rows for one source pack. Entries
        /// are merged by identity so the same preset is never listed twice. Disk
        /// imports keep using AddPack (each .tfpack import is its own record).</summary>
        public void AddOrMergePack(InstalledPack pack)
        {
            if (pack == null || pack.Entries == null || pack.Entries.Count == 0) return;
            var file = Load();
            InstalledPack existing = null;
            if (!string.IsNullOrEmpty(pack.CommunitySourceId))
            {
                foreach (var p in file.Packs)
                    if (p != null
                        && string.Equals(p.CommunitySourceId, pack.CommunitySourceId, StringComparison.Ordinal))
                    { existing = p; break; }
            }
            if (existing == null)
            {
                file.Packs.Add(pack);
                SaveToDisk(file);
                return;
            }
            if (existing.Entries == null) existing.Entries = new List<InstalledPackEntry>();
            foreach (var e in pack.Entries)
            {
                if (e == null) continue;
                var match = existing.Entries.Find(x => SameEntry(x, e));
                if (match == null) { existing.Entries.Add(e); continue; }
                // Identity match = this download just REWROTE the entry's file,
                // so refresh the baseline to the newly-written content. Keeping
                // the first install's hash misclassified a re-downloaded updated
                // entry as "user-edited" (file = v2, recorded baseline = v1),
                // which made pack removal conservatively keep files the user
                // never touched. Direction-safe: worst case is same-hash no-op.
                match.BaselineHash = e.BaselineHash ?? match.BaselineHash;
                match.GameName     = e.GameName     ?? match.GameName;
                if (e.DefaultForGames != null) match.DefaultForGames = e.DefaultForGames;
            }
            // Refresh light metadata so a later download updates the label /
            // version / description; keep the original ImportedAt.
            existing.PackName      = pack.PackName      ?? existing.PackName;
            existing.Author        = pack.Author        ?? existing.Author;
            existing.AuthorVersion = pack.AuthorVersion ?? existing.AuthorVersion;
            existing.Description   = pack.Description    ?? existing.Description;
            SaveToDisk(file);
        }

        // Identity equality for merge dedup: same kind + same on-disk identity
        // (game preset name, or carId + preset name). Hash/metadata are ignored.
        private static bool SameEntry(InstalledPackEntry a, InstalledPackEntry b)
        {
            if (a == null || b == null) return false;
            if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)) return false;
            if (string.Equals(a.Kind, InstalledPackEntry.KindGame, StringComparison.Ordinal))
                return string.Equals(a.Name, b.Name, StringComparison.Ordinal);
            if (string.Equals(a.Kind, InstalledPackEntry.KindCar, StringComparison.Ordinal))
                return string.Equals(a.CarId, b.CarId, StringComparison.Ordinal)
                    && string.Equals(a.PresetName, b.PresetName, StringComparison.Ordinal);
            if (string.Equals(a.Kind, InstalledPackEntry.KindEngine, StringComparison.Ordinal))
                return string.Equals(a.EngineId, b.EngineId, StringComparison.Ordinal);
            return false;
        }

        /// <summary>Remove a pack record by reference. The caller passes the
        /// InstalledPack it got from Load().Packs (or via FindPackFor*). On-disk
        /// preset files are NOT touched here; the Pack Manager handles those
        /// separately so it can preserve user-edited entries.</summary>
        public void RemovePack(InstalledPack pack)
        {
            if (pack == null) return;
            var file = Load();
            if (file.Packs.Remove(pack)) SaveToDisk(file);
        }

        /// <summary>Persist updates to an entry-level field on an already-loaded
        /// pack (e.g. after Pack Manager edits BaselineHash or DefaultForGames).
        /// The cache and disk both reflect the change.</summary>
        public void Save()
        {
            if (_cache != null) SaveToDisk(_cache);
        }

        /// <summary>SHA1 of a UTF8 string, hex-lowercase. Used by the import
        /// flow to stamp each entry's BaselineHash and by the Pack Manager to
        /// detect user edits before destructive remove.</summary>
        public static string ComputeContentHash(string content)
        {
            if (content == null) return null;
            using (var sha = SHA1.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private void SaveToDisk(InstalledPacksFile file)
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicWriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Writing installed-packs.json failed: {ex.Message}");
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
