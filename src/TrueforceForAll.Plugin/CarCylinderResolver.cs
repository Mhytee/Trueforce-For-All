// Resolves a (gameName, carId) pair to a cylinder count + EV flag for
// EnginePulseEffect. Three-stage cascade:
//
//   1. Baked lookup (BuiltinCarCylinders) — Kunos lineup + heuristic-derived
//      pre-bake covering ~91% of the typical AC library at ship time.
//   2. Persistent cache (Settings.CarCylinderCache) — heuristic results
//      from prior sessions for cars not in the bake. Cached forever per
//      install; cleared en masse when CarCylinderCacheVersion bumps.
//   3. Heuristic detector — for carIds that are in neither, we read the
//      game's per-car metadata (AC's content/cars/<carId>/ui/ui_car.json)
//      and pattern-match tags + description + name + carId.
//
// In-process cache: the resolver also keeps a process-lifetime cache so
// repeated car switches to the same carId don't re-hit disk during a
// single session. This is separate from (and faster than) the persistent
// cache, which only matters across SimHub restarts.
//
// AC install root discovery: parses Steam's libraryfolders.vdf to find
// app 244210 (Assetto Corsa). Cached after first successful lookup.
// Returns null if Steam isn't installed or AC isn't in any known library.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    internal static class CarCylinderResolver
    {
        // Result codes mirror BuiltinCarSpec but distinguish a miss (null)
        // from a hit that says "EV". Caller treats the IsElectric flag as
        // a directive to halve EnginePulse amplitude rather than override
        // the cylinder count.
        public sealed class Result
        {
            public int  Cylinders  { get; set; }
            public bool IsElectric { get; set; }
            public string Source   { get; set; }   // "baked", "tag", "desc", "codename", "chassis", "cylword"
            /// <summary>Engine layout chosen for this car. Auto = no explicit
            /// pick, fall back to FiringPatternDb's cyl-count default. Explicit
            /// values come from the bake (BuiltinCarCylinders) or from the
            /// heuristic's brand/codename detector below.</summary>
            public EngineConfig EngineConfig { get; set; } = EngineConfig.Auto;
            /// <summary>How EngineConfig was determined. Same shape as Source
            /// but tracked separately so a "baked" cylinder count can coexist
            /// with a "codename-derived" engine config (e.g. when the bake
            /// only knew cyl count). Null means EngineConfig is Auto.</summary>
            public string EngineConfigSource { get; set; }
        }

        // ----- coverage counters (read from the diagnostics surface) -----
        // Incremented on each ResolveInternal call. Lets the UI show
        // "Cylinder hits: 412 (212 baked + 200 heuristic) / Engine config
        // hits: 387 (180 baked + 207 heuristic)" so users can see how often
        // we're producing specific layout info vs falling back to Auto.
        private static int _cylBakedHits, _cylHeuristicHits;
        private static int _cfgBakedHits, _cfgHeuristicHits;
        public static (int CylBaked, int CylHeuristic, int CfgBaked, int CfgHeuristic) GetCoverageCounters()
            => (Volatile.Read(ref _cylBakedHits),
                Volatile.Read(ref _cylHeuristicHits),
                Volatile.Read(ref _cfgBakedHits),
                Volatile.Read(ref _cfgHeuristicHits));

        // Process-lifetime cache. Two states:
        //   key present, value non-null  → hit, reuse
        //   key present, value null      → miss, don't re-probe disk
        //   key absent                   → not yet probed
        private static readonly Dictionary<string, Result> Cache =
            new Dictionary<string, Result>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        // Bump to invalidate persistent caches across all installs after a
        // heuristic improvement. Compared against Settings.CarCylinderCacheVersion
        // at AttachPersistentCache time; mismatch clears the cache and updates
        // the version stamp before any new entries are written.
        // v2: cascade reorder (codename before desc-layout), per-letter
        //     plausibility filter on V<n>/I<n>/etc, rotor-phrase detection,
        //     engine-context proximity check, chassis lookup before desc-layout.
        // v3: encoded value now carries EngineConfig in bits 8..11 alongside
        //     cyl in low 5 bits. Old v2 entries (cyl-only) are dropped on
        //     attach so cars re-resolve and pick up the new engine-config
        //     bake / heuristic on next session.
        public const int CurrentCacheVersion = 3;

        // Persistent cache (settings-backed). Plugin calls AttachPersistentCache
        // on startup with the loaded Settings.CarCylinderCache reference; the
        // resolver reads through to it on miss and writes back on heuristic hit.
        //
        // Encoding (v3): a single int packs cyl, config, and the EV flag.
        //   bits  0..4  (mask 0x1F): cylinder count, 1..16
        //   bits  8..11 (mask 0xF00): EngineConfig index, 0..12 (Auto = 0)
        //   value -1                : EV sentinel (cyl/config N/A)
        // EV sentinel preserved as -1 for code clarity; readers compare
        // explicitly. JSON shape stays the same as v2 (a flat int per car),
        // so SimHub's serializer doesn't need a custom converter.
        private static Dictionary<string, Dictionary<string, int>> _persistentCache;
        private static readonly object PersistentLock = new object();
        private const int EvSentinel = -1;
        private const int CylBits    = 0x1F;     // low 5 bits
        private const int ConfigShift = 8;
        private const int ConfigBits = 0xF00;    // bits 8..11

        private static int EncodeCacheValue(Result r)
        {
            if (r == null) return 0;
            if (r.IsElectric) return EvSentinel;
            int cfg = (int)r.EngineConfig & 0xF;
            int cyl = r.Cylinders & CylBits;
            return cyl | (cfg << ConfigShift);
        }
        private static bool DecodeCacheValue(int encoded, out int cyl, out EngineConfig cfg, out bool isEv)
        {
            cyl = 0; cfg = EngineConfig.Auto; isEv = false;
            if (encoded == EvSentinel) { isEv = true; return true; }
            cyl = encoded & CylBits;
            int cfgIdx = (encoded & ConfigBits) >> ConfigShift;
            if (cfgIdx < 0 || cfgIdx > (int)EngineConfig.Custom) cfgIdx = 0;
            cfg = (EngineConfig)cfgIdx;
            return cyl >= 1 && cyl <= 16;
        }

        /// <summary>Attach the settings-backed persistent cache. Pass the live
        /// reference from TrueforceSettings; the resolver writes new entries
        /// through it, and the plugin's existing settings save flow flushes
        /// them to disk. Call once at plugin init. If currentVersion does not
        /// match CurrentCacheVersion, the cache is cleared and the version
        /// stamp updated.</summary>
        public static void AttachPersistentCache(
            Dictionary<string, Dictionary<string, int>> cache,
            ref int currentVersion)
        {
            lock (PersistentLock)
            {
                if (cache == null) return;
                if (currentVersion != CurrentCacheVersion)
                {
                    cache.Clear();
                    currentVersion = CurrentCacheVersion;
                }
                _persistentCache = cache;
            }
        }

        /// <summary>Try to resolve a cylinder count for a (game, car) pair.
        /// Returns false on miss; the caller should leave AutoCylinders
        /// alone (so the user's configured Cylinders applies) when this
        /// returns false.</summary>
        public static bool TryResolve(string gameName, string carId, out Result result)
        {
            result = null;
            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(carId)) return false;

            string key = gameName + "|" + carId;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    result = cached;
                    return cached != null;
                }
            }

            Result resolved = ResolveInternal(gameName, carId);
            lock (CacheLock)
            {
                Cache[key] = resolved;   // store nulls too so misses don't re-probe
            }
            result = resolved;
            return resolved != null;
        }

        private static Result ResolveInternal(string gameName, string carId)
        {
            if (BuiltinCarCylinders.TryGet(gameName, carId, out var spec))
            {
                Interlocked.Increment(ref _cylBakedHits);
                bool hasBakedConfig = spec.EngineConfig != EngineConfig.Auto;
                if (hasBakedConfig) Interlocked.Increment(ref _cfgBakedHits);
                var r = new Result
                {
                    Cylinders          = spec.Cylinders,
                    IsElectric         = spec.IsElectric,
                    Source             = "baked",
                    EngineConfig       = spec.EngineConfig,
                    EngineConfigSource = hasBakedConfig ? "baked" : null,
                };
                // Second-pass: if the bake only knows cyl count (config = Auto)
                // and we can read the AC ui_car.json, run the config detector
                // against the description so cars baked from the older single-
                // axis heuristic still get a specific layout when one's
                // detectable. Counts toward the heuristic config coverage.
                if (!hasBakedConfig && !r.IsElectric
                    && string.Equals(gameName, "AssettoCorsa", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryAcDetectConfigOnly(carId, r.Cylinders, out var cfg, out var cfgSrc))
                    {
                        r.EngineConfig       = cfg;
                        r.EngineConfigSource = cfgSrc;
                        Interlocked.Increment(ref _cfgHeuristicHits);
                    }
                }
                return r;
            }

            // Persistent cache: prior heuristic hits saved across sessions.
            if (TryReadPersistent(gameName, carId, out var cached))
            {
                // Cache hits don't double-count against the running counters
                // (we already counted them on the resolution that wrote them);
                // we just return what's stored.
                return cached;
            }

            // Heuristic only implemented for AC today.
            if (string.Equals(gameName, "AssettoCorsa", StringComparison.OrdinalIgnoreCase))
            {
                var hit = TryAcHeuristic(carId);
                if (hit != null)
                {
                    Interlocked.Increment(ref _cylHeuristicHits);
                    if (hit.EngineConfig != EngineConfig.Auto)
                        Interlocked.Increment(ref _cfgHeuristicHits);
                    WritePersistent(gameName, carId, hit);
                }
                return hit;
            }

            return null;
        }

        private static bool TryReadPersistent(string gameName, string carId, out Result result)
        {
            result = null;
            lock (PersistentLock)
            {
                if (_persistentCache == null) return false;
                if (!_persistentCache.TryGetValue(gameName, out var inner)) return false;
                if (!inner.TryGetValue(carId, out var encoded)) return false;
                if (!DecodeCacheValue(encoded, out int cyl, out var cfg, out bool isEv))
                    return false;
                if (isEv)
                {
                    result = new Result { Cylinders = 4, IsElectric = true, Source = "cache-ev" };
                    return true;
                }
                result = new Result
                {
                    Cylinders          = cyl,
                    IsElectric         = false,
                    Source             = "cache",
                    EngineConfig       = cfg,
                    EngineConfigSource = cfg == EngineConfig.Auto ? null : "cache",
                };
                return true;
            }
        }

        private static void WritePersistent(string gameName, string carId, Result r)
        {
            if (r == null) return;
            lock (PersistentLock)
            {
                if (_persistentCache == null) return;
                if (!_persistentCache.TryGetValue(gameName, out var inner))
                {
                    inner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _persistentCache[gameName] = inner;
                }
                inner[carId] = EncodeCacheValue(r);
            }
        }

        // --------- Assetto Corsa heuristic ---------

        private static string _acRootCached;
        private static bool   _acRootResolved;

        private static Result TryAcHeuristic(string carId)
        {
            string root = GetAcInstallRoot();
            if (string.IsNullOrEmpty(root)) return null;

            string uiPath = Path.Combine(root, "content", "cars", carId, "ui", "ui_car.json");
            if (!File.Exists(uiPath)) return null;

            string raw;
            try
            {
                raw = File.ReadAllText(uiPath);
                if (raw.Length > 0 && raw[0] == '﻿') raw = raw.Substring(1);
            }
            catch { return null; }

            // Extract the three fields we care about. Use raw regex rather
            // than JSON parsing — some Kunos files have stray control chars
            // that trip strict parsers.
            string name = ExtractStringField(raw, "name");
            string desc = ExtractStringField(raw, "description");
            string[] tags = ExtractTagsArray(raw);
            string haystack = (name ?? "") + " " + string.Join(" ", tags ?? Array.Empty<string>())
                              + " " + (desc ?? "") + " " + carId;

            // Strip HTML entities so they don't fragment word-boundary matches
            haystack = Regex.Replace(haystack, "&[a-zA-Z]+;", " ");

            // Local Finish helper: every return point runs through here so the
            // EngineConfig field gets populated from the same haystack used to
            // determine cyl count.
            string hsCaptured = haystack;
            string[] tagsCaptured = tags;
            Result Finish(Result r)
            {
                if (r == null) return null;
                if (r.IsElectric) return r;   // EVs ignore EngineConfig downstream
                var (cfg, src) = DetectEngineConfig(r.Cylinders, hsCaptured, tagsCaptured);
                if (cfg != EngineConfig.Auto)
                {
                    r.EngineConfig       = cfg;
                    r.EngineConfigSource = src;
                }
                return r;
            }

            // 1. Tag layout (V8, I6, F4, etc.) — exact tag match
            if (tags != null)
            {
                foreach (var rawTag in tags)
                {
                    string t = (rawTag ?? "").Trim();
                    if (t.Length == 0) continue;
                    if (Regex.IsMatch(t, @"^(?i)(rotary|wankel)$"))
                    {
                        // Most AC rotaries are 13B 2-rotor → 4 effective cyl
                        return Finish(new Result { Cylinders = 4, Source = "tag-rotary",
                                                   EngineConfig = EngineConfig.Rotary,
                                                   EngineConfigSource = "tag-rotary" });
                    }
                    if (Regex.IsMatch(t, @"^(?i)(electric|ev|bev)$"))
                    {
                        return new Result { Cylinders = 4, IsElectric = true, Source = "tag-ev" };
                    }
                    var m = Regex.Match(t, @"^(?i)(?<L>[VILFBWS])(?<C>2|3|4|5|6|8|10|12|16)$");
                    if (m.Success)
                    {
                        // Tag letter encodes layout: F = flat (boxer), B = boxer.
                        // V/I/L/W stay generic — the second-pass DetectEngineConfig
                        // refines (e.g., V8 → cross/flat-plane based on brand).
                        char layoutLetter = char.ToUpperInvariant(m.Groups["L"].Value[0]);
                        var prelim = new Result
                        {
                            Cylinders = int.Parse(m.Groups["C"].Value),
                            Source = "tag",
                        };
                        if (layoutLetter == 'F' || layoutLetter == 'B')
                        {
                            prelim.EngineConfig       = EngineConfig.Boxer;
                            prelim.EngineConfigSource = "tag-boxer";
                        }
                        return Finish(prelim);
                    }
                }
            }

            // 2. "X-cylinder" / "X cylinder" explicit phrase. Strong signal.
            //    Reject "valves per cylinder" by requiring a non-letter prefix.
            var cw = Regex.Match(haystack, @"(?<![a-zA-Z])(\d{1,2})[-\s]?cylinder\b", RegexOptions.IgnoreCase);
            if (cw.Success)
            {
                int n = int.Parse(cw.Groups[1].Value);
                if (n >= 2 && n <= 16)
                    return Finish(new Result { Cylinders = n, Source = "cylword" });
            }

            // 3. Engine codename lookup (LS-series, 2JZ, RB26, EJ20, K20, etc.).
            //    Promoted ahead of the generic desc-layout pattern because
            //    explicit engine names beat ambient product references like
            //    "Wisefab V3 steering" or "Audi B5 chassis" that the layout
            //    regex would otherwise misread as a cylinder declaration.
            //    Case-insensitive — modders often use "2jz" / "rb26" lowercase.
            //    EngineCodenames carries the layout per codename so a "LS3"
            //    hit goes straight to V8CrossPlane without a second-pass guess.
            foreach (var (pat, cyl, cfg) in EngineCodenames)
            {
                if (Regex.IsMatch(haystack, pat, RegexOptions.IgnoreCase))
                {
                    var r = new Result { Cylinders = cyl, Source = "codename" };
                    if (cfg != EngineConfig.Auto)
                    {
                        r.EngineConfig       = cfg;
                        r.EngineConfigSource = "codename";
                    }
                    return Finish(r);
                }
            }

            // 4. Rotor-count phrases: "twin rotor", "2-rotor", "triple-rotor", etc.
            //    Maps rotor count → effective cyl: rotors × 2 (per the firing-
            //    frequency derivation — see EnginePulseEffect comment).
            var rp = Regex.Match(haystack,
                @"\b(twin|two|2|triple|three|3|quad|four|4)[-\s]?rotor[s]?\b",
                RegexOptions.IgnoreCase);
            if (rp.Success)
            {
                string w = rp.Groups[1].Value.ToLowerInvariant();
                int rotors = (w == "twin" || w == "two"   || w == "2") ? 2
                          : (w == "triple"|| w == "three" || w == "3") ? 3
                          : (w == "quad"  || w == "four"  || w == "4") ? 4
                          : 0;
                if (rotors > 0)
                    return Finish(new Result { Cylinders = rotors * 2, Source = "rotor-phrase",
                                               EngineConfig = EngineConfig.Rotary,
                                               EngineConfigSource = "rotor-phrase" });
            }
            // Bare "rotor" / "wankel" without a count → assume 2-rotor (the
            // overwhelmingly common case in AC's catalog).
            if (Regex.IsMatch(haystack, @"\b(rotary engine|wankel)\b", RegexOptions.IgnoreCase))
            {
                return Finish(new Result { Cylinders = 4, Source = "desc-rotary",
                                           EngineConfig = EngineConfig.Rotary,
                                           EngineConfigSource = "desc-rotary" });
            }

            // 5. Word-form layouts: "inline-six", "flat-4", "boxer four"
            //    Moved before desc-layout because the layout-name word is
            //    self-validating (no false-positive risk).
            var dw = Regex.Match(haystack,
                @"\b(?:inline|straight|flat|boxer)[-\s](?:two|three|four|five|six|eight|ten|twelve|2|3|4|5|6|8|10|12)\b",
                RegexOptions.IgnoreCase);
            if (dw.Success)
            {
                string m = dw.Value.ToLowerInvariant();
                int? n = null;
                if (m.Contains("two")) n = 2;
                else if (m.Contains("three")) n = 3;
                else if (m.Contains("four")) n = 4;
                else if (m.Contains("five")) n = 5;
                else if (m.Contains("six")) n = 6;
                else if (m.Contains("eight")) n = 8;
                else if (m.Contains("ten")) n = 10;
                else if (m.Contains("twelve")) n = 12;
                else
                {
                    var dnum = Regex.Match(m, @"\d+");
                    if (dnum.Success) n = int.Parse(dnum.Value);
                }
                if (n.HasValue)
                {
                    // Word forms self-describe layout: "boxer four" / "flat-6"
                    // map to Boxer; "inline-N" / "straight-N" to Inline.
                    string lower = m;
                    EngineConfig wordCfg = EngineConfig.Auto;
                    string wordSrc = null;
                    if (lower.StartsWith("boxer", StringComparison.Ordinal)
                     || lower.StartsWith("flat",  StringComparison.Ordinal))
                    {
                        wordCfg = EngineConfig.Boxer;
                        wordSrc = "desc-word-boxer";
                    }
                    else if (lower.StartsWith("inline",   StringComparison.Ordinal)
                          || lower.StartsWith("straight", StringComparison.Ordinal))
                    {
                        wordCfg = EngineConfig.Inline;
                        wordSrc = "desc-word-inline";
                    }
                    return Finish(new Result {
                        Cylinders = n.Value, Source = "desc-word",
                        EngineConfig = wordCfg, EngineConfigSource = wordSrc,
                    });
                }
            }

            // 6. Brand+chassis fallback (E36 → I6, 911 → F6, Miata → I4, etc.)
            //    Promoted ahead of desc-layout because chassis codes are far
            //    less ambiguous than the V<n>/I<n> regex. A chassis hit on
            //    "gt86" cleanly wins over an unrelated "BDC V6" mention in
            //    the description (tuner-shop name, not an engine layout).
            //    Conservative — engine swaps will give wrong answers, but
            //    swaps are usually called out by codename (LS / JZ / RB)
            //    which the codename pass above already catches.
            //    ChassisLookup carries the layout per chassis so a "911" hit
            //    goes straight to Boxer, "mustang" to V8CrossPlane, etc.
            foreach (var (pat, cyl, cfg) in ChassisLookup)
            {
                if (Regex.IsMatch(haystack, pat, RegexOptions.IgnoreCase))
                {
                    if (cyl == -1)   // rotary sentinel
                        return Finish(new Result { Cylinders = 4, Source = "chassis-rotary",
                                                   EngineConfig = EngineConfig.Rotary,
                                                   EngineConfigSource = "chassis-rotary" });
                    var r = new Result { Cylinders = cyl, Source = "chassis" };
                    if (cfg != EngineConfig.Auto)
                    {
                        r.EngineConfig       = cfg;
                        r.EngineConfigSource = "chassis";
                    }
                    return Finish(r);
                }
            }

            // 7. Inline description layout patterns: "V8", "5.0L V8", "I6", etc.
            //    Last-resort because V<n>/I<n>/B<n> commonly appears in non-
            //    engine contexts (mod versions like "v2", chassis codes like
            //    "B5", tuner shop names like "BDC V6"). To suppress those, we
            //    require the match to be near a word that signals an engine
            //    description (engine, motor, swap, liter, HP, BHP, turbo,
            //    supercharged, etc.). Per-letter plausibility filter applies:
            //    no V2/V3 (real V3 engines don't exist in passenger cars),
            //    but I3 stays valid for Daihatsu Copen / GR Yaris.
            var dl = Regex.Match(haystack, @"\b([VILFBW])[-\s]?(2|3|4|5|6|8|10|12|16)\b");
            while (dl.Success)
            {
                string layout = dl.Groups[1].Value;
                int n = int.Parse(dl.Groups[2].Value);
                bool plausible =
                    (layout == "V" && (n == 4 || n == 6 || n == 8 || n == 10 || n == 12 || n == 16))
                 || (layout == "I" && (n == 3 || n == 4 || n == 5 || n == 6 || n == 8))
                 || (layout == "L" && (n == 4 || n == 5 || n == 6 || n == 8))
                 || (layout == "F" && (n == 4 || n == 6 || n == 8 || n == 12))
                 || (layout == "B" && (n == 4 || n == 6))
                 || (layout == "W" && (n == 12 || n == 16));
                if (plausible && HasEngineContext(haystack, dl.Index, dl.Length))
                {
                    EngineConfig descCfg = EngineConfig.Auto;
                    string descSrc = null;
                    if (layout == "F" || layout == "B")
                    {
                        descCfg = EngineConfig.Boxer;
                        descSrc = "desc-letter-boxer";
                    }
                    return Finish(new Result {
                        Cylinders = n, Source = "desc",
                        EngineConfig = descCfg, EngineConfigSource = descSrc,
                    });
                }
                dl = dl.NextMatch();
            }

            return null;
        }

        // Used by the bake → heuristic-config bridge: a car with a baked cyl
        // count but Auto config gets its config from the same description-
        // scanning logic the full heuristic uses, without re-deriving cyl.
        // Returns false if AC isn't installed or the ui_car.json is missing
        // (no harm — caller leaves Auto and the cyl-count default applies).
        private static bool TryAcDetectConfigOnly(string carId, int cyl, out EngineConfig cfg, out string source)
        {
            cfg = EngineConfig.Auto;
            source = null;
            string root = GetAcInstallRoot();
            if (string.IsNullOrEmpty(root)) return false;
            string uiPath = Path.Combine(root, "content", "cars", carId, "ui", "ui_car.json");
            if (!File.Exists(uiPath)) return false;
            string raw;
            try
            {
                raw = File.ReadAllText(uiPath);
                if (raw.Length > 0 && raw[0] == '﻿') raw = raw.Substring(1);
            }
            catch { return false; }
            string name = ExtractStringField(raw, "name");
            string desc = ExtractStringField(raw, "description");
            string[] tags = ExtractTagsArray(raw);
            string haystack = (name ?? "") + " " + string.Join(" ", tags ?? Array.Empty<string>())
                              + " " + (desc ?? "") + " " + carId;
            haystack = Regex.Replace(haystack, "&[a-zA-Z]+;", " ");
            var (detected, src) = DetectEngineConfig(cyl, haystack, tags);
            if (detected == EngineConfig.Auto) return false;
            cfg = detected;
            source = src;
            return true;
        }

        // -------------------- second-pass engine-config detection --------------------
        //
        // Called after cyl is determined. Looks at the same haystack + tags for
        // brand / codename / layout-word signals to refine Auto into a specific
        // EngineConfig where possible. Returns (Auto, null) when no signal
        // fires — caller leaves the result as-is and the Auto fallback in
        // FiringPatternDb produces the cyl-count-based default.
        //
        // Order matters: explicit codenames beat brand inference beats word
        // matches, so a Lotus 49 with a Cosworth DFV codename gets flat-plane
        // even though its brand-only inference would say nothing.
        private static (EngineConfig Cfg, string Source) DetectEngineConfig(int cyl, string haystack, string[] tags)
        {
            if (string.IsNullOrEmpty(haystack)) return (EngineConfig.Auto, null);

            // Tag matches first: most reliable, no ambiguity.
            if (tags != null)
            {
                foreach (var rawTag in tags)
                {
                    string t = (rawTag ?? "").Trim();
                    if (t.Length == 0) continue;
                    if (Regex.IsMatch(t, @"^(?i)flat[-\s]?plane$"))    return (EngineConfig.V8FlatPlane,  "tag");
                    if (Regex.IsMatch(t, @"^(?i)cross[-\s]?plane$"))   return (EngineConfig.V8CrossPlane, "tag");
                    if (Regex.IsMatch(t, @"^(?i)boxer$"))              return (EngineConfig.Boxer,        "tag");
                    if (Regex.IsMatch(t, @"^(?i)(rotary|wankel)$"))    return (EngineConfig.Rotary,       "tag");
                }
            }

            // Layout words in description.
            if (Regex.IsMatch(haystack, @"\bflat[-\s]?plane\b",   RegexOptions.IgnoreCase))
                return (EngineConfig.V8FlatPlane,  "desc-flat-plane");
            if (Regex.IsMatch(haystack, @"\bcross[-\s]?plane\b",  RegexOptions.IgnoreCase))
                return (EngineConfig.V8CrossPlane, "desc-cross-plane");

            // Engine codename → config (subset of the cyl table that maps
            // cleanly to a specific layout). If a codename ALSO appears in
            // the cyl table this is consistent with that table's cyl count;
            // we don't double-check here — DetectEngineConfig is purely
            // additive and only fires when a layout signal is present.
            foreach (var (pat, cfg) in EngineConfigCodenames)
            {
                if (Regex.IsMatch(haystack, pat, RegexOptions.IgnoreCase))
                    return (cfg, "codename-config");
            }

            // Brand inference from the haystack. Conservative — only fires
            // when both the brand AND the cyl count match a well-known
            // layout for that brand. "Ferrari" alone doesn't tell us
            // flat-plane (the 250 GTO was a Colombo V12 60° — hits the V12
            // branch); only "Ferrari + cyl=8" does.
            bool ferrari = Regex.IsMatch(haystack, @"\bferrari\b", RegexOptions.IgnoreCase);
            bool lotus   = Regex.IsMatch(haystack, @"\blotus\b",   RegexOptions.IgnoreCase);
            bool mclaren = Regex.IsMatch(haystack, @"\bmclaren\b", RegexOptions.IgnoreCase);
            bool maserati= Regex.IsMatch(haystack, @"\bmaserati\b",RegexOptions.IgnoreCase);
            bool porsche = Regex.IsMatch(haystack, @"\bporsche\b", RegexOptions.IgnoreCase);
            bool subaru  = Regex.IsMatch(haystack, @"\bsubaru\b",  RegexOptions.IgnoreCase);
            bool ford    = Regex.IsMatch(haystack, @"\bford\b",    RegexOptions.IgnoreCase);
            bool chevy   = Regex.IsMatch(haystack, @"\b(chevrolet|chevy)\b", RegexOptions.IgnoreCase);
            bool dodge   = Regex.IsMatch(haystack, @"\b(dodge|chrysler|ram)\b", RegexOptions.IgnoreCase);
            bool gm      = Regex.IsMatch(haystack, @"\b(gm|cadillac|pontiac|buick)\b", RegexOptions.IgnoreCase);
            bool ducati  = Regex.IsMatch(haystack, @"\bducati\b",  RegexOptions.IgnoreCase);
            bool harley  = Regex.IsMatch(haystack, @"\b(harley|harley[-\s]?davidson|sportster)\b", RegexOptions.IgnoreCase);
            bool mazda   = Regex.IsMatch(haystack, @"\bmazda\b",   RegexOptions.IgnoreCase);
            bool lambo   = Regex.IsMatch(haystack, @"\b(lamborghini|lambo)\b", RegexOptions.IgnoreCase);
            bool pagani  = Regex.IsMatch(haystack, @"\bpagani\b",  RegexOptions.IgnoreCase);
            bool ams     = Regex.IsMatch(haystack, @"\b(amg|mercedes)\b", RegexOptions.IgnoreCase);

            // V-twin motorcycles (cyl=2): brand identifies the angle.
            if (cyl == 2)
            {
                if (ducati) return (EngineConfig.VTwin90, "brand-ducati");
                if (harley) return (EngineConfig.VTwin45, "brand-harley");
            }

            // V8: flat-plane vs cross-plane based on brand.
            if (cyl == 8)
            {
                if (ferrari || lotus || mclaren || maserati)
                    return (EngineConfig.V8FlatPlane, "brand-flat-plane");
                if (ford || chevy || dodge || gm || ams)
                    return (EngineConfig.V8CrossPlane, "brand-cross-plane");
            }

            // Boxer engines (flat-4 / flat-6) by brand + cyl.
            if ((cyl == 4 || cyl == 6) && (subaru || (porsche && cyl == 6)))
                return (EngineConfig.Boxer, "brand-boxer");

            // Rotaries on Mazda RX-cars are largely covered by codename / chassis,
            // but a bare "Mazda + 4 cyl" with a rotary tone in the description
            // shouldn't false-positive — already handled by tag/codename above.
            _ = mazda;   // currently unused; kept for future heuristic refinement

            // V12: Ferrari / Lambo / Pagani / Aston use 60° conventions.
            if (cyl == 12 && (ferrari || lambo || pagani || ams))
                return (EngineConfig.V60, "brand-v60");

            // V10: Lambo / Audi V10 are 90° wide-angle; Lexus LFA is 72° but
            // we approximate as V90Even since the 18° difference doesn't
            // alter the firing intervals enough to hear at haptic rates.
            if (cyl == 10 && (lambo || Regex.IsMatch(haystack, @"\baudi\b", RegexOptions.IgnoreCase)))
                return (EngineConfig.V90Even, "brand-v90");

            return (EngineConfig.Auto, null);
        }

        // True when the V<n>/I<n>/B<n> match is within ~40 chars of a word
        // that signals an engine description, so we don't false-positive on
        // tuner-shop names ("BDC V6"), version strings ("v2"), or chassis
        // codes ("F5") that share the same regex shape.
        private static bool HasEngineContext(string haystack, int matchIndex, int matchLen)
        {
            int from = Math.Max(0, matchIndex - 40);
            int to = Math.Min(haystack.Length, matchIndex + matchLen + 40);
            string ctx = haystack.Substring(from, to - from).ToLowerInvariant();
            // Whole-word checks via Contains are good enough — these tokens
            // rarely appear coincidentally in ~80-char windows.
            return ctx.Contains("engine") || ctx.Contains("motor")
                || ctx.Contains("powered") || ctx.Contains("power - ")
                || ctx.Contains("liter")  || ctx.Contains("litre")
                || ctx.Contains("turbo")  || ctx.Contains("supercharged")
                || ctx.Contains("naturally") || ctx.Contains("aspirated")
                || ctx.Contains("cylinder") || ctx.Contains("hp")
                || ctx.Contains("bhp") || ctx.Contains("displacement")
                || ctx.Contains("swap") || ctx.Contains("swapped")
                || Regex.IsMatch(ctx, @"\b\d+\.?\d*\s?[lL]\b");  // "5.0L", "4.0 L"
        }

        // ----- Field extractors (regex-based, JSON-tolerant) -----

        private static string ExtractStringField(string raw, string key)
        {
            // "key": "value with \"escapes\" and stuff"
            var m = Regex.Match(raw,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string[] ExtractTagsArray(string raw)
        {
            var m = Regex.Match(raw, "\"tags\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return Array.Empty<string>();
            var inner = m.Groups[1].Value;
            var entries = Regex.Matches(inner, "\"((?:[^\"\\\\]|\\\\.)*)\"");
            var result = new List<string>(entries.Count);
            foreach (Match e in entries) result.Add(e.Groups[1].Value);
            return result.ToArray();
        }

        // ----- Lookup tables (must mirror probe_ac_cylinders.ps1) -----

        // Order matters: more-specific patterns first so "2JZ-GTE" matches
        // before generic "2JZ", and "LS3" before "LS" with no number.
        // Each entry now carries an EngineConfig so a codename hit lands in
        // the right firing pattern without needing a second-pass guess.
        // Auto means "leave layout to FiringPatternDb's cyl-count default."
        private static readonly (string Pat, int Cyl, EngineConfig Cfg)[] EngineCodenames = new (string, int, EngineConfig)[]
        {
            // JDM straight-six (I6)
            (@"\b2JZ(?:[-\s]?GTE|[-\s]?GE)?\b",   6, EngineConfig.Inline),
            (@"\b1JZ(?:[-\s]?GTE|[-\s]?GE)?\b",   6, EngineConfig.Inline),
            (@"\bRB2[567](?:DET{1,2})?\b",        6, EngineConfig.Inline),
            (@"\bRB30\b",                         6, EngineConfig.Inline),
            (@"\b7M[-\s]?GTE\b",                  6, EngineConfig.Inline),
            (@"\bM5[02]B[0-9]+\b",                6, EngineConfig.Inline),  // BMW M50/M52
            (@"\bS5[024]B[0-9]+\b",               6, EngineConfig.Inline),  // BMW S50/S52/S54
            (@"\bN5[24]B[0-9]+\b",                6, EngineConfig.Inline),  // BMW N52/N54
            (@"\bB58\b",                          6, EngineConfig.Inline),
            // JDM straight-four (I4)
            (@"\b4[AG][-\s]?GE\b",                4, EngineConfig.Inline),  // Toyota 4A-GE
            (@"\bSR20(?:DET|VE|DE)?\b",           4, EngineConfig.Inline),
            (@"\bCA18(?:DET|DE)?\b",              4, EngineConfig.Inline),
            (@"\bKA24(?:DE|E)?\b",                4, EngineConfig.Inline),
            (@"\b4G6[34]\b",                      4, EngineConfig.Inline),  // Mitsubishi 4G63/4G64
            (@"\bK20[ACZ]?\b",                    4, EngineConfig.Inline),  // Honda K20
            (@"\bK24[AZ]?\b",                     4, EngineConfig.Inline),
            (@"\bB1[68][AB][0-9]?\b",             4, EngineConfig.Inline),  // Honda B-series
            (@"\bF20C\b",                         4, EngineConfig.Inline),
            (@"\bH22A\b",                         4, EngineConfig.Inline),
            (@"\b3SGTE\b",                        4, EngineConfig.Inline),
            (@"\bS14[BE][0-9]+\b",                4, EngineConfig.Inline),  // BMW S14 (E30 M3)
            // Subaru flat-four (F4) - boxer
            (@"\bEJ20[57]?\b",                    4, EngineConfig.Boxer),
            (@"\bEJ25[57]?\b",                    4, EngineConfig.Boxer),
            (@"\bFA20\b",                         4, EngineConfig.Boxer),
            // USDM V8 - cross-plane
            (@"\bLS[1-9]X?\b",                    8, EngineConfig.V8CrossPlane),
            (@"\b5\.3[\s-]?LS\b",                 8, EngineConfig.V8CrossPlane),
            (@"\bLT[1-5]\b",                      8, EngineConfig.V8CrossPlane),
            (@"\bCoyote\b",                       8, EngineConfig.V8CrossPlane),
            (@"\bHEMI\b",                         8, EngineConfig.V8CrossPlane),
            (@"\bHellcat\b",                      8, EngineConfig.V8CrossPlane),
            // Voodoo (Mustang GT350) is the only well-known American V8 with
            // a flat-plane crank, so it gets called out specifically.
            (@"\bVoodoo\b",                       8, EngineConfig.V8FlatPlane),
            // Nissan V6 - 60° even-fire
            (@"\bVQ3[57]\b",                      6, EngineConfig.V60),
            (@"\bVR38\b",                         6, EngineConfig.V60),
            // Porsche flat-6 (Mezger)
            (@"\bMezger\b",                       6, EngineConfig.Boxer),
            // Mazda rotary — rotor count × 2 = effective cyl in our formula:
            // 13B / Renesis (2-rotor) → 4, 20B (3-rotor) → 6, 26B (4-rotor) → 8.
            // 13B-MSP and 13B-REW are common variant names; include both.
            (@"\b13B(?:[-\s]?(?:REW|MSP|T))?\b",   4, EngineConfig.Rotary),
            (@"\bRenesis\b",                       4, EngineConfig.Rotary),
            (@"\b20B(?:[-\s]?REW)?\b",             6, EngineConfig.Rotary),
            (@"\b26B\b",                           8, EngineConfig.Rotary),
            // Ferrari V8 codenames — all flat-plane.
            (@"\bF13[046][A-Z]?\b",               8, EngineConfig.V8FlatPlane),  // F130 (288 GTO), F134, F136
            (@"\bF154[A-Z]?\b",                   8, EngineConfig.V8FlatPlane),  // 488 / GTC4 V8
            (@"\bF120[A-Z]?\b",                   8, EngineConfig.V8FlatPlane),  // F40
            // Cosworth DFV (Lotus 49) — flat-plane V8
            (@"\bDFV\b",                          8, EngineConfig.V8FlatPlane),
            // McLaren M838 / M840 — flat-plane V8
            (@"\bM83[78]T?\b",                    8, EngineConfig.V8FlatPlane),
            (@"\bM840T?\b",                       8, EngineConfig.V8FlatPlane),
            // AMG cross-plane V8s (M159, M177, M178)
            (@"\bM1[57][89]\b",                   8, EngineConfig.V8CrossPlane),
            (@"\bM177\b",                         8, EngineConfig.V8CrossPlane),
            // Ferrari V12 codenames — 60°
            (@"\bF14[01]\b",                      12, EngineConfig.V60),  // F140 (Enzo, LaFerrari, 812)
            (@"\bColombo\b",                      12, EngineConfig.V60),
            // Lamborghini V12 — 60°
            (@"\bL5(?:39|02|07)\b",               12, EngineConfig.V60),
        };

        // -1 sentinel = rotary (treated as cyl=4 in the cascade above).
        // Each chassis carries an EngineConfig so the chassis-fallback path
        // can pick a specific layout without re-deriving from brand.
        private static readonly (string Pat, int Cyl, EngineConfig Cfg)[] ChassisLookup = new (string, int, EngineConfig)[]
        {
            // BMW chassis codes — straight-6 by default
            (@"\bE3[06]\b|\bE3[06][_\s-]",       6, EngineConfig.Inline),
            (@"\bE36\b|\bE36[_\s-]",             6, EngineConfig.Inline),
            (@"\bE46\b|\bE46[_\s-]",             6, EngineConfig.Inline),
            (@"\bE9[02]\b|\bE9[02][_\s-]",       6, EngineConfig.Inline),
            (@"\bF8[02]\b|\bF8[02][_\s-]",       6, EngineConfig.Inline),
            (@"\bG8[02]\b|\bG8[02][_\s-]",       6, EngineConfig.Inline),
            // Nissan
            (@"\b350Z\b|\bfairlady[_\s]?350|\bz33\b",                    6, EngineConfig.V60),
            (@"\b370Z\b|\bz34\b",                                         6, EngineConfig.V60),
            (@"\b300Z\b|\bz3[12]\b",                                      6, EngineConfig.V60),
            (@"\b240sx\b",                                                4, EngineConfig.Inline),
            (@"\bskyline[_\s]?gtr?[_\s]?r3[234]|\bbnr3[24]\b|\bbcnr-?33\b|\br3[234][_\s-]?gtr?\b", 6, EngineConfig.Inline),
            (@"\b(?:r3[1234]|hr3[12]|hcr3[12])\b",                        6, EngineConfig.Inline),
            (@"\bs1[345]\b|\bsilvia[_\s]?s1[345]",                        4, EngineConfig.Inline),
            (@"\b180sx\b|\bsil80\b|\bsileighty\b",                        4, EngineConfig.Inline),
            (@"\blaurel\b|\bcefiro\b|\bstagea\b",                         6, EngineConfig.Inline),
            // Mazda
            (@"\bmiata\b|\bmx[-_\s]?5\b|\bmx5\b",                         4, EngineConfig.Inline),
            (@"\brx[-_\s]?[78]\b",                                       -1, EngineConfig.Rotary),
            // Subaru — flat-4 boxer
            (@"\bimpreza|\bwrx\b|\bsti\b|\bgrb\b|\bgda\b|\bgdb\b",        4, EngineConfig.Boxer),
            (@"\bbrz\b|\bgt86\b|\bft86\b|\b86\b",                         4, EngineConfig.Boxer),
            // Toyota
            (@"\bsupra[_\s]?(?:mk4|a80)|\bjza80\b",                       6, EngineConfig.Inline),
            (@"\bsupra[_\s]?(?:mk5|a90|gr)\b",                            6, EngineConfig.Inline),
            (@"\b(?:celica[_\s]?)?supra[_\s]?(?:mk2|a60)|\bma6[01]\b",    6, EngineConfig.Inline),
            (@"\bae86\b|\bcorolla[_\s]?levin|\btrueno",                   4, EngineConfig.Inline),
            (@"\bjzx[789]\d\b|\bcresta\b|\bchaser\b|\bsoarer\b|\bmark[_\s]?ii\b", 6, EngineConfig.Inline),
            // Mitsubishi
            (@"\b(?:lancer[_\s])?evo(?:lution)?[_\s]?(?:[ivx]+|\d+)\b|\bevo[_\s]?[ivx]+\b", 4, EngineConfig.Inline),
            // British / European
            (@"\bmini[_\s]?(?:cooper|hatch|clubman|countryman)\b|\baustin[_\s]?mini\b", 4, EngineConfig.Inline),
            // USDM — V8 cross-plane on muscle / trucks; Viper V10 even-fire 90°
            (@"\bviper\b|\brt[/_\s-]?10\b",                               10, EngineConfig.V90Even),
            (@"\bf-?150\b|\bsilverado\b|\bram[_\s]?(?:1500|2500)\b",       8, EngineConfig.V8CrossPlane),
            // Porsche flat-6 (boxer)
            (@"\b911\b|\bcayman\b|\bboxster\b|\b993\b|\b996\b|\b997\b|\b991\b|\b992\b|\b964\b", 6, EngineConfig.Boxer),
            // Honda
            (@"\bs2000\b|\bap[12]\b",                                     4, EngineConfig.Inline),
            (@"\bcivic\b|\bintegra\b|\btype[-_\s]?r\b",                   4, EngineConfig.Inline),
            (@"\bnsx\b",                                                  6, EngineConfig.V60),
            // Ford / Chevy / Dodge — usually V8 cross-plane in AC mods
            (@"\bmustang\b",                                              8, EngineConfig.V8CrossPlane),
            (@"\bcamaro\b|\bcorvette\b|\bvette\b|\bc[5678]\b",            8, EngineConfig.V8CrossPlane),
            (@"\bchallenger\b|\bcharger\b",                               8, EngineConfig.V8CrossPlane),
        };

        // Codename → EngineConfig table for the second-pass detector. Entries
        // here may overlap with EngineCodenames (which carries cyl + config
        // together for the cyl resolution path) — this table is what runs
        // when the cyl already came from a different source (e.g. a tag) but
        // a codename is still present in the description and signals layout.
        private static readonly (string Pat, EngineConfig Cfg)[] EngineConfigCodenames = new (string, EngineConfig)[]
        {
            (@"\bLS[1-9]X?\b",          EngineConfig.V8CrossPlane),
            (@"\bLT[1-5]\b",            EngineConfig.V8CrossPlane),
            (@"\bCoyote\b",             EngineConfig.V8CrossPlane),
            (@"\bHEMI\b",               EngineConfig.V8CrossPlane),
            (@"\bHellcat\b",            EngineConfig.V8CrossPlane),
            (@"\bM1[57][89]\b",         EngineConfig.V8CrossPlane),
            (@"\bM177\b",               EngineConfig.V8CrossPlane),
            (@"\bF13[046][A-Z]?\b",     EngineConfig.V8FlatPlane),
            (@"\bF154[A-Z]?\b",         EngineConfig.V8FlatPlane),
            (@"\bF120[A-Z]?\b",         EngineConfig.V8FlatPlane),
            (@"\bDFV\b",                EngineConfig.V8FlatPlane),
            (@"\bVoodoo\b",             EngineConfig.V8FlatPlane),
            (@"\bM83[78]T?\b",          EngineConfig.V8FlatPlane),
            (@"\bM840T?\b",             EngineConfig.V8FlatPlane),
            (@"\bMezger\b",             EngineConfig.Boxer),
            (@"\bEJ2[05][57]?\b",       EngineConfig.Boxer),
            (@"\bFA20\b",               EngineConfig.Boxer),
            (@"\b13B(?:[-\s]?(?:REW|MSP|T))?\b", EngineConfig.Rotary),
            (@"\bRenesis\b",            EngineConfig.Rotary),
            (@"\b20B(?:[-\s]?REW)?\b",  EngineConfig.Rotary),
            (@"\b26B\b",                EngineConfig.Rotary),
            (@"\bF14[01]\b",            EngineConfig.V60),
            (@"\bColombo\b",            EngineConfig.V60),
            (@"\bL5(?:39|02|07)\b",     EngineConfig.V60),
            (@"\bVQ3[57]\b",            EngineConfig.V60),
            (@"\bVR38\b",               EngineConfig.V60),
        };

        // ----- AC install root discovery (Steam VDF) -----

        public static string GetAcInstallRoot()
        {
            if (_acRootResolved) return _acRootCached;
            _acRootCached = FindAcInstallRoot();
            _acRootResolved = true;
            return _acRootCached;
        }

        private static string FindAcInstallRoot()
        {
            // 1. Find Steam install dir via registry (HKCU first, then HKLM).
            string steamPath = ReadRegistryString(Registry.CurrentUser,
                @"Software\Valve\Steam", "SteamPath");
            if (string.IsNullOrEmpty(steamPath))
            {
                steamPath = ReadRegistryString(Registry.LocalMachine,
                    @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            }
            if (string.IsNullOrEmpty(steamPath)) return null;

            // 2. Read libraryfolders.vdf and probe each "path" for AC.
            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) return TryDirectAcUnder(steamPath);

            string vdf;
            try { vdf = File.ReadAllText(vdfPath); }
            catch { return TryDirectAcUnder(steamPath); }

            // VDF has shape:
            //   "libraryfolders" { "0" { "path" "C:\\Steam" "apps" { "244210" "..." } } "1" {...} }
            // We want each "path" entry, then check it for AC.
            var paths = Regex.Matches(vdf, "\"path\"\\s*\"([^\"]+)\"");
            foreach (Match pm in paths)
            {
                string libPath = pm.Groups[1].Value.Replace("\\\\", "\\");
                string candidate = Path.Combine(libPath, "steamapps", "common", "assettocorsa");
                if (Directory.Exists(candidate)) return candidate;
            }

            return TryDirectAcUnder(steamPath);
        }

        private static string TryDirectAcUnder(string steamPath)
        {
            string candidate = Path.Combine(steamPath, "steamapps", "common", "assettocorsa");
            return Directory.Exists(candidate) ? candidate : null;
        }

        private static string ReadRegistryString(RegistryKey root, string subKey, string valueName)
        {
            try
            {
                using (var k = root.OpenSubKey(subKey))
                {
                    return k?.GetValue(valueName) as string;
                }
            }
            catch { return null; }
        }
    }
}
