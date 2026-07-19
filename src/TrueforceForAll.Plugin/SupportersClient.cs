// Phase 2: read-only transport for the Supporters wall. Calls the PUBLIC list_supporters RPC
// (granted to anon + authenticated) so the wall renders even when the user isn't signed in.
// The roster itself is populated server-side from Patreon by the patreon-supporters-sync Edge
// Function; this client only reads the already-public display string + tier. Mirrors the
// static-HttpClient + apikey/Bearer + trusted-URL-resolve pattern from EntitlementClient.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class SupportersClient
    {
        public sealed class SupporterRow
        {
            public string Name { get; set; }
            public string Tier { get; set; }
            public string Source { get; set; }   // "patreon" (tier badge) or "manual" (one-time donor)
            // No monetary fields: the server orders by lifetime support and returns rows
            // already in display order, so amounts never reach the client.
        }

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public SupportersClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>Read the public supporters roster (list_supporters RPC), best-pledge first.
        /// Returns null when not configured / unreachable / the read fails (so the caller can
        /// tell a load failure from a genuinely empty roster); an empty list when nobody has pledged yet.</summary>
        public async Task<List<SupporterRow>> GetSupportersAsync(CancellationToken ct)
        {
            var result = new List<SupporterRow>();
            if (!TryResolve(out string baseUrl, out string anonKey)) return null;

            // Public read: prefer the signed-in token if we have one, else fall back to the
            // anon key as the bearer (the RPC is granted to anon).
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) bearer = anonKey;

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/rest/v1/rpc/list_supporters"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Supporters read failed: {(int)resp.StatusCode} {Trunc(body)}");
                            return null;
                        }
                        var arr = JArray.Parse(body);
                        foreach (var item in arr)
                        {
                            var row = item as JObject;
                            if (row == null) continue;
                            string name = (string)row["display_name"];
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            result.Add(new SupporterRow { Name = name, Tier = (string)row["tier"], Source = (string)row["source"] });
                        }
                        return result;
                    }
                }
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Supporters read exception: {ex.Message}"); return null; }
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
            string url = (s.CommunityBackendUrl ?? "").Trim();
            string key = (s.CommunityBackendAnonKey ?? "").Trim();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return false;
            if (!ChannelValidation.IsTrustedSupabaseUrl(url))
            {
                _log?.Invoke($"[TF4ALL] Supporters: rejecting untrusted backend URL: {url}");
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
