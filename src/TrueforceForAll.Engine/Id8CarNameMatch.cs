// Matching TeknoParrot's car strings onto the game's CarIDs.
//
// Their board names a car as free text, "MAZDA RX-7 III (FC3S)", where the game knows it as CarID
// 768, "RX-7 (infinity)III (FC3S)". Getting from one to the other is what lets their times reach the
// per-car leaderboards; without it TeknoParrot can only fill the any-car boards.
//
// The two spellings differ in ways that are individually small and collectively fatal to a naive
// compare:
//   * they prefix the maker, we do not:      "TOYOTA TRUENO GT-APEX" vs "TRUENO GT-APEX"
//   * roman numerals: they use ASCII, the game uses the single-character forms U+2160..U+2169
//   * the tuned cars are "JZA80 Kai" to them and "JZA80" plus U+6539 to the game
//   * HTML entities, since this comes out of a web page: "K&#x27;s" for "K's"
//   * one car is named in Japanese by the game and romanised by them
//
// So the chassis code in the trailing parentheses does the work, and the model text only breaks
// ties. That ordering matters: the chassis code is nearly unique and stable, while the model text
// is where every one of the differences above lives.
//
// Verified against the live page: all 47 cars TeknoParrot lists resolve, and the three of ours that
// do not appear (RX-8 Type S, Impreza Ver.V, BRZ) are genuinely absent from their board.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TrueforceForAll.Core
{
    public static class Id8CarNameMatch
    {
        /// <summary>CarID for one of TeknoParrot's car strings, or -1 when it is not one we know.
        ///
        /// -1 rather than a guess: a wrong car here writes a real time onto the wrong car's board,
        /// which looks entirely normal on screen and would never be reported as a bug.</summary>
        public static int CarIdFor(string siteCarName)
        {
            if (string.IsNullOrEmpty(siteCarName)) return -1;

            string chassis = Chassis(siteCarName);
            if (chassis.Length == 0) return MatchByName(Normalise(siteCarName), null);

            List<int> byChassis;
            if (!ByChassis.Value.TryGetValue(chassis, out byChassis)) return -1;
            if (byChassis.Count == 1) return byChassis[0];

            // Several cars share the chassis (three on AE86, three on FD3S), so the model text has
            // to separate them.
            return MatchByName(Normalise(siteCarName), byChassis);
        }

        private static int MatchByName(string wanted, List<int> candidates)
        {
            if (wanted.Length == 0) return -1;
            IEnumerable<int> pool = candidates ?? (IEnumerable<int>)Id8CarTable.CarIdsInSlotOrder();

            int exact = -1, exactCount = 0;
            int partial = -1, partialCount = 0;
            foreach (int id in pool)
            {
                Id8Car car = Id8CarTable.Find(id);
                if (car == null) continue;
                string mine = Normalise(car.Name);
                if (mine.Length == 0) continue;

                if (mine == wanted) { exact = id; exactCount++; }
                else if (wanted.IndexOf(mine, StringComparison.Ordinal) >= 0 ||
                         mine.IndexOf(wanted, StringComparison.Ordinal) >= 0)
                { partial = id; partialCount++; }
            }

            if (exactCount == 1) return exact;
            // An ambiguous partial is worse than none: it would silently pick whichever came last.
            if (exactCount == 0 && partialCount == 1) return partial;
            return -1;
        }

        /// <summary>The chassis code from the trailing parentheses, normalised.</summary>
        private static string Chassis(string name)
        {
            string s = DecodeEntities(name);
            int close = s.LastIndexOf(')');
            if (close < 0) return "";
            int open = s.LastIndexOf('(', close);
            if (open < 0 || close <= open + 1) return "";
            return Normalise(s.Substring(open + 1, close - open - 1));
        }

        private static readonly string[] Makers =
        {
            // Longest first, so "COMPLETE CAR" is not left as "CAR" by a shorter match.
            "COMPLETE CAR", "MITSUBISHI", "INITIAL D", "SUBARU", "SUZUKI", "TOYOTA", "NISSAN",
            "HONDA", "MAZDA",
        };

        /// <summary>Reduce a name to the letters and digits both sides agree on.</summary>
        internal static string Normalise(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = DecodeEntities(name).ToUpperInvariant();

            foreach (string maker in Makers)
                if (s.StartsWith(maker + " ", StringComparison.Ordinal))
                {
                    s = s.Substring(maker.Length + 1);
                    break;
                }

            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                // Single-character roman numerals to their ASCII spelling. The game uses these and
                // the web page does not, so without this every Lancer and the 180SX fail to match.
                if (c >= 'Ⅰ' && c <= 'Ⅹ') { sb.Append(RomanAscii[c - 'Ⅰ']); continue; }
                if (c == '改') { sb.Append("KAI"); continue; }   // 改, the tuned suffix
                if (char.IsLetterOrDigit(c) && c < 0x80) sb.Append(c);
                // Everything else, including the infinity sign and Japanese model names, is dropped:
                // the chassis code has already done the identifying.
            }
            return sb.ToString();
        }

        private static readonly string[] RomanAscii =
            { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

        /// <summary>The few HTML entities their markup actually uses.</summary>
        private static string DecodeEntities(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s ?? "";
            return s.Replace("&#x27;", "'").Replace("&#39;", "'").Replace("&apos;", "'")
                    .Replace("&amp;", "&").Replace("&quot;", "\"")
                    .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&nbsp;", " ");
        }

        private static readonly Lazy<Dictionary<string, List<int>>> ByChassis =
            new Lazy<Dictionary<string, List<int>>>(BuildChassisIndex);

        private static Dictionary<string, List<int>> BuildChassisIndex()
        {
            var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int id in Id8CarTable.CarIdsInSlotOrder())
            {
                Id8Car car = Id8CarTable.Find(id);
                if (car == null) continue;
                string chassis = Chassis(car.Name);
                if (chassis.Length == 0) continue;

                List<int> list;
                if (!map.TryGetValue(chassis, out list)) map[chassis] = list = new List<int>();
                list.Add(id);
            }
            return map;
        }
    }
}
