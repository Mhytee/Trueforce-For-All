// Supabase Auth client for community preset ownership. Email-OTP flow:
// 1. send_otp(email)   - server emails a 6-digit code
// 2. verify_otp(code)  - server returns access_token + refresh_token
// 3. plugin attaches Authorization: Bearer <access_token> to RPCs
//    that need an authenticated identity (upload_preset stamps
//    owner_user_id from auth.uid(); update_preset / delete_preset
//    require auth.uid() = owner_user_id).
//
// Sessions are persisted to Settings.AuthSession so a signed-in user
// stays signed in across plugin restarts. Refresh-token rotation
// happens lazily when an access token is requested and is within ~60s
// of expiry. Failures clear the session and force a fresh sign-in.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>Persisted Supabase Auth session for the local install.
    /// Stored on TrueforceSettings.AuthSession so it survives plugin
    /// restarts. Token rotation handled by CommunityAuth.GetAccessToken
    /// (refreshes near expiry, clears on permanent failure).</summary>
    public sealed class CommunityAuthSession
    {
        public string   AccessToken  { get; set; }
        public string   RefreshToken { get; set; }
        public string   UserId       { get; set; }
        public string   Email        { get; set; }
        public DateTime ExpiresAt    { get; set; }
        // B5/F07: idle expiry. Updated on every successful
        // GetAccessTokenAsync return; if > 30 days since the last use,
        // the session is cleared at the next refresh.
        public DateTime LastUsedAt   { get; set; } = DateTime.MinValue;
        // B5/F07: device binding. Captured at VerifyOtpAsync success and
        // compared on every token return; mismatch clears the session.
        public string   DeviceFingerprint { get; set; } = "";
    }

    /// <summary>Categorized auth call outcome so the modal can pick
    /// specific copy (housinggrade pattern: rate-limit vs expired vs
    /// generic surface different copy and lifecycle handling).</summary>
    internal enum AuthCallResult
    {
        Ok,
        InvalidInput,
        RateLimited,
        Expired,
        BadCode,
        NetworkFailure,
        Generic,
    }

    /// <summary>Result of a check_username_available RPC. Mirrors the
    /// server's reason vocabulary so the modal can pick specific copy.</summary>
    internal enum UsernameAvailability
    {
        Unknown,
        Available,
        SelfOwned,    // Caller already owns this username (no-op rename)
        Format,       // Failed [a-zA-Z0-9_]{3,32}
        Reserved,     // Hardcoded reserved name (admin/root/etc.)
        Taken,        // Someone else has it
        Profanity,    // Contains a blocked word
        Network,
    }

    internal sealed class CommunityAuth
    {
        private const string OtpPath     = "/auth/v1/otp";
        private const string VerifyPath  = "/auth/v1/verify";
        private const string RefreshPath = "/auth/v1/token?grant_type=refresh_token";
        private const string LogoutPath  = "/auth/v1/logout";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action _persistSettings;
        private readonly Action<string> _log;
        // Fired after the authenticated identity changes (sign-in,
        // sign-out, refresh that flips to a different email). The
        // TrueforcePlugin slot manager uses this to mount the right
        // per-user data slot; UI panels reset their per-session
        // latches. The string is the new active slot key: lower-cased
        // email or "" for anonymous.
        public event Action<string> ActiveIdentityChanged;

        // Serialize refreshes so two concurrent RPCs don't both try to
        // burn the refresh_token.
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        // Refresh-failure backoff. Without this, every RPC that calls
        // GetAccessTokenAsync re-attempts the refresh during a backend
        // outage; the _refreshLock serializes them, but the UI still
        // wedges for minutes as each one waits for HttpClient to fail.
        // After any refresh failure we stamp the time and short-circuit
        // future GetAccessTokenAsync calls (returning the cached token
        // if non-expired, or null) until the cooldown elapses. Cleared
        // on a successful refresh, on VerifyOtpAsync success, and on
        // SignOut so the next session starts clean.
        private DateTime? _lastRefreshFailureAt;
        private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(60);

        // Set by the most recent SendOtpAsync: when the server rate-limits
        // the request (HTTP 429), GoTrue's body carries the exact wait
        // ("...you can only request this after N seconds"), which we parse
        // so the sign-in modal can show an accurate resend countdown instead
        // of a fixed guess. Null when the last send was not rate-limited.
        public int? LastSendRetryAfterSeconds { get; private set; }

        public CommunityAuth(Func<TrueforceSettings> settingsProvider,
            Action persistSettings, Action<string> log)
        {
            _settingsProvider = settingsProvider
                ?? throw new ArgumentNullException(nameof(settingsProvider));
            _persistSettings = persistSettings;
            _log = log;
        }

        public bool IsSignedIn
        {
            get
            {
                var s = _settingsProvider()?.AuthSession;
                return s != null && !string.IsNullOrEmpty(s.AccessToken);
            }
        }

        /// <summary>B5/F03 migration: legacy installs persisted plaintext
        /// tokens. Wrap them via DPAPI on first plugin load after B5
        /// ships so users stay signed in but their on-disk tokens are
        /// no longer readable by other processes. Idempotent (Protect
        /// skips already-marked values), safe to call on every Init.
        /// Returns true if anything was actually rewritten.</summary>
        public bool MigrateLegacyPlaintextSession()
        {
            var settings = _settingsProvider();
            var s = settings?.AuthSession;
            if (s == null) return false;
            bool needsEncryption =
                (!string.IsNullOrEmpty(s.AccessToken) && !DpapiCipher.IsProtected(s.AccessToken))
                || (!string.IsNullOrEmpty(s.RefreshToken) && !DpapiCipher.IsProtected(s.RefreshToken));
            if (!needsEncryption) return false;
            // Route the existing session back through SaveSession so it
            // re-encrypts in place. We pass the same instance, not a
            // copy: SaveSession mutates AccessToken/RefreshToken to the
            // "dpapi:" form and persists. Single Info-level log line.
            _log?.Invoke("[TF4ALL] Migration: encrypting legacy plaintext AuthSession");
            SaveSession(s);
            return true;
        }

        public string SignedInEmail
            => _settingsProvider()?.AuthSession?.Email;

        public string SignedInUserId
            => _settingsProvider()?.AuthSession?.UserId;

        // ---- OTP flow ------------------------------------------------------

        /// <summary>Kick off the email-OTP sign-in: server emails a
        /// 6-digit code. Returns a categorized result so the modal can
        /// surface specific copy (rate limit / network / generic).</summary>
        public async Task<AuthCallResult> SendOtpAsync(string email)
        {
            LastSendRetryAfterSeconds = null;
            if (!ShouldRun(out var url, out var anonKey)) return AuthCallResult.NetworkFailure;
            if (string.IsNullOrWhiteSpace(email)) return AuthCallResult.InvalidInput;
            email = email.Trim().ToLowerInvariant();

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    email,
                    create_user = true,
                });
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth send-otp serialize failed: {ex.Message}");
                return AuthCallResult.Generic;
            }

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + OtpPath))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + anonKey);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return AuthCallResult.Ok;
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        var classified = ClassifyAuthError((int)resp.StatusCode, detail);
                        // F19: log the classified category, not the raw body, to avoid leaking PII / token fragments.
                        _log?.Invoke($"[TF4ALL] Auth send-otp failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {classified}");
                        if (classified == AuthCallResult.RateLimited)
                            LastSendRetryAfterSeconds = ParseRetryAfterSeconds(resp, detail);
                        return classified;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth send-otp exception: {ex.Message}");
                return AuthCallResult.NetworkFailure;
            }
        }

        /// <summary>Exchange the 6-digit code for a session. Persists
        /// the session on success. Returns a categorized result so the
        /// modal can pick specific copy (expired vs wrong code vs rate
        /// limit) - housinggrade pattern.</summary>
        public async Task<AuthCallResult> VerifyOtpAsync(string email, string code)
        {
            if (!ShouldRun(out var url, out var anonKey)) return AuthCallResult.NetworkFailure;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
                return AuthCallResult.InvalidInput;
            email = email.Trim().ToLowerInvariant();
            code = code.Trim();
            // housinggrade requires 6 digits before sending.
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[0-9]{6}$"))
                return AuthCallResult.InvalidInput;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    email,
                    token = code,
                    type = "email",
                });
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth verify-otp serialize failed: {ex.Message}");
                return AuthCallResult.Generic;
            }

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + VerifyPath))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + anonKey);
                    // Stamp a descriptive User-Agent so GoTrue records something identifiable
                    // in auth.sessions.user_agent for THIS session (it captures the UA only at
                    // session creation, so it must go on the verify-otp request). This is the
                    // fallback label in the Account session list before/without the metadata
                    // heartbeat, and for old clients that never heartbeat. Static facts only
                    // (version + machine name); the wheel changes and can't live in an
                    // immutable UA. A malformed UA must never block sign-in, so it's guarded.
                    try
                    {
                        string ver = TrueforcePlugin.CurrentVersionString();
                        string machine = SanitizeUaToken(Environment.MachineName);
                        req.Headers.UserAgent.ParseAdd($"TrueforceForAll/{ver} ({machine}; Windows)");
                    }
                    catch (Exception uaEx) { _log?.Invoke($"[TF4ALL] Auth verify-otp UA skipped: {uaEx.Message}"); }
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            var classified = ClassifyAuthError((int)resp.StatusCode, respBody);
                            // F19: log the classified category, not the raw body.
                            _log?.Invoke($"[TF4ALL] Auth verify-otp failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {classified}");
                            return classified;
                        }
                        var session = ParseSession(respBody, email);
                        if (session == null) return AuthCallResult.Generic;
                        // B5/F07: bind the new session to this device + stamp
                        // sign-in time so the idle window starts at sign-in,
                        // not at the first token refresh.
                        session.DeviceFingerprint = DeviceFingerprint.Compute();
                        session.LastUsedAt = DateTime.UtcNow;
                        // If a DIFFERENT account is currently signed in, hand off through the
                        // anonymous slot FIRST so the incoming account seeds from the shared baseline,
                        // never the prior account's private library/feel. The shipped UI already forces
                        // a manual sign-out, but guarding here makes any future inline-reauth path safe
                        // too. Not a full SignOut() (no server logout): we're switching accounts on
                        // this device, not revoking the prior session.
                        var priorSession = _settingsProvider()?.AuthSession;
                        if (priorSession != null && !string.IsNullOrEmpty(priorSession.AccessToken)
                            && !string.Equals(priorSession.UserId ?? "", session.UserId ?? "", StringComparison.Ordinal))
                            FireIdentityChanged("");   // stashes the prior account into its slot + mounts anonymous (re-points the library to the baseline)
                        SaveSession(session);
                        // Successful fresh sign-in: clear any stale
                        // refresh-failure backoff so the next RPC can
                        // use the new token immediately (rather than
                        // waiting for the prior cooldown to elapse).
                        _lastRefreshFailureAt = null;
                        // Notify subscribers (slot manager + UI panels) that the active identity
                        // is now this account. Key by the immutable user-id (fall back to the email
                        // key only if the session somehow lacks one) so an email change can't orphan
                        // the slot.
                        FireIdentityChanged(
                            !string.IsNullOrEmpty(session.UserId) ? session.UserId : SlotKeyFromEmail(session.Email));
                        return AuthCallResult.Ok;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth verify-otp exception: {ex.Message}");
                return AuthCallResult.NetworkFailure;
            }
        }

        // Pull the retry-after seconds from a rate-limited (429) send-otp
        // response so the modal can count down to the real moment the next
        // request is allowed. Prefers the HTTP Retry-After header; falls
        // back to GoTrue's message body ("...you can only request this
        // after N seconds."). Returns null if neither is present or
        // parseable, in which case the caller uses its own default.
        private static int? ParseRetryAfterSeconds(HttpResponseMessage resp, string body)
        {
            try
            {
                var ra = resp?.Headers?.RetryAfter;
                if (ra != null)
                {
                    if (ra.Delta.HasValue)
                        return Math.Max(1, (int)Math.Ceiling(ra.Delta.Value.TotalSeconds));
                    if (ra.Date.HasValue)
                    {
                        double secs = (ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                        if (secs >= 1) return (int)Math.Ceiling(secs);
                    }
                }
            }
            catch { }
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    body ?? "", @"after\s+(\d+)\s+second");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > 0)
                    return n;
            }
            catch { }
            return null;
        }

        // Map Supabase auth error responses to our categorized result.
        // The body shape varies by endpoint / version; we look for
        // recognizable substrings rather than parsing strictly.
        private static AuthCallResult ClassifyAuthError(int status, string body)
        {
            if (status == 429) return AuthCallResult.RateLimited;
            string lower = (body ?? "").ToLowerInvariant();
            if (lower.Contains("rate") || lower.Contains("too many")
                || lower.Contains("frequency") || lower.Contains("wait"))
                return AuthCallResult.RateLimited;
            if (lower.Contains("expired")) return AuthCallResult.Expired;
            if (lower.Contains("invalid") && (lower.Contains("token")
                                              || lower.Contains("otp")
                                              || lower.Contains("code")))
                return AuthCallResult.BadCode;
            if (status >= 500) return AuthCallResult.NetworkFailure;
            return AuthCallResult.Generic;
        }

        // ---- Token refresh -------------------------------------------------

        /// <summary>Returns a valid access token, refreshing if within ~60s
        /// of expiry. Returns null when no session exists OR refresh
        /// permanently failed (in which case the session has been
        /// cleared).</summary>
        public async Task<string> GetAccessTokenAsync()
        {
            // B5 (reviewer fix): all decrypt + validation runs INSIDE
            // _refreshLock so concurrent SignOut / SaveSession(null)
            // callers cannot clear the session between our checks and
            // our return. The lock was already required for the refresh
            // path; extending it to cover the fast path costs a
            // semaphore wait (microseconds) per RPC. DPAPI Unprotect is
            // also microsecond-cheap relative to the network RPC it
            // gates, so we don't cache the decrypted token in memory.
            await _refreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var s = _settingsProvider()?.AuthSession;
                if (s == null || string.IsNullOrEmpty(s.AccessToken)) return null;

                // B5/F03: decrypt the access token. Legacy plaintext
                // tokens (no "dpapi:" marker) pass through unchanged so
                // installs that upgrade through B5 don't lose their
                // session. Decrypt failure on a marker-prefixed value
                // means the user's DPAPI scope changed (different user,
                // wiped profile, etc.); clear the session and force
                // re-sign-in.
                string decryptedAccessToken;
                if (DpapiCipher.IsProtected(s.AccessToken))
                {
                    decryptedAccessToken = DpapiCipher.Unprotect(s.AccessToken);
                    if (string.IsNullOrEmpty(decryptedAccessToken))
                    {
                        _log?.Invoke("[TF4ALL] Auth session decrypt failed; clearing session");
                        SaveSession(null);
                        return null;
                    }
                }
                else
                {
                    decryptedAccessToken = s.AccessToken;
                }

                // B5/F07: 30-day idle expiry. LastUsedAt == MinValue is
                // tolerated (legacy session pre-B5; the first successful
                // call below stamps a real timestamp).
                if (s.LastUsedAt != DateTime.MinValue
                    && DateTime.UtcNow - s.LastUsedAt > TimeSpan.FromDays(30))
                {
                    _log?.Invoke("[TF4ALL] Auth session idle expiry (30d); clearing session");
                    SaveSession(null);
                    return null;
                }

                // B5/F07: device binding. Empty fingerprint = legacy
                // session or unusual registry setup; skip the check
                // (fail-open) so we don't lock users out unexpectedly.
                if (!string.IsNullOrEmpty(s.DeviceFingerprint))
                {
                    string currentDevice = DeviceFingerprint.Compute();
                    if (!string.IsNullOrEmpty(currentDevice)
                        && !string.Equals(currentDevice, s.DeviceFingerprint, StringComparison.Ordinal))
                    {
                        _log?.Invoke("[TF4ALL] Auth device mismatch; clearing session");
                        SaveSession(null);
                        return null;
                    }
                }

                // B5/F01: JWT email claim vs persisted Settings.Email.
                // The persisted Email is what slot routing and UI display
                // use; if an attacker swapped Settings.AuthSession.Email
                // to spoof a different identity locally, the JWT's claim
                // still says the real owner. Compare both sides
                // case-insensitively (Supabase stores lowercased; JWTs
                // may preserve original case).
                string jwtEmail = JwtClaimDecoder.DecodeEmailClaim(decryptedAccessToken);
                if (!string.IsNullOrEmpty(jwtEmail)
                    && !string.IsNullOrEmpty(s.Email)
                    && !string.Equals(jwtEmail, s.Email, StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke("[TF4ALL] Auth email claim mismatch; clearing session");
                    SaveSession(null);
                    return null;
                }

                // Fast path: token is fresh enough. Stamp LastUsedAt and
                // return the decrypted bearer.
                if (s.ExpiresAt > DateTime.UtcNow.AddSeconds(60))
                {
                    s.LastUsedAt = DateTime.UtcNow;
                    SaveSession(s);
                    return decryptedAccessToken;
                }

                // Refresh cooldown: if we (or another caller) recently
                // failed to refresh, short-circuit to null so callers
                // take their unauthenticated / "not signed in" fallback
                // paths fast instead of stacking up multi-second
                // HttpClient waits while the backend is out.
                if (_lastRefreshFailureAt.HasValue
                    && DateTime.UtcNow < _lastRefreshFailureAt.Value.Add(RefreshCooldown))
                {
                    return null;
                }

                // Decrypt the refresh token for the refresh RPC.
                string refreshTokenPlain;
                if (DpapiCipher.IsProtected(s.RefreshToken))
                {
                    refreshTokenPlain = DpapiCipher.Unprotect(s.RefreshToken);
                    if (string.IsNullOrEmpty(refreshTokenPlain))
                    {
                        _log?.Invoke("[TF4ALL] Auth refresh-token decrypt failed; clearing session");
                        SaveSession(null);
                        _lastRefreshFailureAt = DateTime.UtcNow;
                        return null;
                    }
                }
                else
                {
                    refreshTokenPlain = s.RefreshToken;
                }
                if (string.IsNullOrEmpty(refreshTokenPlain))
                {
                    SaveSession(null);
                    _lastRefreshFailureAt = DateTime.UtcNow;
                    return null;
                }
                if (!ShouldRun(out var url, out var anonKey))
                    return decryptedAccessToken;  // best-effort; let the RPC try

                string body;
                try
                {
                    body = JsonConvert.SerializeObject(new { refresh_token = refreshTokenPlain });
                }
                catch
                {
                    _lastRefreshFailureAt = DateTime.UtcNow;
                    return null;
                }

                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + RefreshPath))
                    {
                        req.Headers.Add("apikey", anonKey);
                        req.Headers.Add("Authorization", "Bearer " + anonKey);
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                        {
                            string respBody = resp.Content != null
                                ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                                : "";
                            if (!resp.IsSuccessStatusCode)
                            {
                                // Only nuke the session on a DEFINITIVE
                                // refresh-token rejection. Generic 4xx
                                // (network blip, server overload, rate
                                // limit) leaves the saved session alone
                                // so a later retry can succeed. Wiping
                                // on every 4xx silently signed users
                                // out on transient errors.
                                // F19: log error category instead of raw response body to avoid leaking token fragments.
                                string errorCategory = ClassifyAuthError((int)resp.StatusCode, respBody).ToString();
                                _log?.Invoke($"[TF4ALL] Auth refresh failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errorCategory}");
                                if (IsDefinitiveRefreshRejection(resp.StatusCode, respBody))
                                    SaveSession(null);
                                _lastRefreshFailureAt = DateTime.UtcNow;
                                return null;
                            }
                            var refreshed = ParseSession(respBody, s.Email);
                            if (refreshed == null)
                            {
                                _lastRefreshFailureAt = DateTime.UtcNow;
                                return null;
                            }
                            // B5: preserve device binding + stamp idle
                            // clock on refresh. The new tokens get
                            // encrypted by SaveSession.
                            refreshed.DeviceFingerprint = s.DeviceFingerprint;
                            refreshed.LastUsedAt = DateTime.UtcNow;
                            string newPlainAccessToken = refreshed.AccessToken;
                            SaveSession(refreshed);
                            _lastRefreshFailureAt = null;
                            return newPlainAccessToken;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[TF4ALL] Auth refresh exception: {ex.Message}");
                    _lastRefreshFailureAt = DateTime.UtcNow;
                    return null;
                }
            }
            finally { _refreshLock.Release(); }
        }

        // ---- Profile / username --------------------------------------------

        /// <summary>Fetches the signed-in user's profile (id + username).
        /// Returns (signedIn=false, ...) when no session is active. Used
        /// by the post-sign-in flow to decide whether the user needs to
        /// pick a username.</summary>
        public async Task<(bool SignedIn, string UserId, string Username)> GetMyProfileAsync()
        {
            if (!ShouldRun(out var url, out var anonKey))
                return (false, null, null);
            string bearer = await GetAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (false, null, null);

            string body = "{}";
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    url.TrimEnd('/') + "/rest/v1/rpc/get_my_profile"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return (false, null, null);
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var root = JToken.Parse(respBody);
                        var obj = root.Type == JTokenType.Array ? root[0] : root;
                        bool signedIn = obj?["signed_in"]?.ToObject<bool>() ?? false;
                        string uid = obj?["user_id"]?.ToString();
                        string uname = obj?["username"]?.ToString();
                        return (signedIn, uid, uname);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth get-my-profile exception: {ex.Message}");
                return (false, null, null);
            }
        }

        /// <summary>Server-side availability check. Returns a categorized
        /// reason so the modal can show "taken" vs "format invalid" vs
        /// "reserved" without guessing.</summary>
        public async Task<UsernameAvailability> CheckUsernameAvailableAsync(string username)
        {
            if (!ShouldRun(out var url, out var anonKey)) return UsernameAvailability.Network;
            if (string.IsNullOrWhiteSpace(username)) return UsernameAvailability.Format;
            // Bearer is optional - check_username_available returns
            // self=true when the caller is signed in and owns the name.
            string bearer = await GetAccessTokenAsync().ConfigureAwait(false);

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new { p_username = username.Trim() });
            }
            catch { return UsernameAvailability.Network; }

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    url.TrimEnd('/') + "/rest/v1/rpc/check_username_available"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + (bearer ?? anonKey));
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return UsernameAvailability.Network;
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var root = JToken.Parse(respBody);
                        var obj = root.Type == JTokenType.Array ? root[0] : root;
                        bool available = obj?["available"]?.ToObject<bool>() ?? false;
                        string reason = obj?["reason"]?.ToString();
                        if (available)
                            return string.Equals(reason, "self", StringComparison.OrdinalIgnoreCase)
                                ? UsernameAvailability.SelfOwned
                                : UsernameAvailability.Available;
                        switch ((reason ?? "").ToLowerInvariant())
                        {
                            case "format":    return UsernameAvailability.Format;
                            case "reserved":  return UsernameAvailability.Reserved;
                            case "taken":     return UsernameAvailability.Taken;
                            case "profanity": return UsernameAvailability.Profanity;
                            default:          return UsernameAvailability.Format;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth check-username exception: {ex.Message}");
                return UsernameAvailability.Network;
            }
        }

        /// <summary>Claim a username. Auth required. Returns Ok on
        /// success; Taken / Format / Reserved on validation failure;
        /// NetworkFailure / Generic on transport issues.</summary>
        public async Task<UsernameAvailability> SetUsernameAsync(string username)
        {
            if (!ShouldRun(out var url, out var anonKey)) return UsernameAvailability.Network;
            if (string.IsNullOrWhiteSpace(username)) return UsernameAvailability.Format;
            string bearer = await GetAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return UsernameAvailability.Network;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new { p_username = username.Trim() });
            }
            catch { return UsernameAvailability.Network; }

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    url.TrimEnd('/') + "/rest/v1/rpc/set_username"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode) return UsernameAvailability.Available;
                        // Map server errors back to categories. Postgres
                        // 23505 = unique_violation -> Taken; otherwise
                        // surface as Generic-via-Format.
                        if (respBody.IndexOf("23505", StringComparison.Ordinal) >= 0
                            || respBody.IndexOf("taken", StringComparison.OrdinalIgnoreCase) >= 0)
                            return UsernameAvailability.Taken;
                        if (respBody.IndexOf("reserved", StringComparison.OrdinalIgnoreCase) >= 0)
                            return UsernameAvailability.Reserved;
                        if (respBody.IndexOf("format", StringComparison.OrdinalIgnoreCase) >= 0)
                            return UsernameAvailability.Format;
                        _log?.Invoke($"[TF4ALL] Auth set-username failed: {(int)resp.StatusCode} {respBody}");
                        return UsernameAvailability.Network;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth set-username exception: {ex.Message}");
                return UsernameAvailability.Network;
            }
        }

        // ---- Change email --------------------------------------------------

        /// <summary>Initiate an email change via Supabase Auth's PUT
        /// /auth/v1/user endpoint. Server sends a confirmation email to
        /// the NEW address; the change takes effect after the user
        /// clicks the confirmation link. Until then the old address keeps
        /// working. Returns categorized result for UI copy.</summary>
        public async Task<AuthCallResult> UpdateEmailAsync(string newEmail)
        {
            if (!ShouldRun(out var url, out var anonKey)) return AuthCallResult.NetworkFailure;
            if (string.IsNullOrWhiteSpace(newEmail)) return AuthCallResult.InvalidInput;
            newEmail = newEmail.Trim().ToLowerInvariant();
            string bearer = await GetAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return AuthCallResult.NetworkFailure;

            string body;
            try { body = JsonConvert.SerializeObject(new { email = newEmail }); }
            catch { return AuthCallResult.Generic; }

            try
            {
                using (var req = new HttpRequestMessage(new HttpMethod("PUT"), url.TrimEnd('/') + "/auth/v1/user"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            // F25: a change-email request often signals "I
                            // suspect this account is compromised" - so
                            // invalidate every OTHER active session for the
                            // user. Supabase's GoTrue logout endpoint
                            // supports scope=others which leaves the current
                            // session alone while revoking everyone else's
                            // refresh tokens. Fire-and-forget; the email
                            // change itself already succeeded and we don't
                            // want a logout RPC blip to mask that.
                            _ = InvalidateOtherSessionsAsync(url, anonKey, bearer);
                            return AuthCallResult.Ok;
                        }
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        var classified = ClassifyAuthError((int)resp.StatusCode, detail);
                        // F19: log the classified category, not the raw body.
                        _log?.Invoke($"[TF4ALL] Auth update-email failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {classified}");
                        return classified;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth update-email exception: {ex.Message}");
                return AuthCallResult.NetworkFailure;
            }
        }

        // Best-effort POST /auth/v1/logout?scope=others. Revokes every
        // refresh token for the user EXCEPT the one tied to this bearer
        // (our current session stays valid). Failures only log; the
        // caller has already committed the success-side state.
        private async Task InvalidateOtherSessionsAsync(string url, string anonKey, string bearer)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + LogoutPath + "?scope=others"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            _log?.Invoke($"[TF4ALL] Invalidate-other-sessions failed: {(int)resp.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Invalidate-other-sessions exception: {ex.Message}");
            }
        }

        /// <summary>User-initiated "sign out everywhere else". Revokes every OTHER
        /// active session's refresh token for this user, leaving the current device
        /// signed in. Returns true on a successful server logout. The other devices
        /// keep their current access token until it expires (&lt;= 1h), then can no
        /// longer refresh - same semantics as the per-session revoke RPC.</summary>
        public async Task<bool> SignOutOtherSessionsAsync()
        {
            if (!ShouldRun(out var url, out var anonKey)) return false;
            string bearer = await GetAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return false;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + LogoutPath + "?scope=others"))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            _log?.Invoke($"[TF4ALL] Sign-out-others failed: {(int)resp.StatusCode}");
                        return resp.IsSuccessStatusCode;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Sign-out-others exception: {ex.Message}");
                return false;
            }
        }

        // ---- Session info from JWT ----------------------------------------

        /// <summary>Decode the session JWT's claims for display in the
        /// Account expander. Returns issued-at + expires-at + email +
        /// session-id when available; nulls otherwise. No server
        /// round-trip; reads the cached access_token payload.</summary>
        public (DateTime? IssuedAt, DateTime? ExpiresAt, string SessionId) GetCurrentSessionInfo()
        {
            var s = _settingsProvider()?.AuthSession;
            if (s == null || string.IsNullOrEmpty(s.AccessToken)) return (null, null, null);
            try
            {
                // B5/F03: persisted token may be DPAPI-wrapped; decrypt
                // for the JWT decode. Legacy plaintext passes through.
                string accessToken = s.AccessToken;
                if (DpapiCipher.IsProtected(accessToken))
                {
                    accessToken = DpapiCipher.Unprotect(accessToken);
                    if (string.IsNullOrEmpty(accessToken)) return (null, null, null);
                }
                // JWT format: header.payload.signature, payload is base64url-encoded JSON.
                string[] parts = accessToken.Split('.');
                if (parts.Length < 2) return (null, null, null);
                string payload = parts[1];
                // base64url -> base64 padding fix
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                byte[] decoded = Convert.FromBase64String(payload);
                string json = Encoding.UTF8.GetString(decoded);
                var claims = JObject.Parse(json);
                long iat = claims["iat"]?.ToObject<long>() ?? 0;
                long exp = claims["exp"]?.ToObject<long>() ?? 0;
                string sessionId = claims["session_id"]?.ToString();
                return (
                    iat > 0 ? DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime : (DateTime?)null,
                    exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : (DateTime?)null,
                    sessionId);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Session info decode failed: {ex.Message}");
                return (null, null, null);
            }
        }

        // ---- Sign out ------------------------------------------------------

        /// <summary>Drop the local session. Best-effort server logout
        /// (fire-and-forget) so the refresh_token gets invalidated;
        /// failure doesn't block the local clear. Also clears the
        /// cached SharingAuthor so the next sign-up's username picker
        /// doesn't pre-fill with the prior account's name.</summary>
        public void SignOut()
        {
            var settings = _settingsProvider();
            var s = settings?.AuthSession;
            if (s == null) return;
            // B5/F03 (reviewer fix): the persisted AccessToken may be
            // DPAPI-wrapped; the logout RPC needs the plaintext bearer.
            // Decrypt before clearing the session. If decrypt fails on
            // a marker-prefixed value we skip the server logout (the
            // local clear still happens below).
            string token = s.AccessToken;
            if (DpapiCipher.IsProtected(token))
            {
                token = DpapiCipher.Unprotect(token);
            }
            SaveSession(null);
            // Sign-out resets the refresh-failure backoff so a follow-up
            // sign-in attempt doesn't inherit a stale cooldown.
            _lastRefreshFailureAt = null;
            // Identity change fires BEFORE per-slot data is moved. The
            // slot manager subscriber will stash the current user's
            // data under their key and mount the anonymous slot so
            // SharingAuthor + DownloadedCommunityPresets reflect the
            // anon view from here on (and the prior user's data is
            // preserved for re-sign-in, not lost).
            FireIdentityChanged("");
            if (settings != null)
            {
                try { _persistSettings?.Invoke(); } catch { }
            }

            if (!ShouldRun(out var url, out var anonKey) || string.IsNullOrEmpty(token))
                return;
            Task.Run(async () =>
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + LogoutPath))
                    {
                        req.Headers.Add("apikey", anonKey);
                        req.Headers.Add("Authorization", "Bearer " + token);
                        using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                                _log?.Invoke($"[TF4ALL] Auth sign-out (server) failed: {(int)resp.StatusCode}");
                        }
                    }
                }
                catch (Exception ex) { _log?.Invoke($"[TF4ALL] Auth sign-out exception: {ex.Message}"); }
            });
        }

        // ---- Helpers -------------------------------------------------------

        // Recognize the GoTrue error codes that mean "this refresh
        // token is permanently dead": revoked, used twice past the
        // rotation reuse interval, user no longer exists. Everything
        // else (network blips, 500s, rate limits, the occasional 403
        // from a JWT skew) is treated as transient.
        private static bool IsDefinitiveRefreshRejection(
            System.Net.HttpStatusCode status, string respBody)
        {
            int code = (int)status;
            if (code < 400 || code >= 500) return false;
            if (string.IsNullOrEmpty(respBody)) return false;
            // GoTrue returns JSON with either `error` (legacy) or
            // `error_code` / `code` (current). Match the strings that
            // mean the refresh token cannot recover.
            string lower = respBody.ToLowerInvariant();
            return lower.Contains("invalid_grant")
                || lower.Contains("refresh_token_not_found")
                || lower.Contains("refresh_token_already_used")
                || lower.Contains("user_not_found")
                || lower.Contains("user_banned");
        }

        private CommunityAuthSession ParseSession(string respBody, string fallbackEmail)
        {
            try
            {
                var root = JObject.Parse(respBody);
                string access  = root["access_token"]?.ToString();
                string refresh = root["refresh_token"]?.ToString();
                if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
                    return null;

                int expiresIn = root["expires_in"]?.ToObject<int>() ?? 3600;
                long expiresAtUnix = root["expires_at"]?.ToObject<long>() ?? 0;
                DateTime expiresAt = expiresAtUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix).UtcDateTime
                    : DateTime.UtcNow.AddSeconds(expiresIn);

                string email = root["user"]?["email"]?.ToString() ?? fallbackEmail;
                string userId = root["user"]?["id"]?.ToString();

                return new CommunityAuthSession
                {
                    AccessToken  = access,
                    RefreshToken = refresh,
                    UserId       = userId,
                    Email        = email,
                    ExpiresAt    = expiresAt,
                };
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Auth session parse failed: {ex.Message}");
                return null;
            }
        }

        private void SaveSession(CommunityAuthSession session)
        {
            var s = _settingsProvider();
            if (s == null) return;
            if (session != null)
            {
                // B5/F03: encrypt tokens at rest before persist. Protect is
                // idempotent (an already-"dpapi:"-wrapped value returns
                // unchanged) and returns null only for an empty input or a
                // genuine DPAPI failure.
                string protectedAccess  = DpapiCipher.Protect(session.AccessToken);
                string protectedRefresh = DpapiCipher.Protect(session.RefreshToken);
                // M15: fail CLOSED. If a NON-empty token could not be
                // protected (DPAPI threw), do not persist it in the clear;
                // drop the whole session so the next launch starts signed
                // out and the user re-authenticates.
                bool accessFailed  = !string.IsNullOrEmpty(session.AccessToken)  && string.IsNullOrEmpty(protectedAccess);
                bool refreshFailed = !string.IsNullOrEmpty(session.RefreshToken) && string.IsNullOrEmpty(protectedRefresh);
                if (accessFailed || refreshFailed)
                {
                    _log?.Invoke("[TF4ALL] Token encryption unavailable (DPAPI); clearing session instead of storing plaintext.");
                    session = null;
                }
                else
                {
                    if (!string.IsNullOrEmpty(protectedAccess))  session.AccessToken  = protectedAccess;
                    if (!string.IsNullOrEmpty(protectedRefresh)) session.RefreshToken = protectedRefresh;
                }
            }
            s.AuthSession = session;
            try { _persistSettings?.Invoke(); }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Auth session persist failed: {ex.Message}"); }
        }

        // User-Agent comment tokens are strict about characters (no spaces/parens/etc.);
        // keep [A-Za-z0-9._-] so Headers.UserAgent.ParseAdd accepts the machine name, and
        // fall back to "PC" if it sanitizes to empty.
        private static string SanitizeUaToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "PC";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')
                    sb.Append(c);
            return sb.Length == 0 ? "PC" : sb.ToString();
        }

        private bool ShouldRun(out string url, out string anonKey)
        {
            url = ""; anonKey = "";
            var s = _settingsProvider();
            if (s == null) return false;
            url = (s.CommunityBackendUrl ?? "").Trim();
            anonKey = (s.CommunityBackendAnonKey ?? "").Trim();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(anonKey)) return false;
            if (!ChannelValidation.IsTrustedSupabaseUrl(url))
            {
                _log?.Invoke($"[TF4ALL] Rejecting untrusted backend URL: {url}");
                return false;
            }
            return true;
        }

        // Email -> stable lower-cased slot key. "" = anonymous.
        internal static string SlotKeyFromEmail(string email)
            => string.IsNullOrEmpty(email) ? "" : email.Trim().ToLowerInvariant();

        private void FireIdentityChanged(string newKey)
        {
            try { ActiveIdentityChanged?.Invoke(newKey ?? ""); }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] ActiveIdentityChanged subscriber threw: {ex.Message}"); }
        }
    }
}
