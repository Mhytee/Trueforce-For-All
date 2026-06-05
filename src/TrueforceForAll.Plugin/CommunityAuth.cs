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

        // Serialize refreshes so two concurrent RPCs don't both try to
        // burn the refresh_token.
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

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
                _log?.Invoke($"[Trueforce] Auth send-otp serialize failed: {ex.Message}");
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
                        _log?.Invoke($"[Trueforce] Auth send-otp failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return ClassifyAuthError((int)resp.StatusCode, detail);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Auth send-otp exception: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] Auth verify-otp serialize failed: {ex.Message}");
                return AuthCallResult.Generic;
            }

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + VerifyPath))
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
                            _log?.Invoke($"[Trueforce] Auth verify-otp failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            return ClassifyAuthError((int)resp.StatusCode, respBody);
                        }
                        var session = ParseSession(respBody, email);
                        if (session == null) return AuthCallResult.Generic;
                        SaveSession(session);
                        return AuthCallResult.Ok;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Auth verify-otp exception: {ex.Message}");
                return AuthCallResult.NetworkFailure;
            }
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
            var s = _settingsProvider()?.AuthSession;
            if (s == null || string.IsNullOrEmpty(s.AccessToken)) return null;

            if (s.ExpiresAt > DateTime.UtcNow.AddSeconds(60))
                return s.AccessToken;

            // Near expiry - try to refresh once. Serialized so concurrent
            // RPCs don't both burn the refresh_token.
            await _refreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-read after taking the lock; another caller may have
                // refreshed already.
                s = _settingsProvider()?.AuthSession;
                if (s == null || string.IsNullOrEmpty(s.AccessToken)) return null;
                if (s.ExpiresAt > DateTime.UtcNow.AddSeconds(60))
                    return s.AccessToken;
                if (string.IsNullOrEmpty(s.RefreshToken))
                {
                    SaveSession(null);
                    return null;
                }
                if (!ShouldRun(out var url, out var anonKey))
                    return s.AccessToken;  // best-effort; let the RPC try

                string body;
                try
                {
                    body = JsonConvert.SerializeObject(new { refresh_token = s.RefreshToken });
                }
                catch { return null; }

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
                                // 4xx on refresh = the token is dead.
                                // Clear the session so the UI prompts a
                                // fresh sign-in.
                                _log?.Invoke($"[Trueforce] Auth refresh failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                                if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500)
                                    SaveSession(null);
                                return null;
                            }
                            var refreshed = ParseSession(respBody, s.Email);
                            if (refreshed == null) return null;
                            SaveSession(refreshed);
                            return refreshed.AccessToken;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Trueforce] Auth refresh exception: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] Auth get-my-profile exception: {ex.Message}");
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
                            case "format":   return UsernameAvailability.Format;
                            case "reserved": return UsernameAvailability.Reserved;
                            case "taken":    return UsernameAvailability.Taken;
                            default:         return UsernameAvailability.Format;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Auth check-username exception: {ex.Message}");
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
                        _log?.Invoke($"[Trueforce] Auth set-username failed: {(int)resp.StatusCode} {respBody}");
                        return UsernameAvailability.Network;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Auth set-username exception: {ex.Message}");
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
                        if (resp.IsSuccessStatusCode) return AuthCallResult.Ok;
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        _log?.Invoke($"[Trueforce] Auth update-email failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return ClassifyAuthError((int)resp.StatusCode, detail);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Auth update-email exception: {ex.Message}");
                return AuthCallResult.NetworkFailure;
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
                // JWT format: header.payload.signature, payload is base64url-encoded JSON.
                string[] parts = s.AccessToken.Split('.');
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
                _log?.Invoke($"[Trueforce] Session info decode failed: {ex.Message}");
                return (null, null, null);
            }
        }

        // ---- Sign out ------------------------------------------------------

        /// <summary>Drop the local session. Best-effort server logout
        /// (fire-and-forget) so the refresh_token gets invalidated;
        /// failure doesn't block the local clear.</summary>
        public void SignOut()
        {
            var s = _settingsProvider()?.AuthSession;
            if (s == null) return;
            string token = s.AccessToken;
            SaveSession(null);

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
                                _log?.Invoke($"[Trueforce] Auth sign-out (server) failed: {(int)resp.StatusCode}");
                        }
                    }
                }
                catch (Exception ex) { _log?.Invoke($"[Trueforce] Auth sign-out exception: {ex.Message}"); }
            });
        }

        // ---- Helpers -------------------------------------------------------

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
                _log?.Invoke($"[Trueforce] Auth session parse failed: {ex.Message}");
                return null;
            }
        }

        private void SaveSession(CommunityAuthSession session)
        {
            var s = _settingsProvider();
            if (s == null) return;
            s.AuthSession = session;
            try { _persistSettings?.Invoke(); }
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Auth session persist failed: {ex.Message}"); }
        }

        private bool ShouldRun(out string url, out string anonKey)
        {
            url = ""; anonKey = "";
            var s = _settingsProvider();
            if (s == null) return false;
            url = (s.CommunityBackendUrl ?? "").Trim();
            anonKey = (s.CommunityBackendAnonKey ?? "").Trim();
            return !(string.IsNullOrEmpty(url) || string.IsNullOrEmpty(anonKey));
        }
    }
}
