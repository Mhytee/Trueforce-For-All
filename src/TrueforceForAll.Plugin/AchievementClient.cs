// Phase 2: achievement-tracker transport. Reads the signed-in user's achievement progress
// (get_my_achievements RPC, account-based, independent of Discord linking) and can trigger
// an immediate self-scoped Discord role sync (discord-role-sync op=sync with the user's JWT,
// which the function scopes to the caller). Mirrors the static-HttpClient + apikey/Bearer +
// trusted-URL-resolve pattern from the other clients.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class AchievementClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public AchievementClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>One achievement's definition + the caller's progress toward it.</summary>
        public sealed class AchievementRow
        {
            public string Key;
            public string Label;
            public string Description;
            public string Metric;
            public string Kind;          // "threshold" | "percentile"
            public int    Tier;
            public int    Sort;
            public double Threshold;
            public double CurrentValue;
            public bool   Earned;
        }

        /// <summary>The caller's full achievement list (earned + unearned + progress).
        /// Returns null when not configured / not signed in / unreachable.</summary>
        public async Task<List<AchievementRow>> GetMyAchievementsAsync(CancellationToken ct)
        {
            var (ok, body) = await PostAsync("/rest/v1/rpc/get_my_achievements", "{}", ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return null;
            try
            {
                var arr = JArray.Parse(body);
                var list = new List<AchievementRow>(arr.Count);
                foreach (var t in arr)
                {
                    var o = t as JObject;
                    if (o == null) continue;
                    list.Add(new AchievementRow
                    {
                        Key          = (string)o["key"],
                        Label        = (string)o["label"],
                        Description  = (string)o["description"],
                        Metric       = (string)o["metric"],
                        Kind         = (string)o["kind"],
                        Tier         = (int?)o["tier"] ?? 1,
                        Sort         = (int?)o["sort"] ?? 0,
                        Threshold    = (double?)o["threshold"] ?? 0,
                        CurrentValue = (double?)o["current_value"] ?? 0,
                        Earned       = (bool?)o["earned"] ?? false,
                    });
                }
                return list;
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Achievements parse error: {ex.Message}"); return null; }
        }

        /// <summary>Fire an immediate self-scoped Discord role sync (claims any earned roles
        /// right away). The Edge Function scopes the work to the caller's own uid.</summary>
        public async Task<(bool ok, int applied)> SyncMyRolesAsync(CancellationToken ct)
        {
            var (ok, body) = await PostAsync("/functions/v1/discord-role-sync?op=sync", "{}", ct).ConfigureAwait(false);
            if (!ok) return (false, 0);
            int applied = 0;
            try { applied = (int?)JObject.Parse(body)["applied"] ?? 0; } catch { /* dryRun or no body */ }
            return (true, applied);
        }

        /// <summary>Dev preview: email the stage-N backup-deletion warning to the signed-in
        /// user's own address (op=preview is self-scoped via the JWT email claim).</summary>
        public async Task<(bool ok, string message)> SendWarnPreviewAsync(int stage, CancellationToken ct)
        {
            var (ok, body) = await PostAsync($"/functions/v1/backup-retention-warn?op=preview&stage={stage}", "{}", ct).ConfigureAwait(false);
            if (ok) return (true, "Sent the stage-" + stage + " warning email to your address.");
            string err = null; try { err = (string)JObject.Parse(body)["error"]; } catch { }
            return (false, string.IsNullOrEmpty(err) ? "Couldn't send the preview email." : err);
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
                _log?.Invoke($"[TF4ALL] Achievements: rejecting untrusted backend URL: {url}");
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
