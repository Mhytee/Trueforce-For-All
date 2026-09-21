// Anonymous usage-statistics transport. Fire-and-forget POST to the
// telemetry_ping RPC (migration 0127), keyed by a random plugin-minted id
// (Settings.AnalyticsAnonId), never an account and never hardware. Deliberately
// anonymous: it authenticates with the plain anon key only, never a user token,
// so a report can never be tied to a signed-in account. The caller gates on
// Settings.ShareUsageStats and the once-a-day stamp; this class only transports.
//
// Mirrors the static-HttpClient + apikey + trusted-URL-resolve pattern of the
// other clients (SessionClient / CommunityClient).

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class TelemetryClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;

        public TelemetryClient(Func<TrueforceSettings> settingsProvider, Action<string> log)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
        }

        /// <summary>Fire-and-forget anonymous usage ping. Safe to call often: the
        /// server upserts one row per (anon_id, day). Never throws to the caller and
        /// never blocks. <paramref name="onSent"/> runs ONLY on a 2xx, on a
        /// ThreadPool thread, so the caller can commit "already sent" state (day
        /// stamp, payload hashes, queue drain) after the server accepted it rather
        /// than before; a failed send then simply retries on the next tick.</summary>
        public void SendPing(string anonId, string pluginVersion, string wheel,
            string game, string settingsJson, string gamesJson = null, string gamePresetsJson = null,
            Action onSent = null)
        {
            if (string.IsNullOrWhiteSpace(anonId)) return;
            if (!TryResolve(out string baseUrl, out string anonKey)) return;

            string body;
            try
            {
                body = new JObject
                {
                    ["p_anon_id"]        = anonId.Trim(),
                    ["p_plugin_version"] = NullIfEmpty(pluginVersion),
                    ["p_wheel"]          = NullIfEmpty(wheel),
                    ["p_game"]           = NullIfEmpty(game),
                    ["p_settings"]       = ParseOrNull(settingsJson),
                    ["p_games"]          = ParseOrNull(gamesJson),        // [{g,d}] games played since the last ping
                    ["p_game_presets"]   = ParseOrNull(gamePresetsJson),  // [{g,p}] per-game preset bodies, only when changed
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] Telemetry serialize failed: " + ex.Message);
                return;
            }

            string fullUrl = baseUrl + "/rest/v1/rpc/telemetry_ping";
            string key = anonKey;
            // ThreadPool task, not awaited. Every exception is caught inside so
            // nothing reaches the unobserved-task finalizer (net48 can terminate
            // the process on it).
            Task.Run(async () =>
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                    {
                        // Anonymous by design: the anon key is both apikey AND bearer,
                        // never a user token, so a report is never tied to an account.
                        req.Headers.Add("apikey", key);
                        req.Headers.Add("Authorization", "Bearer " + key);
                        req.Headers.Add("Prefer", "return=minimal");
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        using (var resp = await _http.SendAsync(req,
                            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false))
                        {
                            if (resp.IsSuccessStatusCode)
                            {
                                try { onSent?.Invoke(); }
                                catch (Exception cex)
                                { _log?.Invoke("[TF4ALL] Telemetry ping commit error: " + cex.Message); }
                            }
                            else
                            {
                                _log?.Invoke($"[TF4ALL] Telemetry ping failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke("[TF4ALL] Telemetry ping error: " + ex.Message);
                }
            });
        }

        private static JToken NullIfEmpty(string s) =>
            string.IsNullOrWhiteSpace(s) ? (JToken)JValue.CreateNull() : new JValue(s.Trim());

        // A pre-serialized JSON fragment, or JSON null when absent / malformed.
        private static JToken ParseOrNull(string json)
        {
            if (string.IsNullOrEmpty(json)) return JValue.CreateNull();
            try { return JToken.Parse(json); } catch { return JValue.CreateNull(); }
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
                _log?.Invoke("[TF4ALL] Telemetry: rejecting untrusted backend URL: " + url);
                return false;
            }
            baseUrl = url.TrimEnd('/');
            anonKey = key;
            return true;
        }
    }
}
