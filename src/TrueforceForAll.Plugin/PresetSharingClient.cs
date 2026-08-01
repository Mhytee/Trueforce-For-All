// HTTP client for community preset sharing (presets table + upload/vote/
// download RPCs). Companion to CommunityClient.cs which handles the
// per-car fact pipeline (engine_layout, car_name, redline, custom).
//
// Shares the same gating contract: inert when Settings.CommunityEnabled is
// false OR backend URL / anon key are missing. Uses the same static
// HttpClient pattern (TLS 1.2, no socket exhaustion).
//
// Uploads are user-initiated (the user clicks Share in the preset
// manager) so they're synchronous w/r/t the modal flow: callers can
// await success and surface the new id, or fall back to "not now" on
// failure. Vote / record_download are fire-and-forget.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace TrueforceForAll.Plugin
{
    /// <summary>List-view row for a community preset (no body). What the
    /// browser tab renders before the user clicks Download.</summary>
    internal sealed class PresetSummary
    {
        public string Id            { get; set; }
        // "car" or "game". Determines which table the id lives in and
        // which RPC suite (vote_preset vs vote_game_preset etc) the
        // client must call. Set by ParseSummary from the row's "kind"
        // field when present, otherwise inferred from caller context.
        public string Kind          { get; set; } = "car";
        public string Name          { get; set; }
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string Game          { get; set; }
        // Empty for game presets.
        public string CarId         { get; set; }
        public List<string> EffectTags { get; set; } = new List<string>();
        public int    Upvotes       { get; set; }
        public int    Downvotes     { get; set; }
        public double WilsonScore   { get; set; }
        public int    Downloads     { get; set; }
        public DateTime? CreatedAt  { get; set; }
        // Set by the post-fetch pass that joins against preset_votes for
        // the local voter_id: -1 / 0 / +1 for downvoted / no-vote / upvoted.
        public int    MyVote        { get; set; }
        // Stable uuid of the signed-in user who uploaded this preset.
        // Null when the upload was anonymous (no auth.uid() at upload
        // time). The browser shows Edit/Delete only when this matches
        // the local signed-in user's id.
        public string OwnerUserId   { get; set; }
        // Server-tracked content version. Bumps on every update_preset
        // call. The plugin compares this against
        // Settings.DownloadedCommunityPresets[id].SeenContentVersion to
        // flag updates available.
        public int    ContentVersion { get; set; }
        public DateTime? UpdatedAt   { get; set; }
        // Pack-only: number of entries the pack body bundles. Server
        // denormalizes this onto the packs row so the browse list
        // can show "12 entries" without cracking the body.
        public int    EntryCount     { get; set; }
        // Pack-only: author's pack version label (e.g. "1.0"). Free
        // text, capped to 32 chars server-side.
        public string AuthorVersion  { get; set; }
        // Author-set permission: when true, this preset / game preset /
        // custom engine may be re-bundled inside someone else's pack.
        // Default false (opt-in). The pack creator UI filters the
        // selectable rows by user-ownership OR this flag, so built-ins
        // and "don't redistribute" community items never accidentally
        // ride along.
        public bool   AllowInPacks   { get; set; }
        // Game-preset-only: which games this preset is tuned for. Empty
        // means universal (applies to any game). Used by the browser
        // ranking + the "for <game>" / "Universal" / "Other games (N)"
        // tier badges. Populated by migration 0022's get_game_presets_by_ids.
        public string[] TargetGames  { get; set; } = new string[0];

        /// <summary>Deep copy, including the mutable EffectTags / TargetGames
        /// collections. Used at the browse-cache boundary so a cached summary and
        /// the copy a caller renders / optimistically mutates are independent
        /// objects (a UI vote/edit must not write through to the cache).</summary>
        public PresetSummary Clone()
        {
            return new PresetSummary
            {
                Id = Id, Kind = Kind, Name = Name, Author = Author, Description = Description,
                Game = Game, CarId = CarId,
                EffectTags = EffectTags != null ? new List<string>(EffectTags) : null,
                Upvotes = Upvotes, Downvotes = Downvotes, WilsonScore = WilsonScore, Downloads = Downloads,
                CreatedAt = CreatedAt, MyVote = MyVote, OwnerUserId = OwnerUserId,
                ContentVersion = ContentVersion, UpdatedAt = UpdatedAt, EntryCount = EntryCount,
                AuthorVersion = AuthorVersion, AllowInPacks = AllowInPacks,
                TargetGames = TargetGames != null ? (string[])TargetGames.Clone() : null,
            };
        }
    }

    /// <summary>Detail row: summary + full body. Returned by FetchPresetBody
    /// when the user clicks Download. Body is the CarPresetFile.Override
    /// payload plus any referenced CustomEngineDefs the curator's library
    /// had (so the recipient can play custom firing patterns).</summary>
    internal sealed class PresetFull
    {
        public PresetSummary Summary { get; set; }
        public JObject       Body    { get; set; }
    }

    /// <summary>Last-failure categorization for upload paths so the
    /// modal can show specific copy (rate limit / sign-in expired /
    /// validation) instead of one generic "network or rate limit"
    /// fallback. Set by the four Upload*Async methods alongside their
    /// nullable id return.</summary>
    internal enum UploadError
    {
        None,
        NotAuthenticated,
        RateLimited,
        ValidationFailed,
        ServerError,
        NetworkFailure,
        // The account (or its device) is banned from uploading. Server raises
        // 'submitter blocked'. Distinct so the UI can pop the appeal modal.
        Blocked,
        // The user already has a live upload of this kind with this name
        // (migration 0099 trigger raises 'duplicate name…'; the backstop
        // unique index raises 23505). Distinct so the UI can say exactly
        // that instead of a generic validation error.
        DuplicateName,
    }

    internal sealed class PresetSharingClient
    {
        // PostgREST RPC + table paths.
        private const string UploadRpcPath    = "/rest/v1/rpc/upload_preset";
        private const string VoteRpcPath      = "/rest/v1/rpc/vote_preset";
        private const string DownloadRpcPath  = "/rest/v1/rpc/record_preset_download";
        private const string ReportRpcPath    = "/rest/v1/rpc/report_preset";
        private const string PresetsPath      = "/rest/v1/presets";
        private const string PresetVotesPath  = "/rest/v1/preset_votes";

        // Game-preset RPC + table paths. Separate table (game_presets)
        // and separate vote table (game_preset_votes). 0012 introduced
        // both so they get their own ownership / voting / update-detection
        // infra without sharing rows or sentinels with car presets.
        private const string UploadGameRpcPath   = "/rest/v1/rpc/upload_game_preset";
        private const string UpdateGameRpcPath   = "/rest/v1/rpc/update_game_preset";
        private const string VoteGameRpcPath     = "/rest/v1/rpc/vote_game_preset";
        private const string DownloadGameRpcPath = "/rest/v1/rpc/record_game_preset_download";
        private const string ReportGameRpcPath   = "/rest/v1/rpc/report_game_preset";
        private const string GamePresetsPath     = "/rest/v1/game_presets";

        // Custom-engine RPC + table paths. Third kind, introduced by
        // migration 0013. Game-agnostic (no game/car scoping). Body
        // is a serialized CustomEngineDef.
        private const string UploadEngineRpcPath   = "/rest/v1/rpc/upload_custom_engine";
        private const string UpdateEngineRpcPath   = "/rest/v1/rpc/update_custom_engine";
        private const string VoteEngineRpcPath     = "/rest/v1/rpc/vote_custom_engine";
        private const string DownloadEngineRpcPath = "/rest/v1/rpc/record_custom_engine_download";
        private const string ReportEngineRpcPath   = "/rest/v1/rpc/report_custom_engine";
        private const string CustomEnginesPath     = "/rest/v1/custom_engines";

        // Pack RPC + table paths. Fourth kind, introduced by migration
        // 0014. Body bundles multiple entries across kinds (game
        // presets, car presets, custom engines).
        private const string UploadPackRpcPath   = "/rest/v1/rpc/upload_pack";
        private const string UpdatePackRpcPath   = "/rest/v1/rpc/update_pack";
        private const string VotePackRpcPath     = "/rest/v1/rpc/vote_pack";
        private const string DownloadPackRpcPath = "/rest/v1/rpc/record_pack_download";
        private const string ReportPackRpcPath   = "/rest/v1/rpc/report_pack";
        private const string PacksPath           = "/rest/v1/packs";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            ContractResolver  = new DefaultContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        /// <summary>Last upload failure category. Set whenever an
        /// Upload*Async method returns null so the caller can surface
        /// specific copy. Reset to None on each fresh upload call.</summary>
        public UploadError LastUploadError    { get; private set; } = UploadError.None;
        public string      LastUploadDetail   { get; private set; }

        private readonly Func<TrueforceSettings>     _settingsProvider;
        private readonly Action<string>              _log;
        private readonly string                      _pluginVersion;
        // When non-null, an authenticated bearer-token provider. Awaited
        // before each auth-gated RPC so token refresh happens lazily.
        // Null in unit-test / no-auth contexts; PresetSharingClient gracefully
        // falls back to the anon key only (uploads still succeed, but the
        // server stamps owner_user_id NULL = unmanageable forever).
        private readonly Func<Task<string>>          _accessTokenProvider;

        public PresetSharingClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, string pluginVersion,
            Func<Task<string>> accessTokenProvider = null)
        {
            _settingsProvider = settingsProvider
                ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _pluginVersion = pluginVersion ?? "";
            _accessTokenProvider = accessTokenProvider;
        }

        private async Task<string> GetAccessTokenOrNullAsync()
        {
            if (_accessTokenProvider == null) return null;
            try { return await _accessTokenProvider().ConfigureAwait(false); }
            catch { return null; }
        }

        private void ResetUploadError()
        {
            LastUploadError  = UploadError.None;
            LastUploadDetail = null;
        }

        private void StampUploadError(UploadError category, string detail = null)
        {
            LastUploadError  = category;
            LastUploadDetail = detail;
        }

        private void StampUploadErrorFromStatus(System.Net.HttpStatusCode status, string detail)
        {
            int c = (int)status;
            // The ban gate raises 'submitter blocked' (P0001 -> 4xx). Detect the
            // message so a banned upload reads as Blocked, not generic validation.
            if (!string.IsNullOrEmpty(detail) &&
                detail.IndexOf("submitter blocked", StringComparison.OrdinalIgnoreCase) >= 0)
                LastUploadError = UploadError.Blocked;
            // Per-owner name uniqueness (migration 0099): the table trigger
            // raises 'duplicate name: …' (P0001 -> 400); the race-backstop
            // unique index surfaces as a duplicate-key 23505 (-> 409) whose
            // detail names the …_owner_name_live_key index.
            else if (!string.IsNullOrEmpty(detail) &&
                (detail.IndexOf("duplicate name", StringComparison.OrdinalIgnoreCase) >= 0
                 || detail.IndexOf("_owner_name_live_key", StringComparison.OrdinalIgnoreCase) >= 0))
                LastUploadError = UploadError.DuplicateName;
            else if (c == 401 || c == 403) LastUploadError = UploadError.NotAuthenticated;
            else if (c == 429)        LastUploadError = UploadError.RateLimited;
            else if (c >= 400 && c < 500) LastUploadError = UploadError.ValidationFailed;
            else if (c >= 500)        LastUploadError = UploadError.ServerError;
            else                       LastUploadError = UploadError.NetworkFailure;
            LastUploadDetail = detail;
            // Keep the raw server detail out of the user-facing message but in the
            // log, so support can still see the backend reason (duplicate key,
            // validation constraint, etc.) without showing the user jargon.
            if (!string.IsNullOrEmpty(detail))
                SimHub.Logging.Current.Info("[TF4ALL] Upload rejected (" + LastUploadError + "): " + detail);
        }

        // ---- Upload --------------------------------------------------------

        /// <summary>Upload a CarPresetFile body to the community.
        /// effectTags is the list of non-null section names (engine,
        /// revlimiter, ...) computed by the caller; server validates
        /// against a whitelist. Returns the new preset id on success or
        /// null on any failure (network, validation, rate limit). The
        /// server stamps author = profiles.username; the local
        /// 'author' arg is accepted for API back-compat but ignored.</summary>
        public async Task<string> UploadCarPresetAsync(
            string name, string game, string carId, JObject body,
            string author, string description, List<string> effectTags,
            int bodyVersion = 1, int timeoutMs = 15000,
            bool allowInPacks = false)
        {
            ResetUploadError();
            if (!ShouldSubmit(out var url, out var anonKey))
            {
                StampUploadError(UploadError.NetworkFailure, "community not configured");
                return null;
            }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(game)
                || string.IsNullOrEmpty(carId) || body == null)
            {
                StampUploadError(UploadError.ValidationFailed, "missing required field");
                return null;
            }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_name           = name,
                    p_game           = game,
                    p_car_id         = carId,
                    p_body           = body,
                    p_author         = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
                    p_description    = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    p_effect_tags    = effectTags ?? new List<string>(),
                    p_body_version   = bodyVersion,
                    p_plugin_version = _pluginVersion,
                    p_allow_in_packs = allowInPacks,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Preset upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[TF4ALL] Upload aborted: no auth bearer; refusing to upload as anonymous.");
                StampUploadError(UploadError.NotAuthenticated, "no bearer");
                return null;
            }

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Headers.Add("Prefer", "return=representation");
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Preset upload failed: "
                                + $"{(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            StampUploadErrorFromStatus(resp.StatusCode, respBody);
                            return null;
                        }
                        var root = JToken.Parse(respBody);
                        var obj = root.Type == JTokenType.Array ? root[0] : root;
                        return obj?["id"]?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Preset upload exception: {ex.Message}");
                StampUploadError(UploadError.NetworkFailure, ex.Message);
                return null;
            }
        }

        // ---- Update (auth-gated) -------------------------------------------

        /// <summary>Edit an owned preset's metadata + body. Requires a
        /// signed-in session (server gates on auth.uid() = owner_user_id).
        /// Returns true on success, false on any failure (rate limit,
        /// permission denied, network).</summary>
        public async Task<bool> UpdatePresetAsync(string id,
            string name, string description, JObject body, List<string> effectTags,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null)
        {
            var result = await UpdatePresetWithVersionAsync(id, name, description, body,
                effectTags, bodyVersion, timeoutMs, allowInPacks).ConfigureAwait(false);
            return result.HasValue;
        }

        /// <summary>Same as UpdatePresetAsync but returns the server's new
        /// content_version on success (the update_preset RPC returns
        /// {id, content_version}). Null on any failure.</summary>
        public async Task<int?> UpdatePresetWithVersionAsync(string id,
            string name, string description, JObject body, List<string> effectTags,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(id)) return null;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[TF4ALL] Update preset attempted while signed out.");
                return null;
            }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_preset_id      = id,
                    p_name           = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    p_description    = description,  // null is fine; "" clears
                    p_body           = (object)body,
                    p_effect_tags    = effectTags,
                    p_body_version   = bodyVersion,
                    // null = no change; bool = flip allow_in_packs.
                    p_allow_in_packs = allowInPacks,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Update preset serialize failed: {ex.Message}");
                return null;
            }

            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/update_preset";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Update preset failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            // Stamp so the share modal's update path can say WHY
                            // (rename collisions raise 'duplicate name', 0099).
                            StampUploadErrorFromStatus(resp.StatusCode, respBody);
                            return null;
                        }
                        return ParseContentVersionOrZero(respBody);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Update preset exception: {ex.Message}");
                return null;
            }
        }

        // Server update_* RPCs return {id, content_version} as jsonb. Surface
        // 0 when parse fails so the caller still treats the update as success
        // (the bool path used HEAD-only and returned true without reading body).
        private static int ParseContentVersionOrZero(string respBody)
        {
            if (string.IsNullOrWhiteSpace(respBody)) return 0;
            try
            {
                var root = JToken.Parse(respBody);
                var obj = root.Type == JTokenType.Array ? root[0] : root;
                var cv = obj?["content_version"];
                if (cv != null && cv.Type != JTokenType.Null)
                    return cv.Value<int>();
            }
            catch { }
            return 0;
        }

        // ---- Delete (auth-gated) -------------------------------------------

        /// <summary>Soft-delete an owned car preset. Server sets
        /// is_suppressed=true so vote history stays for moderation. Returns
        /// true on success, false on any failure.</summary>
        public Task<bool> DeletePresetAsync(string id, int timeoutMs = 10000)
            => DeleteViaRpcAsync("delete_preset", "p_preset_id", id, timeoutMs);

        /// <summary>Soft-delete an owned game preset (see DeletePresetAsync).</summary>
        public Task<bool> DeleteGamePresetAsync(string id, int timeoutMs = 10000)
            => DeleteViaRpcAsync("delete_game_preset", "p_game_preset_id", id, timeoutMs);

        /// <summary>Soft-delete an owned community pack (see DeletePresetAsync).</summary>
        public Task<bool> DeletePackAsync(string id, int timeoutMs = 10000)
            => DeleteViaRpcAsync("delete_pack", "p_pack_id", id, timeoutMs);

        /// <summary>Soft-delete an owned custom engine (see DeletePresetAsync).</summary>
        public Task<bool> DeleteCustomEngineAsync(string id, int timeoutMs = 10000)
            => DeleteViaRpcAsync("delete_custom_engine", "p_custom_engine_id", id, timeoutMs);

        // Shared owner-delete plumbing. Each kind has its own RPC + id param
        // name (delete_preset/p_preset_id, delete_game_preset/p_game_preset_id,
        // etc.) because the rows live in separate tables; the call shape is
        // otherwise identical.
        private async Task<bool> DeleteViaRpcAsync(string rpc, string paramName, string id, int timeoutMs)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return false;
            if (string.IsNullOrWhiteSpace(id)) return false;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke($"[TF4ALL] {rpc} attempted while signed out.");
                return false;
            }

            string requestBody;
            try
            {
                var payload = new JObject { [paramName] = id };
                requestBody = payload.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return false; }

            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/" + rpc;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return true;
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        _log?.Invoke($"[TF4ALL] {rpc} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] {rpc} exception: {ex.Message}");
                return false;
            }
        }

        // ---- My presets (auth-required) ------------------------------------

        /// <summary>List the signed-in user's own preset uploads. Sort
        /// is one of "newest" / "top" / "downloads". Returns null on
        /// network/auth failure; empty list when no uploads exist.</summary>
        public async Task<List<PresetSummary>> FetchMyPresetsAsync(
            string sort = "newest", int limit = 100, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return null;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_sort = sort ?? "newest",
                    p_limit = limit,
                }, _jsonSettings);
            }
            catch { return null; }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_my_presets";

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(respBody)) return new List<PresetSummary>();
                        // get_my_presets returns SETOF jsonb; PostgREST
                        // wraps each row as a single-key object.
                        var arr = JArray.Parse(respBody);
                        var list = new List<PresetSummary>(arr.Count);
                        foreach (var row in arr)
                        {
                            JToken inner = row;
                            // SETOF jsonb comes back as either a JObject
                            // directly OR wrapped under a key like "get_my_presets".
                            if (row is JObject ro && ro.Count == 1
                                && ro.Properties().First().Value is JObject inside)
                                inner = inside;
                            list.Add(ParseSummary(inner));
                        }
                        return list;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] My-presets fetch failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Bulk-fetch the signed-in user's vote on each preset
        /// id in the list. Returns a dictionary preset_id -> value
        /// (-1/+1). Missing keys mean no vote. Returns null when not
        /// signed in or on network failure.</summary>
        public async Task<Dictionary<string, int>> FetchMyVotesAsync(
            IList<string> presetIds, int timeoutMs = 4000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (presetIds == null || presetIds.Count == 0)
                return new Dictionary<string, int>();
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return null;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_preset_ids = presetIds,
                }, _jsonSettings);
            }
            catch { return null; }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_my_votes";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var map = new Dictionary<string, int>(presetIds.Count);
                        if (string.IsNullOrEmpty(respBody)) return map;
                        var arr = JArray.Parse(respBody);
                        foreach (var row in arr)
                        {
                            JToken inner = row;
                            if (row is JObject ro && ro.Count == 1
                                && ro.Properties().First().Value is JObject inside)
                                inner = inside;
                            string id = inner?["preset_id"]?.ToString();
                            int val = inner?["value"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(id)) map[id] = val;
                        }
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] My-votes fetch failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Bulk read of presets by id. Used by the update-
        /// notification pass at plugin load. Returns null on failure;
        /// empty list when none of the ids are alive. Suppressed rows
        /// (author-deleted) just don't come back, which the caller
        /// treats as "stop nagging about this id".</summary>
        public List<PresetSummary> FetchPresetsByIds(IList<string> ids, int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (ids == null || ids.Count == 0) return new List<PresetSummary>();

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new { p_ids = ids }, _jsonSettings);
            }
            catch { return null; }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_presets_by_ids";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (List<PresetSummary>)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (List<PresetSummary>)null;
                                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(respBody)) return new List<PresetSummary>();
                                var arr = JArray.Parse(respBody);
                                var list = new List<PresetSummary>(arr.Count);
                                foreach (var row in arr)
                                {
                                    JToken inner = row;
                                    if (row is JObject ro && ro.Count == 1
                                        && ro.Properties().First().Value is JObject inside)
                                        inner = inside;
                                    bool suppressed = inner?["is_suppressed"]?.ToObject<bool>() ?? false;
                                    if (suppressed) continue;
                                    list.Add(ParseSummary(inner));
                                }
                                return list;
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] FetchPresetsByIds failed: {ex.Message}");
                return null;
            }
        }

        // ---- Account ops (auth-gated) --------------------------------------

        public async Task<JObject> GetAccountStatsAsync(int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return null;

            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_account_stats";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var root = JToken.Parse(respBody);
                        return (root.Type == JTokenType.Array ? root[0] : root) as JObject;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Account stats fetch failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Returns the raw JSON blob from export_my_data so the
        /// caller can dump it to a file. Null on failure.</summary>
        public async Task<string> ExportMyDataRawAsync(int timeoutMs = 30000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return null;

            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/export_my_data";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Data export failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Delete the signed-in user's account. Returns true on
        /// success. Caller should clear the local session and surface a
        /// confirmation to the user.</summary>
        public async Task<bool> DeleteMyAccountAsync(int timeoutMs = 15000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return false;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return false;

            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/delete_my_account";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        return resp.IsSuccessStatusCode;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Delete account failed: {ex.Message}");
                return false;
            }
        }

        // ---- Fetch list ----------------------------------------------------

        /// <summary>List presets for a (game, carId), sorted server-side.
        /// sort = "wilson" / "newest" / "downloads". Returns null on
        /// network/auth failure; empty list when no presets exist.</summary>
        public List<PresetSummary> FetchPresetsForCar(
            string game, string carId, string sort = "wilson",
            int limit = 20, int offset = 0, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId))
                return new List<PresetSummary>();

            string orderClause = SortToOrderClause(sort);

            // PostgREST select - exclude body to keep the list response
            // small. body comes through FetchPresetBody only.
            string qs = "?game=eq."   + Uri.EscapeDataString(game)
                      + "&car_id=eq." + Uri.EscapeDataString(carId)
                      + "&select=id,name,author,description,game,car_id,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            string fullUrl = url.TrimEnd('/') + PresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "car", timeoutMs);
        }

        /// <summary>List game presets for a given game from the
        /// dedicated game_presets table. Same sort vocab as car
        /// presets ("wilson" / "newest" / "downloads").</summary>
        public List<PresetSummary> FetchGamePresetsForGame(
            string game, string sort = "wilson", int limit = 20, int offset = 0, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game)) return new List<PresetSummary>();

            string orderClause = SortToOrderClause(sort);

            string qs = "?game=eq." + Uri.EscapeDataString(game)
                      + "&select=id,name,author,description,game,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs,target_games"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            string fullUrl = url.TrimEnd('/') + GamePresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "game", timeoutMs);
        }

        /// <summary>Cross-game trending car presets: no game/car scoping,
        /// used as a fallback when the panel is in "for car" mode but no
        /// game/car is loaded yet. Pattern parallels
        /// FetchCommunityCustomEngines (which has always been global).</summary>
        public List<PresetSummary> FetchTrendingCarPresets(
            string sort = "wilson", int limit = 50, int offset = 0, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,game,car_id,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            string fullUrl = url.TrimEnd('/') + PresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "car", timeoutMs);
        }

        /// <summary>Cross-game trending game presets: no game scoping,
        /// used as the for-car-mode fallback when no game is loaded.</summary>
        public List<PresetSummary> FetchTrendingGamePresets(
            string sort = "wilson", int limit = 50, int offset = 0, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,game,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs,target_games"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            string fullUrl = url.TrimEnd('/') + GamePresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "game", timeoutMs);
        }

        // ---- Search + cross-car browse -------------------------------------
        // The browser's search box / game filter drop the active-CAR scope so
        // the user can browse all cars across the selected game(s). Text match
        // is case-insensitive substring (PostgREST ilike) on name + author;
        // for car presets the caller also passes carIdMatches (car_ids whose
        // HUMAN name matched, resolved from the local car-name tables) so a
        // query like "mx5" finds presets even though rows store opaque ids.
        // An empty query with a game filter is a valid browse (lists every
        // matching car). games == null/empty means "all games".

        /// <summary>Browse / search car presets across the selected games and
        /// all cars. carIdMatches = car_ids resolved from a human-name query
        /// (may be null). Returns null on network/auth failure.</summary>
        public List<PresetSummary> SearchPresets(
            string query, IList<string> carIdMatches, IList<string> games,
            string sort = "wilson", int limit = 25, int offset = 0, int timeoutMs = 6000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string qs = "?select=id,name,author,description,game,car_id,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + BuildGamesInClause(games)
                      + BuildSearchOrClause(query, carIdMatches)
                      + "&order=" + SortToOrderClause(sort)
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            return RunGetList(url.TrimEnd('/') + PresetsPath + qs, anonKey, "car", timeoutMs);
        }

        /// <summary>Browse / search game presets across the selected games by
        /// name / author. games == null/empty = all games.</summary>
        public List<PresetSummary> SearchGamePresets(
            string query, IList<string> games,
            string sort = "wilson", int limit = 25, int offset = 0, int timeoutMs = 6000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string qs = "?select=id,name,author,description,game,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs,target_games"
                      + BuildGamesInClause(games)
                      + BuildSearchOrClause(query, null)
                      + "&order=" + SortToOrderClause(sort)
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            return RunGetList(url.TrimEnd('/') + GamePresetsPath + qs, anonKey, "game", timeoutMs);
        }

        /// <summary>Search community custom engines by name / author (engines
        /// are game-agnostic; no game filter). Empty query = no rows.</summary>
        public List<PresetSummary> SearchCustomEngines(
            string query, string sort = "wilson", int limit = 25, int offset = 0, int timeoutMs = 6000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orClause = BuildSearchOrClause(query, null);
            if (string.IsNullOrEmpty(orClause)) return new List<PresetSummary>();
            string qs = "?select=id,name,author,description,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + orClause
                      + "&order=" + SortToOrderClause(sort)
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            return RunGetList(url.TrimEnd('/') + CustomEnginesPath + qs, anonKey, "engine", timeoutMs);
        }

        /// <summary>Search community packs by name / author. Empty query = no rows.</summary>
        public List<PresetSummary> SearchPacks(
            string query, string sort = "wilson", int limit = 25, int offset = 0, int timeoutMs = 6000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orClause = BuildSearchOrClause(query, null);
            if (string.IsNullOrEmpty(orClause)) return new List<PresetSummary>();
            string qs = "?select=id,name,author,description,author_version,entry_count,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at"
                      + orClause
                      + "&order=" + SortToOrderClause(sort)
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            return RunGetList(url.TrimEnd('/') + PacksPath + qs, anonKey, "pack", timeoutMs);
        }

        /// <summary>Distinct game names across ALL shared car + game presets,
        /// for the browser's game-filter chips. Without this the chips only
        /// offered games known to the local install, so a preset for a game
        /// the viewer never played was unreachable. PostgREST has no
        /// DISTINCT, so this pulls just the game column from both tables and
        /// dedupes here; one short string per shared preset, capped at the
        /// server's 1000-row page horizon per table. Returns null when both
        /// reads fail (network/auth); one failing table degrades to the
        /// other's list.</summary>
        public List<string> FetchCommunityGameNames(int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyOk = false;
            foreach (string path in new[] { PresetsPath, GamePresetsPath })
            {
                var games = RunGetGameColumn(
                    url.TrimEnd('/') + path + "?select=game&limit=1000",
                    anonKey, timeoutMs);
                if (games == null) continue;
                anyOk = true;
                foreach (var g in games)
                    if (!string.IsNullOrWhiteSpace(g)) set.Add(g.Trim());
            }
            if (!anyOk) return null;
            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Column-only sibling of RunGetList: rows are {game: "..."} and the
        // caller wants the raw strings. Same signed-in read requirement as
        // the browse fetches.
        private List<string> RunGetGameColumn(string fullUrl, string anonKey, int timeoutMs)
        {
            string capturedKey = anonKey;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (List<string>)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (List<string>)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return new List<string>();
                                var arr = JArray.Parse(body);
                                var list = new List<string>(arr.Count);
                                foreach (var row in arr)
                                    list.Add((string)row?["game"]);
                                return list;
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Community game-list fetch failed: {ex.Message}");
                return null;
            }
        }

        // Build the "&game=in.(...)" filter for a multi-game browse. Values
        // are double-quoted + URL-encoded so a game name with spaces (e.g.
        // "Forza Horizon 6") survives the in-list grammar. Null/empty = no
        // filter (all games).
        private static string BuildGamesInClause(IList<string> games)
        {
            if (games == null || games.Count == 0) return "";
            var quoted = new List<string>(games.Count);
            foreach (var g in games)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                quoted.Add(Uri.EscapeDataString("\"" + g.Replace("\"", "") + "\""));
            }
            return quoted.Count == 0 ? "" : "&game=in.(" + string.Join(",", quoted) + ")";
        }

        // Build the "&or=(col.ilike.*term*,...)" fragment. The term is
        // sanitized of PostgREST grammar characters (the or-list is comma /
        // paren / dot delimited) then URL-encoded, so a stray comma or paren
        // in the user's query can't break or inject into the filter. When
        // carIdMatches is non-null the search also matches car_id text and an
        // in-list of the resolved (human-name) car_ids. Returns "" when there
        // is nothing to match (so an empty-query game browse stays unfiltered).
        private static string BuildSearchOrClause(string query, IList<string> carIdMatches)
        {
            var conds = new List<string>();
            string term = SanitizeSearchTerm(query);
            if (!string.IsNullOrEmpty(term))
            {
                string enc = Uri.EscapeDataString("*" + term + "*");
                conds.Add("name.ilike."   + enc);
                conds.Add("author.ilike." + enc);
                if (carIdMatches != null) conds.Add("car_id.ilike." + enc);
            }
            if (carIdMatches != null && carIdMatches.Count > 0)
            {
                var quoted = new List<string>(carIdMatches.Count);
                foreach (var id in carIdMatches)
                    if (!string.IsNullOrWhiteSpace(id))
                        quoted.Add(Uri.EscapeDataString("\"" + id.Replace("\"", "") + "\""));
                if (quoted.Count > 0)
                    conds.Add("car_id.in.(" + string.Join(",", quoted) + ")");
            }
            return conds.Count == 0 ? "" : "&or=(" + string.Join(",", conds) + ")";
        }

        // Make the user's text a literal contains-match for the PostgREST
        // filter grammar. Two treatments, deliberately different:
        //  - STRIP what the or=() grammar itself eats: , ( ) . delimit the
        //    logic tree, " ' would need value-quoting, and * is PostgREST's
        //    own wildcard alias (unconditionally rewritten to %, no escape
        //    exists for it).
        //  - ESCAPE the ilike metacharacters \ % _ (backslash is Postgres
        //    LIKE/ILIKE's default escape char) so they match LITERALLY.
        //    Stripping them broke underscore car_id searches: "bmw_m3"
        //    became "bmwm3" and matched nothing, and car_id.ilike is the
        //    ONLY search path for AC's underscore-delimited ids (AC builtins
        //    carry no display name). Verified against PostgREST + Postgres
        //    live 2026-06-28: *bmw\_m3* parses in or=() and matches
        //    bmw_m3_e30 but not bmwXm3.
        // Interior whitespace maps to the ilike wildcard so multi-word
        // queries match across gaps ("wet drift" -> *wet*drift*).
        private static string SanitizeSearchTerm(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";
            var sb = new System.Text.StringBuilder(query.Length + 8);
            foreach (char c in query.Trim())
            {
                if (c == ',' || c == '(' || c == ')' || c == '.'
                    || c == '*' || c == '"' || c == '\'')
                    continue;
                if (c == '\\' || c == '%' || c == '_')
                {
                    sb.Append('\\').Append(c);
                    continue;
                }
                sb.Append(char.IsWhiteSpace(c) ? '*' : c);
            }
            // Trimming stray leading/trailing wildcards can't orphan an escape:
            // a lone user backslash was emitted as \\ above.
            return sb.ToString().Trim('*');
        }

        private static string SortToOrderClause(string sort)
        {
            // The id (uuid PK) tiebreaker makes the total order STABLE across
            // separate LIMIT/OFFSET requests. Without it, ties on the primary key
            // (very common: every 0-vote preset shares wilson_score 0, every
            // 0-download preset shares downloads 0) let offset pagination duplicate
            // or skip rows at page boundaries on "Show more".
            switch ((sort ?? "").ToLowerInvariant())
            {
                case "newest":    return "created_at.desc,id.asc";
                case "downloads": return "downloads.desc,id.asc";
                case "wilson":
                default:          return "wilson_score.desc,id.asc";
            }
        }

        private List<PresetSummary> RunGetList(string fullUrl,
            string anonKey, string kind, int timeoutMs)
        {
            string capturedKey = anonKey;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (List<PresetSummary>)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (List<PresetSummary>)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return new List<PresetSummary>();
                                var arr = JArray.Parse(body);
                                var list = new List<PresetSummary>(arr.Count);
                                foreach (var row in arr)
                                {
                                    var s = ParseSummary(row);
                                    if (s != null) { s.Kind = kind; list.Add(s); }
                                }
                                return list;
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Preset list fetch failed: {ex.Message}");
                return null;
            }
        }

        // ---- Fetch body ----------------------------------------------------

        /// <summary>Fetch the full body for a single preset. Used when the
        /// user clicks Download in the browser. Returns null on
        /// network/auth/missing-row failure.</summary>
        public PresetFull FetchPresetBody(string id, int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(id)) return null;

            string qs = "?id=eq." + Uri.EscapeDataString(id)
                      + "&select=id,name,author,description,game,car_id,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,allow_in_packs,body"
                      + "&limit=1";
            string fullUrl = url.TrimEnd('/') + PresetsPath + qs;
            string capturedKey = anonKey;

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (PresetFull)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                // Refuse oversized responses before we ever allocate
                                // a string buffer. Content-Length is advisory but
                                // honored by Supabase + GitHub PostgREST.
                                long contentLen = resp.Content?.Headers?.ContentLength ?? -1;
                                if (contentLen > ChannelValidation.MaxPresetBodyBytes && contentLen > 0)
                                {
                                    _log?.Invoke($"[TF4ALL] Preset body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                // Belt-and-suspenders: re-check after read in case
                                // the server lied or omitted Content-Length.
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[TF4ALL] Preset body exceeds max size after read");
                                    return (PresetFull)null;
                                }
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (PresetFull)null;
                                var row = arr[0];
                                return new PresetFull
                                {
                                    Summary = ParseSummary(row),
                                    Body    = row["body"] as JObject,
                                };
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Preset body fetch failed: {ex.Message}");
                return null;
            }
        }

        // ---- Vote ----------------------------------------------------------
        // Awaitable: caller awaits + reconciles its optimistic counter / arrow
        // state on failure. The vote RPCs are auth-gated and idempotent on
        // (target_id, voter_id); value = +1, -1, or 0 (retract, via row-delete).
        // Used by ToggleVote so a server rejection (rate limit, blocked, no
        // auth) doesn't leave the row visually voted forever.

        public Task<bool> TryVotePresetAsync(string id, int value, int timeoutMs = 10000)
            => CallVoteRpcAsync(VoteRpcPath, "p_preset_id", id, value, timeoutMs);

        public Task<bool> TryVoteGamePresetAsync(string id, int value, int timeoutMs = 10000)
            => CallVoteRpcAsync(VoteGameRpcPath, "p_game_preset_id", id, value, timeoutMs);

        public Task<bool> TryVoteCustomEngineAsync(string id, int value, int timeoutMs = 10000)
            => CallVoteRpcAsync(VoteEngineRpcPath, "p_custom_engine_id", id, value, timeoutMs);

        public Task<bool> TryVotePackAsync(string id, int value, int timeoutMs = 10000)
            => CallVoteRpcAsync(VotePackRpcPath, "p_pack_id", id, value, timeoutMs);

        private async Task<bool> CallVoteRpcAsync(string rpcPath, string idParam,
            string id, int value, int timeoutMs)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return false;
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (value != 1 && value != -1 && value != 0) return false;

            string body;
            try
            {
                var payload = new System.Collections.Generic.Dictionary<string, object>
                {
                    [idParam] = id,
                    ["p_value"] = value,
                };
                body = JsonConvert.SerializeObject(payload, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Vote serialize failed: {ex.Message}");
                return false;
            }

            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            string fullUrl = url.TrimEnd('/') + rpcPath;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer "
                        + (string.IsNullOrEmpty(bearer) ? anonKey : bearer));
                    req.Headers.Add("Prefer", "return=minimal");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return true;
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        _log?.Invoke($"[TF4ALL] Vote failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Vote exception: {ex.Message}");
                return false;
            }
        }

        // ---- Record download ----------------------------------------------

        /// <summary>Fire-and-forget download counter bump. Called once per
        /// user-initiated download so the trending sort has a signal
        /// beyond raw votes.</summary>
        public void RecordPresetDownloadAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_preset_id = id,
                }, _jsonSettings);
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Download/report serialize failed: {ex.Message}"); return; }
            // record_preset_download is auth-gated (migration 0017 added
            // per-user dedup). Attach the bearer if we have one; the
            // server returns 'sign-in required' for anonymous calls
            // and the counter never increments without it.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, DownloadRpcPath, body, bearer);
            });
        }

        // ---- Report --------------------------------------------------------

        // Allowed report categories. Must match the CHECK constraint + RPC
        // validation in migration 0080; anything else is coerced to "other"
        // so a stale client can never trip the server-side raise.
        private static readonly HashSet<string> _reportCategories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        { "broken", "inappropriate", "spam", "wrong_data", "stolen", "other" };

        private static string NormalizeReportCategory(string category)
        {
            var c = (category ?? "").Trim().ToLowerInvariant();
            return _reportCategories.Contains(c) ? c : "other";
        }

        // null (not "") so PostgREST passes SQL NULL into the nullable p_note;
        // server also trims + caps at 1000, this just avoids sending blanks.
        private static string NormalizeReportNote(string note)
        {
            var n = (note ?? "").Trim();
            if (n.Length == 0) return null;
            return n.Length > 1000 ? n.Substring(0, 1000) : n;
        }

        /// <summary>Flag a preset as reported. Requires sign-in. Server-side
        /// rate-limited to one report per user per target per 24 hours.</summary>
        public void ReportPresetAsync(string id, string category = "other", string note = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_preset_id = id,
                    p_category  = NormalizeReportCategory(category),
                    p_note      = NormalizeReportNote(note),
                }, _jsonSettings);
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Report serialize failed: {ex.Message}"); return; }
            // Auth required: anonymous reports are rejected server-side.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(bearer)) return;
                FireAndForgetRpc(url, anonKey, ReportRpcPath, body, bearer);
            });
        }

        // ---- Game preset operations ---------------------------------------
        // Parallel suite for the dedicated game_presets table introduced
        // by 0012. Same shapes as the car-preset ops above; the only
        // schema difference is no car_id and a separate id space.

        /// <summary>Build the "snapshot" token for a SHARED community body, with
        /// the personal fields stripped. Master gain and FFB scale are kept in
        /// the snapshot so a user's own presets remember them and they travel in
        /// backup, but they are never part of the public shared payload (the
        /// download side also ignores them via the CommunitySourceId gate in
        /// ApplyGamePreset). Mirrors PresetBodyHasher's hash exclusion; keep the
        /// two personal-field sets in sync.</summary>
        internal static JObject BuildShareableSnapshotToken(GameSettingsSnapshot snap)
        {
            var tok = (JObject)JToken.FromObject(snap);
            tok.Remove("MasterGain");
            tok.Remove("FfbScale");
            return tok;
        }

        /// <summary>Upload a whole-game preset body. The server stamps
        /// author = profiles.username; the local 'author' arg is
        /// accepted for back-compat but ignored.</summary>
        public async Task<string> UploadGamePresetAsync(
            string name, string game, JObject body,
            string description, List<string> effectTags,
            int bodyVersion = 1, int timeoutMs = 15000,
            bool allowInPacks = false,
            string[] targetGames = null)
        {
            ResetUploadError();
            if (!ShouldSubmit(out var url, out var anonKey))
            { StampUploadError(UploadError.NetworkFailure, "community not configured"); return null; }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(game)
                || body == null)
            { StampUploadError(UploadError.ValidationFailed, "missing required field"); return null; }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_name           = name,
                    p_game           = game,
                    p_body           = body,
                    p_description    = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    p_effect_tags    = effectTags ?? new List<string>(),
                    p_body_version   = bodyVersion,
                    p_plugin_version = _pluginVersion,
                    p_allow_in_packs = allowInPacks,
                    // Empty array = universal; non-empty = tuned-for list.
                    p_target_games   = targetGames ?? new string[0],
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Game preset upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadGameRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[TF4ALL] Game preset upload aborted: no auth bearer.");
                StampUploadError(UploadError.NotAuthenticated, "no bearer");
                return null;
            }
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Headers.Add("Prefer", "return=representation");
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Game preset upload failed: "
                                + $"{(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            StampUploadErrorFromStatus(resp.StatusCode, respBody);
                            return null;
                        }
                        var root = JToken.Parse(respBody);
                        var obj = root.Type == JTokenType.Array ? root[0] : root;
                        return obj?["id"]?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Game preset upload exception: {ex.Message}");
                StampUploadError(UploadError.NetworkFailure, ex.Message);
                return null;
            }
        }

        public async Task<bool> UpdateGamePresetAsync(string id,
            string name, string description, JObject body, List<string> effectTags,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null,
            string[] targetGames = null)
        {
            var result = await UpdateGamePresetWithVersionAsync(id, name, description, body,
                effectTags, bodyVersion, timeoutMs, allowInPacks, targetGames).ConfigureAwait(false);
            return result.HasValue;
        }

        /// <summary>Same as UpdateGamePresetAsync but returns the new
        /// server content_version on success (null on failure).</summary>
        public async Task<int?> UpdateGamePresetWithVersionAsync(string id,
            string name, string description, JObject body, List<string> effectTags,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null,
            string[] targetGames = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(id)) return null;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[TF4ALL] Update game preset attempted while signed out.");
                return null;
            }

            string requestBody;
            try
            {
                // null targetGames means "no change"; NullValueHandling.Ignore
                // drops p_target_games from the payload. Empty array = reset
                // to universal; populated = replace.
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_game_preset_id = id,
                    p_name           = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    p_description    = description,
                    p_body           = (object)body,
                    p_effect_tags    = effectTags,
                    p_body_version   = bodyVersion,
                    p_allow_in_packs = allowInPacks,
                    p_target_games   = targetGames,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Update game preset serialize failed: {ex.Message}");
                return null;
            }

            string fullUrl = url.TrimEnd('/') + UpdateGameRpcPath;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Update game preset failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            StampUploadErrorFromStatus(resp.StatusCode, respBody);
                            return null;
                        }
                        return ParseContentVersionOrZero(respBody);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Update game preset exception: {ex.Message}");
                return null;
            }
        }

        public void RecordGamePresetDownloadAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try
            {
                body = JsonConvert.SerializeObject(new { p_game_preset_id = id }, _jsonSettings);
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Download/report serialize failed: {ex.Message}"); return; }
            // Auth-gated per migration 0017; attach bearer if present.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, DownloadGameRpcPath, body, bearer);
            });
        }

        /// <summary>Flag a game preset as reported. Requires sign-in.</summary>
        public void ReportGamePresetAsync(string id, string category = "other", string note = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_game_preset_id = id,
                    p_category       = NormalizeReportCategory(category),
                    p_note           = NormalizeReportNote(note),
                }, _jsonSettings);
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Report serialize failed: {ex.Message}"); return; }
            // Auth required: anonymous reports are rejected server-side.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(bearer)) return;
                FireAndForgetRpc(url, anonKey, ReportGameRpcPath, body, bearer);
            });
        }

        /// <summary>Fetch the full body for a single game preset.</summary>
        public PresetFull FetchGamePresetBody(string id, int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(id)) return null;

            string qs = "?id=eq." + Uri.EscapeDataString(id)
                      + "&select=id,name,author,description,game,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,allow_in_packs,body"
                      + "&limit=1";
            string fullUrl = url.TrimEnd('/') + GamePresetsPath + qs;
            string capturedKey = anonKey;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (PresetFull)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                long contentLen = resp.Content?.Headers?.ContentLength ?? -1;
                                if (contentLen > ChannelValidation.MaxPresetBodyBytes && contentLen > 0)
                                {
                                    _log?.Invoke($"[TF4ALL] Game preset body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[TF4ALL] Game preset body exceeds max size after read");
                                    return (PresetFull)null;
                                }
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (PresetFull)null;
                                var row = arr[0];
                                var s = ParseSummary(row);
                                if (s != null) s.Kind = "game";
                                return new PresetFull
                                {
                                    Summary = s,
                                    Body    = row["body"] as JObject,
                                };
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Game preset body fetch failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Bulk read of game presets by id. Used by the
        /// update-notification pass at plugin load (parallel to
        /// FetchPresetsByIds for car presets).</summary>
        public List<PresetSummary> FetchGamePresetsByIds(IList<string> ids, int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (ids == null || ids.Count == 0) return new List<PresetSummary>();

            string body;
            try { body = JsonConvert.SerializeObject(new { p_ids = ids }, _jsonSettings); }
            catch { return null; }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_game_presets_by_ids";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (List<PresetSummary>)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (List<PresetSummary>)null;
                                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(respBody)) return new List<PresetSummary>();
                                var arr = JArray.Parse(respBody);
                                var list = new List<PresetSummary>(arr.Count);
                                foreach (var row in arr)
                                {
                                    JToken inner = row;
                                    if (row is JObject ro && ro.Count == 1
                                        && ro.Properties().First().Value is JObject inside)
                                        inner = inside;
                                    bool suppressed = inner?["is_suppressed"]?.ToObject<bool>() ?? false;
                                    if (suppressed) continue;
                                    var s = ParseSummary(inner);
                                    if (s != null) { s.Kind = "game"; list.Add(s); }
                                }
                                return list;
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] FetchGamePresetsByIds failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Bulk-fetch the signed-in user's vote on each game
        /// preset id. Same shape as FetchMyVotes but keyed on
        /// game_preset_id and reading game_preset_votes.</summary>
        public async Task<Dictionary<string, int>> FetchMyGameVotesAsync(
            IList<string> gamePresetIds, int timeoutMs = 4000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (gamePresetIds == null || gamePresetIds.Count == 0)
                return new Dictionary<string, int>();
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return null;

            string body;
            try { body = JsonConvert.SerializeObject(new { p_game_preset_ids = gamePresetIds }, _jsonSettings); }
            catch { return null; }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_my_game_votes";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var map = new Dictionary<string, int>(gamePresetIds.Count);
                        if (string.IsNullOrEmpty(respBody)) return map;
                        var arr = JArray.Parse(respBody);
                        foreach (var row in arr)
                        {
                            JToken inner = row;
                            if (row is JObject ro && ro.Count == 1
                                && ro.Properties().First().Value is JObject inside)
                                inner = inside;
                            string gid = inner?["game_preset_id"]?.ToString();
                            int val = inner?["value"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(gid)) map[gid] = val;
                        }
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] My-game-votes fetch failed: {ex.Message}");
                return null;
            }
        }

        // ---- Custom engine operations -------------------------------------
        // Parallel to game preset suite; introduced by migration 0013.
        // Game-agnostic body (CustomEngineDef serialized as JSON);
        // discovery scope is global, not per-game.

        public async Task<string> UploadCustomEngineAsync(
            string name, Newtonsoft.Json.Linq.JObject body, string description,
            int bodyVersion = 1, int timeoutMs = 15000,
            bool allowInPacks = false)
        {
            ResetUploadError();
            if (!ShouldSubmit(out var url, out var anonKey))
            { StampUploadError(UploadError.NetworkFailure, "community not configured"); return null; }
            if (string.IsNullOrWhiteSpace(name) || body == null)
            { StampUploadError(UploadError.ValidationFailed, "missing required field"); return null; }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_name           = name,
                    p_body           = body,
                    p_description    = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    p_body_version   = bodyVersion,
                    p_plugin_version = _pluginVersion,
                    p_allow_in_packs = allowInPacks,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Engine upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadEngineRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[TF4ALL] Engine upload aborted: no auth bearer.");
                StampUploadError(UploadError.NotAuthenticated, "no bearer");
                return null;
            }
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Headers.Add("Prefer", "return=representation");
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Engine upload failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            StampUploadErrorFromStatus(resp.StatusCode, respBody);
                            return null;
                        }
                        var root = JToken.Parse(respBody);
                        var obj = root.Type == JTokenType.Array ? root[0] : root;
                        return obj?["id"]?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Engine upload exception: {ex.Message}");
                StampUploadError(UploadError.NetworkFailure, ex.Message);
                return null;
            }
        }

        public async Task<bool> UpdateCustomEngineAsync(string id,
            string name, string description, Newtonsoft.Json.Linq.JObject body,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null)
        {
            var result = await UpdateCustomEngineWithVersionAsync(id, name, description, body,
                bodyVersion, timeoutMs, allowInPacks).ConfigureAwait(false);
            return result.HasValue;
        }

        /// <summary>Same as UpdateCustomEngineAsync but returns the
        /// server's new content_version on success (null on failure).</summary>
        public async Task<int?> UpdateCustomEngineWithVersionAsync(string id,
            string name, string description, Newtonsoft.Json.Linq.JObject body,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(id)) return null;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[TF4ALL] Update engine attempted while signed out.");
                return null;
            }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_custom_engine_id = id,
                    p_name             = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    p_description      = description,
                    p_body             = (object)body,
                    p_body_version     = bodyVersion,
                    p_allow_in_packs   = allowInPacks,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Update engine serialize failed: {ex.Message}");
                return null;
            }

            string fullUrl = url.TrimEnd('/') + UpdateEngineRpcPath;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Update engine failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            StampUploadErrorFromStatus(resp.StatusCode, respBody);
                            return null;
                        }
                        return ParseContentVersionOrZero(respBody);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Update engine exception: {ex.Message}");
                return null;
            }
        }

        public void RecordCustomEngineDownloadAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try { body = JsonConvert.SerializeObject(new { p_custom_engine_id = id }, _jsonSettings); }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Download/report serialize failed: {ex.Message}"); return; }
            // Auth-gated per migration 0017; attach bearer if present.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, DownloadEngineRpcPath, body, bearer);
            });
        }

        /// <summary>Flag a custom engine as reported. Requires sign-in.</summary>
        public void ReportCustomEngineAsync(string id, string category = "other", string note = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_custom_engine_id = id,
                    p_category         = NormalizeReportCategory(category),
                    p_note             = NormalizeReportNote(note),
                }, _jsonSettings);
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Report serialize failed: {ex.Message}"); return; }
            // Auth required: anonymous reports are rejected server-side.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(bearer)) return;
                FireAndForgetRpc(url, anonKey, ReportEngineRpcPath, body, bearer);
            });
        }

        /// <summary>List community custom engines, sorted. No game/car
        /// scoping (engines are game-agnostic).</summary>
        public List<PresetSummary> FetchCommunityCustomEngines(
            string sort = "wilson", int limit = 20, int offset = 0, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            string fullUrl = url.TrimEnd('/') + CustomEnginesPath + qs;
            return RunGetList(fullUrl, anonKey, "engine", timeoutMs);
        }

        public PresetFull FetchCommunityCustomEngineBody(string id, int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(id)) return null;

            string qs = "?id=eq." + Uri.EscapeDataString(id)
                      + "&select=id,name,author,description,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,allow_in_packs,body"
                      + "&limit=1";
            string fullUrl = url.TrimEnd('/') + CustomEnginesPath + qs;
            string capturedKey = anonKey;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (PresetFull)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                long contentLen = resp.Content?.Headers?.ContentLength ?? -1;
                                if (contentLen > ChannelValidation.MaxPresetBodyBytes && contentLen > 0)
                                {
                                    _log?.Invoke($"[TF4ALL] Engine body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[TF4ALL] Engine body exceeds max size after read");
                                    return (PresetFull)null;
                                }
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (PresetFull)null;
                                var row = arr[0];
                                var s = ParseSummary(row);
                                if (s != null) s.Kind = "engine";
                                return new PresetFull
                                {
                                    Summary = s,
                                    Body    = row["body"] as Newtonsoft.Json.Linq.JObject,
                                };
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Engine body fetch failed: {ex.Message}");
                return null;
            }
        }

        public List<PresetSummary> FetchCustomEnginesByIds(IList<string> ids, int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (ids == null || ids.Count == 0) return new List<PresetSummary>();

            string body;
            try { body = JsonConvert.SerializeObject(new { p_ids = ids }, _jsonSettings); }
            catch { return null; }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_custom_engines_by_ids";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (List<PresetSummary>)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (List<PresetSummary>)null;
                                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(respBody)) return new List<PresetSummary>();
                                var arr = JArray.Parse(respBody);
                                var list = new List<PresetSummary>(arr.Count);
                                foreach (var row in arr)
                                {
                                    JToken inner = row;
                                    if (row is JObject ro && ro.Count == 1
                                        && ro.Properties().First().Value is JObject inside)
                                        inner = inside;
                                    bool suppressed = inner?["is_suppressed"]?.ToObject<bool>() ?? false;
                                    if (suppressed) continue;
                                    var s = ParseSummary(inner);
                                    if (s != null) { s.Kind = "engine"; list.Add(s); }
                                }
                                return list;
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] FetchCustomEnginesByIds failed: {ex.Message}");
                return null;
            }
        }

        public async Task<Dictionary<string, int>> FetchMyEngineVotesAsync(
            IList<string> engineIds, int timeoutMs = 4000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (engineIds == null || engineIds.Count == 0)
                return new Dictionary<string, int>();
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer)) return null;

            string body;
            try { body = JsonConvert.SerializeObject(new { p_custom_engine_ids = engineIds }, _jsonSettings); }
            catch { return null; }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_my_custom_engine_votes";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var map = new Dictionary<string, int>(engineIds.Count);
                        if (string.IsNullOrEmpty(respBody)) return map;
                        var arr = JArray.Parse(respBody);
                        foreach (var row in arr)
                        {
                            JToken inner = row;
                            if (row is JObject ro && ro.Count == 1
                                && ro.Properties().First().Value is JObject inside)
                                inner = inside;
                            string eid = inner?["custom_engine_id"]?.ToString();
                            int v = inner?["value"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(eid)) map[eid] = v;
                        }
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] My-engine-votes fetch failed: {ex.Message}");
                return null;
            }
        }

        // ---- Pack operations ----------------------------------------------
        // Parallel suite for community pack bundles. Body is a JSON
        // object holding multi-kind entries.

        public async Task<string> UploadPackAsync(
            string name, Newtonsoft.Json.Linq.JObject body, string description,
            string authorVersion, int entryCount,
            int bodyVersion = 1, int timeoutMs = 20000)
        {
            ResetUploadError();
            if (!ShouldSubmit(out var url, out var anonKey))
            { StampUploadError(UploadError.NetworkFailure, "community not configured"); return null; }
            if (string.IsNullOrWhiteSpace(name) || body == null)
            { StampUploadError(UploadError.ValidationFailed, "missing required field"); return null; }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_name           = name,
                    p_body           = body,
                    p_description    = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    p_author_version = string.IsNullOrWhiteSpace(authorVersion) ? null : authorVersion.Trim(),
                    p_entry_count    = entryCount,
                    p_body_version   = bodyVersion,
                    p_plugin_version = _pluginVersion,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Pack upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadPackRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[TF4ALL] Pack upload aborted: no auth bearer.");
                StampUploadError(UploadError.NotAuthenticated, "no bearer");
                return null;
            }
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + bearer);
                    req.Headers.Add("Prefer", "return=representation");
                    req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req,
                        HttpCompletionOption.ResponseContentRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        string respBody = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log?.Invoke($"[TF4ALL] Pack upload failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
                            StampUploadErrorFromStatus(resp.StatusCode, respBody);
                            return null;
                        }
                        var root = JToken.Parse(respBody);
                        var obj = root.Type == JTokenType.Array ? root[0] : root;
                        return obj?["id"]?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Pack upload exception: {ex.Message}");
                StampUploadError(UploadError.NetworkFailure, ex.Message);
                return null;
            }
        }

        public void RecordPackDownloadAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try { body = JsonConvert.SerializeObject(new { p_pack_id = id }, _jsonSettings); }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Download/report serialize failed: {ex.Message}"); return; }
            // Auth-gated per migration 0017; attach bearer if present.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, DownloadPackRpcPath, body, bearer);
            });
        }

        /// <summary>Flag a pack as reported. Requires sign-in.</summary>
        public void ReportPackAsync(string id, string category = "other", string note = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_pack_id  = id,
                    p_category = NormalizeReportCategory(category),
                    p_note     = NormalizeReportNote(note),
                }, _jsonSettings);
            }
            catch (Exception ex) { _log?.Invoke($"[TF4ALL] Report serialize failed: {ex.Message}"); return; }
            // Auth required: anonymous reports are rejected server-side.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(bearer)) return;
                FireAndForgetRpc(url, anonKey, ReportPackRpcPath, body, bearer);
            });
        }

        /// <summary>List community packs, sorted. No game/car scoping
        /// (a pack might cover any games / cars).</summary>
        public List<PresetSummary> FetchCommunityPacks(
            string sort = "wilson", int limit = 20, int offset = 0, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,author_version,entry_count,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100))
                      + (offset > 0 ? "&offset=" + offset : "");
            string fullUrl = url.TrimEnd('/') + PacksPath + qs;
            return RunGetList(fullUrl, anonKey, "pack", timeoutMs);
        }

        public PresetFull FetchCommunityPackBody(string id, int timeoutMs = 20000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(id)) return null;
            string qs = "?id=eq." + Uri.EscapeDataString(id)
                      + "&select=id,name,author,description,author_version,entry_count,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,body"
                      + "&limit=1";
            string fullUrl = url.TrimEnd('/') + PacksPath + qs;
            string capturedKey = anonKey;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (PresetFull)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                long contentLen = resp.Content?.Headers?.ContentLength ?? -1;
                                if (contentLen > ChannelValidation.MaxPresetBodyBytes && contentLen > 0)
                                {
                                    _log?.Invoke($"[TF4ALL] Pack body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[TF4ALL] Pack body exceeds max size after read");
                                    return (PresetFull)null;
                                }
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (PresetFull)null;
                                var row = arr[0];
                                var s = ParseSummary(row);
                                if (s != null) s.Kind = "pack";
                                return new PresetFull
                                {
                                    Summary = s,
                                    Body    = row["body"] as Newtonsoft.Json.Linq.JObject,
                                };
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Pack body fetch failed: {ex.Message}");
                return null;
            }
        }

        public List<PresetSummary> FetchPacksByIds(IList<string> ids, int timeoutMs = 8000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (ids == null || ids.Count == 0) return new List<PresetSummary>();
            string body;
            try { body = JsonConvert.SerializeObject(new { p_ids = ids }, _jsonSettings); }
            catch { return null; }
            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/get_packs_by_ids";
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // Community reads now require a signed-in user; send
                        // the bearer token and bail when there isn't one.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) return (List<PresetSummary>)null;
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (List<PresetSummary>)null;
                                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(respBody)) return new List<PresetSummary>();
                                var arr = JArray.Parse(respBody);
                                var list = new List<PresetSummary>(arr.Count);
                                foreach (var row in arr)
                                {
                                    JToken inner = row;
                                    if (row is JObject ro && ro.Count == 1
                                        && ro.Properties().First().Value is JObject inside)
                                        inner = inside;
                                    bool suppressed = inner?["is_suppressed"]?.ToObject<bool>() ?? false;
                                    if (suppressed) continue;
                                    var s = ParseSummary(inner);
                                    if (s != null) { s.Kind = "pack"; list.Add(s); }
                                }
                                return list;
                            }
                        }
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] FetchPacksByIds failed: {ex.Message}");
                return null;
            }
        }

        // ---- Helpers -------------------------------------------------------

        private static DateTime? TryParseDate(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (DateTime.TryParse(s, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return null;
        }

        private static PresetSummary ParseSummary(JToken row)
        {
            if (row == null) return null;
            var tags = new List<string>();
            var tagToken = row["effect_tags"];
            if (tagToken is JArray ta)
                foreach (var t in ta)
                    tags.Add(t?.ToString());
            // target_games is game-preset-only; absent / null = universal (empty array).
            string[] targetGames = new string[0];
            var tgToken = row["target_games"];
            if (tgToken is JArray tga)
            {
                var tgList = new List<string>(tga.Count);
                foreach (var g in tga)
                {
                    string s = g?.ToString();
                    if (!string.IsNullOrEmpty(s)) tgList.Add(s);
                }
                targetGames = tgList.ToArray();
            }
            DateTime? created = null;
            string c = row["created_at"]?.ToString();
            if (!string.IsNullOrEmpty(c)
                && DateTime.TryParse(c, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var dt))
                created = dt;
            return new PresetSummary
            {
                Id          = row["id"]?.ToString(),
                // get_my_presets tags each row with "kind"; PostgREST
                // selects on a single table don't. Leave the default
                // ("car") and let the caller stamp game-table results.
                Kind        = row["kind"]?.ToString() ?? "car",
                Name        = row["name"]?.ToString(),
                Author      = row["author"]?.ToString(),
                Description = row["description"]?.ToString(),
                Game        = row["game"]?.ToString(),
                CarId       = row["car_id"]?.ToString() ?? "",
                EffectTags  = tags,
                Upvotes     = AsInt(row["upvotes"]),
                Downvotes   = AsInt(row["downvotes"]),
                WilsonScore = AsDouble(row["wilson_score"]),
                Downloads   = AsInt(row["downloads"]),
                CreatedAt   = created,
                OwnerUserId    = row["owner_user_id"]?.ToString(),
                ContentVersion = AsInt(row["content_version"], 1),
                UpdatedAt      = TryParseDate(row["updated_at"]?.ToString()),
                EntryCount     = AsInt(row["entry_count"]),
                AuthorVersion  = row["author_version"]?.ToString(),
                AllowInPacks   = AsBool(row["allow_in_packs"]),
                TargetGames    = targetGames,
            };
        }

        // Null-safe JSON scalar parsers. The Newtonsoft `?.` operator
        // does NOT short-circuit on a JSON `null` value - `row["x"]`
        // returns a JValue whose Type == Null, not a C# null reference,
        // so a chained ?.ToObject<int>() throws on null-to-value. The
        // new get_my_presets RPC emits explicit null for entry_count /
        // author_version (non-pack rows) and allow_in_packs (pack rows),
        // so without these helpers FetchMyPresetsAsync threw on the
        // first row and the whole list came back null - which the UI
        // surfaces as "Could not reach the community backend."
        private static int AsInt(JToken t, int def = 0)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.ToObject<int>(); } catch { return def; }
        }

        private static double AsDouble(JToken t, double def = 0)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.ToObject<double>(); } catch { return def; }
        }

        private static bool AsBool(JToken t, bool def = false)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.ToObject<bool>(); } catch { return def; }
        }

        private bool ShouldSubmit(out string url, out string anonKey)
        {
            url = ""; anonKey = "";
            var s = _settingsProvider();
            if (s == null || !s.CommunityEnabled) return false;
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

        private void FireAndForgetRpc(string baseUrl, string anonKey,
            string rpcPath, string body, string bearer = null)
        {
            if (body == null) return;
            string fullUrl = baseUrl.TrimEnd('/') + rpcPath;
            string capturedKey = anonKey;
            string capturedBearer = bearer;

            Task.Run(async () =>
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                    {
                        req.Headers.Add("apikey", capturedKey);
                        req.Headers.Add("Authorization", "Bearer "
                            + (string.IsNullOrEmpty(capturedBearer) ? capturedKey : capturedBearer));
                        req.Headers.Add("Prefer", "return=minimal");
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        using (var resp = await _http.SendAsync(req,
                            HttpCompletionOption.ResponseHeadersRead,
                            CancellationToken.None).ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                string detail = resp.Content != null
                                    ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                                    : "";
                                _log?.Invoke(
                                    $"[TF4ALL] Preset RPC failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[TF4ALL] Preset RPC exception: {ex.Message}");
                }
            });
        }
    }
}
