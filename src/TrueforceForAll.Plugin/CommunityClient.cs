// HTTP client for the Trueforce community Supabase backend (see
// supabase/README.md for schema + setup). Inert when
// Settings.CommunityEnabled is false OR when BackendUrl / AnonKey are
// missing, so a release build that ships the constants in a downgraded
// state still works fine for users who never opted in.
//
// Submission goes through SECURITY DEFINER RPC functions (submit_car_fact,
// vote_car_fact) NOT direct table writes - the backend treats the anon key
// as public and writes can't be authenticated, so identity (submitter_id /
// voter_id) is derived server-side from the client IP. The plugin never
// asserts or relies on a submitter_id of its own.
//
// Submission is fire-and-forget by design: a user just clicked Correct on
// the plugin UI; their local CarFacts is already saved. The network call
// runs on a background thread with no retry, no queue, and any failure logs
// + drops the submission. If the submission was dropped, the user can re-
// trigger by saving again, OR a later background pull will retroactively
// learn what others submitted for the same car. v1 ships without a retry
// queue; we add one only if telemetry shows submissions are getting lost.
//
// All public methods are safe to call from any thread. The HttpClient
// instance is a static singleton so repeated calls don't churn TCP/TLS
// sessions. Plugin targets net48 where HttpClient uses HttpClientHandler /
// WinHttpHandler under the covers; the singleton pattern still avoids
// socket-exhaustion (the classic per-call HttpClient leak); DNS pinning
// over the process lifetime is acceptable here because Supabase URLs are
// stable.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    /// <summary>Snapshot of the community consensus for one car/fact, used
    /// to drive the share-prompt copy ("you're the first" / "confirming X
    /// drivers" / "alternative to current consensus"). All fields are
    /// optional; SupportingSubmissions counts distinct submitters of the
    /// winning payload; Confirmations counts up-votes on that payload.</summary>
    internal sealed class EngineLayoutConsensus
    {
        public string Layout { get; set; }
        public int    Confirmations { get; set; }
        public int    SupportingSubmissions { get; set; }
        // sha256 of Postgres's canonical jsonb::text of the payload. The
        // server populates this on every recompute; vote callers pass it
        // back through vote_car_fact's p_expected_payload_hash CAS guard
        // so a vote landing after a candidate flip raises instead of
        // silently re-attributing.
        public string PayloadHash { get; set; }
        // Set only when Layout == "CUSTOM". Carries the full custom
        // engine definition (name + firing pattern + electric flag) so
        // the receiver can play it without having the def in their
        // local CustomEngines library. Transient: the plugin synthesizes
        // a one-car-only CustomEngineDef in memory and discards it on
        // car change. Null for non-custom layouts.
        public CommunityCustomEngine Custom { get; set; }
    }

    /// <summary>Snapshot of a community-submitted custom engine def,
    /// rides on EngineLayoutConsensus when Layout=="CUSTOM". Mirrors the
    /// fields of CustomEngineDef that are meaningful for synthesis. Not
    /// imported into the receiver's library - the plugin's apply path
    /// reads these fields directly when a community custom wins the
    /// resolver cascade for the active car.</summary>
    internal sealed class CommunityCustomEngine
    {
        public string Name         { get; set; }
        public string Pattern      { get; set; }   // FiringPatternDb.ParseCustom format
        public bool   IsElectric   { get; set; }
        public string ElectricMode { get; set; }   // "MUTEDHUM" / "SILENT"
    }

    /// <summary>Snapshot of the community consensus for the car_name fact,
    /// pulled per (game, carId). Variant-blind on purpose: a car's name is
    /// chassis-level - the variant_signature column always carries '' for
    /// car_name submissions so all engine swaps for one carId contribute
    /// to the same name consensus.</summary>
    internal sealed class CarNameConsensus
    {
        public string Name { get; set; }
        public int    Confirmations { get; set; }
        public int    SupportingSubmissions { get; set; }
        public string PayloadHash { get; set; }
    }

    /// <summary>Snapshot of the community consensus for the redline fact,
    /// per (game, carId, variant_signature). Variant-aware: different
    /// engines on the same chassis (Forza swaps) have different redlines,
    /// so the consensus row keys on the same fingerprint as
    /// engine_layout.</summary>
    internal sealed class RedlineConsensus
    {
        public int    Rpm { get; set; }
        public int    Confirmations { get; set; }
        public int    SupportingSubmissions { get; set; }
        public string PayloadHash { get; set; }
    }

    internal sealed class CommunityClient
    {
        // PostgREST RPC endpoint format: <project>/rest/v1/rpc/<function>.
        // We never call the table endpoints directly for writes - the schema
        // revokes anon-key writes to the tables. Identity (submitter_id) is
        // derived server-side from the client IP, so submissions only need to
        // pass {p_game, p_car_id, p_fact_type, p_payload, p_plugin_version}.
        private const string SubmitRpcPath = "/rest/v1/rpc/submit_car_fact";
        private const string VoteRpcPath   = "/rest/v1/rpc/vote_car_fact";

        // car_fact_consensus IS anon-readable (the only table that is).
        // The share-prompt fetches the current consensus row to render
        // accurate "first / confirming / alternative" framing.
        private const string ConsensusPath = "/rest/v1/car_fact_consensus";

        // One static HttpClient for the whole plugin lifetime: matches the
        // SocketsHttpHandler pooling expectation and avoids the well-known
        // "ephemeral HttpClient leaks sockets" issue.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // PostgREST passes RPC body keys to the SQL function as named
        // arguments verbatim, so the JSON keys must be exactly the function
        // arg names ("p_game", "p_car_id", ...). We send anonymous objects
        // with the right property names and no naming-strategy transform.
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver(),  // identity
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly Func<TrueforceSettings> _settingsProvider;
        private readonly Action<string>          _log;
        private readonly string                  _pluginVersion;

        public CommunityClient(Func<TrueforceSettings> settingsProvider,
            Action<string> log, string pluginVersion)
        {
            _settingsProvider = settingsProvider
                ?? throw new ArgumentNullException(nameof(settingsProvider));
            _log = log;
            _pluginVersion = pluginVersion ?? "";
        }

        /// <summary>Vote on the current community consensus for a car's
        /// engine layout. direction = +1 (confirm) or -1 (refute). The
        /// payload_hash from the consensus row is passed through as a
        /// CAS guard so a vote landing after a candidate-flip raises
        /// 'consensus changed, refetch' server-side instead of silently
        /// re-attributing the user's intent.</summary>
        public void VoteEngineLayoutAsync(string game, string carId,
            int direction, string expectedPayloadHash,
            string variantSignature = "")
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (direction != 1 && direction != -1) return;

            // PostgREST RPC body keys match the SQL function arg names.
            // p_variant_signature is optional server-side (defaults to '')
            // but we always send it - "" is the explicit no-discriminator
            // marker for non-Forza games where telemetry can't fingerprint.
            string body;
            try
            {
                body = JsonConvert.SerializeObject(new
                {
                    p_game                  = game,
                    p_car_id                = carId,
                    p_fact_type             = "engine_layout",
                    p_direction             = direction,
                    p_expected_payload_hash = expectedPayloadHash,
                    p_variant_signature     = variantSignature ?? "",
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Community vote serialize failed: {ex.Message}");
                return;
            }

            FireAndForgetRpc(url, anonKey, VoteRpcPath, body);
        }

        /// <summary>Submit a User-source engine-layout correction. No-op
        /// when community is disabled or backend is not configured.
        /// Returns immediately; the HTTP call runs on a thread-pool task.
        /// Failures (rate limit, validation, network) are logged but not
        /// surfaced - by the time this is called the local CarFacts write
        /// has already succeeded, so the user experience is the same
        /// regardless of network outcome.
        ///
        /// Auto / Electric / Custom short-circuit without firing because
        /// they have no representation in the community whitelist: Auto
        /// is "I don't know," Electric is a different feature, Custom is
        /// authored via the Custom Engine library.</summary>
        public void SubmitEngineLayoutAsync(string game, string carId, EngineLayout layout,
            string variantSignature = "")
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            // Custom + Electric submissions go through dedicated entry
            // points (SubmitCustomEngineAsync); Auto is the no-claim
            // sentinel. The built-in EngineLayout enums (V8CROSSPLANE,
            // INLINE6, ...) are the only fact_type=engine_layout payloads
            // submitted from this path.
            if (layout == EngineLayout.Auto
                || layout == EngineLayout.Electric
                || layout == EngineLayout.Custom) return;

            var payload = new { layout = layout.ToString().ToUpperInvariant() };
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "engine_layout", payload, variantSignature));
        }

        /// <summary>Submit a custom-engine assignment for (game, carId).
        /// The full def rides on the payload so the receiver can play the
        /// pattern without having it in their library. fact_type stays
        /// engine_layout so per-car consensus + voting + variant routing
        /// all reuse the existing plumbing - layout=CUSTOM is just
        /// another value in the whitelist server-side.</summary>
        public void SubmitCustomEngineAsync(string game, string carId,
            string name, string pattern, bool isElectric, string electricMode,
            string variantSignature = "")
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            name = (name ?? "").Trim();
            if (name.Length < 2 || name.Length > 96) return;
            pattern = pattern ?? "";
            if (pattern.Length > 512) return;
            string modeUpper = string.IsNullOrEmpty(electricMode)
                ? "MUTEDHUM"
                : electricMode.Trim().ToUpperInvariant();
            if (modeUpper != "MUTEDHUM" && modeUpper != "SILENT") modeUpper = "MUTEDHUM";

            var payload = new
            {
                layout = "CUSTOM",
                custom = new
                {
                    name,
                    pattern,
                    electric = isElectric,
                    electric_mode = modeUpper,
                },
            };
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "engine_layout", payload, variantSignature));
        }

        /// <summary>Submit a User-source car-name fact. Stage 2.1 wires the
        /// "Name this car" affordance to this method.</summary>
        public void SubmitCarNameAsync(string game, string carId, string name)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.Length < 2 || name.Length > 96) return;

            var payload = new { name };
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "car_name", payload));
        }

        /// <summary>Submit a User-source redline fact. Variant-aware
        /// because a swap changes the redline. Caller computes the
        /// signature from the active EnginePulse observations and passes
        /// it through.</summary>
        public void SubmitRedlineAsync(string game, string carId, int rpm,
            string variantSignature = "")
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (rpm < 500 || rpm > 25000) return;

            var payload = new { rpm };
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "redline", payload, variantSignature));
        }

        /// <summary>Fetch the current engine_layout consensus row for the
        /// given (game, carId) - blocking with a short timeout so the
        /// share-prompt can render accurate "first / confirming /
        /// alternative" copy. Returns null on any of: community disabled,
        /// no backend configured, timeout, network error, no consensus
        /// row, malformed response. Callers should degrade to generic
        /// copy when null comes back.
        ///
        /// Synchronous wrapper around an async HTTP call because the
        /// caller is a WPF dialog opening on the UI thread; the consensus
        /// state is needed before the dialog can render. timeoutMs caps
        /// the UI block.</summary>
        /// <summary>Variant-aware fetch of the consensus redline for
        /// (game, carId, variantSignature). Variant-aware because a swap
        /// changes the redline along with the engine, so each variant
        /// gets its own row. Mirrors the layout fetch shape (sync wrapper
        /// + bounded timeout).</summary>
        public RedlineConsensus FetchRedlineConsensus(
            string game, string carId,
            string variantSignature = "",
            int timeoutMs = 2000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return null;

            string qs = "?game=eq."  + Uri.EscapeDataString(game)
                      + "&car_id=eq." + Uri.EscapeDataString(carId)
                      + "&fact_type=eq.redline"
                      + "&variant_signature=eq." + Uri.EscapeDataString(variantSignature ?? "")
                      + "&select=payload,payload_hash,confirmations,supporting_submissions"
                      + "&limit=1";
            string fullUrl = url.TrimEnd('/') + ConsensusPath + qs;
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
                                if (!resp.IsSuccessStatusCode) return (RedlineConsensus)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (RedlineConsensus)null;
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (RedlineConsensus)null;
                                var row = arr[0];
                                int rpm = row?["payload"]?["rpm"]?.ToObject<int>() ?? 0;
                                if (rpm <= 0) return (RedlineConsensus)null;
                                return new RedlineConsensus
                                {
                                    Rpm                   = rpm,
                                    Confirmations         = row["confirmations"]?.ToObject<int>() ?? 0,
                                    SupportingSubmissions = row["supporting_submissions"]?.ToObject<int>() ?? 0,
                                    PayloadHash           = row["payload_hash"]?.ToString(),
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
                _log?.Invoke($"[Trueforce] Community redline fetch failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Variant-blind fetch of the consensus car_name for
        /// (game, carId). Mirrors <see cref="FetchEngineLayoutConsensus"/>'
        /// shape (sync wrapper + bounded timeout) so the SettingsControl
        /// car-change hook can ride on the same per-tick fetch loop.</summary>
        public CarNameConsensus FetchCarNameConsensus(
            string game, string carId, int timeoutMs = 2000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return null;

            // car_name submissions always carry variant_signature='' so the
            // consensus row is unique on (game, car_id, 'car_name', '').
            string qs = "?game=eq."  + Uri.EscapeDataString(game)
                      + "&car_id=eq." + Uri.EscapeDataString(carId)
                      + "&fact_type=eq.car_name"
                      + "&variant_signature=eq."
                      + "&select=payload,payload_hash,confirmations,supporting_submissions"
                      + "&limit=1";
            string fullUrl = url.TrimEnd('/') + ConsensusPath + qs;
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
                                if (!resp.IsSuccessStatusCode) return (CarNameConsensus)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (CarNameConsensus)null;
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (CarNameConsensus)null;
                                var row = arr[0];
                                string name = row?["payload"]?["name"]?.ToString();
                                if (string.IsNullOrEmpty(name)) return (CarNameConsensus)null;
                                return new CarNameConsensus
                                {
                                    Name                  = name,
                                    Confirmations         = row["confirmations"]?.ToObject<int>() ?? 0,
                                    SupportingSubmissions = row["supporting_submissions"]?.ToObject<int>() ?? 0,
                                    PayloadHash           = row["payload_hash"]?.ToString(),
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
                _log?.Invoke($"[Trueforce] Community car_name fetch failed: {ex.Message}");
                return null;
            }
        }

        public EngineLayoutConsensus FetchEngineLayoutConsensus(
            string game, string carId,
            string variantSignature = "",
            int timeoutMs = 2000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return null;

            // Filter on variant_signature so non-Forza ("") fetches the no-
            // discriminator row; Forza fetches the row matching what the
            // user is currently driving. Forces an exact match - falling
            // back to a different variant's consensus would put the wrong
            // value under the user's Auto-detect line.
            string qs = "?game=eq."  + Uri.EscapeDataString(game)
                      + "&car_id=eq." + Uri.EscapeDataString(carId)
                      + "&fact_type=eq.engine_layout"
                      + "&variant_signature=eq." + Uri.EscapeDataString(variantSignature ?? "")
                      + "&select=payload,payload_hash,confirmations,supporting_submissions"
                      + "&limit=1";
            string fullUrl = url.TrimEnd('/') + ConsensusPath + qs;
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
                                if (!resp.IsSuccessStatusCode) return (EngineLayoutConsensus)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (EngineLayoutConsensus)null;
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (EngineLayoutConsensus)null;
                                var row = arr[0];
                                string layout = row?["payload"]?["layout"]?.ToString();
                                if (string.IsNullOrEmpty(layout)) return (EngineLayoutConsensus)null;
                                CommunityCustomEngine custom = null;
                                if (string.Equals(layout, "CUSTOM", StringComparison.OrdinalIgnoreCase))
                                {
                                    var c = row["payload"]?["custom"];
                                    if (c != null && c.Type == JTokenType.Object)
                                    {
                                        custom = new CommunityCustomEngine
                                        {
                                            Name         = c["name"]?.ToString(),
                                            Pattern      = c["pattern"]?.ToString() ?? "",
                                            IsElectric   = c["electric"]?.ToObject<bool>() ?? false,
                                            ElectricMode = c["electric_mode"]?.ToString() ?? "MUTEDHUM",
                                        };
                                    }
                                    // A CUSTOM layout with no def is malformed - treat as no consensus.
                                    if (custom == null || string.IsNullOrEmpty(custom.Name))
                                        return (EngineLayoutConsensus)null;
                                }
                                return new EngineLayoutConsensus
                                {
                                    Layout                = layout,
                                    Confirmations         = row["confirmations"]?.ToObject<int>() ?? 0,
                                    SupportingSubmissions = row["supporting_submissions"]?.ToObject<int>() ?? 0,
                                    PayloadHash           = row["payload_hash"]?.ToString(),
                                    Custom                = custom,
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
                _log?.Invoke($"[Trueforce] Community consensus fetch failed: {ex.Message}");
                return null;
            }
        }

        // True iff a submission should actually go out: community is enabled
        // AND both URL and anon key are non-blank. Identity is server-derived
        // so the client has nothing to lazy-init here.
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

        // Build the JSON body for a submit_car_fact RPC call. PostgREST
        // expects the SQL function's argument names as JSON keys.
        // variant_signature is always sent (defaults to "" for fact_types
        // that don't carry a variant discriminator like car_name).
        private string BuildSubmitBody(string game, string carId, string factType,
            object payload, string variantSignature = "")
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    p_game              = game,
                    p_car_id            = carId,
                    p_fact_type         = factType,
                    p_payload           = payload,
                    p_plugin_version    = _pluginVersion,
                    p_variant_signature = variantSignature ?? "",
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Community submit serialize failed: {ex.Message}");
                return null;
            }
        }

        private void FireAndForgetRpc(string baseUrl, string anonKey,
            string rpcPath, string body)
        {
            if (body == null) return;
            string fullUrl = baseUrl.TrimEnd('/') + rpcPath;
            string capturedKey = anonKey;

            // ThreadPool task; we don't await it. Any exception is caught
            // inside the lambda so it never bubbles up to the
            // TaskScheduler.UnobservedTaskException finalizer path (which on
            // net48 can terminate the process on
            // ThrowUnobservedTaskExceptions). All paths log + drop.
            Task.Run(async () =>
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                    {
                        req.Headers.Add("apikey", capturedKey);
                        req.Headers.Add("Authorization", "Bearer " + capturedKey);
                        // RPC calls return a value; we don't care about the
                        // value but Prefer: return=minimal can still cut the
                        // response body.
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
                                    $"[Trueforce] Community submit failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Trueforce] Community submit error: {ex.Message}");
                }
            });
        }
    }
}
