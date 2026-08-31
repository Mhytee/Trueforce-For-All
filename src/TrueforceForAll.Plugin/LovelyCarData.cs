// Parsing for the community-maintained lovely-car-data dataset
// (github.com/Lovely-Sim-Racing/lovely-car-data, CC BY-NC-SA 4.0), which
// carries per-car rev-light data keyed by SimHub's own game and car ids.
//
// Scope note: the format is RPM ONLY. It has no pit limiter, flag, DRS or any
// other light state, so nothing here should grow a notion of one. A per-car
// state-LED schema (statusLeds) exists upstream on unmerged PRs and carries a
// single event (hybridDeploy); if that ever lands and widens, it is a new
// parse path, not a change to this one.
//
// Everything here is pure: no WPF, no SimHub, no network, Newtonsoft only, so
// the file link-compiles into TrueforceForAll.Core.Tests the same way
// InstalledPacksStore.cs does.
//
// TOLERANCE IS DELIBERATE. This is third-party data fetched from the internet
// and it is measurably imperfect (2026-08-17 census of all 717 files: 6 files
// break the dataset's own ledColor length rule, 2 color values are malformed
// hex, 17 gear ramps carry two or fewer distinct thresholds). A car we cannot
// read must degrade to "no data, use the built-in ramp", never to an
// exception on a telemetry thread.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>One gear's light ramp. <see cref="RedlineRpm"/> is that gear's
    /// own redline (the dataset stores it as element 0 of the gear array) and
    /// <see cref="Thresholds"/> holds the per-LED switch-on RPMs in dash order,
    /// dead slots included as 0 so LED positions stay aligned with the color
    /// array.</summary>
    public sealed class LovelyGearRamp
    {
        public int RedlineRpm { get; set; }
        public int[] Thresholds { get; set; } = new int[0];

        /// <summary>Switch-on RPMs with the dash's physical gaps removed, low to
        /// high. This is what the level mapping counts against: a dead slot is a
        /// hole in the bar, not a step in the sequence.</summary>
        public int[] ActiveThresholds =>
            (Thresholds ?? new int[0]).Where(t => t > 0).OrderBy(t => t).ToArray();

        /// <summary>The car publishes enough switch-on points to render its own
        /// curve, so we use them exactly as given.</summary>
        public bool IsFaithful => ActiveThresholds.Distinct().Count() >= 3;

        /// <summary>Whether this ramp can drive the strip at all: either it is
        /// faithful, or it at least tells us where the lights start and where the
        /// redline is, which is enough to fill the gap ourselves.</summary>
        public bool IsUsable
        {
            get
            {
                if (IsFaithful) return true;
                var a = ActiveThresholds;
                return a.Length >= 1 && RedlineRpm > a[0];
            }
        }

        /// <summary>The switch-on points to actually render, given a strip of
        /// <paramref name="steps"/> levels.
        ///
        /// A car with three or more distinct points gets its own curve untouched:
        /// that nonlinear shape is the whole reason to consume this dataset.
        ///
        /// Coarser entries are FILLED IN rather than reproduced. Six distinct
        /// datasets upstream publish one or two points (nine of the files are
        /// livery duplicates of a single BMW entry), and they are approximations
        /// rather than real dashes: the same physical BMW M4 GT3 carries five
        /// progressive points in iRacing's entry and two in LMU's and AC Evo's,
        /// and a real dash does not change between sims. Reproducing them costs
        /// real shift-timing resolution for nothing (iRacing's NASCAR stock cars
        /// publish a single point covering a 2100 rpm band, which would leave the
        /// strip inert for 2000 rpm and then slam it full). So we keep both facts
        /// the entry genuinely establishes, where the lights start and where the
        /// redline sits, and spread the levels evenly between them.</summary>
        public int[] RenderThresholds(int steps)
        {
            var active = ActiveThresholds;
            if (IsFaithful) return active;
            if (steps <= 0 || !IsUsable) return new int[0];

            int lo = active[0], hi = RedlineRpm;
            if (steps == 1) return new[] { lo };

            var synth = new int[steps];
            for (int i = 0; i < steps; i++)
                synth[i] = lo + (int)Math.Round((double)(hi - lo) * i / (steps - 1));
            return synth;
        }
    }

    /// <summary>One car's rev-light data as published upstream.</summary>
    public sealed class LovelyCarProfile
    {
        public string CarName { get; set; }
        public string CarId { get; set; }
        public string CarClass { get; set; }

        /// <summary>Physical telemetry LEDs on the car's own dash. Not our
        /// wheel's strip length, which is read from the wheel at runtime.</summary>
        public int LedNumber { get; set; }

        /// <summary>Redline blink period in milliseconds. 0 means the car does
        /// not blink (470 of 717 files), NOT "use a default".</summary>
        public int RedlineBlinkIntervalMs { get; set; }

        /// <summary>Per-LED colors in dash order, dead slots included as null.
        /// The dataset's leading redline color is split off into
        /// <see cref="RedlineColor"/>, so index 0 here is the car's first LED.</summary>
        public LovelyColor[] LedColors { get; set; } = new LovelyColor[0];

        /// <summary>The color the dash flashes at redline (element 0 of the
        /// published color array). Null when absent or unparseable.</summary>
        public LovelyColor? RedlineColor { get; set; }

        /// <summary>Gear key to ramp. Keys are the dataset's own: "R", "N" and
        /// "1".."8". Case-sensitive on purpose, matching upstream.</summary>
        public Dictionary<string, LovelyGearRamp> Gears { get; set; }
            = new Dictionary<string, LovelyGearRamp>(StringComparer.Ordinal);

        /// <summary>The ramp for a gear named the way SimHub names it ("R", "N",
        /// "1".., matching this codebase's existing convention). The dataset keys
        /// its gears the same way, so the common case is a direct lookup.
        ///
        /// Fallbacks are split by DIRECTION rather than lumped together, because
        /// the two mean opposite things. A forward gear higher than the car
        /// publishes (a game reporting an eighth on a six-speed, or a gearbox with
        /// more ratios than whoever authored the entry allowed for) should use the
        /// car's top gear. A reverse or neutral the car does not publish must NOT:
        /// borrowing top gear there would light the strip off high-gear thresholds
        /// while reversing. It returns nothing instead, and the built-in ramp
        /// takes over.
        ///
        /// Reverse is matched by prefix so multi-reverse gearboxes work. Farming
        /// Simulator and truck sims report several reverse ratios ("R1", "R2"),
        /// which no exact match would catch; they all fold onto the dataset's
        /// single "R".</summary>
        public LovelyGearRamp RampForGear(string gear)
        {
            if (Gears == null || Gears.Count == 0) return null;

            string key = NormalizeGearKey(gear);
            if (key == null) return HighestForwardGear();   // unknown: assume driving

            LovelyGearRamp hit;
            if (Gears.TryGetValue(key, out hit)
                && hit != null && (hit.IsUsable || hit.RedlineRpm > 0))
                return hit;

            bool forward = key.Length > 0 && key[0] >= '1' && key[0] <= '9';
            return forward ? HighestForwardGear() : null;
        }

        /// <summary>Fold a game's gear name onto the dataset's vocabulary
        /// ("R", "N", "1".."n"). Null means we cannot tell.</summary>
        internal static string NormalizeGearKey(string gear)
        {
            if (string.IsNullOrWhiteSpace(gear)) return null;
            string g = gear.Trim();

            int n;
            if (int.TryParse(g, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                if (n >= 1) return n.ToString(CultureInfo.InvariantCulture);
                // 0 is neutral by this codebase's convention (see
                // RevLimiterEffect.ParseForwardGear); negatives are reverse
                // ratios in the sims that number them that way.
                return n == 0 ? "N" : "R";
            }

            char c = char.ToUpperInvariant(g[0]);
            if (c == 'R') return "R";   // R, R1, R2, REVERSE
            if (c == 'N') return "N";
            return null;
        }

        /// <summary>The ramp to render in <paramref name="gear"/>, falling back to
        /// the highest defined forward gear when that gear is missing or unknown
        /// (an 8-speed car in a game reporting gear 9, a gear we never saw). Null
        /// when the profile carries no usable ramp at all.</summary>
        public LovelyGearRamp RampForGear(int gear)
        {
            if (Gears == null || Gears.Count == 0) return null;

            // The gear's own entry wins whenever it exists, even if its ramp is too
            // coarse to drive the strip: its redline is still this gear's real
            // shift point, and borrowing another gear's would move the blink.
            LovelyGearRamp direct;
            if (gear > 0 && Gears.TryGetValue(gear.ToString(CultureInfo.InvariantCulture), out direct)
                && direct != null && (direct.IsUsable || direct.RedlineRpm > 0))
                return direct;

            // Neutral and reverse have their own published ramps; use them when
            // they are real, but never let them stand in for a missing forward
            // gear (their thresholds describe a stationary car).
            if (gear <= 0)
            {
                string key = gear == 0 ? "N" : "R";
                LovelyGearRamp rn;
                if (Gears.TryGetValue(key, out rn) && rn != null && rn.IsUsable) return rn;
            }

            return HighestForwardGear();
        }

        /// <summary>Published redlines for the forward gears, keyed by gear
        /// number, for feeding the rev-limiter's redline cascade. Reverse and
        /// neutral are excluded: their published ramps describe a stationary car
        /// and say nothing about a shift point. Empty when nothing is published.</summary>
        public Dictionary<int, int> ForwardGearRedlines()
        {
            var map = new Dictionary<int, int>();
            if (Gears == null) return map;
            foreach (var kv in Gears)
            {
                int n;
                if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                if (n < 1 || kv.Value == null) continue;
                if (kv.Value.RedlineRpm > 0) map[n] = kv.Value.RedlineRpm;
            }
            return map;
        }

        /// <summary>Highest numbered forward gear that carries a usable ramp.</summary>
        public LovelyGearRamp HighestForwardGear()
        {
            LovelyGearRamp best = null;
            int bestNum = int.MinValue;
            foreach (var kv in Gears)
            {
                int n;
                if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                if (kv.Value == null || !kv.Value.IsUsable) continue;
                if (n > bestNum) { bestNum = n; best = kv.Value; }
            }
            return best;
        }

        /// <summary>True when at least one gear carries a ramp that can drive the
        /// strip.</summary>
        public bool HasUsableRamp => Gears != null && Gears.Values.Any(g => g != null && g.IsUsable);

        /// <summary>True when the car is worth keeping at all. Deliberately wider
        /// than <see cref="HasUsableRamp"/>: a published redline is valuable on its
        /// own (it is this gear's real shift point, and no sim telemetry gives us
        /// one per gear), so a car whose ramp is too coarse to render is still kept
        /// for its redlines rather than discarded whole.</summary>
        public bool HasAnyUsefulData => Gears != null
            && Gears.Values.Any(g => g != null && (g.IsUsable || g.RedlineRpm > 0));
    }

    /// <summary>A color as the dataset publishes it. Kept as components rather
    /// than a UI color type so this file stays free of WPF and testable on
    /// net8.</summary>
    public struct LovelyColor
    {
        public byte A, R, G, B;

        public LovelyColor(byte a, byte r, byte g, byte b) { A = a; R = r; G = g; B = b; }

        /// <summary>Fully transparent entries mark a physical gap in the dash
        /// bar, and line up with a 0 threshold at the same index.</summary>
        public bool IsGap => A == 0;

        public static LovelyColor Rgb(byte r, byte g, byte b) => new LovelyColor(0xFF, r, g, b);

        public override string ToString() =>
            "#" + A.ToString("X2") + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");
    }

    /// <summary>Turns SimHub's game and car ids into the dataset's file names.
    /// This is a direct port of the nameCleaner reference implementation in the
    /// upstream README: the published contract is that a consumer derives both
    /// path segments itself, so there is no game-name mapping table to keep in
    /// sync.</summary>
    public static class LovelyNameCleaner
    {
        /// <summary>Lowercase, strip accents to their ASCII base, turn every
        /// run of non-alphanumeric characters into a single hyphen, and trim
        /// hyphens off both ends.</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            // Decompose so accented letters split into base letter plus combining
            // mark, then drop the marks: "Nürburgring" becomes "nurburgring", which
            // is what upstream's iconv//TRANSLIT step produces.
            string decomposed = raw.Normalize(NormalizationForm.FormD);

            var sb = new StringBuilder(decomposed.Length);
            bool pendingHyphen = false;
            foreach (char ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

                char lower = char.ToLowerInvariant(ch);
                bool alnum = (lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9');
                if (alnum)
                {
                    // Collapse runs and drop leading hyphens by only emitting a
                    // separator once a real character follows it.
                    if (pendingHyphen && sb.Length > 0) sb.Append('-');
                    pendingHyphen = false;
                    sb.Append(lower);
                }
                else
                {
                    pendingHyphen = true;
                }
            }
            return sb.ToString();
        }

        /// <summary>Repository-relative path for a car, e.g.
        /// "iracing/bmwm4gt3.json". Empty when either id cleans to nothing.</summary>
        public static string RelativePath(string game, string carId)
        {
            string g = Clean(game), c = Clean(carId);
            if (g.Length == 0 || c.Length == 0) return string.Empty;
            return g + "/" + c + ".json";
        }
    }

    /// <summary>Reads one published car file. Every failure mode returns null
    /// rather than throwing: this runs behind a telemetry-driven lookup and a
    /// malformed upstream file must never surface as a crash.</summary>
    public static class LovelyCarDataParser
    {
        /// <summary>Parse a car document. Returns null for anything we cannot
        /// use, including a document that parses cleanly but carries no usable
        /// ramp.</summary>
        public static LovelyCarProfile Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JObject root;
            try
            {
                // SafeJson caps nesting depth; this is untrusted network content
                // and gets the same guard as imported presets and packs.
                root = JsonConvert.DeserializeObject<JObject>(json, SafeJson.Settings);
            }
            catch { return null; }
            if (root == null) return null;

            var profile = new LovelyCarProfile
            {
                CarName  = (string)root["carName"],
                CarId    = (string)root["carId"],
                CarClass = (string)root["carClass"],
            };

            profile.LedNumber              = ReadInt(root["ledNumber"], 0);
            profile.RedlineBlinkIntervalMs = Math.Max(0, ReadInt(root["redlineBlinkInterval"], 0));

            ReadColors(root["ledColor"] as JArray, profile);
            ReadGears(root["ledRpm"], profile);

            return profile.HasAnyUsefulData ? profile : null;
        }

        // ledColor leads with the redline/blink color and then runs led1..ledN,
        // so a well-formed file holds ledNumber + 1 entries. Six files upstream
        // break that rule, so the count is never trusted: we split element 0 off
        // and take whatever remains, letting the threshold array decide how many
        // LEDs actually matter.
        private static void ReadColors(JArray arr, LovelyCarProfile profile)
        {
            if (arr == null || arr.Count == 0) return;

            profile.RedlineColor = ParseColor((string)arr[0]);

            var leds = new List<LovelyColor>(Math.Max(0, arr.Count - 1));
            for (int i = 1; i < arr.Count; i++)
            {
                var c = ParseColor((string)arr[i]);
                // An unparseable entry becomes a gap rather than dropping the
                // position, so color indices stay aligned with threshold indices.
                leds.Add(c ?? new LovelyColor(0, 0, 0, 0));
            }
            profile.LedColors = leds.ToArray();
        }

        // ledRpm is published as an array holding a single object of gear keys.
        // Accept the bare object too: it costs one branch and survives an
        // upstream format tidy-up that drops the redundant wrapper.
        private static void ReadGears(JToken ledRpm, LovelyCarProfile profile)
        {
            JObject gears = ledRpm as JObject;
            if (gears == null)
            {
                var arr = ledRpm as JArray;
                if (arr != null && arr.Count > 0) gears = arr[0] as JObject;
            }
            if (gears == null) return;

            foreach (var prop in gears.Properties())
            {
                var values = prop.Value as JArray;
                if (values == null || values.Count < 2) continue;

                var ramp = new LovelyGearRamp { RedlineRpm = ReadInt(values[0], 0) };

                var thresholds = new int[values.Count - 1];
                for (int i = 1; i < values.Count; i++)
                {
                    int t = ReadInt(values[i], 0);
                    thresholds[i - 1] = t > 0 ? t : 0;   // negatives are as meaningless as 0
                }
                ramp.Thresholds = thresholds;

                // A gear whose redline is missing or nonsense still has usable
                // switch-on points; recover it from the highest of them so the
                // blink point is not lost with it.
                if (ramp.RedlineRpm <= 0)
                {
                    var active = ramp.ActiveThresholds;
                    ramp.RedlineRpm = active.Length > 0 ? active[active.Length - 1] : 0;
                }

                profile.Gears[prop.Name] = ramp;
            }
        }

        private static int ReadInt(JToken t, int fallback)
        {
            if (t == null) return fallback;
            try
            {
                if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (int)t;
                int parsed;
                if (t.Type == JTokenType.String &&
                    int.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            catch { }
            return fallback;
        }

        /// <summary>Parse one published color. The format permits "#AARRGGBB",
        /// "#RRGGBB" and the HTML color names, and separately contains two
        /// malformed values (a 6-digit and a 7-digit string behind an 8-digit
        /// convention), so anything that does not parse cleanly returns null and
        /// the caller treats the slot as a gap.</summary>
        public static LovelyColor? ParseColor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();

            if (s[0] == '#')
            {
                string hex = s.Substring(1);
                // Only the two documented widths. The malformed 7-digit value
                // upstream would otherwise silently parse as a shifted color.
                if (hex.Length != 8 && hex.Length != 6) return null;
                foreach (char c in hex)
                    if (!Uri.IsHexDigit(c)) return null;

                uint v;
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                    return null;

                if (hex.Length == 6)
                    return new LovelyColor(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
                return new LovelyColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }

            LovelyColor named;
            if (HtmlColorNames.TryGetValue(s, out named)) return named;
            return null;
        }

        // The dataset's published color vocabulary is hex in practice (all 20
        // distinct values as of the 2026-08-17 census), but the format also
        // permits HTML color names, so the ones a rev strip plausibly uses are
        // resolved here. An unlisted name is treated as unreadable, not guessed.
        private static readonly Dictionary<string, LovelyColor> HtmlColorNames =
            new Dictionary<string, LovelyColor>(StringComparer.OrdinalIgnoreCase)
            {
                { "red",         LovelyColor.Rgb(0xFF, 0x00, 0x00) },
                { "green",       LovelyColor.Rgb(0x00, 0x80, 0x00) },
                { "lime",        LovelyColor.Rgb(0x00, 0xFF, 0x00) },
                { "blue",        LovelyColor.Rgb(0x00, 0x00, 0xFF) },
                { "yellow",      LovelyColor.Rgb(0xFF, 0xFF, 0x00) },
                { "orange",      LovelyColor.Rgb(0xFF, 0xA5, 0x00) },
                { "darkorange",  LovelyColor.Rgb(0xFF, 0x8C, 0x00) },
                { "white",       LovelyColor.Rgb(0xFF, 0xFF, 0xFF) },
                { "black",       LovelyColor.Rgb(0x00, 0x00, 0x00) },
                { "magenta",     LovelyColor.Rgb(0xFF, 0x00, 0xFF) },
                { "fuchsia",     LovelyColor.Rgb(0xFF, 0x00, 0xFF) },
                { "cyan",        LovelyColor.Rgb(0x00, 0xFF, 0xFF) },
                { "aqua",        LovelyColor.Rgb(0x00, 0xFF, 0xFF) },
                { "cornflowerblue", LovelyColor.Rgb(0x64, 0x95, 0xED) },
                { "deepskyblue", LovelyColor.Rgb(0x00, 0xBF, 0xFF) },
                { "skyblue",     LovelyColor.Rgb(0x87, 0xCE, 0xEB) },
                { "gray",        LovelyColor.Rgb(0x80, 0x80, 0x80) },
                { "grey",        LovelyColor.Rgb(0x80, 0x80, 0x80) },
                { "silver",      LovelyColor.Rgb(0xC0, 0xC0, 0xC0) },
                { "purple",      LovelyColor.Rgb(0x80, 0x00, 0x80) },
                { "pink",        LovelyColor.Rgb(0xFF, 0xC0, 0xCB) },
            };
    }
}
