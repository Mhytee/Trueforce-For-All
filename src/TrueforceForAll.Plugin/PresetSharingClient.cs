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
        public string Name          { get; set; }
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string Game          { get; set; }
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

    internal sealed class PresetSharingClient
    {
        // PostgREST RPC + table paths.
        private const string UploadRpcPath    = "/rest/v1/rpc/upload_preset";
        private const string VoteRpcPath      = "/rest/v1/rpc/vote_preset";
        private const string DownloadRpcPath  = "/rest/v1/rpc/record_preset_download";
        private const string ReportRpcPath    = "/rest/v1/rpc/report_preset";
        private const string PresetsPath      = "/rest/v1/presets";
        private const string PresetVotesPath  = "/rest/v1/preset_votes";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            ContractResolver  = new DefaultContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

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

        // ---- Upload --------------------------------------------------------

        /// <summary>Upload a CarPresetFile body to the community.
        /// effectTags is the list of non-null section names (engine,
        /// revlimiter, ...) computed by the caller; server validates
        /// against a whitelist. Returns the new preset id on success or
        /// null on any failure (network, validation, rate limit). The
        /// 'author' value is sourced from Settings.SharingAuthor in the
        /// caller; pass through verbatim.</summary>
        public async Task<string> UploadCarPresetAsync(
            string name, string game, string carId, JObject body,
            string author, string description, List<string> effectTags,
            int bodyVersion = 1, int timeoutMs = 15000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(game)
                || string.IsNullOrEmpty(carId) || body == null) return null;

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
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Preset upload serialize failed: {ex.Message}");
                return null;
            }

            string capturedKey = anonKey;
            string fullUrl = url.TrimEnd('/') + UploadRpcPath;
            // Pass the authenticated bearer token when signed in so the
            // server can stamp owner_user_id from auth.uid(). Anonymous
            // uploads still succeed (owner_user_id stays NULL = read-only
            // forever); the modal can warn but doesn't block.
            string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                {
                    req.Headers.Add("apikey", capturedKey);
                    req.Headers.Add("Authorization", "Bearer " + (bearer ?? capturedKey));
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
            int? bodyVersion = null, int timeoutMs = 15000)
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
                    p_preset_id    = id,
                    p_name         = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    p_description  = description,  // null is fine; "" clears
                    p_body         = (object)body,
                    p_effect_tags  = effectTags,
                    p_body_version = bodyVersion,
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
        public List<PresetSummary> FetchMyPresets(
            string sort = "newest", int limit = 100, int timeoutMs = 5000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            string bearer = GetAccessTokenOrNullAsync().GetAwaiter().GetResult();
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
                {
                    var task = Task.Run(async () =>
                    {
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
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
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
        public Dictionary<string, int> FetchMyVotes(
            IList<string> presetIds, int timeoutMs = 4000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (presetIds == null || presetIds.Count == 0)
                return new Dictionary<string, int>();
            string bearer = GetAccessTokenOrNullAsync().GetAwaiter().GetResult();
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
                {
                    var task = Task.Run(async () =>
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (Dictionary<string, int>)null;
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
                    }, cts.Token);
                    if (!task.Wait(timeoutMs)) return null;
                    return task.Result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] My-votes fetch failed: {ex.Message}");
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

            string orderClause;
            switch ((sort ?? "").ToLowerInvariant())
            {
                case "newest":    orderClause = "created_at.desc"; break;
                case "downloads": orderClause = "downloads.desc";  break;
                case "wilson":
                default:          orderClause = "wilson_score.desc"; break;
            }

            // PostgREST select - exclude body to keep the list response
            // small. body comes through FetchPresetBody only.
            string qs = "?game=eq."   + Uri.EscapeDataString(game)
                      + "&car_id=eq." + Uri.EscapeDataString(carId)
                      + "&select=id,name,author,description,game,car_id,effect_tags,"
                      + "upvotes,downvotes,wilson_score,downloads,created_at,owner_user_id"
                      + "&order=" + orderClause
                      + "&limit=" + Math.Max(1, Math.Min(limit, 100));
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
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + capturedKey);
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
                                    list.Add(ParseSummary(row));
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
                      + "upvotes,downvotes,wilson_score,downloads,created_at,body"
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
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + capturedKey);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (PresetFull)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (PresetFull)null;
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
            catch { return; }
            FireAndForgetRpc(url, anonKey, DownloadRpcPath, body);
        }

        // ---- Report --------------------------------------------------------

        /// <summary>Flag a preset as reported. Out-of-band moderation
        /// reviews the row; reporting is idempotent so multiple flags
        /// don't escalate beyond a binary.</summary>
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
            catch { return; }
            FireAndForgetRpc(url, anonKey, ReportRpcPath, body);
        }

        // ---- Helpers -------------------------------------------------------

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
                Name        = row["name"]?.ToString(),
                Author      = row["author"]?.ToString(),
                Description = row["description"]?.ToString(),
                Game        = row["game"]?.ToString(),
                CarId       = row["car_id"]?.ToString(),
                EffectTags  = tags,
                Upvotes     = row["upvotes"]?.ToObject<int>() ?? 0,
                Downvotes   = row["downvotes"]?.ToObject<int>() ?? 0,
                WilsonScore = row["wilson_score"]?.ToObject<double>() ?? 0,
                Downloads   = row["downloads"]?.ToObject<int>() ?? 0,
                CreatedAt   = created,
                OwnerUserId = row["owner_user_id"]?.ToString(),
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
