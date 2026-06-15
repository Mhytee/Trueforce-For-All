// Phase 2, M5: standalone Discord account link transport. Talks to the `discord-link`
// Edge Function (config + code exchange) and the get_my_discord / unlink_my_discord RPCs,
// authenticated with the signed-in user's JWT. The OAuth dance itself (browser + loopback
// capture) lives in DiscordOAuthFlow; this class is pure HTTP transport, mirroring
// BackupClient's static-HttpClient + apikey/Bearer + trusted-URL-resolve pattern.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class DiscordLinkClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public DiscordLinkClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>The public OAuth parameters served by the Edge Function.</summary>
        public sealed class OAuthConfig
        {
            public string   ClientId;
            public string[] RedirectUris;
            public string   Scope;
        }

        /// <summary>GET the client id + registered loopback redirects so the plugin can build
        /// the authorize URL. Returns (cfg, null) on success, or (null, message) with a reason
        /// distinct enough for the UI to give the right remedy (expired session vs network vs
        /// genuinely-not-configured).</summary>
        public async Task<(OAuthConfig cfg, string error)> GetConfigAsync(CancellationToken ct)
        {
            if (!TryResolve(out string baseUrl, out string anonKey)) return (null, "Discord linking isn't configured.");
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (null, "Sign-in expired; sign in again.");
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    baseUrl + "/functions/v1/discord-link?op=config"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[Trueforce] Discord config failed: {(int)resp.StatusCode} {Trunc(body)}");
                            return (null, "Discord linking isn't set up yet.");
                        }
                        var o = JObject.Parse(body);
                        var cfg = new OAuthConfig
                        {
                            ClientId = (string)o["client_id"],
                            Scope    = (string)o["scope"] ?? "identify",
                        };
                        var arr = o["redirect_uris"] as JArray;
                        if (arr != null)
                        {
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var t in arr) { var s = (string)t; if (!string.IsNullOrEmpty(s)) list.Add(s); }
                            cfg.RedirectUris = list.ToArray();
                        }
                        if (string.IsNullOrEmpty(cfg.ClientId) || cfg.RedirectUris == null || cfg.RedirectUris.Length == 0)
                            return (null, "Discord linking isn't set up yet.");
                        return (cfg, null);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Discord config exception: {ex.Message}");
                return (null, "Network error reaching Discord linking. Try again.");
            }
        }

        /// <summary>Hand the captured authorization code to the Edge Function, which
        /// exchanges it server-side and records the link for the signed-in user.</summary>
        public async Task<(bool ok, string message, string username)> ExchangeAsync(
            string code, string redirectUri, CancellationToken ct)
        {
            if (!TryResolve(out string baseUrl, out string anonKey))
                return (false, "Discord linking isn't configured.", null);
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (false, "Sign-in expired; sign in again.", null);
            try
            {
                var payload = new JObject { ["code"] = code, ["redirect_uri"] = redirectUri }.ToString();
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    baseUrl + "/functions/v1/discord-link?op=exchange"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (resp.IsSuccessStatusCode)
                        {
                            string username = null, join = null;
                            try { var o = JObject.Parse(body); username = (string)o["discord_username"]; join = (string)o["join"]; } catch { }
                            string who = string.IsNullOrEmpty(username) ? "Discord linked" : "Linked as " + username;
                            return (true, join == "joined" ? who + " and added to the Trueforce For All Discord." : who + ".", username);
                        }
                        string err = null;
                        try { err = (string)JObject.Parse(body)["error"]; } catch { }
                        _log?.Invoke($"[Trueforce] Discord exchange failed: {(int)resp.StatusCode} {Trunc(body)}");
                        // Only surface the server's text for the curated 409 conflict; everything
                        // else gets a generic line so internal/proxy strings can't reach the UI.
                        string shown = ((int)resp.StatusCode == 409 && !string.IsNullOrEmpty(err))
                            ? Trunc(err) : "Couldn't link Discord. Try again.";
                        return (false, shown, null);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Discord exchange exception: {ex.Message}");
                return (false, "Network error linking Discord.", null);
            }
        }

        /// <summary>Read the caller's current link (get_my_discord RPC).</summary>
        public async Task<(bool linked, string username)> GetMyDiscordAsync(CancellationToken ct)
        {
            var (ok, body) = await RpcAsync("get_my_discord", ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return (false, null);
            try
            {
                var arr = JArray.Parse(body);
                if (arr.Count == 0) return (false, null);
                var row = arr[0] as JObject;
                if (row == null) return (false, null);
                string id = (string)row["discord_id"];
                if (string.IsNullOrEmpty(id)) return (false, null);
                return (true, (string)row["discord_username"]);
            }
            catch { return (false, null); }
        }

        /// <summary>Remove the caller's link (unlink_my_discord RPC).</summary>
        public async Task<bool> UnlinkAsync(CancellationToken ct)
        {
            var (ok, _) = await RpcAsync("unlink_my_discord", ct).ConfigureAwait(false);
            return ok;
        }

        private async Task<(bool ok, string body)> RpcAsync(string fn, CancellationToken ct)
        {
            if (!TryResolve(out string baseUrl, out string anonKey)) return (false, null);
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (false, null);
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/rest/v1/rpc/" + fn))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                            _log?.Invoke($"[Trueforce] Discord {fn} failed: {(int)resp.StatusCode} {Trunc(body)}");
                        return (resp.IsSuccessStatusCode, body);
                    }
                }
            }
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Discord {fn} exception: {ex.Message}"); return (false, null); }
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
                _log?.Invoke($"[Trueforce] Discord link: rejecting untrusted backend URL: {url}");
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
