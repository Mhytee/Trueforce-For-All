using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    /// <summary>Persistent, on-disk cache of community BROWSE results (lists of
    /// <see cref="PresetSummary"/>) so the preset browser doesn't re-pull from the
    /// server on every open. Mirrors the community-FACT cache pattern (ISO-8601
    /// timestamp + TTL freshness + prune-on-write) but lives in its OWN file rather
    /// than the GeneralSettings blob, because browse lists are large and the
    /// settings blob is re-serialized on every settings change.
    ///
    /// Per-IDENTITY scoped: cached summaries embed per-user fields (MyVote,
    /// OwnerUserId), so the file name is keyed by the active local-user slot. A
    /// slot switch transparently loads that slot's own file (or an empty one), so
    /// one account never sees another's vote/ownership state.
    ///
    /// Keys are opaque strings built by the caller as "type/game/carId/sort/limit";
    /// invalidation is by prefix (drop "car/&lt;game&gt;/&lt;carId&gt;/" etc.).
    /// Thread-safe: browse fetches run on background tasks while invalidation fires
    /// from UI callbacks.</summary>
    internal sealed class CommunityBrowseCacheStore
    {
        private const int SchemaVersion = 1;
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(72);
        // A cap on distinct VIEWS (keys), not rows: each entry is one browser view
        // (a car/game list for one sort) holding many summaries. Keys accumulate
        // faster than it looks because the active-card top-list auto-caches a small
        // entry per car driven, plus per-sort browser entries per car/game. Entries
        // are tiny and this is a dedicated file (not the settings blob), so the cap
        // is generous; eviction is only a backstop against pathological growth.
        private const int MaxEntries = 1000;
        // Per-entry row cap (20 pages). Bounds the on-disk file + per-Append write
        // cost for a deeply-paged popular view. The cache stops growing past this;
        // the UI still shows further live pages (the read-through returns them), so
        // browsing isn't limited, only persistence is.
        private const int MaxSummariesPerEntry = 500;

        private readonly Func<string> _slotKeyProvider;
        private readonly Action<string> _log;
        private readonly object _lock = new object();

        private Dictionary<string, BrowseCacheEntry> _entries
            = new Dictionary<string, BrowseCacheEntry>(StringComparer.Ordinal);
        private string _loadedSlotKey;   // the slot the in-memory dict belongs to; null = not loaded
        private bool _loaded;
        // Invalidation generation. Bumped by InvalidatePrefix/Clear (NOT
        // MarkAllStale: that's a staleness hint, not a correctness barrier).
        // A writer captures it BEFORE its network read and hands it back to
        // Put/Append; a mismatch means an invalidation ran while the fetch was
        // in flight, so the (pre-invalidation) result must not be cached or a
        // just-deleted/voted row resurrects from cache for up to the TTL.
        // In-memory only: a process restart can't have an in-flight fetch.
        private int _generation;

        public CommunityBrowseCacheStore(Func<string> slotKeyProvider, Action<string> log = null)
        {
            _slotKeyProvider = slotKeyProvider ?? (() => "");
            _log = log;
        }

        private sealed class RootDoc
        {
            public int SchemaVersion { get; set; }
            public string SlotKey { get; set; }
            public Dictionary<string, BrowseCacheEntry> Entries { get; set; }
        }

        internal sealed class BrowseCacheEntry
        {
            public string FetchedAtUtc { get; set; }
            public List<PresetSummary> Summaries { get; set; }
        }

        // ---------- public API ----------

        /// <summary>Snapshot the invalidation generation. Capture BEFORE starting
        /// the network read and pass to <see cref="Put"/>/<see cref="Append"/> so a
        /// result read before an invalidation can't be cached after it.</summary>
        public int CurrentGeneration
        {
            get { lock (_lock) return _generation; }
        }

        /// <summary>Return the cached list for the key (a copy), and whether it is
        /// still fresh (within the TTL). Returns null when there is no entry. A
        /// stale entry is still returned (with fresh=false) so callers can fall
        /// back to it when a live fetch fails (offline / signed out).</summary>
        public List<PresetSummary> TryGet(string key, out bool fresh)
        {
            fresh = false;
            if (string.IsNullOrEmpty(key)) return null;
            lock (_lock)
            {
                EnsureLoaded_Locked();
                if (!_entries.TryGetValue(key, out var e) || e?.Summaries == null) return null;
                fresh = IsFresh(e.FetchedAtUtc);
                // DEEP copy: callers re-rank AND optimistically mutate elements
                // (vote/edit), which must not write through to the cached objects.
                return e.Summaries.Select(s => s?.Clone()).ToList();
            }
        }

        /// <summary>Store (or replace) the list for the key, stamp it now, prune to
        /// the cap, and persist. No-op for a null list (never cache a failed fetch
        /// as a fresh empty - that would suppress the next refresh). Pass the
        /// pre-fetch <see cref="CurrentGeneration"/> as asOfGeneration; the write is
        /// dropped if an invalidation ran since (null skips the check).</summary>
        public void Put(string key, List<PresetSummary> list, int? asOfGeneration = null)
        {
            if (string.IsNullOrEmpty(key) || list == null) return;
            lock (_lock)
            {
                if (asOfGeneration.HasValue && asOfGeneration.Value != _generation)
                {
                    _log?.Invoke("[TF4ALL] Browse-cache Put dropped (invalidated mid-fetch): " + key);
                    return;
                }
                EnsureLoaded_Locked();
                _entries[key] = new BrowseCacheEntry
                {
                    FetchedAtUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    // DEEP copy so the cache owns isolated objects: the read-through
                    // returns the original fetched list to the caller, distinct from
                    // these, so caller mutations can't reach the cache.
                    Summaries = list.Select(s => s?.Clone()).ToList(),
                };
                Prune_Locked();
                Save_Locked();
            }
        }

        /// <summary>Append a freshly-pulled page to the accumulated list for this
        /// view (the "Show more" path) and re-stamp it fresh. If no base entry
        /// exists (e.g. invalidated between page 1 and the page), the page becomes
        /// the new entry. Deep-copies so the cache owns isolated objects. Same
        /// asOfGeneration contract as <see cref="Put"/>.</summary>
        public void Append(string key, List<PresetSummary> more, int? asOfGeneration = null)
        {
            if (string.IsNullOrEmpty(key) || more == null || more.Count == 0) return;
            lock (_lock)
            {
                if (asOfGeneration.HasValue && asOfGeneration.Value != _generation)
                {
                    _log?.Invoke("[TF4ALL] Browse-cache Append dropped (invalidated mid-fetch): " + key);
                    return;
                }
                EnsureLoaded_Locked();
                if (!_entries.TryGetValue(key, out var e) || e?.Summaries == null)
                {
                    e = new BrowseCacheEntry { Summaries = new List<PresetSummary>() };
                    _entries[key] = e;
                }
                // Dedup by Id (a concurrent server upload/vote between page pulls can
                // slide a row across the offset boundary even with the id tiebreaker)
                // and stop at the per-entry cap (the UI still got the live page).
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in e.Summaries) if (s?.Id != null) seen.Add(s.Id);
                int added = 0;
                foreach (var s in more)
                {
                    if (e.Summaries.Count >= MaxSummariesPerEntry) break;
                    if (s == null || (s.Id != null && !seen.Add(s.Id))) continue;
                    e.Summaries.Add(s.Clone());
                    added++;
                }
                // Nothing landed (all deduped away, or the entry is at cap):
                // skip the re-stamp + full-file rewrite. Re-stamping a no-op
                // append would phantom-extend the TTL of rows this page didn't
                // actually refresh, and the atomic rewrite isn't free.
                if (added == 0) return;
                e.FetchedAtUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                Prune_Locked();
                Save_Locked();
            }
        }

        /// <summary>Drop every entry whose key starts with the prefix (Ordinal).
        /// Used to invalidate a family (e.g. "car/&lt;game&gt;/&lt;carId&gt;/") on the
        /// user's own upload / delete / vote so their change shows immediately.</summary>
        public void InvalidatePrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return;
            lock (_lock)
            {
                EnsureLoaded_Locked();
                // Bump BEFORE the no-match early-return: in the delete/vote race
                // the in-flight fetch usually hasn't written its entry yet, so
                // there's nothing to drop here - the bump is what dooms that
                // fetch's late Put/Append.
                unchecked { _generation++; }
                var doomed = _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (doomed.Count == 0) return;
                foreach (var k in doomed) _entries.Remove(k);
                Save_Locked();
            }
        }

        /// <summary>Drop everything (e.g. a full manual refresh, or defensively).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                EnsureLoaded_Locked();
                unchecked { _generation++; }   // same pre-return bump as InvalidatePrefix
                if (_entries.Count == 0) return;
                _entries.Clear();
                Save_Locked();
            }
        }

        /// <summary>Mark every entry stale (null its timestamp) WITHOUT dropping it,
        /// so the next browse refetches fresh while the cached list still serves as
        /// the offline fallback. Soft counterpart to <see cref="Clear"/>.</summary>
        public void MarkAllStale()
        {
            lock (_lock)
            {
                EnsureLoaded_Locked();
                bool any = false;
                foreach (var e in _entries.Values)
                    if (e != null && e.FetchedAtUtc != null) { e.FetchedAtUtc = null; any = true; }
                if (any) Save_Locked();
            }
        }

        // ---------- internals ----------

        private static bool IsFresh(string fetchedAtUtc)
        {
            if (string.IsNullOrEmpty(fetchedAtUtc)) return false;
            if (!DateTime.TryParse(fetchedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                return false;
            return (DateTime.UtcNow - when.ToUniversalTime()) < Ttl;
        }

        // Load the dict for the CURRENT slot; reload if the active slot changed
        // since last load (transparent identity switch, no cross-user leak).
        private void EnsureLoaded_Locked()
        {
            string slot = _slotKeyProvider() ?? "";
            if (_loaded && _loadedSlotKey == slot) return;
            _entries = new Dictionary<string, BrowseCacheEntry>(StringComparer.Ordinal);
            _loadedSlotKey = slot;
            _loaded = true;
            try
            {
                string path = PathForSlot(slot);
                if (!File.Exists(path)) return;
                var doc = JsonConvert.DeserializeObject<RootDoc>(File.ReadAllText(path));
                // Drop a foreign-schema or wrong-slot file (defense in depth).
                if (doc == null || doc.SchemaVersion != SchemaVersion || doc.SlotKey != slot
                    || doc.Entries == null) return;
                _entries = new Dictionary<string, BrowseCacheEntry>(doc.Entries, StringComparer.Ordinal);
            }
            catch (Exception ex) { _log?.Invoke("[TF4ALL] Browse-cache load failed: " + ex.Message); }
        }

        private void Save_Locked()
        {
            try
            {
                string path = PathForSlot(_loadedSlotKey ?? "");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var doc = new RootDoc { SchemaVersion = SchemaVersion, SlotKey = _loadedSlotKey ?? "", Entries = _entries };
                string json = JsonConvert.SerializeObject(doc);
                // Atomic write: temp then replace, so a crash mid-write can't corrupt.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (!File.Exists(path)) { File.Move(tmp, path); return; }
                try { File.Replace(tmp, path, null); }
                catch (IOException)
                {
                    // File.Replace can throw intermittently under AV / indexer locks.
                    // Fall back to delete-then-move so an invalidation still reaches
                    // disk (otherwise a deleted/voted entry could resurface on relaunch).
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex) { _log?.Invoke("[TF4ALL] Browse-cache save failed: " + ex.Message); }
        }

        private void Prune_Locked()
        {
            if (_entries.Count <= MaxEntries) return;
            var oldestFirst = _entries.ToList();
            oldestFirst.Sort((a, b) => string.CompareOrdinal(
                a.Value?.FetchedAtUtc ?? "", b.Value?.FetchedAtUtc ?? ""));   // ISO-8601 sorts chronologically
            int toRemove = _entries.Count - MaxEntries;
            for (int i = 0; i < toRemove && i < oldestFirst.Count; i++)
                _entries.Remove(oldestFirst[i].Key);
        }

        private static string PathForSlot(string slot)
        {
            string dir = Path.Combine(TfPaths.CommonRoot, BuiltinPresets.RootFolderName);
            return Path.Combine(dir, "community-browse-cache-" + SlotFileToken(slot) + ".json");
        }

        // Filename-safe, collision-resistant token for a slot key (which may be a
        // user id, an email-derived key, or empty for anonymous).
        private static string SlotFileToken(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return "anon";
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(slot));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
