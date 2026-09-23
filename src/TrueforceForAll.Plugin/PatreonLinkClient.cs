// Phase 2: Patreon account link transport. Talks to the `patreon-link` Edge Function (config +
// code exchange) and the get_my_patreon / unlink_my_patreon RPCs, authenticated with the
// signed-in user's JWT. The OAuth dance (browser + loopback) lives in PatreonOAuthFlow; this is
// pure HTTP transport, mirroring DiscordLinkClient.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class PatreonLinkClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public PatreonLinkClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        public sealed class OAuthConfig
        {
            public string   ClientId;
            public string[] RedirectUris;
            public string   Scope;
        }

        /// <summary>GET the client id + registered loopback redirects so the plugin can build the
        /// authorize URL.</summary>
        public async Task<(OAuthConfig cfg, string error)> GetConfigAsync(CancellationToken ct)
        {
            if (!TryResolve(out string baseUrl, out string anonKey)) return (null, "Patreon linking isn't configured.");
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (null, "Sign-in expired; sign in again.");
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    baseUrl + "/functions/v1/patreon-link?op=config"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Patreon config failed: {(int)resp.StatusCode} {Trunc(body)}");
                            return (null, "Patreon linking isn't set up yet.");
                        }
                        var o = JObject.Parse(body);
                        var cfg = new OAuthConfig
                        {
                            ClientId = (string)o["client_id"],
                            Scope    = (string)o["scope"] ?? "identity",
                        };
                        var arr = o["redirect_uris"] as JArray;
                        if (arr != null)
                        {
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var t in arr) { var s = (string)t; if (!string.IsNullOrEmpty(s)) list.Add(s); }
                            cfg.RedirectUris = list.ToArray();
                        }
                        if (string.IsNullOrEmpty(cfg.ClientId) || cfg.RedirectUris == null || cfg.RedirectUris.Length == 0)
                            return (null, "Patreon linking isn't set up yet.");
                        return (cfg, null);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Patreon config exception: {ex.Message}");
                return (null, "Network error reaching Patreon linking. Try again.");
            }
        }

        /// <summary>Hand the captured authorization code to the Edge Function, which exchanges it
        /// server-side, records the link, and (if an active pledge) flips supporter status.</summary>
        public async Task<(bool ok, string message, string patreonName)> ExchangeAsync(
            string code, string redirectUri, CancellationToken ct)
        {
            if (!TryResolve(out string baseUrl, out string anonKey))
                return (false, "Patreon linking isn't configured.", null);
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (false, "Sign-in expired; sign in again.", null);
            try
            {
                var payload = new JObject { ["code"] = code, ["redirect_uri"] = redirectUri }.ToString();
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    baseUrl + "/functions/v1/patreon-link?op=exchange"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (resp.IsSuccessStatusCode)
                        {
                            string name = null; bool isSupporter = false, discordLinked = false, discordJoinNeeded = false;
                            try
                            {
                                var o = JObject.Parse(body);
                                name = (string)o["patreon_full_name"];
                                isSupporter = (bool?)o["is_supporter"] ?? false;
                                discordLinked = (bool?)o["discord_linked"] ?? false;
                                discordJoinNeeded = (bool?)o["discord_join_needed"] ?? false;
                            }
                            catch { }
                            string who = string.IsNullOrEmpty(name) ? "Patreon linked" : "Linked Patreon as " + name;
                            string msg = isSupporter
                                ? who + ". Supporter status is active."
                                : who + ". No active pledge found yet.";
                            // Discord auto-links only when the patron is already in the server (a real,
                            // useful link). Otherwise point them at Join Discord to get into the server.
                            if (discordLinked) msg += " You're in the community Discord too, so it's linked.";
                            else if (discordJoinNeeded) msg += " Your Patreon has Discord connected; use Join Discord to get into the server for roles.";
                            return (true, msg, name);
                        }
                        string err = null;
                        try { err = (string)JObject.Parse(body)["error"]; } catch { }
                        _log?.Invoke($"[TF4ALL] Patreon exchange failed: {(int)resp.StatusCode} {Trunc(body)}");
                        string shown = ((int)resp.StatusCode == 409 && !string.IsNullOrEmpty(err))
                            ? Trunc(err) : "Couldn't link Patreon. Try again.";
                        return (false, shown, null);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Patreon exchange exception: {ex.Message}");
                return (false, "Network error linking Patreon.", null);
            }
        }

        /// <summary>Read the caller's current link (get_my_patreon RPC).</summary>
        public async Task<(bool linked, string name)> GetMyPatreonAsync(CancellationToken ct)
        {
            var (ok, body) = await RpcAsync("get_my_patreon", ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return (false, null);
            try
            {
                var arr = JArray.Parse(body);
                if (arr.Count == 0) return (false, null);
                var row = arr[0] as JObject;
                if (row == null) return (false, null);
                string id = (string)row["patreon_user_id"];
                if (string.IsNullOrEmpty(id)) return (false, null);
                return (true, (string)row["patreon_full_name"]);
            }
            catch { return (false, null); }
        }

        /// <summary>Remove the caller's link (unlink_my_patreon RPC).</summary>
        public async Task<bool> UnlinkAsync(CancellationToken ct)
        {
            var (ok, _) = await RpcAsync("unlink_my_patreon", ct).ConfigureAwait(false);
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
                            _log?.Invoke($"[TF4ALL] Patreon {fn} failed: {(int)resp.StatusCode} {Trunc(body)}");
                        return (resp.IsSuccessStatusCode, body);
                    }
                }
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Patreon {fn} exception: {ex.Message}"); return (false, null); }
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
                _log?.Invoke($"[TF4ALL] Patreon link: rejecting untrusted backend URL: {url}");
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
