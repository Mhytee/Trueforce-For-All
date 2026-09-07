// Transport for the arcade leaderboards: reads community times for a board and submits the
// player's own. Mirrors the static-HttpClient + apikey/Bearer + trusted-URL-resolve pattern the
// other clients use (AchievementClient, EntitlementClient, ...).
//
// Signed-in only, by design and by the server. submit_arcade_lap_time returns
// {"ok":false,"error":"sign-in required"} when auth.uid() is null, and anon has no EXECUTE on
// either RPC (migration 0107), so a signed-out plugin cannot read or write leaderboard rows even
// if it tried. The display name is read server-side from profiles.username, so nothing here sends
// a name: a client cannot claim to be somebody else.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    internal sealed class ArcadeLeaderboardClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public ArcadeLeaderboardClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>The community board for one course and direction, best first.
        /// Null when not configured, not signed in, or unreachable, which the caller must treat as
        /// "no community rows" rather than "no rows": an empty board and an unknown board lead to
        /// very different writes.</summary>
        public async Task<List<Id8LeaderboardEntry>> GetBoardAsync(
            string game, int courseId, int direction, int? carId, int limit, CancellationToken ct)
        {
            string payload = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "p_game", game },
                { "p_course_id", courseId },
                { "p_direction", direction },
                { "p_car_id", carId },
                { "p_limit", limit },
            });

            var (ok, body) = await PostAsync("/rest/v1/rpc/get_arcade_leaderboard", payload, ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return null;

            try
            {
                var arr = JArray.Parse(body);
                var list = new List<Id8LeaderboardEntry>(arr.Count);
                foreach (JToken row in arr)
                {
                    int goal = (int?)row["goal_ms"] ?? 0;
                    if (goal <= 0) continue;
                    list.Add(new Id8LeaderboardEntry
                    {
                        Username = (string)row["author"],
                        GoalMs = goal,
                        Section1 = (int?)row["section1_ms"] ?? 0,
                        Section2 = (int?)row["section2_ms"] ?? 0,
                        Section3 = (int?)row["section3_ms"] ?? 0,
                        UnixTime = ToUnix((string)row["set_at"]),
                    });
                }
                return list;
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] Arcade leaderboard parse failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Every board for the game at once, keyed by slot (direction + 2 * course).
        ///
        /// One call rather than 32. The game has 32 boards and the plugin fills all of them up
        /// front, because a player browsing the leaderboard menu looks at courses they have not
        /// driven this session: filling only the current course left every other one showing SEGA's
        /// filler. The whole payload is at most 320 rows.</summary>
        public async Task<Dictionary<int, List<Id8LeaderboardEntry>>> GetAllBoardsAsync(
            string game, int limit, CancellationToken ct)
        {
            string payload = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "p_game", game },
                { "p_limit", limit },
            });

            var (ok, body) = await PostAsync("/rest/v1/rpc/get_arcade_leaderboards_all", payload, ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return null;

            try
            {
                var bySlot = new Dictionary<int, List<Id8LeaderboardEntry>>();
                foreach (JToken row in JArray.Parse(body))
                {
                    int course = (int?)row["course_id"] ?? -1;
                    int dir = (int?)row["direction"] ?? -1;
                    int goal = (int?)row["goal_ms"] ?? 0;
                    if (course < 0 || course > 15 || dir < 0 || dir > 1 || goal <= 0) continue;

                    int slot = 2 * course + dir;
                    if (!bySlot.TryGetValue(slot, out List<Id8LeaderboardEntry> list))
                        bySlot[slot] = list = new List<Id8LeaderboardEntry>();

                    list.Add(new Id8LeaderboardEntry
                    {
                        Username = (string)row["author"],
                        GoalMs = goal,
                        Section1 = (int?)row["section1_ms"] ?? 0,
                        Section2 = (int?)row["section2_ms"] ?? 0,
                        Section3 = (int?)row["section3_ms"] ?? 0,
                    });
                }
                return bySlot;
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] Arcade bulk leaderboard parse failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>One row per car per board: that car's best on that course, with who set it.
        ///
        /// A different shape from the top-ten boards, because the game's per-car boards hold
        /// exactly one record per car rather than a ranking. Keyed here by
        /// (slot, carId) so the caller can write each straight to its car's page.</summary>
        public async Task<Dictionary<int, Dictionary<int, Id8LeaderboardEntry>>> GetCarBestsAsync(
            string game, CancellationToken ct)
        {
            string payload = JsonConvert.SerializeObject(new Dictionary<string, object> { { "p_game", game } });
            var (ok, body) = await PostAsync("/rest/v1/rpc/get_arcade_car_bests", payload, ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return null;

            try
            {
                var bySlot = new Dictionary<int, Dictionary<int, Id8LeaderboardEntry>>();
                foreach (JToken row in JArray.Parse(body))
                {
                    int course = (int?)row["course_id"] ?? -1;
                    int dir = (int?)row["direction"] ?? -1;
                    int car = (int?)row["car_id"] ?? -1;
                    int goal = (int?)row["goal_ms"] ?? 0;
                    if (course < 0 || course > 15 || dir < 0 || dir > 1 || car < 0 || goal <= 0) continue;

                    int slot = 2 * course + dir;
                    if (!bySlot.TryGetValue(slot, out Dictionary<int, Id8LeaderboardEntry> cars))
                        bySlot[slot] = cars = new Dictionary<int, Id8LeaderboardEntry>();

                    cars[car] = new Id8LeaderboardEntry
                    {
                        Username = (string)row["author"],
                        GoalMs = goal,
                        Section1 = (int?)row["section1_ms"] ?? 0,
                        Section2 = (int?)row["section2_ms"] ?? 0,
                        Section3 = (int?)row["section3_ms"] ?? 0,
                        UnixTime = ToUnix((string)row["set_at"]),
                    };
                }
                return bySlot;
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] Arcade car-best parse failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Submit one finished run. The server decides whether it is an improvement, so
        /// this can be called for every completed lap without the caller tracking bests.</summary>
        public async Task<bool> SubmitAsync(
            string game, int courseId, int direction, int carId, int goalMs,
            int[] sections, int? tuningLevel, string pluginVersion,
            CancellationToken ct)
        {
            // The whole array, not the first three. Courses have different section counts and the
            // time is their sum, so sending three of four silently dropped a 48 second split and
            // stored a total nothing on the row added up to.
            int[] s = sections ?? new int[0];
            string payload = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "p_game", game },
                { "p_course_id", courseId },
                { "p_direction", direction },
                { "p_car_id", carId },
                { "p_goal_ms", goalMs },
                // The three legacy columns stay populated for readers that predate the array.
                { "p_section1_ms", s.Length > 0 ? (int?)s[0] : null },
                { "p_section2_ms", s.Length > 1 ? (int?)s[1] : null },
                { "p_section3_ms", s.Length > 2 ? (int?)s[2] : null },
                { "p_tuning_level", tuningLevel },
                { "p_plugin_version", pluginVersion },
                { "p_sections_ms", s.Length > 0 ? s : null },
            });

            var (ok, body) = await PostAsync("/rest/v1/rpc/submit_arcade_lap_time", payload, ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return false;

            try
            {
                JObject o = JObject.Parse(body);
                if ((bool?)o["ok"] != true)
                {
                    _log?.Invoke("[TF4ALL] Arcade submit rejected: " + (string)o["error"]);
                    return false;
                }
                bool stored = (bool?)o["stored"] == true;
                _log?.Invoke(stored
                    ? $"[TF4ALL] Arcade time submitted: course {courseId} dir {direction} car {carId} {goalMs} ms"
                    : $"[TF4ALL] Arcade time not stored ({(string)o["reason"] ?? "no reason given"})");
                return stored;
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] Arcade submit parse failed: " + ex.Message);
                return false;
            }
        }

        private static uint ToUnix(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return 0;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt))
                return 0;
            double secs = (dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return secs > 0 && secs < uint.MaxValue ? (uint)secs : 0;
        }

        private async Task<(bool ok, string body)> PostAsync(string path, string payload, CancellationToken ct)
        {
            if (!TryResolve(out string baseUrl, out string anonKey)) return (false, null);
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (false, null);
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(payload ?? "{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode) _log?.Invoke($"[TF4ALL] {path} failed: {(int)resp.StatusCode} {Trunc(body)}");
                        return (resp.IsSuccessStatusCode, body);
                    }
                }
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] {path} exception: {ex.Message}"); return (false, null); }
        }

        private async Task<string> GetBearerAsync()
        {
            if (_accessTokenProvider == null) return null;
            try { return await _accessTokenProvider().ConfigureAwait(false); }
            catch { return null; }
        }

        private bool TryResolve(out string baseUrl, out string anonKey)
        {
            baseUrl = null; anonKey = null;
            var s = _settingsProvider();
            if (s == null) return false;
            string url    = (s.CommunityBackendUrl ?? "").Trim();
            string key    = (s.CommunityBackendAnonKey ?? "").Trim();
            string userId = (s.AuthSession?.UserId ?? "").Trim();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(userId))
                return false;
            if (!ChannelValidation.IsTrustedSupabaseUrl(url))
            {
                _log?.Invoke($"[TF4ALL] Arcade leaderboard: rejecting untrusted backend URL: {url}");
                return false;
            }
            baseUrl = url.TrimEnd('/');
            anonKey = key;
            return true;
        }

        private static string Trunc(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s.Substring(0, 300));
    }
}
