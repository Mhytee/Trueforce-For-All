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
    /// <summary>What the server said it actually STORED. Every filter in
    /// telemetry_ping (size caps, date window, per-install game cap) drops its
    /// input silently and still answers 2xx, so "the POST succeeded" is not the
    /// same as "the payload landed". Committing local already-sent state on a
    /// bare 2xx therefore marked discarded payloads as delivered, and since the
    /// resend test is a hash compare, they were then never resent: permanent,
    /// and invisible from both ends. The caller commits only what is listed
    /// here and leaves the rest queued for the next ping.</summary>
    internal sealed class TelemetryReceipt
    {
        /// <summary>The settings snapshot was stored (false when it was dropped
        /// for size, in which case the caller must not stamp its hash).</summary>
        public bool SettingsStored;

        /// <summary>Accepted game-day keys, echoed in the caller's own
        /// "&lt;game&gt;|yyyy-MM-dd" form so they can be matched by value.</summary>
        public readonly System.Collections.Generic.HashSet<string> Games =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>Game names whose preset body was stored.</summary>
        public readonly System.Collections.Generic.HashSet<string> Presets =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    }

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
        /// than before; a failed send then simply retries on the next tick. It is
        /// handed the server's receipt (migration 0131) naming exactly what was
        /// stored, or null if the response carried none, so the caller can commit
        /// per item instead of trusting the status code.</summary>
        public void SendPing(string anonId, string pluginVersion, string wheel,
            string game, string settingsJson, string gamesJson = null, string gamePresetsJson = null,
            Action<TelemetryReceipt> onSent = null)
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
                        // No "Prefer: return=minimal": the receipt IS the response body,
                        // and it is the only way to tell an accepted payload from a
                        // silently filtered one. It is a few hundred bytes once a day.
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        using (var resp = await _http.SendAsync(req,
                            HttpCompletionOption.ResponseContentRead, CancellationToken.None).ConfigureAwait(false))
                        {
                            if (resp.IsSuccessStatusCode)
                            {
                                TelemetryReceipt receipt = null;
                                try
                                {
                                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                    receipt = ParseReceipt(text);
                                }
                                catch { /* no receipt: the caller commits the day stamp only */ }
                                try { onSent?.Invoke(receipt); }
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

        /// <summary>Read the server's receipt. Returns null for anything it cannot
        /// read as one (an old server, an empty body, a proxy's error page), which
        /// the caller treats as "nothing confirmed" rather than "everything
        /// confirmed": a payload that may not have landed stays queued and is
        /// retried, which costs one more ping and can never lose data.</summary>
        private static TelemetryReceipt ParseReceipt(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            JObject o;
            try { o = JToken.Parse(text) as JObject; } catch { return null; }
            if (o == null || o["ok"] == null) return null;
            if (o["ok"].Type != JTokenType.Boolean || !(bool)o["ok"]) return null;
            var r = new TelemetryReceipt
            {
                SettingsStored = o["settings"] != null
                                 && o["settings"].Type == JTokenType.Boolean
                                 && (bool)o["settings"],
            };
            AddStrings(o["games"]   as JArray, r.Games);
            AddStrings(o["presets"] as JArray, r.Presets);
            return r;
        }

        private static void AddStrings(JArray a, System.Collections.Generic.HashSet<string> into)
        {
            if (a == null) return;
            foreach (var t in a)
                if (t != null && t.Type == JTokenType.String)
                {
                    string s = (string)t;
                    if (!string.IsNullOrEmpty(s)) into.Add(s);
                }
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
