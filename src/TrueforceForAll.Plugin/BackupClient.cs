// Phase 2 backup/sync, milestone M2: storage transport for the per-user backup
// envelope. Uploads/downloads a single JSON object at
//   backups/<user_id>/setup.json
// over the Supabase Storage REST API, authenticated with the signed-in user's JWT
// so the owner-scoped RLS on storage.objects (migration 0033) lets them touch only
// their own '<user_id>/' prefix.
//
// Reuses the static-HttpClient + apikey/Bearer header pattern from
// PresetSharingClient / CommunityClient. Transport ONLY: the caller builds and
// serializes the BackupEnvelope (BackupProjection + BackupLibrary) and applies it
// on restore. Gated by a valid session (and, from M3, supporter status), NOT by the
// community-browse toggle, so it intentionally does not require CommunityEnabled.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>Outcome of a backup storage transfer, mapped from the HTTP status so
    /// the UI can show specific copy (sign-in expired / not a supporter / nothing to
    /// restore / network).</summary>
    internal enum BackupTransfer
    {
        Success,
        NotSignedIn,    // no session / expired token / HTTP 401
        NotConfigured,  // backend url/key/uid missing or untrusted
        Forbidden,      // RLS denied (e.g. not a supporter once M3 gates uploads), 403
        TooLarge,       // 413
        NotFound,       // 404 (download: no backup exists yet)
        ServerError,    // 5xx or unexpected 4xx
        NetworkError,   // timeout / socket / exception
    }

    internal sealed class BackupClient
    {
        public const string BucketId   = "backups";
        public const string ObjectName = "setup.json";

        // Backups can be a few MB; allow a generous timeout vs the RPC clients.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90),
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public BackupClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>Create or overwrite the user's backup envelope.</summary>
        public async Task<BackupTransfer> UploadAsync(string envelopeJson, int timeoutMs = 90000)
        {
            if (string.IsNullOrEmpty(envelopeJson)) return BackupTransfer.ServerError;
            if (!TryResolve(out string baseUrl, out string anonKey, out string keyPath))
                return BackupTransfer.NotConfigured;
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return BackupTransfer.NotSignedIn;

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    baseUrl + "/storage/v1/object/" + keyPath))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Headers.Add("x-upsert", "true");   // overwrite the prior backup
                    req.Content = new StringContent(envelopeJson, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        return await ClassifyAsync("upload", resp).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Backup upload exception: {ex.Message}");
                return BackupTransfer.NetworkError;
            }
        }

        /// <summary>Download the user's backup envelope JSON. result == NotFound means
        /// no backup exists yet (a clean "nothing to restore", not an error).</summary>
        public async Task<(BackupTransfer result, string json)> DownloadAsync(int timeoutMs = 90000)
        {
            if (!TryResolve(out string baseUrl, out string anonKey, out string keyPath))
                return (BackupTransfer.NotConfigured, null);
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (BackupTransfer.NotSignedIn, null);

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Get,
                    baseUrl + "/storage/v1/object/authenticated/" + keyPath))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        var classified = await ClassifyAsync("download", resp).ConfigureAwait(false);
                        if (classified != BackupTransfer.Success) return (classified, null);
                        string json = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : null;
                        return (BackupTransfer.Success, json);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Backup download exception: {ex.Message}");
                return (BackupTransfer.NetworkError, null);
            }
        }

        /// <summary>Delete the user's backup object (account deletion / opt-out).
        /// A missing object is treated as success.</summary>
        public async Task<BackupTransfer> DeleteAsync(int timeoutMs = 30000)
        {
            if (!TryResolve(out string baseUrl, out string anonKey, out string keyPath))
                return BackupTransfer.NotConfigured;
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return BackupTransfer.NotSignedIn;

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Delete,
                    baseUrl + "/storage/v1/object/" + keyPath))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        var c = await ClassifyAsync("delete", resp).ConfigureAwait(false);
                        return c == BackupTransfer.NotFound ? BackupTransfer.Success : c;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Backup delete exception: {ex.Message}");
                return BackupTransfer.NetworkError;
            }
        }

        /// <summary>Cheaply read the cloud backup's current revision token (the Storage
        /// object `version`, which changes on every write) WITHOUT downloading the body.
        /// result == NotFound means no backup exists yet. Used for the
        /// fast-forward-vs-diverged decision before a manual backup / on auto-sync.</summary>
        public async Task<(BackupTransfer result, string revision)> GetRevisionAsync(int timeoutMs = 30000)
        {
            if (!TryResolve(out string baseUrl, out string anonKey, out string keyPath))
                return (BackupTransfer.NotConfigured, null);
            string bearer = await GetBearerAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return (BackupTransfer.NotSignedIn, null);

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Get,
                    baseUrl + "/storage/v1/object/info/authenticated/" + keyPath))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        var classified = await ClassifyAsync("info", resp).ConfigureAwait(false);
                        if (classified != BackupTransfer.Success) return (classified, null);
                        string body = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        string rev = ExtractRevision(body);
                        if (rev == null)
                            _log?.Invoke("[Trueforce] Backup info: no revision token in object metadata "
                                + "(a false 'cloud changed' prompt may follow). Body: " + Trunc(body));
                        return (BackupTransfer.Success, rev);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Backup info exception: {ex.Message}");
                return (BackupTransfer.NetworkError, null);
            }
        }

        // The Storage object-info response carries a per-write `version` (uuid) plus
        // updated_at / etag. Prefer `version` as the opaque revision token; fall back
        // defensively so a field-name change upstream doesn't break divergence checks.
        private static string ExtractRevision(string infoJson)
        {
            if (string.IsNullOrEmpty(infoJson)) return null;
            try
            {
                var o = JObject.Parse(infoJson);
                foreach (var key in new[] { "version", "id", "etag", "updated_at", "lastModified" })
                {
                    var v = o[key];
                    if (v != null && v.Type != JTokenType.Null)
                    {
                        string s = v.ToString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
            catch { /* non-JSON / unexpected shape */ }
            return null;
        }

        private async Task<BackupTransfer> ClassifyAsync(string op, HttpResponseMessage resp)
        {
            if (resp.IsSuccessStatusCode) return BackupTransfer.Success;

            string body = "";
            try { body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : ""; }
            catch { /* ignore body read failure */ }

            // Supabase Storage frequently returns HTTP 400 with the REAL code in the
            // JSON body ({"statusCode":"403"|"404", "error":..., "message":...}), so
            // trust the body's statusCode when present and fall back to the HTTP code.
            int code = (int)resp.StatusCode;
            try
            {
                if (!string.IsNullOrEmpty(body))
                {
                    var sc = JObject.Parse(body)["statusCode"];
                    // Only trust the body's statusCode for known HTTP error codes (don't
                    // let an unexpected/spoofed value drive classification).
                    if (sc != null && int.TryParse(sc.ToString(), out int parsed)
                        && (parsed == 401 || parsed == 403 || parsed == 404 || parsed == 413
                            || (parsed >= 500 && parsed < 600)))
                        code = parsed;
                }
            }
            catch { /* non-JSON body: keep the transport status code */ }

            _log?.Invoke($"[Trueforce] Backup {op} failed: {code} {Trunc(body)}");
            switch (code)
            {
                case 401: return BackupTransfer.NotSignedIn;
                case 403: return BackupTransfer.Forbidden;
                case 404: return BackupTransfer.NotFound;
                case 413: return BackupTransfer.TooLarge;
                default:  return BackupTransfer.ServerError;
            }
        }

        private static string Trunc(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s.Substring(0, 300));

        private async Task<string> GetBearerAsync()
        {
            if (_accessTokenProvider == null) return null;
            try { return await _accessTokenProvider().ConfigureAwait(false); }
            catch { return null; }
        }

        // Resolves the trusted Supabase base URL + anon key + the object key path
        // 'backups/<user_id>/setup.json'. Does NOT require CommunityEnabled (backup is
        // its own auth-gated feature, distinct from community browsing).
        private bool TryResolve(out string baseUrl, out string anonKey, out string keyPath)
        {
            baseUrl = null; anonKey = null; keyPath = null;
            var s = _settingsProvider();
            if (s == null) return false;

            string url    = (s.CommunityBackendUrl ?? "").Trim();
            string key    = (s.CommunityBackendAnonKey ?? "").Trim();
            string userId = (s.AuthSession?.UserId ?? "").Trim();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(userId))
                return false;
            if (!ChannelValidation.IsTrustedSupabaseUrl(url))
            {
                _log?.Invoke($"[Trueforce] Backup: rejecting untrusted backend URL: {url}");
                return false;
            }

            baseUrl = url.TrimEnd('/');
            anonKey = key;
            // userId is a uuid (no path-sensitive chars); escape defensively anyway.
            keyPath = BucketId + "/" + Uri.EscapeDataString(userId) + "/" + ObjectName;
            return true;
        }
    }
}
