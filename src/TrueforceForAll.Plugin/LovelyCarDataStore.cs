// Offline-first cache over the lovely-car-data repository.
//
// Shape follows the community fact cache: serve what we already hold
// immediately, refresh in the background when it goes stale, and NEVER let a
// failed fetch erase a good copy. A sim racer with no internet keeps the data
// they had; a car we have never seen simply has none.
//
// The dataset is CC BY-NC-SA 4.0. This consumes it at runtime and caches per
// machine, which is use rather than redistribution, so no ShareAlike obligation
// attaches to anything we ship. Do NOT add a path that uploads any of this to
// our own backend, and keep the attribution the settings UI carries.
//
// Self-contained on purpose (Newtonsoft + BCL only, no WPF or SimHub), so it
// link-compiles into Core.Tests alongside the parser it feeds.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    /// <summary>Fetches and caches per-car rev-light data, keyed the way the
    /// dataset is keyed: SimHub's own game and car ids, cleaned to its file
    /// naming rules.</summary>
    public sealed class LovelyCarDataStore
    {
        /// <summary>Where the published files live. The main branch is always the
        /// current version; there is no API in front of it.</summary>
        public const string BaseUrl =
            "https://raw.githubusercontent.com/Lovely-Sim-Racing/lovely-car-data/main/data/";

        /// <summary>How long a cached answer stays fresh. Matches the community
        /// fact cache. The dataset changes slowly (liveries and new cars), so a
        /// week of staleness costs nothing and keeps us off their servers.</summary>
        public TimeSpan Ttl { get; set; } = TimeSpan.FromDays(7);

        /// <summary>Swappable for tests and for offline operation. Returns the
        /// document body, an empty string for "the server says this car does not
        /// exist", or null for "the fetch failed, we learned nothing". That third
        /// case is the one that must never overwrite a cached copy.</summary>
        public Func<string, string> Fetch;

        /// <summary>Injected so tests can age entries without sleeping.</summary>
        public Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public Action<string> Log;

        private readonly string _root;
        private readonly object _gate = new object();

        // Parsed profiles by "game/carId". A car change is rare and a telemetry
        // frame is not, so nothing here is allowed to touch the disk per frame.
        private readonly Dictionary<string, LovelyCarProfile> _memory =
            new Dictionary<string, LovelyCarProfile>(StringComparer.OrdinalIgnoreCase);

        // When each entry was fetched, and whether the answer was "no such car".
        //
        // Deliberately NOT the file's modified time. That would compare our clock
        // against the filesystem's, and the two disagree whenever the system clock
        // moves backwards (an NTP correction, a timezone repair, a dual boot):
        // a cached file stamped later than "now" reads as fresh forever and the
        // car never updates again. Keeping the timestamp in our own index means
        // one clock decides, and it is the same one throughout.
        //
        // Car documents stay on disk byte for byte as published, which also keeps
        // any future bundled snapshot a verbatim copy for licensing purposes.
        private Dictionary<string, CacheStamp> _index;

        private sealed class CacheStamp
        {
            public DateTime At { get; set; }
            public bool Missing { get; set; }
        }

        /// <param name="cacheRoot">Folder to hold the cache, e.g.
        /// &lt;SimHub&gt;\PluginsData\Common\TrueforceForAll-Library\lovely-cache.</param>
        public LovelyCarDataStore(string cacheRoot)
        {
            _root = cacheRoot;
            Fetch = DefaultFetch;
        }

        /// <summary>What we hold for this car right now, without any network work.
        /// Null means "nothing cached", which is not the same as "no such car".
        /// Safe to call from a telemetry thread: memory only after the first
        /// load.</summary>
        public LovelyCarProfile GetCached(string game, string carId)
        {
            string key = MemoryKey(game, carId);
            if (key == null) return null;
            lock (_gate)
            {
                LovelyCarProfile hit;
                if (_memory.TryGetValue(key, out hit)) return hit;   // may be null: a known miss
            }

            string rel = LovelyNameCleaner.RelativePath(game, carId);
            if (rel.Length == 0) return null;

            LovelyCarProfile parsed = null;
            try
            {
                string path = CachePath(rel);
                if (path != null && File.Exists(path))
                    parsed = LovelyCarDataParser.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) { Warn("cache read failed: " + ex.Message); }

            lock (_gate) { _memory[key] = parsed; }
            return parsed;
        }

        /// <summary>Bring this car up to date, fetching when we hold nothing or
        /// what we hold has gone stale. BLOCKS on the network, so callers run it
        /// off the telemetry thread (the car-load edge, on a background task).
        /// Returns the profile now in force.</summary>
        public LovelyCarProfile Refresh(string game, string carId)
        {
            string rel = LovelyNameCleaner.RelativePath(game, carId);
            if (rel.Length == 0) return null;
            string key = MemoryKey(game, carId);

            string path = CachePath(rel);
            if (path == null) return null;

            if (IsFresh(rel, path)) return GetCached(game, carId);

            string body = null;
            try { body = Fetch != null ? Fetch(rel) : null; }
            catch (Exception ex) { Warn("fetch threw for " + rel + ": " + ex.Message); body = null; }

            if (body == null)
            {
                // The fetch failed. Whatever we already hold stays exactly as it
                // is, stale or absent; this is the case that must never destroy a
                // good cached copy, and the stamp is deliberately left alone so
                // the next session tries again rather than treating the failure
                // as an answer.
                return GetCached(game, carId);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (body.Length == 0)
                {
                    // The car genuinely is not in the dataset. Remember that too,
                    // or every session re-asks for the same absent car.
                    if (File.Exists(path)) { try { File.Delete(path); } catch { } }
                    Stamp(rel, missing: true);
                    lock (_gate) { _memory[key] = null; }
                    return null;
                }

                AtomicWrite(path, body);
                Stamp(rel, missing: false);
            }
            catch (Exception ex) { Warn("cache write failed: " + ex.Message); }

            var parsed = LovelyCarDataParser.Parse(body);
            lock (_gate) { _memory[key] = parsed; }
            return parsed;
        }

        /// <summary>Drop the in-memory copies so the next lookup re-reads the
        /// disk. For the settings toggle and for tests.</summary>
        public void ForgetMemory()
        {
            lock (_gate) { _memory.Clear(); _index = null; }
        }

        /// <summary>Whether the stored answer is still within the TTL. A stamp
        /// that claims a car we no longer have on disk is not fresh, so a manually
        /// deleted cache file refetches rather than being trusted forever. A stamp
        /// dated in the future (clock moved backwards between sessions) is also
        /// rejected, so skew costs one refetch rather than freezing the entry.</summary>
        private bool IsFresh(string rel, string path)
        {
            var index = LoadIndex();
            CacheStamp stamp;
            lock (_gate)
            {
                if (!index.TryGetValue(rel, out stamp) || stamp == null) return false;
            }

            if (!stamp.Missing && !File.Exists(path)) return false;

            TimeSpan age = UtcNow() - stamp.At;
            if (age < TimeSpan.Zero) return false;
            return age < Ttl;
        }

        private void Stamp(string rel, bool missing)
        {
            var index = LoadIndex();
            lock (_gate) { index[rel] = new CacheStamp { At = UtcNow(), Missing = missing }; }
            SaveIndex();
        }

        private string IndexPath => string.IsNullOrEmpty(_root)
            ? null : Path.Combine(_root, "cache-index.json");

        private Dictionary<string, CacheStamp> LoadIndex()
        {
            lock (_gate)
            {
                if (_index != null) return _index;
                _index = new Dictionary<string, CacheStamp>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string p = IndexPath;
                    if (p != null && File.Exists(p))
                    {
                        var loaded = JsonConvert.DeserializeObject<Dictionary<string, CacheStamp>>(
                            File.ReadAllText(p), SafeJson.Settings);
                        if (loaded != null)
                            foreach (var kv in loaded)
                                if (kv.Value != null) _index[kv.Key] = kv.Value;
                    }
                }
                catch (Exception ex) { Warn("cache index unreadable, starting fresh: " + ex.Message); }
                return _index;
            }
        }

        private void SaveIndex()
        {
            try
            {
                string p = IndexPath;
                if (p == null) return;
                Directory.CreateDirectory(_root);
                string json;
                lock (_gate) { json = JsonConvert.SerializeObject(_index); }
                AtomicWrite(p, json);
            }
            catch (Exception ex) { Warn("cache index write failed: " + ex.Message); }
        }

        /// <summary>Cache path for a repository-relative path. Returns null if the
        /// relative path is not the plain "sim/car.json" the cleaner produces;
        /// belt and braces against a cleaner change ever letting a separator or a
        /// traversal through into a file path.</summary>
        private string CachePath(string rel)
        {
            if (string.IsNullOrEmpty(_root) || string.IsNullOrEmpty(rel)) return null;
            var parts = rel.Split('/');
            if (parts.Length != 2) return null;
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..") return null;
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            }
            return Path.Combine(_root, parts[0], parts[1]);
        }

        // Temp file then replace, so a crash mid-write cannot leave a truncated
        // document that would parse as a car with no ramp. Same pattern as the
        // preset stores.
        private static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private string MemoryKey(string game, string carId)
        {
            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(carId)) return null;
            return game + "/" + carId;
        }

        private void Warn(string msg)
        {
            var log = Log;
            if (log != null) { try { log("[TF4ALL] lovely: " + msg); } catch { } }
        }

        // ---- default network fetch ----

        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() =>
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Identify ourselves rather than arriving as an anonymous scraper; it
            // is a courtesy to a small community project whose bandwidth this is.
            c.DefaultRequestHeaders.Add("User-Agent", "TrueforceForAll");
            return c;
        });

        /// <summary>Empty string means the server says no such car (404), null
        /// means we failed to learn anything (offline, timeout, server error).
        /// The distinction is the whole point: only the first is worth
        /// remembering.</summary>
        private string DefaultFetch(string relativePath)
        {
            try
            {
                var resp = Http.Value.GetAsync(BaseUrl + relativePath).GetAwaiter().GetResult();
                if (resp.StatusCode == HttpStatusCode.NotFound) return string.Empty;
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch { return null; }
        }
    }
}
