// Phase 2: read-only supporter entitlement transport. Calls the get_my_entitlement RPC
// with the signed-in user's JWT so the in-plugin supporter badge can show the user's tier.
// Read-only + advisory: the REAL backup gate is enforced server-side by RLS, so nothing the
// client shows here can grant access. Mirrors the static-HttpClient + apikey/Bearer +
// trusted-URL-resolve pattern from BackupClient / DiscordLinkClient.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class EntitlementClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public EntitlementClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>Read the caller's entitlement (get_my_entitlement RPC). Returns
        /// (false, null) when not configured / not signed in / unreachable.</summary>
        public async Task<(bool isSupporter, string tier, DateTime? retainUntil)> GetMyEntitlementAsync(CancellationToken ct)
        {
            if (!TryResolve(out string baseUrl, out string anonKey)) return (false, null, null);
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (false, null, null);
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/rest/v1/rpc/get_my_entitlement"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Entitlement read failed: {(int)resp.StatusCode} {Trunc(body)}");
                            return (false, null, null);
                        }
                        var arr = JArray.Parse(body);
                        if (arr.Count == 0) return (false, null, null);
                        var row = arr[0] as JObject;
                        if (row == null) return (false, null, null);
                        bool isSup = (bool?)row["is_supporter"] ?? false;
                        string tier = (string)row["tier"];
                        DateTime? retain = null;   // set on lapse to now()+2y; null while supporter / never-supporter
                        try
                        {
                            var ru = row["backup_retain_until"];
                            if (ru != null && ru.Type != JTokenType.Null)
                                retain = ((DateTimeOffset)ru).UtcDateTime;
                        }
                        catch { retain = null; }
                        return (isSup, tier, retain);
                    }
                }
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Entitlement read exception: {ex.Message}"); return (false, null, null); }
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
                _log?.Invoke($"[TF4ALL] Entitlement: rejecting untrusted backend URL: {url}");
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
