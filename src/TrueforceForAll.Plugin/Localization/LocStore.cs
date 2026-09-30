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

        /// <summary>The subfolder holding what the community translated: one flat
        /// language file per tag, fetched and rewritten wholesale by LocCommunity.
        /// Not the user's own file, which sits in the root and wins over it.</summary>
        public const string CommunityFolder = "community";

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
        private bool _useCommunityLayer = true;

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
        /// en-US served by English), "region" when only a regional file of the
        /// same language exists (es served by es-ES), "english" when nothing
        /// matched.</summary>
        public string ActiveSource { get; private set; } = "english";

        /// <summary>The tag Load was last asked for, before resolution.</summary>
        public string RequestedTag { get; private set; } = EnglishTag;

        /// <summary>The folder the disk layers live in, or null.</summary>
        public string LanguagesRoot => _languagesRoot;

        /// <summary>Whether the community layer is read. False skips it and deletes
        /// nothing, so turning the setting off is instant and turning it back on
        /// costs no fetch. The caller reloads after changing it.</summary>
        public bool UseCommunityLayer
        {
            get => _useCommunityLayer;
            set => _useCommunityLayer = value;
        }

        /// <summary>The folder the community cache lives in, or null with no
        /// languages root. LocCommunity owns what goes in it.</summary>
        public string CommunityRoot =>
            _languagesRoot == null ? null : Path.Combine(_languagesRoot, CommunityFolder);

        /// <summary>Keys English defines (embedded "en" plus a root en.json).</summary>
        public int EnglishKeyCount => _english.Count;
        /// <summary>Every key English defines, ordinally sorted. This is the
        /// list a translator works down, so it comes from the table the build
        /// actually shows rather than from a file on disk.</summary>
        public IReadOnlyList<string> EnglishKeys
        {
            get
            {
                var list = new List<string>(_english.Keys);
                list.Sort(StringComparer.Ordinal);
                return list;
            }
        }

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
            var active = _active;
            var english = _english;

            // The form THIS language uses for THIS count. English has two and decides by
            // whether the count is 1; Russian has three and decides by the last digit,
            // with the teens carved out; Japanese has one. Asking the language is the
            // whole point: the old rule showed "2 файлов" where Russian wants "2 файла",
            // which is wrong in a way nobody reports because the sentence still appears.
            string form = LocPlurals.Category(ActiveTag, n);
            string wanted = baseKey + "." + form;
            if (active.ContainsKey(wanted)) return F(wanted, args);

            // The language declares that form but has not translated it yet. Its own
            // 'other' is the right next step: that is the form it uses for every count it
            // did not single out, so it reads as this language even when it is not exact.
            string otherForm = baseKey + "." + LocPlurals.Other;
            if (active.ContainsKey(otherForm)) return F(otherForm, args);

            // Nothing in this language, so English, which always has the pair. Chosen by
            // English's own rule rather than by this language's form, because a Russian
            // 'few' has no English counterpart and the plural is the one that reads.
            string englishForm = baseKey + "." + (n == 1 ? LocPlurals.One : LocPlurals.Other);
            if (english.ContainsKey(englishForm)) return F(englishForm, args);
            if (english.ContainsKey(wanted)) return F(wanted, args);

            // A key with no plural forms at all on either side: show it as written and say
            // so once, which is what this did before.
            if (active.ContainsKey(baseKey) || english.ContainsKey(baseKey))
            {
                WarnOnce("plural:" + wanted, "[TF4ALL] Language: no plural form '" + wanted + "' in " + ActiveTag
                    + (_activeIsEnglish ? "" : " or English") + "; showing '" + baseKey + "' for that count.");
                return F(baseKey, args);
            }
            return F(wanted, args);
        }

        /// <summary>The English a plural form was written against. English carries two
        /// forms, so a language that needs four has no English row of its own for the
        /// other two: they translate the English plural.
        ///
        /// Both the Translate window and the fetch need this. The window shows the
        /// translator what they are translating, and the fetch hashes it to decide whether
        /// a row was written for the English this build displays.</summary>
        public string EnglishForPluralKey(string key)
        {
            string direct = EnglishText(key);
            if (direct != null) return direct;
            string baseKey, form;
            if (!LocPlurals.TrySplit(key, out baseKey, out form)) return null;
            // The singular translates the singular; every other form translates the
            // plural, which is the only other thing English has to offer.
            string source = baseKey + "." + (form == LocPlurals.One ? LocPlurals.One : LocPlurals.Other);
            return EnglishText(source);
        }

        /// <summary>Whether this key is a plural form the given language needs, even when
        /// English has no row for it. The window builds its rows from this.
        ///
        /// The tag is a parameter rather than the active language on purpose: a translator
        /// filling in Russian is usually reading the plugin in English, so the language
        /// being edited is not the one on screen.</summary>
        public bool IsPluralFormFor(string tag, string key)
        {
            string baseKey, form;
            if (!LocPlurals.TrySplit(key, out baseKey, out form)) return false;
            if (EnglishText(baseKey + "." + LocPlurals.Other) == null) return false;
            foreach (string f in LocPlurals.FormsFor(tag))
                if (f == form) return true;
            return false;
        }

        /// <summary>True and the English text when English defines the key.
        /// No fallback marking, no Warn: this is for reports, not display.</summary>
        /// <summary>What this key would read as with the community layer taken
        /// away: the user's own file, else shipped, else embedded, else the English.
        /// A fetched row equal to this is a row that changes nothing, so the fetch
        /// drops it and the cache file stays a set of differences.</summary>
        public string ResolveBelowCommunity(string tag, string key)
        {
            if (key == null) return null;
            string t = NormalizeTag(tag) ?? EnglishTag;
            var built = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!IsEnglish(t))
            {
                List<string> chain = Chain(t);
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    if (IsEnglish(chain[i])) continue;
                    Overlay(built, ReadEmbeddedLayer(chain[i]));
                    Overlay(built, ReadDiskLayer(ShippedPath(chain[i])));
                    Overlay(built, ReadDiskLayer(RootPath(chain[i])));
                }
            }
            string value;
            if (built.TryGetValue(key, out value)) return value;
            return EnglishText(key);
        }

        /// <summary>What this key reads as from the build alone: the shipped file, else
        /// the embedded one, else the English. Neither the community layer nor the user's
        /// own file is consulted.
        ///
        /// This is what separates a translation someone wrote from the one that came with
        /// the plugin. Only the difference is theirs to publish: without this test, opening
        /// the window on a language the build already ships would offer every one of its
        /// rows to the server as this person's work.</summary>
        public string ResolveShipped(string tag, string key)
        {
            if (key == null) return null;
            string t = NormalizeTag(tag) ?? EnglishTag;
            var built = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!IsEnglish(t))
            {
                List<string> chain = Chain(t);
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    if (IsEnglish(chain[i])) continue;
                    Overlay(built, ReadEmbeddedLayer(chain[i]));
                    Overlay(built, ReadDiskLayer(ShippedPath(chain[i])));
                }
            }
            string value;
            if (built.TryGetValue(key, out value)) return value;
            return EnglishText(key);
        }

        /// <summary>The English this build displays for a key, or null when the key
        /// is not one of ours. The fetch hashes this to decide whether a row was
        /// written for the same English.</summary>
        public string EnglishText(string key)
        {
            if (key == null) return null;
            string text;
            return _english.TryGetValue(key, out text) ? text : null;
        }

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

            // Nothing for the requested tag or any parent of it. A regional file
            // of the same language is still that language, so a plain "es" is
            // served by an es-ES.json a translator or the community left on disk
            // rather than by English.
            string regional = null;
            if (start < 0)
            {
                for (int i = 0; i < chain.Count && regional == null; i++) regional = RegionalStandIn(chain[i]);
                if (regional != null)
                {
                    activeTag = regional;
                    source = "region";
                }
            }

            var active = new Dictionary<string, string>(StringComparer.Ordinal);
            // Lowest precedence first: the farthest parent, up to the active tag,
            // so a nearer tag's key wins by overwriting.
            List<string> activeChain = start >= 0 ? chain.GetRange(start, chain.Count - start)
                : (regional != null ? Chain(regional) : null);
            if (activeChain != null)
            {
                for (int i = activeChain.Count - 1; i >= 0; i--)
                {
                    if (IsEnglish(activeChain[i])) continue;   // the English base covers it
                    OverlayTagLayers(active, activeChain[i]);
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

        /// <summary>The name a language calls itself, from the _meta.name of its
        /// highest-precedence layer: a root override first, then the shipped copy,
        /// then the embedded one. Null when the tag has no layer or its file
        /// carries no usable _meta. This is what a language picker shows, so it is
        /// the language's own name and is never itself translated.</summary>
        public string DisplayName(string tag)
        {
            string t = NormalizeTag(tag);
            if (t == null) return null;
            string[] sources = { SafeReadFile(RootPath(t)), SafeReadFile(ShippedPath(t)), SafeReadEmbedded(t) };
            foreach (string json in sources)
            {
                if (json == null) continue;
                try
                {
                    string name;
                    ParseLanguageJson(json, out name, m => { });
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                catch { /* a broken file is reported by the layer reads; not here */ }
            }
            return null;
        }

        private string SafeReadFile(string path)
        {
            try { return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
            catch { return null; }
        }

        /// <summary>Everything the diagnostics print about one language.
        /// Reads the layers fresh; nothing here changes the active table.</summary>
        public TagSummary Describe(string tag)
        {
            string t = NormalizeTag(tag) ?? EnglishTag;
            var s = new TagSummary { Tag = t };

            var rootLayer = ReadDiskLayer(RootPath(t));
            s.RootOverrides = SortedKeys(rootLayer);
            var rootUnknown = new List<string>();

            var communityLayer = _useCommunityLayer ? ReadDiskLayer(CommunityPath(t)) : null;
            s.CommunityCount = communityLayer == null ? 0 : communityLayer.Count;

            var layers = new List<string>();
            if (rootLayer != null) layers.Add("root");
            if (communityLayer != null) layers.Add(CommunityFolder);
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

            /// <summary>How many keys the community layer defines for this tag.
            /// RootOverrides keeps its own meaning, the user's own file, which is
            /// what the divergence marks read.</summary>
            public int CommunityCount { get; internal set; }
        }

        // Layers.

        private void OverlayTagLayers(Dictionary<string, string> into, string tag)
        {
            Overlay(into, ReadEmbeddedLayer(tag));
            Overlay(into, ReadDiskLayer(ShippedPath(tag)));
            if (_useCommunityLayer) Overlay(into, ReadDiskLayer(CommunityPath(tag)));
            Overlay(into, ReadDiskLayer(RootPath(tag)));
        }

        /// <summary>A language on disk whose parent chain contains this tag:
        /// "es" served by an es-ES.json, "zh" by a zh-Hans-CN.json. Ordinally
        /// first so the choice is stable across machines. Null when there is
        /// none, and null for English, which is the base already. Disk only:
        /// the languages this build embeds are neutral by design, so an embedded
        /// regional tag cannot exist.</summary>
        private string RegionalStandIn(string tag)
        {
            if (_languagesRoot == null || string.IsNullOrEmpty(tag) || IsEnglish(tag)) return null;
            string best = null;
            string[] folders = { _languagesRoot, Path.Combine(_languagesRoot, ShippedFolder) };
            foreach (string folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string file in Directory.GetFiles(folder, tag + "-*.json"))
                    {
                        string found = Path.GetFileNameWithoutExtension(file);
                        if (NormalizeTag(found) == null) continue;
                        bool related = false;
                        for (string p = ParentTag(found); p != null; p = ParentTag(p))
                            if (string.Equals(p, tag, StringComparison.OrdinalIgnoreCase)) { related = true; break; }
                        if (!related) continue;
                        if (best == null || string.CompareOrdinal(found, best) < 0) best = found;
                    }
                }
                catch { }
            }
            return best;
        }

        private bool HasAnyLayer(string tag)
        {
            if (SafeReadEmbedded(tag) != null) return true;
            if (_languagesRoot == null) return false;
            // The community path belongs here, not only in the overlay: this is what
            // decides which chain member becomes active, and Load overlays members
            // only from the active index outward. Without it a fetched
            // community\es-MX.json would be written and never read.
            return File.Exists(RootPath(tag)) || File.Exists(ShippedPath(tag))
                || (_useCommunityLayer && File.Exists(CommunityPath(tag)));
        }

        private string RootPath(string tag) => _languagesRoot == null ? null : Path.Combine(_languagesRoot, tag + ".json");

        private string ShippedPath(string tag) => _languagesRoot == null ? null : Path.Combine(_languagesRoot, ShippedFolder, tag + ".json");

        public string CommunityPath(string tag) => _languagesRoot == null ? null : Path.Combine(_languagesRoot, CommunityFolder, tag + ".json");

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

        /// <summary>The same test, for callers outside this class: the fetch asks
        /// it before making a request, because an English install must not notice
        /// the translation service exists.</summary>
        public static bool IsEnglishTag(string tag) => IsEnglish(tag);

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
