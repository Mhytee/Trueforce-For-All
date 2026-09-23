// The TeknoParrot community leaderboard for an arcade cabinet, as published on their website.
//
// Why read someone else's board rather than build our own: theirs is already populated. The Initial
// D 8 board carries around ten thousand submissions across thirty-two course boards, and TeknoParrot
// submits to it itself when a user turns that on in the game's own settings. A leaderboard is worth
// what its density is worth, so a parallel one of ours would start empty and stay thin. What nobody
// has done is show those times where the cabinet shows times, which needs no storage from us at all.
//
// This file is PARSING ONLY. It takes a page of HTML and returns rows. It opens no sockets, so it
// can be tested against a saved copy of the page with no network and no rig, and whoever fetches
// gets to own the politeness: identifying the plugin, caching, and asking rarely.
//
// The markup is theirs and can change without warning. Every method here is written to return what
// it could read rather than to throw, because a leaderboard that quietly shows nothing is a far
// better failure than one that takes a display down with it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TrueforceForAll.Core
{
    /// <summary>One row of a course board.</summary>
    public sealed class LeaderboardEntry
    {
        /// <summary>Position on that board, as published. 1 is the record.</summary>
        public int Rank;

        /// <summary>The player's name on TeknoParrot, which is their site profile name rather than
        /// the name on the cabinet's card.</summary>
        public string Player;

        /// <summary>The car as the site writes it, e.g. "MAZDA RX-7 III (FC3S)". Deliberately kept
        /// verbatim: matching it to our own car table is a separate job with its own failure mode,
        /// and a name we cannot match is still a name worth showing.</summary>
        public string Car;

        /// <summary>The time exactly as published, e.g. "2:11:053". Kept alongside the parsed
        /// value so a display can show what the site shows without reformatting it.</summary>
        public string TimeText;

        /// <summary>The parsed time, or null when it could not be read. Their format is
        /// minutes:seconds:milliseconds, which is NOT what TimeSpan.Parse expects.</summary>
        public TimeSpan? Time;

        /// <summary>When it was submitted, or null when unreadable. Day-first, their format.</summary>
        public DateTime? Submitted;

        /// <summary>Their id for the entry, for linking back to the detail page. 0 when absent.</summary>
        public int EntryId;
    }

    /// <summary>One course board: a course, a direction, and its rows in published order.</summary>
    public sealed class LeaderboardBoard
    {
        /// <summary>The site's own key, e.g. "Akagi_Downhill". Their identifier, kept as the
        /// dictionary key so nothing depends on our prettified version of it.</summary>
        public string Key;

        /// <summary>The course without its direction, e.g. "Akagi", "Lake Akina".</summary>
        public string Course;

        /// <summary>The direction as the site names it: Downhill, Hill Climb, Inbound, Outbound,
        /// Clockwise, Counterclockwise, or Reverse for Irohazaka.</summary>
        public string Direction;

        public List<LeaderboardEntry> Entries = new List<LeaderboardEntry>();
    }

    /// <summary>Reads a TeknoParrot game highscore page into course boards.</summary>
    public static class TeknoParrotLeaderboard
    {
        /// <summary>The page a game's boards live on. The game id is TeknoParrot's own profile
        /// name, which is the same string the cabinet's UserProfiles xml is named after, so ID8
        /// here is not a coincidence and does not need its own mapping table.</summary>
        public static string PageUrl(string teknoParrotGameId)
        {
            if (string.IsNullOrEmpty(teknoParrotGameId)) return null;
            return "https://teknoparrot.com/en/Highscore/GameSpecific/" + Uri.EscapeDataString(teknoParrotGameId);
        }

        // Each course board is a tbody the site ids by course. Anchoring on that id rather than on
        // table order means a new course, or a reordering, costs us nothing.
        private static readonly Regex BoardRx = new Regex(
            "<tbody\\s+id=\"track-records-(?<key>[^\"]+)\"\\s*>(?<body>.*?)</tbody>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RowRx = new Regex(
            "<tr\\b.*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RankRx = new Regex(
            "class=\"rank-col\"\\s*>(?<v>[^<]*)<", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PlayerRx = new Regex(
            "highscore-player-link\"[^>]*>(?<v>[^<]*)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CarRx = new Regex(
            "class=\"detail-col\"\\s*>(?<v>[^<]*)<", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TimeRx = new Regex(
            "class=\"time-col\"\\s*>(?<v>[^<]*)<", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DateRx = new Regex(
            "class=\"date-single-line[^\"]*\"\\s*>(?<v>[^<]*)<", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EntryIdRx = new Regex(
            "entryId=(?<v>\\d+)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Every course board on the page, keyed by the site's own course key. Returns an
        /// empty dictionary rather than throwing for anything it cannot make sense of, including a
        /// page that is not a leaderboard at all: an error page, a login wall or a redirect all
        /// simply produce no boards.</summary>
        public static Dictionary<string, LeaderboardBoard> Parse(string html)
        {
            var boards = new Dictionary<string, LeaderboardBoard>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(html)) return boards;

            foreach (Match m in BoardRx.Matches(html))
            {
                string key = m.Groups["key"].Value;
                if (string.IsNullOrEmpty(key)) continue;

                var board = new LeaderboardBoard { Key = key };
                SplitCourse(key, out board.Course, out board.Direction);

                foreach (Match r in RowRx.Matches(m.Groups["body"].Value))
                {
                    var entry = ParseRow(r.Value);
                    if (entry != null) board.Entries.Add(entry);
                }

                // A board with no readable rows is still a board: the course exists and is simply
                // empty, which a display should be able to say.
                boards[key] = board;
            }
            return boards;
        }

        private static LeaderboardEntry ParseRow(string row)
        {
            string player = First(PlayerRx, row);
            string time = First(TimeRx, row);
            // A row with neither a driver nor a time is a header or a spacer, not a result.
            if (string.IsNullOrEmpty(player) && string.IsNullOrEmpty(time)) return null;

            var e = new LeaderboardEntry
            {
                Player = player,
                Car = First(CarRx, row),
                TimeText = time,
                Time = ParseTime(time),
                Submitted = ParseDate(First(DateRx, row)),
            };

            int rank;
            if (int.TryParse(First(RankRx, row), NumberStyles.Integer, CultureInfo.InvariantCulture, out rank))
                e.Rank = rank;
            int id;
            if (int.TryParse(First(EntryIdRx, row), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                e.EntryId = id;
            return e;
        }

        private static string First(Regex rx, string text)
        {
            var m = rx.Match(text);
            return m.Success ? Decode(m.Groups["v"].Value.Trim()) : null;
        }

        /// <summary>The handful of entities a name or a car can actually contain. A full HTML
        /// decoder is not worth a dependency here, and an unrecognised entity surviving into a
        /// display is a cosmetic problem rather than a wrong result.</summary>
        private static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s;
            return s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
        }

        /// <summary>Their times read "2:11:053", which is minutes:seconds:milliseconds and NOT
        /// anything TimeSpan.Parse accepts, since it would read the last part as a further unit.
        /// Also handles "11:053" for a sub-minute time and "1:02:11:053" should an hour ever
        /// appear. Returns null rather than a wrong duration.</summary>
        public static TimeSpan? ParseTime(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text.Trim().Split(':');
            if (parts.Length < 2 || parts.Length > 4) return null;

            var n = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]))
                    return null;

            // The last part is always milliseconds, so the ones before it are read from the right:
            // seconds, then minutes, then hours.
            int ms = n[parts.Length - 1];
            int seconds = parts.Length >= 2 ? n[parts.Length - 2] : 0;
            int minutes = parts.Length >= 3 ? n[parts.Length - 3] : 0;
            int hours = parts.Length >= 4 ? n[parts.Length - 4] : 0;
            if (ms < 0 || ms > 999 || seconds < 0 || seconds > 59 || minutes < 0 || minutes > 59) return null;
            return new TimeSpan(0, hours, minutes, seconds, ms);
        }

        /// <summary>Day-first, as the site writes it: "27-04-2025 22:37:49". Parsed with an
        /// explicit format rather than the machine's locale, which would read that as December on
        /// a US install and silently produce a date a year adrift.</summary>
        public static DateTime? ParseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] formats = { "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy" };
            DateTime dt;
            if (DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt))
                return dt;
            return null;
        }

        // The direction suffixes the site uses, longest first so "Hill_Climb" is not mistaken for
        // something shorter and so "Downhill" cannot swallow part of another word.
        private static readonly string[] Directions =
        {
            "Counterclockwise", "Clockwise", "Hill_Climb", "Downhill", "Outbound", "Inbound", "Reverse",
        };

        /// <summary>Split "Akagi_Downhill" into its course and its direction. A key with no
        /// direction we recognise keeps the whole thing as the course and an empty direction,
        /// which is what a new course type should do rather than being dropped.</summary>
        public static void SplitCourse(string key, out string course, out string direction)
        {
            course = (key ?? "").Replace('_', ' ').Trim();
            direction = "";
            if (string.IsNullOrEmpty(key)) return;

            foreach (string d in Directions)
            {
                if (!key.EndsWith("_" + d, StringComparison.OrdinalIgnoreCase)) continue;
                course = key.Substring(0, key.Length - d.Length - 1).Replace('_', ' ').Trim();
                direction = d.Replace('_', ' ');
                return;
            }
        }

        /// <summary>Convert a parsed site board into the entry shape the in-game boards are built
        /// from. Rows with no readable time are dropped: a leaderboard row without a time is not a
        /// row, and writing a zero would sort it to the top of the board.</summary>
        public static List<Id8LeaderboardEntry> ToId8Entries(LeaderboardBoard board)
        {
            var outList = new List<Id8LeaderboardEntry>();
            if (board == null || board.Entries == null) return outList;

            foreach (LeaderboardEntry e in board.Entries)
            {
                if (e == null || !e.Time.HasValue) continue;
                double ms = e.Time.Value.TotalMilliseconds;
                if (ms <= 0 || ms >= Id8LeaderboardRecord.DefaultGoalMs) continue;

                uint stamp = 0;
                if (e.Submitted.HasValue && e.Submitted.Value.Year > 1970)
                {
                    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    double secs = (e.Submitted.Value.ToUniversalTime() - epoch).TotalSeconds;
                    if (secs > 0 && secs < uint.MaxValue) stamp = (uint)secs;
                }

                outList.Add(new Id8LeaderboardEntry
                {
                    Username = e.Player,
                    // Their car is free text, so it goes through the same matcher the per-car
                    // path uses. An unmatched string leaves 0, which the game reads as the AE86
                    // Trueno; that is wrong but it is the pre-existing behaviour for every row,
                    // and the matcher resolved all 1762 rows when it was measured.
                    CarId = Id8CarNameMatch.CarIdFor(e.Car),
                    GoalMs = (int)ms,
                    UnixTime = stamp,
                });
            }
            return outList;
        }

        /// <summary>The best time per car on one board, keyed by CarID.
        ///
        /// The game's per-car boards hold exactly one record per car, so a ranked list has to be
        /// collapsed to its fastest entry for each. Rows whose car cannot be identified are dropped
        /// rather than guessed at: putting a real time on the wrong car's board looks entirely
        /// normal on screen and would never be reported.</summary>
        public static Dictionary<int, Id8LeaderboardEntry> ToId8CarBests(LeaderboardBoard board)
        {
            var best = new Dictionary<int, Id8LeaderboardEntry>();
            if (board?.Entries == null) return best;

            foreach (LeaderboardEntry e in board.Entries)
            {
                if (e == null || !e.Time.HasValue) continue;
                double ms = e.Time.Value.TotalMilliseconds;
                if (ms <= 0 || ms >= Id8LeaderboardRecord.DefaultGoalMs) continue;

                int carId = Id8CarNameMatch.CarIdFor(e.Car);
                if (carId < 0) continue;

                Id8LeaderboardEntry held;
                if (best.TryGetValue(carId, out held) && held.GoalMs <= (int)ms) continue;

                uint stamp = 0;
                if (e.Submitted.HasValue && e.Submitted.Value.Year > 1970)
                {
                    double secs = (e.Submitted.Value.ToUniversalTime()
                                   - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    if (secs > 0 && secs < uint.MaxValue) stamp = (uint)secs;
                }

                best[carId] = new Id8LeaderboardEntry
                {
                    Username = e.Player,
                    GoalMs = (int)ms,
                    UnixTime = stamp,
                };
            }
            return best;
        }

        /// <summary>Course id 0..15 for a course name, or -1 when it is not one the cabinet has.
        ///
        /// The ids are the game's own, read from the exe's course name table at 0x0121c9e0. Twelve
        /// of the sixteen match the site's spelling once punctuation and case are dropped; the four
        /// below do not, and are aliased explicitly rather than fuzzy-matched. Verified against the
        /// live page 2026-09-06: all 16 site courses resolve and none is left over.
        ///
        ///   cabinet     site
        ///   AkinaLake   Lake Akina      (word order differs)
        ///   Happo       Happogahara     (site uses the full name)
        ///   Tsubaki     Tsubaki Line
        ///   Momiji      Momiji Line
        ///
        /// Both spellings are accepted for every course, so this works whether the caller has the
        /// name from the game or from the site.</summary>
        public static int CourseId(string name)
        {
            string n = NormaliseCourse(name);
            if (n.Length == 0) return -1;
            switch (n)
            {
                case "AKINALAKE": case "LAKEAKINA":   return 0;
                case "MYOGI":                         return 1;
                case "AKAGI":                         return 2;
                case "AKINA":                         return 3;
                case "IROHAZAKA":                     return 4;
                case "TSUKUBA":                       return 5;
                case "HAPPO": case "HAPPOGAHARA":     return 6;
                case "NAGAO":                         return 7;
                case "TSUBAKI": case "TSUBAKILINE":   return 8;
                case "USUI":                          return 9;
                case "SADAMINE":                      return 10;
                case "TSUCHISAKA":                    return 11;
                case "AKINASNOW":                     return 12;
                case "HAKONE":                        return 13;
                case "MOMIJI": case "MOMIJILINE":     return 14;
                case "NANAMAGARI":                    return 15;
                default:                              return -1;
            }
        }

        /// <summary>The game's direction index for one of the site's direction words, or -1 when
        /// it is not one we recognise.
        ///
        /// CONFIRMED ON THE CABINET 2026-09-06: a marker written with direction 0 landed on
        /// "Akina DH", so 0 is Downhill and 1 is Hill Climb. This was guesswork until then, and
        /// having it backwards would have quietly put every uphill time on the downhill board,
        /// where the times look merely good rather than obviously wrong.
        ///
        /// The remaining suffixes are for courses whose two directions the site names differently;
        /// they map onto the same pair, with the "outbound" sense on the 0 side.</summary>
        public static int DirectionIndex(string direction)
        {
            if (string.IsNullOrEmpty(direction)) return -1;
            string d = direction.Replace('_', ' ').Replace(" ", "").ToUpperInvariant();
            switch (d)
            {
                case "DOWNHILL":
                case "OUTBOUND":
                case "CLOCKWISE":
                    return 0;
                case "HILLCLIMB":
                case "INBOUND":
                case "REVERSE":
                case "COUNTERCLOCKWISE":
                    return 1;
                default:
                    return -1;
            }
        }

        /// <summary>A course name reduced to letters and digits, upper case, for matching the
        /// cabinet's own name against the site's key. The game says "AKINA" where the site says
        /// "Akina_Downhill", and neither spelling is going to change to suit the other.</summary>
        public static string NormaliseCourse(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        /// <summary>The boards for one course, in whatever directions the site publishes for it.
        /// Matched on the normalised course name, so the cabinet's spelling does not have to agree
        /// with the site's.</summary>
        public static List<LeaderboardBoard> BoardsForCourse(
            Dictionary<string, LeaderboardBoard> boards, string courseName)
        {
            var found = new List<LeaderboardBoard>();
            if (boards == null || string.IsNullOrEmpty(courseName)) return found;
            string want = NormaliseCourse(courseName);
            if (want.Length == 0) return found;
            foreach (var b in boards.Values)
                if (NormaliseCourse(b.Course) == want) found.Add(b);
            return found;
        }
    }
}
