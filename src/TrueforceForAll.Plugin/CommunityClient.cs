// HTTP client for the Trueforce community Supabase backend (see
// supabase/README.md for schema + setup). Inert when
// Settings.CommunityEnabled is false OR when BackendUrl / AnonKey are
// missing, so a release build that ships the constants in a downgraded
// state still works fine for users who never opted in.
//
// Submission goes through the submit_car_fact SECURITY DEFINER RPC, NOT
// direct table writes. Car facts are account-FREE but not account-blind
// (migration 0100): submissions never show a username to other users, and
// no account is needed to contribute. A signed-in user's token still rides
// the request so the server keys the submission to their account (car-fact
// achievements, moderation); signed-out submissions carry
// Settings.CarFactsAnonId, a client-minted random GUID sent as p_anon_id,
// which the server stores as 'anon:<id>' and rate-limits per id AND per
// hashed client IP. Consensus reads work with the plain anon key. All of it
// is gated client-side by the one-time car-data consent
// (Settings.AutoSubmitCarFacts via CarFactsConsentGate).
//
// Car-fact consensus is confirmation/correction based, not voted: every
// driver submits what their car actually reads (confirm = re-submit the same
// value, correct = submit a different one) and the winner is the payload
// backed by the most distinct submitters. (Community PRESETS are voted, but
// that is a separate system in PresetSharingClient.)
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
    /// winning payload - the confirm/correct consensus signal (car facts are
    /// not voted).</summary>
    // Public (not internal) so a snapshot can be persisted in the local
    // community-fact cache (TrueforceSettings.CommunityFactCache) for offline /
    // TTL use. Plain DTO; no behaviour.
    public sealed class EngineLayoutConsensus
    {
        public string Layout { get; set; }
        public int    SupportingSubmissions { get; set; }
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
    public sealed class CommunityCustomEngine
    {
        public string Name         { get; set; }
        public string Pattern      { get; set; }   // FiringPatternDb.ParseCustom format
        public bool   IsElectric   { get; set; }
        public string ElectricMode { get; set; }   // "MUTEDHUM" / "SILENT"
    }

    /// <summary>Snapshot of the community consensus for the car_name fact,
    /// pulled per (game, carId). Engine-blind on purpose (a car's name is
    /// chassis-level, so every engine swap for one carId shares it), but keyed
    /// per LANGUAGE: car_name's variant_signature carries "lang=en" etc. so
    /// each language has its own consensus and an English plurality can't bury
    /// a correct non-English name.</summary>
    public sealed class CarNameConsensus
    {
        public string Name { get; set; }
        public int    SupportingSubmissions { get; set; }
    }

    /// <summary>Snapshot of the community consensus for the redline fact,
    /// per (game, carId, variant_signature). Variant-aware: different
    /// engines on the same chassis (Forza swaps) have different redlines,
    /// so the consensus row keys on the same fingerprint as
    /// engine_layout.</summary>
    public sealed class RedlineConsensus
    {
        public int    Rpm { get; set; }
        public int    SupportingSubmissions { get; set; }
        /// <summary>Optional per-gear overrides from the consensus payload (gear
        /// 1..N -> rpm). Null/empty for the common single-redline case. Gear 0
        /// (the overall value) is <see cref="Rpm"/>.</summary>
        public System.Collections.Generic.Dictionary<int, int> Gears { get; set; }
    }

    internal sealed class CommunityClient
    {
        // PostgREST RPC endpoint format: <project>/rest/v1/rpc/<function>.
        // We never call the table endpoints directly for writes - the schema
        // revokes anon-key writes to the tables. Identity (submitter_id) is
        // derived server-side from the client IP, so submissions only need to
        // pass {p_game, p_car_id, p_fact_type, p_payload, p_plugin_version}.
        private const string SubmitRpcPath = "/rest/v1/rpc/submit_car_fact";

        // car_fact_consensus is authenticated-read only (migration 0027);
        // reads send the signed-in user's bearer and bail when there isn't
        // one. The share-prompt fetches the current consensus row to render
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
        // When non-null, an authenticated bearer-token provider (the same
        // CommunityAuth.GetAccessTokenAsync source PresetSharingClient uses).
        // Awaited before each auth-gated read so token refresh happens
        // lazily. Null in unit-test / no-auth contexts.
        private readonly Func<Task<string>>      _accessTokenProvider;

        public CommunityClient(Func<TrueforceSettings> settingsProvider,
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

        /// <summary>Submit a User-source car-name fact. The official in-game
        /// name, keyed per language so an English plurality can't bury the
        /// correct name in another language.</summary>
        public void SubmitCarNameAsync(string game, string carId, string name,
            string variantSignature = "")
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.Length < 2 || name.Length > 96) return;

            // variant_signature carries the submitter's LANGUAGE ("lang=en") so
            // each language forms its own consensus row. Still chassis-level
            // otherwise (no cyl/engine in the key): one name per language across
            // every engine swap on the car.
            var payload = new { name };
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "car_name", payload, variantSignature));
        }

        /// <summary>Submit a User-source redline fact. Variant-aware
        /// because a swap changes the redline. Caller computes the
        /// signature from the active EnginePulse observations and passes
        /// it through.</summary>
        public void SubmitRedlineAsync(string game, string carId, int rpm,
            string variantSignature = "",
            System.Collections.Generic.IReadOnlyList<GearRedline> perGear = null)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (rpm < 500 || rpm > 25000) return;

            // Per-gear consensus, approach A: each gear is its OWN fact so the
            // server agrees on each gear independently (and partial per-gear data
            // is kept instead of fragmenting the whole profile). Gear 0 (overall)
            // is the 'redline' fact with a BARE { rpm } payload; each forward gear
            // 1..16 is a separate 'redline_gN' fact { rpm }. We deliberately do
            // NOT embed a 'gears' array in the overall payload: that would cluster
            // the gear-0 consensus separately from bare { rpm } and split its
            // support count. Each is fire-and-forget; per-gear count is the user's
            // override count (typically 0-4), well within the per-user submit cap.
            FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                BuildSubmitBody(game, carId, "redline", new { rpm }, variantSignature));

            if (perGear == null) return;
            foreach (var g in perGear)
            {
                if (g == null || g.Gear < 1 || g.Gear > 16 || g.Rpm < 500 || g.Rpm > 25000) continue;
                FireAndForgetRpc(url, anonKey, SubmitRpcPath,
                    BuildSubmitBody(game, carId, "redline_g" + g.Gear, new { rpm = g.Rpm }, variantSignature));
            }
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
            int minGearSupport = 2,
            int timeoutMs = 2000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return null;

            // One round trip for the overall 'redline' fact PLUS every per-gear
            // fact ('redline_g1'..'redline_g16'). Each gear is its own consensus
            // row with its own supporting_submissions, so we assemble them here
            // and floor each gear independently (below). fact_type rides the
            // select so we can map a row back to its gear.
            string qs = "?game=eq."  + Uri.EscapeDataString(game)
                      + "&car_id=eq." + Uri.EscapeDataString(carId)
                      + "&fact_type=in.(" + RedlineFactTypeInList + ")"
                      + "&variant_signature=eq." + Uri.EscapeDataString(variantSignature ?? "")
                      + "&is_suppressed=eq.false"   // don't let a moderator-suppressed row drive the buzz
                      + "&select=fact_type,payload,payload_hash,confirmations,supporting_submissions"
                      + "&limit=18";
            string fullUrl = url.TrimEnd('/') + ConsensusPath + qs;
            string capturedKey = anonKey;
            int gearFloor = minGearSupport;

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // car_fact_consensus is anon-readable (0100): prefer
                        // the signed-in token when one exists, fall back to
                        // the anon key so signed-out users get facts too.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) bearer = capturedKey;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (RedlineConsensus)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (RedlineConsensus)null;
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (RedlineConsensus)null;

                                JToken overall = null;
                                System.Collections.Generic.Dictionary<int, int> gearMap = null;
                                foreach (var row in arr)
                                {
                                    string ft = row?["fact_type"]?.ToString();
                                    if (string.IsNullOrEmpty(ft)) continue;
                                    if (ft == "redline") { overall = row; continue; }
                                    if (!ft.StartsWith("redline_g")) continue;
                                    if (!int.TryParse(ft.Substring("redline_g".Length), out int gear)
                                        || gear < 1 || gear > 16) continue;
                                    // Floor EACH gear independently: a lone (often
                                    // self-) submission is not "the community".
                                    int gsup = row["supporting_submissions"]?.ToObject<int>() ?? 0;
                                    if (gsup < gearFloor) continue;
                                    int gr = row?["payload"]?["rpm"]?.ToObject<int>() ?? 0;
                                    if (gr < 500 || gr > 25000) continue;
                                    (gearMap = gearMap ?? new System.Collections.Generic.Dictionary<int, int>())[gear] = gr;
                                }

                                if (overall == null) return (RedlineConsensus)null;
                                int rpm = overall?["payload"]?["rpm"]?.ToObject<int>() ?? 0;
                                if (rpm <= 0) return (RedlineConsensus)null;
                                return new RedlineConsensus
                                {
                                    Rpm                   = rpm,
                                    SupportingSubmissions = overall["supporting_submissions"]?.ToObject<int>() ?? 0,
                                    // Already per-gear-floored, so downstream (adopt,
                                    // banner, cascade) can trust every entry.
                                    Gears                 = gearMap,
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
                _log?.Invoke($"[TF4ALL] Community redline fetch failed: {ex.Message}");
                return null;
            }
        }

        // Fixed PostgREST in.() list: the overall 'redline' fact plus every
        // per-gear fact. Built once (do not assemble per call).
        private const string RedlineFactTypeInList =
            "redline,redline_g1,redline_g2,redline_g3,redline_g4,redline_g5,redline_g6,redline_g7,redline_g8,"
            + "redline_g9,redline_g10,redline_g11,redline_g12,redline_g13,redline_g14,redline_g15,redline_g16";

        /// <summary>Variant-blind fetch of the consensus car_name for
        /// (game, carId). Mirrors <see cref="FetchEngineLayoutConsensus"/>'
        /// shape (sync wrapper + bounded timeout) so the SettingsControl
        /// car-change hook can ride on the same per-tick fetch loop.</summary>
        public CarNameConsensus FetchCarNameConsensus(
            string game, string carId, string preferredVariantSignature = "",
            int timeoutMs = 2000)
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return null;

            // car_name is keyed per LANGUAGE in variant_signature ("lang=en").
            // Pull every language's row for this car (a handful at most) and
            // pick the user's own language if present, else the most-supported
            // row of any language (English in practice; legacy unkeyed rows from
            // before this split compete here on support too).
            string prefSig = preferredVariantSignature ?? "";
            string qs = "?game=eq."  + Uri.EscapeDataString(game)
                      + "&car_id=eq." + Uri.EscapeDataString(carId)
                      + "&fact_type=eq.car_name"
                      + "&select=payload,supporting_submissions,variant_signature"
                      + "&order=supporting_submissions.desc"
                      + "&limit=24";
            string fullUrl = url.TrimEnd('/') + ConsensusPath + qs;
            string capturedKey = anonKey;

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var task = Task.Run(async () =>
                    {
                        // car_fact_consensus is anon-readable (0100): prefer
                        // the signed-in token when one exists, fall back to
                        // the anon key so signed-out users get facts too.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) bearer = capturedKey;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
                            using (var resp = await _http.SendAsync(req,
                                HttpCompletionOption.ResponseContentRead,
                                cts.Token).ConfigureAwait(false))
                            {
                                if (!resp.IsSuccessStatusCode) return (CarNameConsensus)null;
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(body)) return (CarNameConsensus)null;
                                var arr = JArray.Parse(body);
                                if (arr.Count == 0) return (CarNameConsensus)null;
                                // Prefer the user's-language row; else the top
                                // one (most-supported, since ordered desc).
                                JToken chosen = null;
                                foreach (var r in arr)
                                {
                                    string sig = r?["variant_signature"]?.ToString() ?? "";
                                    if (string.Equals(sig, prefSig, StringComparison.Ordinal))
                                    {
                                        chosen = r;
                                        break;
                                    }
                                }
                                if (chosen == null) chosen = arr[0];
                                string name = chosen?["payload"]?["name"]?.ToString();
                                if (string.IsNullOrEmpty(name)) return (CarNameConsensus)null;
                                return new CarNameConsensus
                                {
                                    Name                  = name,
                                    SupportingSubmissions = chosen["supporting_submissions"]?.ToObject<int>() ?? 0,
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
                _log?.Invoke($"[TF4ALL] Community car_name fetch failed: {ex.Message}");
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
                        // car_fact_consensus is anon-readable (0100): prefer
                        // the signed-in token when one exists, fall back to
                        // the anon key so signed-out users get facts too.
                        string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(bearer)) bearer = capturedKey;
                        using (var req = new HttpRequestMessage(HttpMethod.Get, fullUrl))
                        {
                            req.Headers.Add("apikey", capturedKey);
                            req.Headers.Add("Authorization", "Bearer " + bearer);
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
                                    SupportingSubmissions = row["supporting_submissions"]?.ToObject<int>() ?? 0,
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
                _log?.Invoke($"[TF4ALL] Community consensus fetch failed: {ex.Message}");
                return null;
            }
        }

        // True iff a submission should actually go out: community is enabled
        // AND both URL and anon key are non-blank AND the URL points at a
        // trusted Supabase host. Identity is server-derived so the client has
        // nothing to lazy-init here.
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

        // ---- Message of the Day ----------------------------------------------
        // The motd table is anon-readable (its RLS policy returns only active,
        // in-window rows), so unlike the car-fact reads this does NOT require a
        // signed-in user: the anon key is the bearer. Gated only by
        // CommunityEnabled + a trusted backend URL (ShouldSubmit covers both).
        private const string MotdPath = "/rest/v1/motd";

        /// <summary>Fetch the active MOTD feed. Returns null on any failure
        /// (caller keeps its offline cache); an empty list is a valid "nothing
        /// active right now" answer and SHOULD replace the cache.</summary>
        public async Task<System.Collections.Generic.List<MotdItem>> FetchMotdAsync()
        {
            if (!ShouldSubmit(out var url, out var anonKey)) return null;
            // Page through PostgREST (limit + offset) so the entire library is fetched
            // no matter how large it grows. Recurring rows come back year-round (the
            // client picks the day), so the active set is ~the whole library; a fixed
            // cap would silently hide the oldest rows once the library outgrows it.
            const int pageSize = 500;
            string baseUrl = url.TrimEnd('/') + MotdPath
                + "?select=id,kind,importance,category,body,link_url,link_label,"
                + "recurrence,recur_month,recur_day,recur_window_days,anchor_year,action,audience"
                + "&order=created_at.desc";
            var list = new System.Collections.Generic.List<MotdItem>();
            try
            {
                for (int offset = 0; offset <= 100000; offset += pageSize)
                {
                    string pageUrl = baseUrl + "&limit=" + pageSize + "&offset=" + offset;
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var req = new HttpRequestMessage(HttpMethod.Get, pageUrl))
                    {
                        req.Headers.Add("apikey", anonKey);
                        req.Headers.Add("Authorization", "Bearer " + anonKey);
                        using (var resp = await _http.SendAsync(req,
                            HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                        {
                            // A failed page aborts the whole fetch (return null keeps the
                            // existing offline cache rather than caching a partial set).
                            if (!resp.IsSuccessStatusCode) return null;
                            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (string.IsNullOrEmpty(body)) break;
                            var arr = JArray.Parse(body);
                            foreach (var t in arr)
                            {
                                string text = (t?["body"]?.ToString() ?? "").Trim();
                                if (text.Length == 0) continue;
                                // Defense in depth: the DB already enforces https-only,
                                // but never hand a non-https link to the launcher.
                                string link = t?["link_url"]?.ToString();
                                if (!string.IsNullOrEmpty(link) &&
                                    !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                    link = null;
                                list.Add(new MotdItem
                                {
                                    Id         = t?["id"]?.ToString(),
                                    Kind       = t?["kind"]?.ToString(),
                                    Importance = t?["importance"]?.ToString(),
                                    Category   = t?["category"]?.ToString(),
                                    Body       = text,
                                    LinkUrl    = link,
                                    LinkLabel  = t?["link_label"]?.ToString(),
                                    Recurrence      = t?["recurrence"]?.ToString(),
                                    RecurMonth      = t?["recur_month"]?.ToObject<int?>(),
                                    RecurDay        = t?["recur_day"]?.ToObject<int?>(),
                                    RecurWindowDays = t?["recur_window_days"]?.ToObject<int?>() ?? 1,
                                    AnchorYear      = t?["anchor_year"]?.ToObject<int?>(),
                                    Action          = t?["action"]?.ToString(),
                                    Audience        = t?["audience"]?.ToString(),
                                });
                            }
                            if (arr.Count < pageSize) break;   // last (short) page
                        }
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] MOTD fetch failed: " + ex.Message);
                return null;
            }
        }

        // Build the JSON body for a submit_car_fact RPC call. PostgREST
        // expects the SQL function's argument names as JSON keys.
        // variant_signature is always sent (defaults to "" for fact_types
        // that don't carry a variant discriminator like car_name).
        // p_anon_id is the FALLBACK identity for signed-out submissions;
        // the server prefers auth.uid() when the request carries a user
        // token. Omitted when not minted (NullValueHandling strips the
        // null): a signed-in submission succeeds without it, a signed-out
        // one is rejected server-side with 'sign-in required' and logged.
        private string BuildSubmitBody(string game, string carId, string factType,
            object payload, string variantSignature = "")
        {
            string anonId = (_settingsProvider()?.CarFactsAnonId ?? "").Trim();
            if (anonId.Length == 0) anonId = null;
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
                    p_anon_id           = anonId,
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TF4ALL] Community submit serialize failed: {ex.Message}");
                return null;
            }
        }

        private void FireAndForgetRpc(string baseUrl, string anonKey,
            string rpcPath, string body)
        {
            if (body == null) return;
            // Consent gate, enforced at the last exit: every car-fact
            // submission path (including the preset-upload name passthrough)
            // funnels through here, so a withdrawn consent stops all of them.
            if (_settingsProvider()?.AutoSubmitCarFacts != true)
            {
                _log?.Invoke("[TF4ALL] Community submit skipped: car-data sharing is off");
                return;
            }
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
                    // Prefer the signed-in token so the submission is keyed to
                    // the account server-side (feeds the car-fact achievements
                    // and the owner's moderation view); fall back to the anon
                    // key + the client-minted p_anon_id so account-less users
                    // can submit too. Either way other users never see a
                    // username on car facts.
                    string bearer = await GetAccessTokenOrNullAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(bearer)) bearer = capturedKey;
                    using (var req = new HttpRequestMessage(HttpMethod.Post, fullUrl))
                    {
                        req.Headers.Add("apikey", capturedKey);
                        req.Headers.Add("Authorization", "Bearer " + bearer);
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
                                    $"[TF4ALL] Community submit failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[TF4ALL] Community submit error: {ex.Message}");
                }
            });
        }
    }
}
