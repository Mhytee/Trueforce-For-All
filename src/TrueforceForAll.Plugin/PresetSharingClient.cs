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
            if (c == 401 || c == 403) LastUploadError = UploadError.NotAuthenticated;
            else if (c == 429)        LastUploadError = UploadError.RateLimited;
            else if (c >= 400 && c < 500) LastUploadError = UploadError.ValidationFailed;
            else if (c >= 500)        LastUploadError = UploadError.ServerError;
            else                       LastUploadError = UploadError.NetworkFailure;
            LastUploadDetail = detail;
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
                _log?.Invoke($"[Trueforce] Preset upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Upload aborted: no auth bearer; refusing to upload as anonymous.");
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
                            _log?.Invoke($"[Trueforce] Preset upload failed: "
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
                _log?.Invoke($"[Trueforce] Preset upload exception: {ex.Message}");
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
            if (!ShouldSubmit(out var url, out var anonKey)) return false;
            if (string.IsNullOrWhiteSpace(id)) return false;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Update preset attempted while signed out.");
                return false;
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
                _log?.Invoke($"[Trueforce] Update preset serialize failed: {ex.Message}");
                return false;
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
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return true;
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        _log?.Invoke($"[Trueforce] Update preset failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Update preset exception: {ex.Message}");
                return false;
            }
        }

        // ---- Delete (auth-gated) -------------------------------------------

        /// <summary>Soft-delete an owned preset. Server sets
        /// is_suppressed=true so vote history stays for moderation. Returns
        /// true on success, false on any failure.</summary>
        public async Task<bool> DeletePresetAsync(string id, int timeoutMs = 10000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return false;
            if (string.IsNullOrWhiteSpace(id)) return false;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Delete preset attempted while signed out.");
                return false;
            }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new { p_preset_id = id }, _jsonSettings);
            }
            catch { return false; }

            string fullUrl = url.TrimEnd('/') + "/rest/v1/rpc/delete_preset";
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
                        _log?.Invoke($"[Trueforce] Delete preset failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Delete preset exception: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] My-presets fetch failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] My-votes fetch failed: {ex.Message}");
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
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
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
                _log?.Invoke($"[Trueforce] FetchPresetsByIds failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] Account stats fetch failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] Data export failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] Delete account failed: {ex.Message}");
                return false;
            }
        }

        // ---- Fetch list ----------------------------------------------------

        /// <summary>List presets for a (game, carId), sorted server-side.
        /// sort = "wilson" / "newest" / "downloads". Returns null on
        /// network/auth failure; empty list when no presets exist.</summary>
        public List<PresetSummary> FetchPresetsForCar(
            string game, string carId, string sort = "wilson",
            int limit = 20, int timeoutMs = 5000)
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
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100));
            string fullUrl = url.TrimEnd('/') + PresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "car", timeoutMs);
        }

        /// <summary>List game presets for a given game from the
        /// dedicated game_presets table. Same sort vocab as car
        /// presets ("wilson" / "newest" / "downloads").</summary>
        public List<PresetSummary> FetchGamePresetsForGame(
            string game, string sort = "wilson", int limit = 20, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game)) return new List<PresetSummary>();

            string orderClause = SortToOrderClause(sort);

            string qs = "?game=eq." + Uri.EscapeDataString(game)
                      + "&select=id,name,author,description,game,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100));
            string fullUrl = url.TrimEnd('/') + GamePresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "game", timeoutMs);
        }

        /// <summary>Cross-game trending car presets: no game/car scoping,
        /// used as a fallback when the panel is in "for car" mode but no
        /// game/car is loaded yet. Pattern parallels
        /// FetchCommunityCustomEngines (which has always been global).</summary>
        public List<PresetSummary> FetchTrendingCarPresets(
            string sort = "wilson", int limit = 50, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,game,car_id,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100));
            string fullUrl = url.TrimEnd('/') + PresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "car", timeoutMs);
        }

        /// <summary>Cross-game trending game presets: no game scoping,
        /// used as the for-car-mode fallback when no game is loaded.</summary>
        public List<PresetSummary> FetchTrendingGamePresets(
            string sort = "wilson", int limit = 50, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,game,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100));
            string fullUrl = url.TrimEnd('/') + GamePresetsPath + qs;
            return RunGetList(fullUrl, anonKey, "game", timeoutMs);
        }

        private static string SortToOrderClause(string sort)
        {
            switch ((sort ?? "").ToLowerInvariant())
            {
                case "newest":    return "created_at.desc";
                case "downloads": return "downloads.desc";
                case "wilson":
                default:          return "wilson_score.desc";
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
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
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
                _log?.Invoke($"[Trueforce] Preset list fetch failed: {ex.Message}");
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
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
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
                                    _log?.Invoke($"[Trueforce] Preset body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                // Belt-and-suspenders: re-check after read in case
                                // the server lied or omitted Content-Length.
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[Trueforce] Preset body exceeds max size after read");
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
                _log?.Invoke($"[Trueforce] Preset body fetch failed: {ex.Message}");
                return null;
            }
        }

        // ---- Vote ----------------------------------------------------------

        /// <summary>Fire-and-forget upvote / downvote / retract. value =
        /// +1, -1, or 0 (retract). Caller updates the local row
        /// optimistically; server vote_preset is idempotent on
        /// (preset_id, voter_id) and supports the 0 case via row-delete.</summary>
        public void VotePresetAsync(string id, int value)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            if (value != 1 && value != -1 && value != 0) return;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_preset_id = id,
                    p_value     = value,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Preset vote serialize failed: {ex.Message}");
                return;
            }
            // vote_preset is auth-gated now. Attach the bearer token if
            // available; the server will reject without one.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, VoteRpcPath, body, bearer);
            });
        }

        // ---- Awaitable vote variants --------------------------------------
        // Caller can await + reconcile its optimistic counter / arrow
        // state on failure. Same semantics as VotePresetAsync etc, but
        // returns Task<bool> instead of being fire-and-forget. Used by
        // ToggleVote so a server rejection (rate limit, blocked, no
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
                _log?.Invoke($"[Trueforce] Vote serialize failed: {ex.Message}");
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
                        _log?.Invoke($"[Trueforce] Vote failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Vote exception: {ex.Message}");
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
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Download/report serialize failed: {ex.Message}"); return; }
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

        /// <summary>Flag a preset as reported. Requires sign-in. Server-side
        /// rate-limited to one report per user per target per 24 hours.</summary>
        public void ReportPresetAsync(string id)
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
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Report serialize failed: {ex.Message}"); return; }
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

        /// <summary>Upload a whole-game preset body. The server stamps
        /// author = profiles.username; the local 'author' arg is
        /// accepted for back-compat but ignored.</summary>
        public async Task<string> UploadGamePresetAsync(
            string name, string game, JObject body,
            string description, List<string> effectTags,
            int bodyVersion = 1, int timeoutMs = 15000,
            bool allowInPacks = false)
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
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Game preset upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadGameRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Game preset upload aborted: no auth bearer.");
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
                            _log?.Invoke($"[Trueforce] Game preset upload failed: "
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
                _log?.Invoke($"[Trueforce] Game preset upload exception: {ex.Message}");
                StampUploadError(UploadError.NetworkFailure, ex.Message);
                return null;
            }
        }

        public async Task<bool> UpdateGamePresetAsync(string id,
            string name, string description, JObject body, List<string> effectTags,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return false;
            if (string.IsNullOrWhiteSpace(id)) return false;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Update game preset attempted while signed out.");
                return false;
            }

            string requestBody;
            try
            {
                requestBody = JsonConvert.SerializeObject(new
                {
                    p_game_preset_id = id,
                    p_name           = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    p_description    = description,
                    p_body           = (object)body,
                    p_effect_tags    = effectTags,
                    p_body_version   = bodyVersion,
                    p_allow_in_packs = allowInPacks,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Update game preset serialize failed: {ex.Message}");
                return false;
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
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return true;
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        _log?.Invoke($"[Trueforce] Update game preset failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Update game preset exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>Fire-and-forget upvote / downvote / retract on a
        /// game preset. value = +1, -1, or 0.</summary>
        public void VoteGamePresetAsync(string id, int value)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            if (value != 1 && value != -1 && value != 0) return;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_game_preset_id = id,
                    p_value          = value,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Game preset vote serialize failed: {ex.Message}");
                return;
            }
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, VoteGameRpcPath, body, bearer);
            });
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
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Download/report serialize failed: {ex.Message}"); return; }
            // Auth-gated per migration 0017; attach bearer if present.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, DownloadGameRpcPath, body, bearer);
            });
        }

        /// <summary>Flag a game preset as reported. Requires sign-in.</summary>
        public void ReportGamePresetAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try
            {
                body = JsonConvert.SerializeObject(new { p_game_preset_id = id }, _jsonSettings);
            }
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Report serialize failed: {ex.Message}"); return; }
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
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                long contentLen = resp.Content?.Headers?.ContentLength ?? -1;
                                if (contentLen > ChannelValidation.MaxPresetBodyBytes && contentLen > 0)
                                {
                                    _log?.Invoke($"[Trueforce] Game preset body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[Trueforce] Game preset body exceeds max size after read");
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
                _log?.Invoke($"[Trueforce] Game preset body fetch failed: {ex.Message}");
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
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
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
                _log?.Invoke($"[Trueforce] FetchGamePresetsByIds failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] My-game-votes fetch failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] Engine upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadEngineRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Engine upload aborted: no auth bearer.");
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
                            _log?.Invoke($"[Trueforce] Engine upload failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
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
                _log?.Invoke($"[Trueforce] Engine upload exception: {ex.Message}");
                StampUploadError(UploadError.NetworkFailure, ex.Message);
                return null;
            }
        }

        public async Task<bool> UpdateCustomEngineAsync(string id,
            string name, string description, Newtonsoft.Json.Linq.JObject body,
            int? bodyVersion = null, int timeoutMs = 15000,
            bool? allowInPacks = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return false;
            if (string.IsNullOrWhiteSpace(id)) return false;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Update engine attempted while signed out.");
                return false;
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
                _log?.Invoke($"[Trueforce] Update engine serialize failed: {ex.Message}");
                return false;
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
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return true;
                        string detail = resp.Content != null
                            ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
                        _log?.Invoke($"[Trueforce] Update engine failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Update engine exception: {ex.Message}");
                return false;
            }
        }

        public void VoteCustomEngineAsync(string id, int value)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            if (value != 1 && value != -1 && value != 0) return;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_custom_engine_id = id,
                    p_value            = value,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Engine vote serialize failed: {ex.Message}");
                return;
            }
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, VoteEngineRpcPath, body, bearer);
            });
        }

        public void RecordCustomEngineDownloadAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try { body = JsonConvert.SerializeObject(new { p_custom_engine_id = id }, _jsonSettings); }
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Download/report serialize failed: {ex.Message}"); return; }
            // Auth-gated per migration 0017; attach bearer if present.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, DownloadEngineRpcPath, body, bearer);
            });
        }

        /// <summary>Flag a custom engine as reported. Requires sign-in.</summary>
        public void ReportCustomEngineAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try { body = JsonConvert.SerializeObject(new { p_custom_engine_id = id }, _jsonSettings); }
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Report serialize failed: {ex.Message}"); return; }
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
            string sort = "wilson", int limit = 20, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at,allow_in_packs"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100));
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
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                long contentLen = resp.Content?.Headers?.ContentLength ?? -1;
                                if (contentLen > ChannelValidation.MaxPresetBodyBytes && contentLen > 0)
                                {
                                    _log?.Invoke($"[Trueforce] Engine body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[Trueforce] Engine body exceeds max size after read");
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
                _log?.Invoke($"[Trueforce] Engine body fetch failed: {ex.Message}");
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
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
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
                _log?.Invoke($"[Trueforce] FetchCustomEnginesByIds failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] My-engine-votes fetch failed: {ex.Message}");
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
                _log?.Invoke($"[Trueforce] Pack upload serialize failed: {ex.Message}");
                StampUploadError(UploadError.ValidationFailed, ex.Message);
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadPackRpcPath;
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(bearer))
            {
                _log?.Invoke("[Trueforce] Pack upload aborted: no auth bearer.");
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
                            _log?.Invoke($"[Trueforce] Pack upload failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {respBody}");
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
                _log?.Invoke($"[Trueforce] Pack upload exception: {ex.Message}");
                StampUploadError(UploadError.NetworkFailure, ex.Message);
                return null;
            }
        }

        public void VotePackAsync(string id, int value)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            if (value != 1 && value != -1 && value != 0) return;
            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_pack_id = id,
                    p_value   = value,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Pack vote serialize failed: {ex.Message}");
                return;
            }
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, VotePackRpcPath, body, bearer);
            });
        }

        public void RecordPackDownloadAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try { body = JsonConvert.SerializeObject(new { p_pack_id = id }, _jsonSettings); }
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Download/report serialize failed: {ex.Message}"); return; }
            // Auth-gated per migration 0017; attach bearer if present.
            Task.Run(async () =>
            {
                string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                FireAndForgetRpc(url, anonKey, DownloadPackRpcPath, body, bearer);
            });
        }

        /// <summary>Flag a pack as reported. Requires sign-in.</summary>
        public void ReportPackAsync(string id)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrWhiteSpace(id)) return;
            string body;
            try { body = JsonConvert.SerializeObject(new { p_pack_id = id }, _jsonSettings); }
            catch (Exception ex) { _log?.Invoke($"[Trueforce] Report serialize failed: {ex.Message}"); return; }
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
            string sort = "wilson", int limit = 20, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string orderClause = SortToOrderClause(sort);
            string qs = "?select=id,name,author,description,author_version,entry_count,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id,content_version,updated_at"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100));
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
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                long contentLen = resp.Content?.Headers?.ContentLength ?? -1;
                                if (contentLen > ChannelValidation.MaxPresetBodyBytes && contentLen > 0)
                                {
                                    _log?.Invoke($"[Trueforce] Pack body exceeds max size: {contentLen} > {ChannelValidation.MaxPresetBodyBytes}");
                                    return (PresetFull)null;
                                }
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
                                if (System.Text.Encoding.UTF8.GetByteCount(body) > ChannelValidation.MaxPresetBodyBytes)
                                {
                                    _log?.Invoke($"[Trueforce] Pack body exceeds max size after read");
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
                _log?.Invoke($"[Trueforce] Pack body fetch failed: {ex.Message}");
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
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            // Anonymous read - the apikey header is sufficient
                            // under PostgREST + Supabase RLS. The previous Bearer
                            // header stuffed the anon API key into a JWT-shaped
                            // header, which PostgREST silently ignored but
                            // violated HTTP semantics and would break if
                            // Supabase ever validates Bearer format.
                            req.Headers.Add("apikey", capturedKey);
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
                _log?.Invoke($"[Trueforce] FetchPacksByIds failed: {ex.Message}");
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
                Upvotes     = row["upvotes"]?.ToObject<int>() ?? 0,
                Downvotes   = row["downvotes"]?.ToObject<int>() ?? 0,
                WilsonScore = row["wilson_score"]?.ToObject<double>() ?? 0,
                Downloads   = row["downloads"]?.ToObject<int>() ?? 0,
                CreatedAt   = created,
                OwnerUserId    = row["owner_user_id"]?.ToString(),
                ContentVersion = row["content_version"]?.ToObject<int>() ?? 1,
                UpdatedAt      = TryParseDate(row["updated_at"]?.ToString()),
                EntryCount     = row["entry_count"]?.ToObject<int>() ?? 0,
                AuthorVersion  = row["author_version"]?.ToString(),
                AllowInPacks   = row["allow_in_packs"]?.ToObject<bool>() ?? false,
            };
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
                _log?.Invoke($"[Trueforce] Rejecting untrusted backend URL: {url}");
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
                                    $"[Trueforce] Preset RPC failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Trueforce] Preset RPC exception: {ex.Message}");
                }
            });
        }
    }
}
