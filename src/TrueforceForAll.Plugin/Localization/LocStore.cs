// The text table behind every localized string in the plugin: which language
// is active, where each key's text comes from and what happens when a key is
// missing. Pure C# on purpose (no WPF, no SimHub types, Newtonsoft.Json.Linq
// only): TrueforceForAll.Core.Tests links this file by <Compile Include> and
// runs it on net8, so anything WPF-shaped belongs in Loc.cs, TExtension.cs,
// LocWatcher.cs or LocDiagnostics.cs instead.
//
// Layers, highest precedence first, for the requested tag and then for each
// parent tag (ParentTag: es-MX -> es, zh-Hans-CN -> zh-Hans -> zh):
//   <languagesRoot>\<tag>.json          a user's or translator's corrections
//   <languagesRoot>\shipped\<tag>.json  the reference copy LocSeed writes
//   embedded <tag>                      the translation built into the DLL
// then English, which is the embedded "en" plus a root en.json override, and
// finally "[key]" with one Warn per key per session.
//
// The indexer is what XAML binds to ({loc:T Key} becomes a Binding to
// "[Key]" on this object), so Load and Reload raise PropertyChanged for
// "Item[]" and every bound label re-renders live.
//
// Design, phases and the file format: docs/localization-plan.md.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin.Localization
{
    public sealed class LocStore : INotifyPropertyChanged
    {
        /// <summary>The language every other one falls back to.</summary>
        public const string EnglishTag = "en";

        /// <summary>The one non-string member a language file may carry.</summary>
        public const string MetaMember = "_meta";

        /// <summary>The name of the folder under languagesRoot holding the
        /// reference copies LocSeed writes.</summary>
        public const string ShippedFolder = "shipped";

        // U+27E6 and U+27E7: mathematical white square brackets, chosen because
        // no UI string uses them, so a marked fallback cannot be mistaken for
        // real text.
        private const char FallbackOpen = '⟦';
        private const char FallbackClose = '⟧';

        private readonly Func<string, string> _readEmbedded;
        private readonly string _languagesRoot;
        private readonly Action<string> _log;

        // Both dictionaries are built whole and swapped by reference in Load,
        // so a reader on another thread sees either the old table or the new,
        // never a half-filled one.
        private Dictionary<string, string> _english = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> _active = new Dictionary<string, string>(StringComparer.Ordinal);
        private HashSet<string> _embeddedEnglishKeys = new HashSet<string>(StringComparer.Ordinal);
        private bool _activeIsEnglish = true;
        private bool _markFallbacks;

        // One Warn per key per session, whatever the language does later.
        private readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedFormat = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedOnce = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="readEmbedded">Returns the embedded language JSON for a
        /// culture tag, or null when this build has no such language.</param>
        /// <param name="languagesRoot">The folder holding root overrides
        /// (&lt;tag&gt;.json) and shipped\&lt;tag&gt;.json, or null for no disk
        /// layers at all (tests, or a plugin that could not resolve its
        /// folder).</param>
        /// <param name="log">Receives already-prefixed "[TF4ALL] ..." text.
        /// Everything this class logs is a warning.</param>
        public LocStore(Func<string, string> readEmbedded, string languagesRoot, Action<string> log)
        {
            _readEmbedded = readEmbedded ?? (tag => null);
            _languagesRoot = string.IsNullOrWhiteSpace(languagesRoot) ? null : languagesRoot;
            _log = log;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised after Load or Reload, and when MarkFallbacks
        /// toggles, so C# sinks that assigned a localized string can re-apply
        /// it.</summary>
        public event EventHandler LanguageChanged;

        /// <summary>The culture string.Format uses in F and N. en-US in this
        /// phase, because SimHub pins the thread culture to en-US and every
        /// existing number and date in the panel is rendered that way; a later
        /// phase sets it from the active language once the format strings are
        /// reviewed for it.</summary>
        public CultureInfo FormatCulture { get; set; } = CultureInfo.GetCultureInfo("en-US");

        /// <summary>The tag whose layers are active: "en", "es", "de-DE"...
        /// The requested tag when it has any layer, else the first parent that
        /// has one, else "en".</summary>
        public string ActiveTag { get; private set; } = EnglishTag;

        /// <summary>"requested" when the requested tag itself has layers,
        /// "parent" when a parent tag supplied them (es-MX served by es, or
        /// en-US served by English), "english" when nothing matched.</summary>
        public string ActiveSource { get; private set; } = "english";

        /// <summary>The tag Load was last asked for, before resolution.</summary>
        public string RequestedTag { get; private set; } = EnglishTag;

        /// <summary>The folder the disk layers live in, or null.</summary>
        public string LanguagesRoot => _languagesRoot;

        /// <summary>Keys English defines (embedded "en" plus a root en.json).</summary>
        public int EnglishKeyCount => _english.Count;

        /// <summary>When true, text that fell back to English because the
        /// active language lacks the key is wrapped in U+27E6 and U+27E7 so it
        /// stands out on screen. Never applies while English itself is
        /// active. Toggling raises the change events.</summary>
        public bool MarkFallbacks
        {
            get => _markFallbacks;
            set
            {
                if (_markFallbacks == value) return;
                _markFallbacks = value;
                OnPropertyChanged("MarkFallbacks");
                RaiseChanged();
            }
        }

        /// <summary>The resolved text for a key. A key missing everywhere
        /// returns "[key]" and logs one Warn per key per session.</summary>
        public string this[string key]
        {
            get
            {
                key = key ?? string.Empty;
                string text;
                if (_active.TryGetValue(key, out text)) return text;
                if (_english.TryGetValue(key, out text))
                    return _markFallbacks && !_activeIsEnglish ? FallbackOpen + text + FallbackClose : text;
                WarnMissing(key);
                return "[" + key + "]";
            }
        }

        public string T(string key) => this[key];

        /// <summary>string.Format of the resolved text with FormatCulture. On
        /// a FormatException (a translation broke a placeholder) the English
        /// text is used instead and one Warn per key is logged.</summary>
        public string F(string key, params object[] args)
        {
            string text = this[key];
            object[] a = args ?? new object[0];
            try
            {
                return string.Format(FormatCulture, text, a);
            }
            catch (FormatException)
            {
                WarnFormat(key);
                string english;
                if (_english.TryGetValue(key ?? string.Empty, out english))
                {
                    try { return string.Format(FormatCulture, english, a); }
                    catch (FormatException) { return english; }
                }
                return text;
            }
        }

        /// <summary>Plural form: key + ".one" when n == 1, else key + ".other",
        /// then F with the same args. n is not inserted into args; a caller
        /// that shows the number passes it. When no table defines that form
        /// but one defines the base key, the base key is formatted instead
        /// and the missing form is logged once; a key missing everywhere
        /// behaves as F does ("[key.one]" and one Warn).</summary>
        public string N(string key, int n, params object[] args)
        {
            string baseKey = key ?? string.Empty;
            string plural = baseKey + (n == 1 ? ".one" : ".other");
            var active = _active;
            var english = _english;
            if (!active.ContainsKey(plural) && !english.ContainsKey(plural)
                && (active.ContainsKey(baseKey) || english.ContainsKey(baseKey)))
            {
                WarnOnce("plural:" + plural, "[TF4ALL] Language: no plural form '" + plural + "' in " + ActiveTag
                    + (_activeIsEnglish ? "" : " or English") + "; showing '" + baseKey + "' for that count.");
                return F(baseKey, args);
            }
            return F(plural, args);
        }

        /// <summary>True and the English text when English defines the key.
        /// No fallback marking, no Warn: this is for reports, not display.</summary>
        public bool TryGetEnglish(string key, out string text)
            => _english.TryGetValue(key ?? string.Empty, out text);

        /// <summary>Resolve a language: the requested tag's layers, then each
        /// parent's via ParentTag, then English. Reads every layer fresh, so
        /// this is also what Reload does.</summary>
        public void Load(string requestedTag)
        {
            string requested = NormalizeTag(requestedTag);
            if (requested == null)
            {
                WarnOnce("bad-tag:" + (requestedTag ?? ""), "[TF4ALL] Language tag '" + (requestedTag ?? "")
                    + "' is not a culture tag (letters, digits and hyphens); using English.");
                requested = EnglishTag;
            }
            RequestedTag = requested;

            var embeddedEnglish = ReadEmbeddedLayer(EnglishTag);
            var english = new Dictionary<string, string>(StringComparer.Ordinal);
            Overlay(english, embeddedEnglish);
            Overlay(english, ReadDiskLayer(RootPath(EnglishTag)));
            var embeddedEnglishKeys = new HashSet<string>(StringComparer.Ordinal);
            if (embeddedEnglish != null) foreach (var k in embeddedEnglish.Keys) embeddedEnglishKeys.Add(k);

            List<string> chain = Chain(requested);
            string activeTag = EnglishTag;
            string source = "english";
            int start = -1;
            for (int i = 0; i < chain.Count; i++)
            {
                if (IsEnglish(chain[i]) || HasAnyLayer(chain[i]))
                {
                    activeTag = IsEnglish(chain[i]) ? EnglishTag : chain[i];
                    source = i == 0 ? "requested" : "parent";
                    start = i;
                    break;
                }
            }

            var active = new Dictionary<string, string>(StringComparer.Ordinal);
            if (start >= 0)
            {
                // Lowest precedence first: the farthest parent, up to the
                // active tag, so a nearer tag's key wins by overwriting.
                for (int i = chain.Count - 1; i >= start; i--)
                {
                    if (IsEnglish(chain[i])) continue;   // the English base covers it
                    OverlayTagLayers(active, chain[i]);
                }
            }

            _english = english;
            _embeddedEnglishKeys = embeddedEnglishKeys;
            _active = active;
            ActiveTag = activeTag;
            ActiveSource = source;
            _activeIsEnglish = IsEnglish(activeTag);
            RaiseChanged();
        }

        /// <summary>Same tag, every disk layer re-read. What the folder
        /// watcher calls after a translator saves a file.</summary>
        public void Reload() => Load(RequestedTag);

        /// <summary>English keys a user of the given tag would see in English:
        /// the keys neither the tag's own layers nor any parent's define.
        /// Sorted ordinally. Empty for English itself.</summary>
        public IReadOnlyList<string> MissingKeys(string tag) => Describe(tag).Missing;

        /// <summary>Everything the diagnostics print about one language.
        /// Reads the layers fresh; nothing here changes the active table.</summary>
        public TagSummary Describe(string tag)
        {
            string t = NormalizeTag(tag) ?? EnglishTag;
            var s = new TagSummary { Tag = t };

            var rootLayer = ReadDiskLayer(RootPath(t));
            s.RootOverrides = SortedKeys(rootLayer);
            var rootUnknown = new List<string>();

            var layers = new List<string>();
            if (rootLayer != null) layers.Add("root");
            if (_languagesRoot != null && File.Exists(ShippedPath(t))) layers.Add(ShippedFolder);
            if (SafeReadEmbedded(t) != null) layers.Add("embedded");
            s.Layers = layers.Count == 0 ? "none" : string.Join("+", layers.ToArray());

            var english = _english;
            var missing = new List<string>();
            var unknown = new List<string>();
            if (IsEnglish(t))
            {
                // English is the base: nothing is missing from it by definition,
                // and "unknown" can only mean a root en.json key the embedded
                // English never had (a typo in an override).
                var embeddedKeys = _embeddedEnglishKeys;
                if (rootLayer != null)
                    foreach (var k in rootLayer.Keys)
                        if (!embeddedKeys.Contains(k)) { unknown.Add(k); rootUnknown.Add(k); }
                s.DefinedCount = english.Count;
            }
            else
            {
                var defined = new Dictionary<string, string>(StringComparer.Ordinal);
                List<string> chain = Chain(t);
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    if (IsEnglish(chain[i])) continue;
                    OverlayTagLayers(defined, chain[i]);
                }
                int definedCount = 0;
                foreach (var k in english.Keys)
                {
                    if (defined.ContainsKey(k)) definedCount++;
                    else missing.Add(k);
                }
                foreach (var k in defined.Keys)
                    if (!english.ContainsKey(k)) unknown.Add(k);
                if (rootLayer != null)
                    foreach (var k in rootLayer.Keys)
                        if (!english.ContainsKey(k)) rootUnknown.Add(k);
                s.DefinedCount = definedCount;
            }
            missing.Sort(StringComparer.Ordinal);
            unknown.Sort(StringComparer.Ordinal);
            rootUnknown.Sort(StringComparer.Ordinal);
            s.Missing = missing;
            s.Unknown = unknown;
            s.RootUnknown = rootUnknown;
            return s;
        }

        /// <summary>The next tag up: "es-MX" -> "es", "zh-Hans-CN" -> "zh-Hans"
        /// -> "zh", "es" -> null.</summary>
        public static string ParentTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            int dash = tag.LastIndexOf('-');
            if (dash <= 0) return null;
            return tag.Substring(0, dash);
        }

        /// <summary>Parse one language file: a flat JSON object whose "_meta"
        /// member is an object (at least "culture" and "name") and whose every
        /// other member is a string. Duplicate keys: the last value wins and
        /// the callback hears about it once per duplicate. A key that is not
        /// ^[A-Za-z][A-Za-z0-9_.]*$, or a value that is not a string, is
        /// dropped with one callback line. Malformed JSON throws. The callback
        /// is named for its first job but carries every per-member warning.</summary>
        public static Dictionary<string, string> ParseLanguageJson(string json, out string metaName, Action<string> warnDuplicate)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            metaName = null;
            Action<string> warn = m => warnDuplicate?.Invoke(m);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            bool sawMeta = false;

            using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json.TrimStart('\uFEFF'))))
            {
                reader.DateParseHandling = Newtonsoft.Json.DateParseHandling.None;
                if (!ReadSkippingComments(reader) || reader.TokenType != Newtonsoft.Json.JsonToken.StartObject)
                    throw new FormatException("A language file must be one JSON object.");

                while (true)
                {
                    if (!ReadSkippingComments(reader))
                        throw new FormatException("The language file ends before its object is closed.");
                    if (reader.TokenType == Newtonsoft.Json.JsonToken.EndObject) break;
                    if (reader.TokenType != Newtonsoft.Json.JsonToken.PropertyName)
                        throw new FormatException("Expected a property name at line " + reader.LineNumber + ".");
                    string name = reader.Value as string ?? string.Empty;
                    if (!ReadSkippingComments(reader))
                        throw new FormatException("The language file ends after the name '" + name + "'.");

                    if (name == MetaMember)
                    {
                        if (reader.TokenType != Newtonsoft.Json.JsonToken.StartObject)
                            throw new FormatException("\"" + MetaMember + "\" must be an object.");
                        var meta = JObject.Load(reader);
                        var nameToken = meta["name"];
                        var cultureToken = meta["culture"];
                        metaName = nameToken != null && nameToken.Type == JTokenType.String ? (string)nameToken : null;
                        if (metaName == null || cultureToken == null || cultureToken.Type != JTokenType.String)
                            warn("\"" + MetaMember + "\" should carry \"culture\" and \"name\" strings");
                        sawMeta = true;
                        continue;
                    }

                    if (reader.TokenType == Newtonsoft.Json.JsonToken.String)
                    {
                        if (!IsValidKey(name))
                        {
                            warn("key '" + name + "' dropped: a key is letters, digits, underscores and dots and starts with a letter");
                            continue;
                        }
                        if (result.ContainsKey(name))
                            warn("duplicate key '" + name + "': the last value wins");
                        result[name] = (string)reader.Value ?? string.Empty;
                        continue;
                    }

                    warn("key '" + name + "' dropped: its value is not a string");
                    if (reader.TokenType == Newtonsoft.Json.JsonToken.StartObject
                        || reader.TokenType == Newtonsoft.Json.JsonToken.StartArray)
                        reader.Skip();
                }

                // Newtonsoft throws on its own for a second value after the
                // object; anything else that reads is still a malformed file.
                if (ReadSkippingComments(reader))
                    throw new FormatException("Unexpected content after the language object.");
            }

            if (!sawMeta) warn("no \"" + MetaMember + "\" object (culture and name)");
            return result;
        }

        /// <summary>What Describe reports for one tag.</summary>
        public sealed class TagSummary
        {
            public string Tag { get; internal set; }
            /// <summary>English keys the tag (with its parents) defines.</summary>
            public int DefinedCount { get; internal set; }
            /// <summary>English keys the tag (with its parents) lacks.</summary>
            public IReadOnlyList<string> Missing { get; internal set; }
            /// <summary>Keys the tag defines that English does not.</summary>
            public IReadOnlyList<string> Unknown { get; internal set; }
            /// <summary>Keys the root &lt;tag&gt;.json override defines.</summary>
            public IReadOnlyList<string> RootOverrides { get; internal set; }
            /// <summary>Root override keys English does not know.</summary>
            public IReadOnlyList<string> RootUnknown { get; internal set; }
            /// <summary>"root+shipped+embedded" for the layers present, or "none".</summary>
            public string Layers { get; internal set; }
        }

        // Layers.

        private void OverlayTagLayers(Dictionary<string, string> into, string tag)
        {
            Overlay(into, ReadEmbeddedLayer(tag));
            Overlay(into, ReadDiskLayer(ShippedPath(tag)));
            Overlay(into, ReadDiskLayer(RootPath(tag)));
        }

        private bool HasAnyLayer(string tag)
        {
            if (SafeReadEmbedded(tag) != null) return true;
            if (_languagesRoot == null) return false;
            return File.Exists(RootPath(tag)) || File.Exists(ShippedPath(tag));
        }

        private string RootPath(string tag) => _languagesRoot == null ? null : Path.Combine(_languagesRoot, tag + ".json");

        private string ShippedPath(string tag) => _languagesRoot == null ? null : Path.Combine(_languagesRoot, ShippedFolder, tag + ".json");

        private string SafeReadEmbedded(string tag)
        {
            try { return _readEmbedded(tag); }
            catch (Exception ex)
            {
                WarnOnce("embedded-read:" + tag, "[TF4ALL] Embedded language " + tag + " could not be read: " + ex.Message);
                return null;
            }
        }

        private Dictionary<string, string> ReadEmbeddedLayer(string tag)
        {
            string json = SafeReadEmbedded(tag);
            if (json == null) return null;
            try
            {
                string metaName;
                return ParseLanguageJson(json, out metaName,
                    m => WarnOnce("embedded:" + tag + ":" + m, "[TF4ALL] Embedded language " + tag + ": " + m));
            }
            catch (Exception ex)
            {
                // A build problem, not a user one: the file is ours.
                WarnOnce("embedded-parse:" + tag, "[TF4ALL] Embedded language " + tag + " is malformed and was ignored: " + ex.Message);
                return null;
            }
        }

        private Dictionary<string, string> ReadDiskLayer(string path)
        {
            if (path == null) return null;
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                string metaName;
                string file = Path.GetFileName(path);
                // Logged on every read on purpose: a translator editing the
                // file wants to hear about each save that has a problem.
                return ParseLanguageJson(json, out metaName, m => Log("[TF4ALL] Language file " + file + ": " + m));
            }
            catch (Exception ex)
            {
                Log("[TF4ALL] Language file " + Path.GetFileName(path) + " could not be read and was ignored: " + ex.Message);
                return null;
            }
        }

        private static void Overlay(Dictionary<string, string> into, Dictionary<string, string> layer)
        {
            if (layer == null) return;
            foreach (var kv in layer) into[kv.Key] = kv.Value;
        }

        private static List<string> Chain(string tag)
        {
            var chain = new List<string>();
            for (string t = tag; t != null; t = ParentTag(t)) chain.Add(t);
            return chain;
        }

        private static bool IsEnglish(string tag) => string.Equals(tag, EnglishTag, StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> SortedKeys(Dictionary<string, string> layer)
        {
            var list = new List<string>();
            if (layer != null) list.AddRange(layer.Keys);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // A culture tag as SimHub and Windows spell them: "en", "de-DE",
        // "zh-Hans-CN". Anything else is refused so it never reaches a path.
        // Returns the trimmed tag, or null when it is not one.
        private static string NormalizeTag(string tag)
        {
            if (tag == null) return null;
            string t = tag.Trim();
            if (t.Length == 0) return EnglishTag;
            if (t.Length > 32 || t[0] == '-' || t[t.Length - 1] == '-') return null;
            foreach (char c in t)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) return null;
            }
            return t;
        }

        private static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            char first = key[0];
            if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z'))) return false;
            for (int i = 1; i < key.Length; i++)
            {
                char c = key[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.';
                if (!ok) return false;
            }
            return true;
        }

        private static bool ReadSkippingComments(Newtonsoft.Json.JsonReader reader)
        {
            while (reader.Read())
            {
                if (reader.TokenType != Newtonsoft.Json.JsonToken.Comment) return true;
            }
            return false;
        }

        // Events and logging.

        private void RaiseChanged()
        {
            // "Item[]" is the name WPF listens for when a Binding path is an
            // indexer, so every {loc:T Key} in the tree re-reads its text.
            OnPropertyChanged("Item[]");
            OnPropertyChanged("ActiveTag");
            OnPropertyChanged("ActiveSource");
            OnPropertyChanged("EnglishKeyCount");
            try { LanguageChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void OnPropertyChanged(string name)
        {
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); } catch { }
        }

        private void WarnMissing(string key)
        {
            lock (_warnedMissing)
            {
                if (!_warnedMissing.Add(key)) return;
            }
            Log("[TF4ALL] Language: no text for key '" + key + "' in " + ActiveTag
                + (_activeIsEnglish ? "" : " or English") + "; showing the key.");
        }

        private void WarnFormat(string key)
        {
            lock (_warnedFormat)
            {
                if (!_warnedFormat.Add(key ?? string.Empty)) return;
            }
            Log("[TF4ALL] Language: the " + ActiveTag + " text for '" + key
                + "' has a broken placeholder; showing the English text.");
        }

        private void WarnOnce(string id, string message)
        {
            lock (_warnedOnce)
            {
                if (!_warnedOnce.Add(id)) return;
            }
            Log(message);
        }

        private void Log(string message)
        {
            try { _log?.Invoke(message); } catch { }
        }
    }
}
