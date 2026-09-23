// Account session transport. Lists the signed-in user's auth sessions across devices
// (get_my_sessions RPC) and revokes a chosen one (revoke_my_session RPC). The Supabase
// anon client has no native "list my sessions" endpoint, so both go through the
// SECURITY DEFINER helpers in migration 0058, each scoped server-side to auth.uid().
// Mirrors the static-HttpClient + apikey/Bearer + trusted-URL-resolve pattern of the
// other clients (AchievementClient / EntitlementClient).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>Result of the revoke heartbeat. Unknown (network/error) must NEVER be
    /// treated as Revoked, so a transient outage can't sign a healthy device out.</summary>
    internal enum SessionStatus { Unknown, Active, Revoked }

    internal sealed class SessionClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public SessionClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>One auth session belonging to the signed-in user.</summary>
        public sealed class SessionRow
        {
            public string    Id;
            public DateTime?  CreatedAt;     // UTC
            public DateTime?  LastActive;    // UTC; freshest of heartbeat/last-refresh
            public DateTime?  NotAfter;      // UTC; absolute expiry, often null
            public string    UserAgent;
            public string    Ip;
            public bool      IsCurrent;      // matches this device's JWT session_id
            // Device metadata (from session_metadata via the heartbeat upsert); null for
            // sessions that never sent a heartbeat (brand-new sign-in, or an old client).
            public string    DeviceName;
            public string    Wheel;
            public string    PluginVersion;
            public DateTime?  LastSeen;      // UTC; last heartbeat
        }

        /// <summary>Every active session for the caller (this device flagged IsCurrent).
        /// Returns null when not configured / not signed in / unreachable.</summary>
        public async Task<List<SessionRow>> GetMySessionsAsync(CancellationToken ct)
        {
            var (ok, body) = await PostAsync("/rest/v1/rpc/get_my_sessions", "{}", ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return null;
            try
            {
                var arr = JArray.Parse(body);
                var list = new List<SessionRow>(arr.Count);
                foreach (var t in arr)
                {
                    var o = t as JObject;
                    if (o == null) continue;
                    list.Add(new SessionRow
                    {
                        Id            = (string)o["id"],
                        CreatedAt     = AsUtc(o["created_at"]),
                        LastActive    = AsUtc(o["last_active"]),
                        NotAfter      = AsUtc(o["not_after"]),
                        UserAgent     = (string)o["user_agent"],
                        Ip            = (string)o["ip"],
                        IsCurrent     = (bool?)o["is_current"] ?? false,
                        DeviceName    = (string)o["device_name"],
                        Wheel         = (string)o["wheel"],
                        PluginVersion = (string)o["plugin_version"],
                        LastSeen      = AsUtc(o["last_seen"]),
                    });
                }
                return list;
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Sessions parse error: {ex.Message}"); return null; }
        }

        /// <summary>Revoke a single session by id. The RPC refuses to revoke the caller's
        /// own current session and only touches sessions owned by auth.uid().
        /// Returns true when a session row was actually removed.</summary>
        public async Task<bool> RevokeSessionAsync(string sessionId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            // p_session_id as a JSON string; JObject handles escaping/quoting safely.
            string payload = new JObject { ["p_session_id"] = sessionId }.ToString(Newtonsoft.Json.Formatting.None);
            var (ok, body) = await PostAsync("/rest/v1/rpc/revoke_my_session", payload, ct).ConfigureAwait(false);
            if (!ok) return false;
            try
            {
                // Scalar boolean function: PostgREST returns the bare value `true`/`false`.
                var tok = JToken.Parse(string.IsNullOrEmpty(body) ? "false" : body);
                return tok.Type == JTokenType.Boolean && tok.Value<bool>();
            }
            catch { return false; }
        }

        /// <summary>Heartbeat: is THIS device's session still live on the server? Maps the
        /// is_my_session_active RPC to a strict tri-state. Sign-out is only ever justified by
        /// <see cref="SessionStatus.Revoked"/> (HTTP 200 + boolean false). Any non-success,
        /// empty/garbled body, timeout, or unconfigured state is <see cref="SessionStatus.Unknown"/>
        /// and the caller must leave the session untouched.</summary>
        public async Task<SessionStatus> IsMySessionActiveAsync(CancellationToken ct)
        {
            var (ok, body) = await PostAsync("/rest/v1/rpc/is_my_session_active", "{}", ct).ConfigureAwait(false);
            if (!ok) return SessionStatus.Unknown;
            try
            {
                var tok = JToken.Parse(string.IsNullOrEmpty(body) ? "null" : body);
                if (tok.Type != JTokenType.Boolean) return SessionStatus.Unknown;
                return tok.Value<bool>() ? SessionStatus.Active : SessionStatus.Revoked;
            }
            catch { return SessionStatus.Unknown; }
        }

        /// <summary>Heartbeat + device-metadata upsert in one round-trip. Tells the server
        /// this device is still alive AND records its name / last-used wheel / plugin version
        /// (so the Account session list can identify it). Returns the SAME tri-state as
        /// <see cref="IsMySessionActiveAsync"/>: only <see cref="SessionStatus.Revoked"/>
        /// (HTTP 200 + boolean false) justifies sign-out. The server derives session_id +
        /// user_id from the JWT, so these args only DESCRIBE this device; they can't target
        /// another session. A revoked session returns false and writes nothing.</summary>
        public async Task<SessionStatus> SessionHeartbeatAsync(string deviceName, string wheel, string version, CancellationToken ct)
        {
            // Client-side hygiene: strip control chars + cap length so an odd machine name
            // can't make an ugly/oversized row. The RPC also left()-truncates + CHECK-bounds.
            // All keys are always sent (explicit null when empty) so PostgREST routes to the
            // 4-arg session_heartbeat (migration 0081) and passes SQL NULL for the missing ones.
            string dn = Sanitize(deviceName, 80);
            string wh = Sanitize(wheel, 60);
            string pv = Sanitize(version, 32);
            // Stable hardware fingerprint (sha256 of MachineGuid + username) so a
            // moderator ban can also block the machine (and catch fresh sock-puppet
            // accounts created on it). Server-side only; never returned to clients.
            // Empty on unusual setups -> SQL NULL, device banning just no-ops there.
            string fp = DeviceFingerprint.Compute();
            var payload = new JObject
            {
                ["p_device_name"]    = dn != null ? (JToken)new JValue(dn) : JValue.CreateNull(),
                ["p_wheel"]          = wh != null ? (JToken)new JValue(wh) : JValue.CreateNull(),
                ["p_plugin_version"] = pv != null ? (JToken)new JValue(pv) : JValue.CreateNull(),
                ["p_device_fp"]      = !string.IsNullOrEmpty(fp) ? (JToken)new JValue(fp) : JValue.CreateNull(),
            };
            var (ok, body) = await PostAsync("/rest/v1/rpc/session_heartbeat",
                payload.ToString(Newtonsoft.Json.Formatting.None), ct).ConfigureAwait(false);
            if (!ok) return SessionStatus.Unknown;
            try
            {
                var tok = JToken.Parse(string.IsNullOrEmpty(body) ? "null" : body);
                if (tok.Type != JTokenType.Boolean) return SessionStatus.Unknown;
                return tok.Value<bool>() ? SessionStatus.Active : SessionStatus.Revoked;
            }
            catch { return SessionStatus.Unknown; }
        }

        // Drop control chars and cap length for a human-facing metadata field. Returns null
        // (-> JSON null -> SQL NULL) when nothing printable remains.
        private static string Sanitize(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? null : r;
        }

        private static DateTime? AsUtc(JToken tok)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            string s = tok.Type == JTokenType.String ? (string)tok : tok.ToString();
            if (string.IsNullOrEmpty(s)) return null;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.UtcDateTime;
            return null;
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
                _log?.Invoke($"[TF4ALL] Sessions: rejecting untrusted backend URL: {url}");
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
