// Moderation-notice transport. Fetches the signed-in user's moderation notices
// (warn / removal / ban left by a moderator), marks them acknowledged, and
// submits an appeal ("request review") with a free-text note. All three go
// through SECURITY DEFINER RPCs (migration 0082) scoped server-side to
// auth.uid(); the client never sees anyone else's notices. Mirrors the
// static-HttpClient + apikey/Bearer + trusted-URL pattern of SessionClient.

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
    internal sealed class ModerationClient
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly Func<Task<string>>      _accessTokenProvider;

        public ModerationClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, Func<Task<string>> accessTokenProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _accessTokenProvider = accessTokenProvider;
        }

        /// <summary>One moderator notice shown to the affected uploader.</summary>
        public sealed class NoticeRow
        {
            public string    Id;
            public string    TargetType;     // preset/game_preset/custom_engine/pack, or null for a ban
            public string    TargetId;
            public string    Kind;           // warn | removal | ban
            public string    ReasonCategory;
            public string    Message;
            public DateTime?  CreatedAt;
            public bool      Acknowledged;
            public string    AppealState;    // none | pending | approved | denied
            public bool      Appealable = true; // false = final removal (no review path)
            public string    ItemName;       // resolved upload name (null for bans)
            public string    ItemDescription; // current description, to prefill the fix form

            public bool IsPending => string.Equals(AppealState, "none", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(AppealState, "denied", StringComparison.OrdinalIgnoreCase);
            // A per-item notice the user can act on with a proposed fix.
            public bool HasTarget => !string.IsNullOrEmpty(TargetId)
                                  && !string.Equals(Kind, "ban", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The caller's notices, newest first. Null on not-configured /
        /// not-signed-in / unreachable; empty list when there are none.</summary>
        public async Task<List<NoticeRow>> GetMyNoticesAsync(CancellationToken ct)
        {
            var (ok, body) = await PostAsync("/rest/v1/rpc/get_my_moderation_notices", "{}", ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return null;
            try
            {
                var arr = JArray.Parse(body);
                var list = new List<NoticeRow>(arr.Count);
                foreach (var t in arr)
                {
                    if (!(t is JObject o)) continue;
                    list.Add(new NoticeRow
                    {
                        Id             = (string)o["id"],
                        TargetType     = (string)o["target_type"],
                        TargetId       = (string)o["target_id"],
                        Kind           = (string)o["kind"],
                        ReasonCategory = (string)o["reason_category"],
                        Message        = (string)o["message"],
                        CreatedAt      = AsUtc(o["created_at"]),
                        Acknowledged   = o["acknowledged_at"] != null && o["acknowledged_at"].Type != JTokenType.Null,
                        AppealState    = (string)o["appeal_state"],
                        Appealable     = o["appealable"] == null || o["appealable"].Type == JTokenType.Null || o["appealable"].ToObject<bool>(),
                        ItemName       = (string)o["item_name"],
                        ItemDescription = (string)o["item_description"],
                    });
                }
                return list;
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Notices parse error: {ex.Message}"); return null; }
        }

        /// <summary>Mark a notice acknowledged so it won't pop again. True if a row changed.</summary>
        public async Task<bool> AcknowledgeAsync(string noticeId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(noticeId)) return false;
            string payload = new JObject { ["p_id"] = noticeId }.ToString(Newtonsoft.Json.Formatting.None);
            var (ok, body) = await PostAsync("/rest/v1/rpc/acknowledge_moderation_notice", payload, ct).ConfigureAwait(false);
            return ok && ParseBool(body);
        }

        /// <summary>Submit an appeal: a note plus an optional proposed fix (new
        /// name/description). Flips the notice to pending and (server trigger)
        /// posts an appeal card showing the old->new diff. On Reinstate the mod
        /// applies the proposed values. True if a row changed (false if the
        /// notice wasn't appealable). proposedName/Description null = leave as-is.</summary>
        public async Task<bool> RequestReviewAsync(string noticeId, string note,
            string proposedName, string proposedDescription, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(noticeId)) return false;
            var payload = new JObject { ["p_id"] = noticeId };
            if (!string.IsNullOrWhiteSpace(note))               payload["p_note"] = note.Trim();
            if (!string.IsNullOrWhiteSpace(proposedName))        payload["p_proposed_name"] = proposedName.Trim();
            if (!string.IsNullOrWhiteSpace(proposedDescription)) payload["p_proposed_description"] = proposedDescription.Trim();
            var (ok, body) = await PostAsync("/rest/v1/rpc/request_review",
                payload.ToString(Newtonsoft.Json.Formatting.None), ct).ConfigureAwait(false);
            return ok && ParseBool(body);
        }

        /// <summary>One of the caller's own uploads, INCLUDING suppressed ones
        /// (the normal listing hides those). state = live | suspended |
        /// under_review | removed. NoticeId points at the latest moderation
        /// notice so the UI can jump into the fix/appeal flow from a greyed row.</summary>
        public sealed class MyUpload
        {
            public string Kind, Id, Name, Description, NoticeId, AppealState, State;
            public bool   IsSuppressed, SuppressedByBan;
            public bool   Appealable = true;   // false = final removal (no review path)
            public int    Downloads;
            public int    EntryCount;          // packs only (item count); 0 for other kinds
        }

        /// <summary>The caller's uploads across all kinds, suppressed ones
        /// included with a state tag. Null on not-configured / not-signed-in.</summary>
        public async Task<List<MyUpload>> GetMyUploadsAsync(CancellationToken ct)
        {
            var (ok, body) = await PostAsync("/rest/v1/rpc/get_my_uploads", "{}", ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(body)) return null;
            try
            {
                var arr = JArray.Parse(body);
                var list = new List<MyUpload>(arr.Count);
                foreach (var t in arr)
                {
                    if (!(t is JObject o)) continue;
                    list.Add(new MyUpload
                    {
                        Kind            = (string)o["kind"],
                        Id              = (string)o["id"],
                        Name            = (string)o["name"],
                        Description     = (string)o["description"],
                        IsSuppressed    = o["is_suppressed"] != null && o["is_suppressed"].Type == JTokenType.Boolean && o["is_suppressed"].ToObject<bool>(),
                        SuppressedByBan = o["suppressed_by_ban"] != null && o["suppressed_by_ban"].Type == JTokenType.Boolean && o["suppressed_by_ban"].ToObject<bool>(),
                        Downloads       = o["downloads"] != null && o["downloads"].Type == JTokenType.Integer ? o["downloads"].ToObject<int>() : 0,
                        NoticeId        = (string)o["notice_id"],
                        AppealState     = (string)o["appeal_state"],
                        Appealable      = o["appealable"] == null || o["appealable"].Type == JTokenType.Null || o["appealable"].ToObject<bool>(),
                        State           = (string)o["state"],
                        EntryCount      = o["entry_count"] != null && o["entry_count"].Type == JTokenType.Integer ? o["entry_count"].ToObject<int>() : 0,
                    });
                }
                return list;
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] My uploads parse error: {ex.Message}"); return null; }
        }

        private static bool ParseBool(string body)
        {
            try { var t = JToken.Parse(string.IsNullOrEmpty(body) ? "false" : body); return t.Type == JTokenType.Boolean && t.Value<bool>(); }
            catch { return false; }
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
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(userId)) return false;
            if (!ChannelValidation.IsTrustedSupabaseUrl(url))
            {
                _log?.Invoke($"[TF4ALL] Moderation: rejecting untrusted backend URL: {url}");
                return false;
            }
            baseUrl = url.TrimEnd('/');
            anonKey = key;
            return true;
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

        private static string Trunc(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s.Substring(0, 300));
    }
}
