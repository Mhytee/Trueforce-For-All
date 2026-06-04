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
using Newtonsoft.Json.Serialization;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    internal sealed class CommunityClient
    {
        // PostgREST RPC endpoint format: <project>/rest/v1/rpc/<function>.
        // We never call the table endpoints directly - the schema revokes
        // anon-key writes to the tables. Identity (submitter_id) is derived
        // server-side from the client IP, so submissions only need to pass
        // {p_game, p_car_id, p_fact_type, p_payload, p_plugin_version}.
        private const string SubmitRpcPath = "/rest/v1/rpc/submit_car_fact";
        private const string VoteRpcPath   = "/rest/v1/rpc/vote_car_fact";

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

        /// <summary>Submit a User-source engine-layout correction. No-op when
        /// community is disabled or backend is not configured. Returns
        /// immediately; the HTTP call runs on a thread-pool task. Failures
        /// (rate limit, validation, network) are logged but not surfaced -
        /// by the time this is called the local CarFacts write has already
        /// succeeded, so the user experience is the same regardless of
        /// network outcome.</summary>
        public void SubmitEngineLayoutAsync(string game, string carId,
            int cylinders, EngineConfig config)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (cylinders < 1 || cylinders > 16) return;
            // The SQL whitelist in normalize_car_fact_payload doesn't accept
            // 'CUSTOM' because CarFacts has no firing-pattern field to
            // carry; the Correct dialog excludes EngineConfig.Custom anyway,
            // but this guard prevents a wasted round-trip if a caller (or
            // a future code path) ever passes Custom through.
            if (config == EngineConfig.Custom) return;

            // Payload shape matches normalize_car_fact_payload in
            // 0001_carfacts_init.sql: {cyl: int, config: TEXT}. The server
            // upper-cases config so we send it that way already.
            var payload = new
            {
                cyl    = cylinders,
                config = config.ToString().ToUpperInvariant(),
            };
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "engine_layout", payload));
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

        /// <summary>Submit a User-source redline fact. Stage 2.2 wires the
        /// redline-correction affordance to this method.</summary>
        public void SubmitRedlineAsync(string game, string carId, int rpm)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (rpm < 500 || rpm > 25000) return;

            var payload = new { rpm };
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "redline", payload));
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
        private string BuildSubmitBody(string game, string carId, string factType, object payload)
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    p_game           = game,
                    p_car_id         = carId,
                    p_fact_type      = factType,
                    p_payload        = payload,
                    p_plugin_version = _pluginVersion,
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
