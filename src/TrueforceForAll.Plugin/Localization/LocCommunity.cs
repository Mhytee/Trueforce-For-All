// The translation service, client side: fetch what the community translated, and
// send what this translator wrote.
//
// Offline first. The layer on disk is applied at Load, before any network call, so
// a machine with no connection shows everything it showed yesterday and a failed
// fetch changes nothing. The file is replaced wholesale, never merged, which is
// what makes a row that was superseded on the server disappear here with no
// removal list to maintain.
//
// Read with the anon key as bearer and never with the user's token, so a
// translation fetch is not joinable to an account. Write with the user's token,
// because a submission is attributed.
//
// Every fetched row is re-checked against the English THIS build displays before it
// can reach a label. The server checked it too, against whatever English it held;
// those are not the same thing in a fleet running three channels.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin.Localization
{
    internal sealed class LocCommunity : IDisposable
    {
        private const string ListRpc = "/rest/v1/rpc/list_approved_translations";
        private const string SubmitRpc = "/rest/v1/rpc/submit_translations";

        /// <summary>Above this the response is abandoned and the file on disk kept.
        /// The server's own cap is 10,000 rows, which cannot reach this in any
        /// language we ship; a response that does is not one of ours.</summary>
        private const int MaxResponseBytes = 2 * 1024 * 1024;

        /// <summary>At most three chain members per fetch: es-MX, es, and a third
        /// only a tag like zh-Hans-CN has. Two of those will usually have no rows,
        /// which costs one call each and writes no file.</summary>
        private const int MaxChainMembers = 3;

        /// <summary>Rows per send. The server refuses 51 with a sentence, so the
        /// window chunks rather than finding out.</summary>
        public const int SendChunk = 50;

        private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan Period = TimeSpan.FromHours(6);
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

        private readonly LocStore _store;
        private readonly Func<TrueforceSettings> _settings;
        private readonly Func<Task<string>> _accessToken;
        /// <summary>The install's own contributor id, minted and persisted by the plugin.
        /// Used when there is no account, which is most of the time: translating needs no
        /// sign-in, and an account only adds a name to the credits.</summary>
        private readonly Func<string> _anonId;
        private readonly Action<string> _log;
        private readonly Action<Action> _onUi;
        private readonly string _pluginVersion;
        private readonly HttpClient _http;
        private Timer _timer;
        private int _running;          // single-flight, Interlocked
        private bool _disposed;

        public LocCommunity(LocStore store, Func<TrueforceSettings> settings,
                            Func<Task<string>> accessToken, Func<string> anonId,
                            Action<Action> onUi, string pluginVersion, Action<string> log)
        {
            _store = store;
            _settings = settings;
            _accessToken = accessToken ?? (() => Task.FromResult<string>(null));
            _anonId = anonId ?? (() => null);
            _onUi = onUi ?? (a => a());
            _pluginVersion = pluginVersion;
            _log = log ?? (m => { });
            // Its own client, with a hard ceiling on what it will buffer.
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.MaxResponseContentBufferSize = MaxResponseBytes;
        }

        /// <summary>Start the background fetch: once 20 seconds in, then every six
        /// hours, each run self-gated on the cache's own 24-hour stamp. Its own
        /// timer, deliberately: hanging this off the usage-ping timer would stop
        /// translations reaching anyone who turned usage statistics off.</summary>
        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(_ => { try { _ = RunAsync(false); } catch { } }, null, FirstDelay, Period);
        }

        /// <summary>A language was picked, or the access code was typed, or the
        /// window asked. Forced runs ignore the 24-hour stamp.</summary>
        public void RequestNow() { try { _ = RunAsync(true); } catch { } }

        private const string ProgressRpc = "/rest/v1/rpc/translation_progress";
        private static readonly TimeSpan ProgressTtl = TimeSpan.FromMinutes(10);
        private Dictionary<string, double> _progress;
        private DateTime _progressAt;

        /// <summary>How much of each language exists on the server, as a percentage of
        /// the English it holds. Empty when there is no backend, no consent, or the call
        /// fails, in which case a caller shows what it can see locally.
        ///
        /// Not gated on the active language being a translation, unlike the fetch: the
        /// one caller is the language chooser, and an English install is exactly where
        /// someone opens it to start a language. It is still a request only when a person
        /// opens that window, never in the background.</summary>
        public async Task<Dictionary<string, double>> GetProgressAsync()
        {
            var cached = _progress;
            if (cached != null && DateTime.UtcNow - _progressAt < ProgressTtl) return cached;
            var empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var s = _settings();
            if (s == null || !s.CommunityEnabled || !s.UseCommunityTranslations) return empty;
            string url = (s.CommunityBackendUrl ?? "").Trim();
            string anonKey = (s.CommunityBackendAnonKey ?? "").Trim();
            if (url.Length == 0 || anonKey.Length == 0) return empty;
            if (!ChannelValidation.IsTrustedSupabaseUrl(url)) return empty;
            try
            {
                string json;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + ProgressRpc))
                {
                    req.Headers.Add("apikey", anonKey);
                    req.Headers.Add("Authorization", "Bearer " + anonKey);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                                 .ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return empty;
                        json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in JArray.Parse(json))
                {
                    string tag = row?["tag"]?.ToString();
                    if (string.IsNullOrEmpty(tag)) continue;
                    double percent;
                    if (double.TryParse(row?["percent"]?.ToString(),
                                        System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out percent))
                        map[tag] = percent;
                }
                _progress = map;
                _progressAt = DateTime.UtcNow;
                return map;
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translations: progress is unavailable, showing what is on disk: " + ex.Message);
                return empty;
            }
        }

        private bool Ready(out string url, out string anonKey)
        {
            url = ""; anonKey = "";
            var s = _settings();
            if (s == null || !s.CommunityEnabled || !s.UseCommunityTranslations) return false;
            if (_store == null || _store.LanguagesRoot == null) return false;
            // English needs no fetch, and an English install must not notice this
            // feature exists: no request, no file, no layer.
            if (LocStore.IsEnglishTag(_store.ActiveTag)) return false;
            url = (s.CommunityBackendUrl ?? "").Trim();
            anonKey = (s.CommunityBackendAnonKey ?? "").Trim();
            if (url.Length == 0 || anonKey.Length == 0) return false;
            if (!ChannelValidation.IsTrustedSupabaseUrl(url))
            {
                _log("[TF4ALL] Translations: rejecting untrusted backend URL: " + url);
                return false;
            }
            return true;
        }

        public async Task RunAsync(bool force)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try
            {
                string url, anonKey;
                if (!Ready(out url, out anonKey)) return;

                bool wrote = false;
                var members = new List<string>();
                for (string t = _store.ActiveTag; t != null && members.Count < MaxChainMembers; t = LocStore.ParentTag(t))
                {
                    if (LocStore.IsEnglishTag(t)) break;
                    members.Add(t);
                }
                foreach (string tag in members)
                {
                    if (!force && !IsStale(tag)) continue;
                    if (await FetchOneAsync(url, anonKey, tag).ConfigureAwait(false)) wrote = true;
                }
                // One reload for the whole chain, on the UI thread: every {loc:T}
                // binding re-renders from it, and LocWatcher never sees this
                // subfolder because it watches the root without subdirectories.
                if (wrote) _onUi(() => { try { _store.Reload(); } catch { } });
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translations: fetch failed, keeping what is on disk: " + ex.Message);
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        /// <summary>Whether this member's cache is older than the TTL. The stamp
        /// lives in the file, never in a settings key, so deleting the file is all
        /// it takes to start over.</summary>
        private bool IsStale(string tag)
        {
            try
            {
                string path = _store.CommunityPath(tag);
                if (path == null || !File.Exists(path)) return true;
                var meta = ReadMeta(path);
                string when = meta?["fetched_at"]?.ToString();
                DateTime stamp;
                if (string.IsNullOrEmpty(when)
                    || !DateTime.TryParse(when, System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.AdjustToUniversal
                                          | System.Globalization.DateTimeStyles.AssumeUniversal, out stamp))
                    return true;
                return DateTime.UtcNow - stamp > Ttl;
            }
            catch { return true; }
        }

        private JObject ReadMeta(string path)
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(path));
                return o[LocStore.MetaMember] as JObject;
            }
            catch { return null; }
        }

        private long KnownRevision(string tag)
        {
            var meta = ReadMeta(_store.CommunityPath(tag));
            if (meta == null) return -1;
            long rev;
            return long.TryParse(meta["revision"]?.ToString(), out rev) ? rev : -1;
        }

        /// <summary>One chain member. Returns true when the file on disk changed.</summary>
        private async Task<bool> FetchOneAsync(string url, string anonKey, string tag)
        {
            long known = KnownRevision(tag);
            var body = new JObject { ["p_culture"] = tag };
            if (known >= 0) body["p_known_revision"] = known;

            string json;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + ListRpc))
            {
                req.Headers.Add("apikey", anonKey);
                // The anon key, never the user's token: a translation read must not
                // be joinable to an account.
                req.Headers.Add("Authorization", "Bearer " + anonKey);
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None),
                                                Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                             .ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        _log("[TF4ALL] Translations: " + tag + " fetch returned " + (int)resp.StatusCode);
                        return false;
                    }
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            if (string.IsNullOrEmpty(json)) return false;
            if (json.Length > MaxResponseBytes)
            {
                _log("[TF4ALL] Translations: " + tag + " response was too large; keeping the file on disk.");
                return false;
            }

            JObject answer;
            try { answer = JObject.Parse(json); }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translations: " + tag + " response was not JSON: " + ex.Message);
                return false;
            }
            if (answer["unchanged"] != null && (bool?)answer["unchanged"] == true)
            {
                TouchFetchedAt(tag);     // still current: restart the TTL, write nothing else
                return false;
            }
            if (answer["truncated"] != null && (bool?)answer["truncated"] == true)
            {
                // No rows at all by design: a partial set would be half-applied by a
                // client whose file is a wholesale replace.
                _log("[TF4ALL] Translations: " + tag + " has more rows than the server will serve at once; keeping the file on disk.");
                return false;
            }
            var rows = answer["rows"] as JArray;
            if (rows == null)
            {
                _log("[TF4ALL] Translations: " + tag + " response carried no rows array.");
                return false;
            }
            long revision = 0;
            long.TryParse(answer["revision"]?.ToString(), out revision);

            var kept = new Dictionary<string, string>(StringComparer.Ordinal);
            var stale = new List<string>();
            int droppedUnknown = 0, droppedStale = 0, droppedUnsafe = 0, sameAsShipped = 0;
            foreach (var row in rows)
            {
                string key = row?["k"]?.ToString();
                string text = row?["t"]?.ToString();
                string hash = row?["h"]?.ToString();
                if (string.IsNullOrEmpty(key) || text == null) { droppedUnsafe++; continue; }
                string english = _store.EnglishText(key);
                if (english == null) { droppedUnknown++; continue; }
                // Written for a different English: record it, do not serve it. The key
                // falls through to the shipped translation or to English rather than
                // showing a sentence written for wording this build does not have.
                if (!string.Equals(hash, LocFile.EnglishSha256(english), StringComparison.Ordinal))
                {
                    if (stale.Count < 200) stale.Add(key);
                    droppedStale++;
                    continue;
                }
                string why = LocPlaceholderRules.Reject(english, text);
                if (why != null)
                {
                    droppedUnsafe++;
                    _log("[TF4ALL] Translations: " + tag + " dropped " + key + ": " + why + ".");
                    continue;
                }
                // Counted, not dropped. It was dropped when this file was only a set
                // of fixes; now it is also the record of what the server holds, which is
                // how the window knows which rows are still unsent. Overlaying text that
                // matches the layer below changes nothing on screen.
                if (string.Equals(text, _store.ResolveBelowCommunity(tag, key), StringComparison.Ordinal))
                    sameAsShipped++;
                if (kept.ContainsKey(key))
                    _log("[TF4ALL] Translations: " + tag + " served " + key + " twice; taking the last.");
                kept[key] = text;
            }

            string path = _store.CommunityPath(tag);
            if (kept.Count == 0)
            {
                // Nothing to add: no file, and a stale one goes, so the two empty
                // members of a zh-Hans-CN chain cost one call each and no disk.
                try
                {
                    if (File.Exists(path)) { File.Delete(path); return true; }
                }
                catch (Exception ex) { _log("[TF4ALL] Translations: could not remove " + tag + ": " + ex.Message); }
                return false;
            }

            var meta = new JObject
            {
                ["culture"] = tag,
                // From the shipped or embedded file, never from the response: without
                // a name ParseLanguageJson warns on every read.
                ["name"] = _store.DisplayName(tag) ?? tag,
                ["source"] = LocStore.CommunityFolder,
                ["revision"] = revision,
                ["fetched_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["served"] = kept.Count,
                ["dropped_unknown"] = droppedUnknown,
                ["dropped_stale"] = droppedStale,
                ["dropped_unsafe"] = droppedUnsafe,
                ["same_as_shipped"] = sameAsShipped,
            };
            if (stale.Count > 0) meta["stale"] = new JArray(stale.ToArray());
            try
            {
                LocFile.WriteAtomic(path, LocFile.Serialize(kept, meta.ToString(Newtonsoft.Json.Formatting.None)));
                _log("[TF4ALL] Translations: " + tag + " at revision " + revision + ", " + kept.Count
                     + " held, " + sameAsShipped + " of them already what this build shows, dropped "
                     + droppedStale + " stale, " + droppedUnsafe + " refused, " + droppedUnknown + " unknown.");
                return true;
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translations: could not write " + tag + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>What the server holds for this language, as key to text, read from
        /// the cache the fetch writes. Empty when there is no cache yet, which reads as
        /// "the server has nothing of mine", so a first publish sends everything.
        ///
        /// This is the whole bookkeeping behind publishing as you type: a row whose local
        /// text differs from this has not reached the server, and one that matches has.
        /// No separate ledger to keep in step, and it survives a restart because it is a
        /// file.</summary>
        public Dictionary<string, string> Published(string tag)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string path = _store.CommunityPath(tag);
                if (path == null || !File.Exists(path)) return map;
                var o = JObject.Parse(File.ReadAllText(path));
                foreach (var p in o.Properties())
                {
                    if (p.Name == LocStore.MetaMember) continue;
                    map[p.Name] = p.Value?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translations: could not read what the server holds for " + tag + ": " + ex.Message);
            }
            return map;
        }

        /// <summary>The server says nothing changed, so only the stamp moves. Rewritten
        /// through the same atomic path; a failure here costs one early re-fetch.</summary>
        private void TouchFetchedAt(string tag)
        {
            try
            {
                string path = _store.CommunityPath(tag);
                if (path == null || !File.Exists(path)) return;
                var o = JObject.Parse(File.ReadAllText(path));
                var meta = o[LocStore.MetaMember] as JObject;
                if (meta == null) return;
                meta["fetched_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in o.Properties())
                {
                    if (p.Name == LocStore.MetaMember) continue;
                    values[p.Name] = p.Value?.ToString() ?? "";
                }
                LocFile.WriteAtomic(path, LocFile.Serialize(values, meta.ToString(Newtonsoft.Json.Formatting.None)));
            }
            catch { }
        }

        /// <summary>What one send produced: what the server took, and the sentence it
        /// refused each row with. Both are shown verbatim, because the server writes
        /// them for a translator to read.</summary>
        internal sealed class SendResult
        {
            public bool Ok;
            public int Accepted;
            public List<string> Errors = new List<string>();
            /// <summary>Set when the whole call was refused, which is a cap or a
            /// closed language rather than one bad row.</summary>
            public string Refusal;
        }

        /// <summary>Send rows for one language, in chunks of 50. Needs the user's
        /// token: a submission is attributed, unlike a read.</summary>
        public async Task<SendResult> SendAsync(string tag, IList<KeyValuePair<string, string>> rows)
        {
            var result = new SendResult();
            string url, anonKey;
            if (!Ready(out url, out anonKey))
            {
                result.Refusal = Loc.T("Translate_PublishOff");
                return result;
            }
            // An account is optional. With one, the row carries the person's id and
            // their name can appear in the credits; without one, it carries the install's
            // own id and the credits say (anonymous). Either way the text is the same
            // contribution, and refusing it for want of an account would lose the fix
            // from the person most likely to have noticed it.
            string token = await _accessToken().ConfigureAwait(false);
            string anonId = (_anonId() ?? "").Trim();
            if (string.IsNullOrEmpty(token) && anonId.Length == 0)
            {
                result.Refusal = Loc.T("Translate_PublishNoId");
                return result;
            }
            if (rows == null || rows.Count == 0) { result.Ok = true; return result; }

            for (int start = 0; start < rows.Count; start += SendChunk)
            {
                var batch = new JArray();
                for (int i = start; i < rows.Count && i < start + SendChunk; i++)
                {
                    string english = _store.EnglishText(rows[i].Key);
                    if (english == null) continue;          // not ours to send
                    batch.Add(new JObject
                    {
                        ["k"] = rows[i].Key,
                        ["t"] = rows[i].Value,
                        ["h"] = LocFile.EnglishSha256(english),
                    });
                }
                if (batch.Count == 0) continue;
                var body = new JObject
                {
                    ["p_culture"] = tag,
                    ["p_rows"] = batch,
                    ["p_source"] = "plugin",
                    ["p_plugin_version"] = _pluginVersion,
                };
                // Sent whether or not there is a token: the server takes the account when
                // the request carries one and falls back to this, and a row already
                // attributed to an account ignores it.
                if (anonId.Length > 0) body["p_anon_id"] = anonId;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    using (var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + SubmitRpc))
                    {
                        req.Headers.Add("apikey", anonKey);
                        // The user's token when there is one, so the row is theirs; the
                        // anon key otherwise, which is what makes a signed-out send reach
                        // the function at all.
                        req.Headers.Add("Authorization",
                            "Bearer " + (string.IsNullOrEmpty(token) ? anonKey : token));
                        req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None),
                                                        Encoding.UTF8, "application/json");
                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                                     .ConfigureAwait(false))
                        {
                            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                            {
                                // A P0001 raise arrives as a PostgREST error object whose
                                // message is the sentence the server wrote to be shown.
                                // The server's own sentence when it wrote one. Those are English, whatever the
                                // panel is in: they come from a P0001 raise and there is no
                                // table of them on this side to translate.
                                result.Refusal = MessageFrom(text)
                                    ?? Loc.F("Translate_PublishRefusedCode_Fmt", (int)resp.StatusCode);
                                return result;
                            }
                            var answer = JObject.Parse(text);
                            var accepted = answer["accepted"] as JArray;
                            if (accepted != null) result.Accepted += accepted.Count;
                            var rejected = answer["rejected"] as JArray;
                            if (rejected != null)
                                foreach (var r in rejected)
                                {
                                    string k = r?["k"]?.ToString();
                                    string e = r?["error"]?.ToString();
                                    if (!string.IsNullOrEmpty(e)) result.Errors.Add((k ?? "?") + ": " + e);
                                }
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Refusal = Loc.F("Translate_PublishNoReach_Fmt", ex.Message);
                    return result;
                }
            }
            result.Ok = true;
            return result;
        }

        private static string MessageFrom(string body)
        {
            try
            {
                var o = JObject.Parse(body ?? "");
                string m = o["message"]?.ToString();
                return string.IsNullOrWhiteSpace(m) ? null : m;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            try { _http?.Dispose(); } catch { }
        }
    }
}
