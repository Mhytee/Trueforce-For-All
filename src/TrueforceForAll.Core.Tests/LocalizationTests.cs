using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Plugin.Localization;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Localization: the LocStore runtime and the XAML/C# inventory checks from
    // docs/localization-plan.md (Phase 1). LocStore.cs is link-compiled from
    // the plugin (see the csproj); everything else here reads repo files: the
    // plugin's XAML, Languages/en.json, and tools/loc's baseline CSV and list
    // files. The repo root is found by walking up from the test binary until
    // src/Directory.Build.props exists.
    //
    // Two independent XAML walkers are deliberate. tools/loc/_common.ps1
    // (Get-LocInventory) generates the baseline and drives the converter; the
    // C# walk in this file re-implements the same denylist, element path and
    // real-string rule (decision A, the short-token rule, in SkipReason) and
    // must produce the same rows for every unconverted file and for the
    // unconverted parts of a partly converted one
    // (LocXamlWalker_AgreesWithBaselineForUnconvertedFiles). Once a file or a
    // part of it is converted, the C# walk is the oracle that says the
    // conversion lost nothing. Keep the two in step: a rule added to one
    // belongs in the other.
    //
    // converted-files.txt lists a file whole or by scope (decision B, see
    // ReadConvertedFiles and InScope). Tests 4 and 5 look only at the rows
    // inside a scoped file's listed scopes; test 3 keeps checking the rows
    // outside them against the baseline.
    //
    // What each inventory test catches once a conversion is wrong:
    //   LocXamlInventoryRoundTrip  a {loc:T Key} whose English text is not the
    //                              original byte for byte, a string converted
    //                              on the wrong element or attribute, a string
    //                              that vanished, a key missing from en.json.
    //   LocNoXamlLiteralsRemain    a letter-bearing literal left in a converted
    //                              file that xaml-keep-literal.txt does not
    //                              allow (input-box Text excepted), and a file
    //                              with {loc:T} references that
    //                              converted-files.txt does not list.
    //   LocKeysResolve             a key used in XAML or C# that en.json lacks,
    //                              a malformed key, a Loc.N base without both
    //                              plural forms, an en.json key nothing uses.
    //   LocFormatArity             a Loc.F call whose argument count is not the
    //                              highest {n} plus one, a Loc.N call whose
    //                              values after the count do not match its
    //                              plural forms, a placeholder string used raw
    //                              through Loc.T or {loc:T}.
    public sealed class LocalizationTests
    {
        private const string PluginRel = "src/TrueforceForAll.Plugin";
        private const string ToolsRel = "tools/loc";

        // The runtime's key shape (LocStore.IsValidKey): dots only appear in
        // the plural suffixes Loc.N appends.
        private static readonly Regex KeyRegex = new Regex(@"^[A-Za-z][A-Za-z0-9_.]*$");

        // A whole attribute value that is a {loc:T Key} reference, positional
        // or with the Key= property form.
        private static readonly Regex LocRefWhole = new Regex(@"^\{loc:T\s+(?:Key\s*=\s*)?([A-Za-z][A-Za-z0-9_.]*)\s*\}$");

        // Any {loc:T ...} in raw XAML text, however malformed the key.
        private static readonly Regex LocRefAny = new Regex(@"\{loc:T\s+([^}]*)\}");

        // Loc.T("Key"), Loc.F("Key", ...), Loc.N("Key", n, ...) whose key is one
        // string literal that is the WHOLE key argument: the lookahead wants
        // the comma or the closing parenthesis right after the closing quote.
        // Group 1 is the method, group 2 the key. A concatenated key such as
        // Loc.T("Effect_" + id + "_Name") fails the lookahead and is skipped as
        // dynamic; DynamicFamily exempts the keys it can produce.
        //
        // Decision C: C# call sites pass the key as a string literal. There is
        // no LocKeys constants class, so nothing here resolves an identifier
        // to a key, and a key built at runtime is dynamic and exempt.
        private static readonly Regex CallRegex = new Regex(@"\bLoc\.(T|F|N)\(\s*@?""((?:[^""\\]|\\.)*)""(?=\s*[,)])");

        // The second C# reference form: a Binding straight to the store's
        // indexer, new Binding("[Key]") { Source = Loc.Instance, ... }, which is
        // how a label set once at wire time follows a language change with no
        // relabel pass (EditableReadout's shared readout tooltip). The shape is
        // the one TExtension.ProvideValue builds for {loc:T}, so the key sits
        // inside the path brackets; a path built by concatenation, as TExtension
        // itself builds one, stays dynamic and is not matched.
        private static readonly Regex BindingRegex = new Regex(@"\bnew\s+Binding\(\s*""\[([A-Za-z][A-Za-z0-9_.]*)\]""\s*\)");

        // Keys looked up by a runtime identifier, never by a literal.
        private static readonly Regex DynamicFamily = new Regex(@"^(Effect_.+_Name|EngineLayout_.+)$");

        // ------------------------------------------------------------------
        // 1. The store: layers, parents, fallback marking, missing keys.
        // ------------------------------------------------------------------

        [Fact]
        public void LocStore_RegionalFileServesTheNeutralTag()
        {
            // Resolution walks child to parent, so a plain "pt" finds nothing
            // below it and a community pt-BR.json would go unread while the user
            // sat in English. A regional file of the same language is still that
            // language, so it is the last step before English.
            string root = Path.Combine(Path.GetTempPath(), "tf4all-loc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var utf8 = new UTF8Encoding(false);
            try
            {
                var embedded = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["en"] = LangJson("en", ("A", "A-en"), ("B", "B-en")),
                };
                var log = new List<string>();
                Func<string, string> readEmbedded = tag => embedded.TryGetValue(tag, out string json) ? json : null;
                var store = new LocStore(readEmbedded, root, log.Add);

                File.WriteAllText(Path.Combine(root, "pt-BR.json"), LangJson("pt-BR", ("A", "A-pt-BR")), utf8);

                store.Load("pt");
                Assert.Equal("pt-BR", store.ActiveTag);
                Assert.Equal("region", store.ActiveSource);
                Assert.Equal("pt", store.RequestedTag);
                Assert.Equal("A-pt-BR", store["A"]);
                Assert.Equal("B-en", store["B"]);

                // A sibling region counts too: pt-PT has no file, its parent has
                // none, and Brazilian Portuguese beats English for that reader.
                store.Load("pt-PT");
                Assert.Equal("pt-BR", store.ActiveTag);
                Assert.Equal("region", store.ActiveSource);
                Assert.Equal("A-pt-BR", store["A"]);

                // Ordinally first, so two regional files resolve the same way on
                // every machine whatever order the folder enumerates.
                File.WriteAllText(Path.Combine(root, "pt-AO.json"), LangJson("pt-AO", ("A", "A-pt-AO")), utf8);
                store.Load("pt");
                Assert.Equal("pt-AO", store.ActiveTag);
                Assert.Equal("A-pt-AO", store["A"]);

                // The neutral file itself wins the moment it exists.
                File.WriteAllText(Path.Combine(root, "pt.json"), LangJson("pt", ("A", "A-pt")), utf8);
                store.Load("pt");
                Assert.Equal("pt", store.ActiveTag);
                Assert.Equal("requested", store.ActiveSource);
                Assert.Equal("A-pt", store["A"]);

                // An unrelated language is not a stand-in for anything.
                store.Load("de");
                Assert.Equal("en", store.ActiveTag);
                Assert.Equal("english", store.ActiveSource);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void LocStore_LayerPrecedence_AndParentFallback()
        {
            string root = Path.Combine(Path.GetTempPath(), "tf4all-loc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var utf8 = new UTF8Encoding(false);
            try
            {
                var embedded = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["en"] = LangJson("en",
                        ("A", "A-en"), ("B", "B-en"), ("C", "C-en"),
                        ("Fmt", "{0} items"),
                        ("Count.one", "one item"), ("Count.other", "{0} items")),
                    // Fmt needs two arguments here while English needs one: a
                    // translator broke the placeholder.
                    ["es"] = LangJson("es", ("A", "A-es"), ("Fmt", "{0} elementos {1}")),
                };
                var log = new List<string>();
                Func<string, string> readEmbedded = tag => embedded.TryGetValue(tag, out string json) ? json : null;
                var store = new LocStore(readEmbedded, root, log.Add);
                int itemChanges = 0, languageChanges = 0;
                store.PropertyChanged += (s, e) => { if (e.PropertyName == "Item[]") itemChanges++; };
                store.LanguageChanged += (s, e) => languageChanges++;

                File.WriteAllText(Path.Combine(root, "es-MX.json"), LangJson("es-MX", ("B", "B-es-MX-root")), utf8);
                store.Load("es-MX");

                Assert.Equal(1, itemChanges);
                Assert.Equal(1, languageChanges);
                Assert.Equal("es-MX", store.ActiveTag);
                Assert.Equal("requested", store.ActiveSource);
                Assert.Equal("es-MX", store.RequestedTag);
                Assert.Equal(6, store.EnglishKeyCount);
                Assert.Equal("A-es", store["A"]);           // the parent language's embedded layer
                Assert.Equal("B-es-MX-root", store["B"]);   // the root override for the requested tag
                Assert.Equal("C-en", store["C"]);           // English, nothing else has it
                Assert.Equal("A-es", store.T("A"));

                // MarkFallbacks brackets only what came from English.
                string open = ((char)0x27E6).ToString();
                string close = ((char)0x27E7).ToString();
                store.MarkFallbacks = true;
                Assert.Equal(2, itemChanges);
                Assert.Equal(2, languageChanges);
                Assert.Equal(open + "C-en" + close, store["C"]);
                Assert.Equal("A-es", store["A"]);
                Assert.Equal("B-es-MX-root", store["B"]);
                store.MarkFallbacks = false;
                Assert.Equal("C-en", store["C"]);

                // A key missing everywhere: bracketed key, one Warn per key.
                Assert.Equal("[nope]", store.T("nope"));
                Assert.Equal("[nope]", store["nope"]);
                Assert.Equal(1, log.Count(l => l.Contains("'nope'")));

                // F: the broken es placeholder falls back to the English text,
                // does not throw, and warns once.
                Assert.Equal("en-US", store.FormatCulture.Name);
                Assert.Equal("5 items", store.F("Fmt", 5));
                Assert.Equal("5 items", store.F("Fmt", 5));
                Assert.Equal(1, log.Count(l => l.Contains("'Fmt'")));

                // N: .one when n == 1, else .other; n is not inserted into args.
                Assert.Equal("one item", store.N("Count", 1));
                Assert.Equal("3 items", store.N("Count", 3, 3));
                Assert.Equal("0 items", store.N("Count", 0, 0));

                // MissingKeys counts a parent's keys as defined: es-MX inherits
                // A and Fmt from es, adds B itself, lacks the rest.
                Assert.Equal(new[] { "C", "Count.one", "Count.other" }, store.MissingKeys("es-MX").ToArray());
                Assert.Equal(new[] { "B", "C", "Count.one", "Count.other" }, store.MissingKeys("es").ToArray());
                Assert.Empty(store.MissingKeys("en"));

                // Disk layers: shipped beats embedded, root beats shipped, and
                // the parent's root layer reaches the child tag.
                Directory.CreateDirectory(Path.Combine(root, LocStore.ShippedFolder));
                File.WriteAllText(Path.Combine(root, LocStore.ShippedFolder, "es.json"), LangJson("es", ("A", "A-es-shipped")), utf8);
                store.Load("es");
                Assert.Equal("es", store.ActiveTag);
                Assert.Equal("requested", store.ActiveSource);
                Assert.Equal("A-es-shipped", store["A"]);
                File.WriteAllText(Path.Combine(root, "es.json"), LangJson("es", ("A", "A-es-root")), utf8);
                store.Reload();
                Assert.Equal("es", store.RequestedTag);
                Assert.Equal("A-es-root", store["A"]);
                store.Load("es-MX");
                Assert.Equal("A-es-root", store["A"]);
                Assert.Equal("B-es-MX-root", store["B"]);

                // A root en.json joins the English base and can add keys the
                // embedded English never had; Describe reports those.
                File.WriteAllText(Path.Combine(root, "en.json"), LangJson("en", ("C", "C-en-root"), ("Zed", "zed")), utf8);
                store.Reload();
                Assert.Equal("C-en-root", store["C"]);
                Assert.Equal(7, store.EnglishKeyCount);
                Assert.Equal(new[] { "Zed" }, store.Describe("en").RootUnknown.ToArray());

                // Parent fallback, English for en-US, English for the unknown.
                store.Load("es-AR");
                Assert.Equal("es", store.ActiveTag);
                Assert.Equal("parent", store.ActiveSource);
                Assert.Equal("A-es-root", store["A"]);

                store.Load("en-US");
                Assert.Equal("en", store.ActiveTag);
                Assert.Equal("parent", store.ActiveSource);
                Assert.Equal("A-en", store["A"]);

                store.Load("xx-YY");
                Assert.Equal("en", store.ActiveTag);
                Assert.Equal("english", store.ActiveSource);
                Assert.Equal("A-en", store["A"]);
                Assert.Equal("B-en", store["B"]);
                store.MarkFallbacks = true;
                Assert.Equal("A-en", store["A"]);   // never marked while English itself is active
                store.MarkFallbacks = false;

                store.Load("");
                Assert.Equal("en", store.ActiveTag);

                // Not a culture tag: refused before it can reach a path, and
                // English is loaded in its place.
                store.Load("../evil");
                Assert.Equal("en", store.ActiveTag);
                Assert.Equal("en", store.RequestedTag);
                Assert.Equal("A-en", store["A"]);
                Assert.Contains(log, l => l.Contains("not a culture tag"));

                Assert.DoesNotContain(log, l => l.Contains("could not be read") || l.Contains("malformed"));

                // ParentTag.
                Assert.Equal("es", LocStore.ParentTag("es-MX"));
                Assert.Equal("zh-Hans", LocStore.ParentTag("zh-Hans-CN"));
                Assert.Equal("zh", LocStore.ParentTag("zh-Hans"));
                Assert.Null(LocStore.ParentTag("zh"));
                Assert.Null(LocStore.ParentTag("es"));
                Assert.Null(LocStore.ParentTag(""));
                Assert.Null(LocStore.ParentTag(null));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
            }
        }

        // ------------------------------------------------------------------
        // 2. Parsing: duplicates, strictness, the shipped language files.
        // ------------------------------------------------------------------

        [Fact]
        public void LocStore_ParseLanguageJson_DuplicateLastWinsWithWarn_AndStrictParseRejects()
        {
            const string dup = "{ \"_meta\": { \"culture\": \"en\", \"name\": \"English\" }, "
                + "\"Same\": \"first\", \"Other\": \"x\", \"Same\": \"second\" }";

            var warns = new List<string>();
            var table = LocStore.ParseLanguageJson(dup, out string metaName, warns.Add);
            Assert.Equal("English", metaName);
            Assert.Equal(2, table.Count);
            Assert.Equal("second", table["Same"]);
            Assert.Equal("x", table["Other"]);
            Assert.Single(warns);
            Assert.Contains("duplicate key 'Same'", warns[0]);

            // The same text under a strict reader is an error, which is what
            // the test suite wants for a file in the repo.
            var strict = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            Assert.ThrowsAny<JsonException>(() => JObject.Parse(dup, strict));

            // Malformed JSON throws; a bad key and a non-string value are each
            // dropped with one warning.
            Assert.ThrowsAny<Exception>(() => LocStore.ParseLanguageJson("{ \"A\": \"unterminated", out _, null));
            warns.Clear();
            var dropped = LocStore.ParseLanguageJson(
                "{ \"_meta\": { \"culture\": \"en\", \"name\": \"E\" }, \"bad key!\": \"x\", \"Num\": 5, \"Ok\": \"y\" }",
                out _, warns.Add);
            Assert.Equal(new[] { "Ok" }, dropped.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(2, warns.Count);

            // Every shipped language file: strict parse, _meta matching its
            // file name, string members only, keys in the runtime's shape, and
            // the runtime's own parser agreeing without a single warning.
            string languagesDir = Path.Combine(RepoRoot(), PluginRel, "Languages");
            string[] files = Directory.GetFiles(languagesDir, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Contains(files, p => Path.GetFileName(p) == "en.json");
            foreach (string path in files)
            {
                string tag = Path.GetFileNameWithoutExtension(path);
                string json = File.ReadAllText(path, Encoding.UTF8);
                JObject o = JObject.Parse(json, strict);
                var meta = o["_meta"] as JObject;
                Assert.True(meta != null, tag + ".json: no _meta object");
                Assert.Equal(tag, (string)meta["culture"]);
                Assert.False(string.IsNullOrWhiteSpace((string)meta["name"]), tag + ".json: _meta.name is missing");
                foreach (var prop in o.Properties())
                {
                    if (prop.Name == LocStore.MetaMember) continue;
                    Assert.True(KeyRegex.IsMatch(prop.Name), tag + ".json: key '" + prop.Name + "' does not match " + KeyRegex);
                    Assert.True(prop.Value.Type == JTokenType.String, tag + ".json: key '" + prop.Name + "' is not a string");
                }
                var fileWarns = new List<string>();
                var parsed = LocStore.ParseLanguageJson(json, out string name, fileWarns.Add);
                Assert.True(fileWarns.Count == 0, tag + ".json: " + string.Join("; ", fileWarns));
                Assert.Equal((string)meta["name"], name);
                Assert.Equal(o.Properties().Count(p => p.Name != LocStore.MetaMember), parsed.Count);
            }
        }

        // ------------------------------------------------------------------
        // 3. The two walkers agree on every unconverted file, and on the rows
        //    outside the listed scopes of a partly converted one.
        // ------------------------------------------------------------------

        [Fact]
        public void LocXamlWalker_AgreesWithBaselineForUnconvertedFiles()
        {
            string repo = RepoRoot();
            var baseline = LoadBaseline(repo);
            var converted = ReadConvertedFiles(repo);
            var common = ReadListFile(Path.Combine(repo, ToolsRel, "common-keys.txt"));

            var files = new SortedSet<string>(baseline.Keys, StringComparer.Ordinal);
            foreach (string rel in PluginXamlFiles(repo)) files.Add(rel);

            var failures = new List<string>();
            foreach (string rel in files)
            {
                converted.TryGetValue(rel, out HashSet<string> specs);
                if (specs != null && specs.Count == 0) continue;   // converted whole: tests 4 and 5 own it
                string abs = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) { failures.Add(rel + ": in the baseline but not on disk"); continue; }
                // A file that carries {loc:T} references but has no
                // converted-files.txt line is not unconverted: the baseline
                // diff below would only report its keys as changed text and
                // point at the regenerate step. Name the real fix instead.
                if (specs == null && ReadUtf8(abs).Contains("{loc:T "))
                {
                    failures.Add(rel + ": this file has {loc:T} references; list it in tools/loc/converted-files.txt first");
                    continue;
                }
                var rows = WalkXaml(abs, rel, common, out XDocument doc);
                if (!baseline.TryGetValue(rel, out List<CsvRow> baseRows))
                {
                    if (rows.Count > 0)
                        failures.Add(rel + ": " + rows.Count + " candidate row(s) but no baseline rows; run tools/loc/inventory.ps1 on it "
                            + "and add the CSV under tools/loc/baseline before converting it");
                    continue;
                }
                if (specs != null)
                {
                    // A scoped file: the rows outside its converted scopes, on
                    // both sides, without the line. The converter puts the
                    // xmlns:loc declaration on a line of its own, so every row
                    // after the root's start tag moves down by one on the
                    // first conversion. Text nodes stay on their line (none
                    // spans lines), so the rest of the row is stable.
                    var ctx = ScopeContextOf(doc);
                    string outside = rel + " outside " + string.Join(",", specs.OrderBy(s => s, StringComparer.Ordinal));
                    Diff(failures, outside + " (elementType|xName|attribute|skipReason|value)",
                        baseRows.Where(b => !InScope(rel, LocateBaselineElement(doc, b), specs, ctx))
                            .Select(r => r.ElementType + "|" + r.XName + "|" + r.Attribute + "|" + r.SkipReason + "|" + r.Value),
                        rows.Where(r => !InScope(rel, r.Element, specs, ctx))
                            .Select(r => r.ElementType + "|" + r.XName + "|" + r.Attribute + "|" + r.SkipReason + "|" + r.Value));
                    continue;
                }
                // The contract's multiset first, then the whole row so the C#
                // element path and skip rule are known to agree as well.
                Diff(failures, rel + " (line|attribute|value)",
                    baseRows.Select(r => r.Line + "|" + r.Attribute + "|" + r.Value),
                    rows.Select(r => Inv(r.Line) + "|" + r.Attribute + "|" + r.Value));
                Diff(failures, rel + " (full row: line|elementType|xName|attribute|skipReason|value)",
                    baseRows.Select(r => r.Line + "|" + r.ElementType + "|" + r.XName + "|" + r.Attribute + "|" + r.SkipReason + "|" + r.Value),
                    rows.Select(r => Inv(r.Line) + "|" + r.ElementType + "|" + r.XName + "|" + r.Attribute + "|" + r.SkipReason + "|" + r.Value));
            }
            AssertNoFailures(failures);
        }

        // ------------------------------------------------------------------
        // 4. A converted file still holds every baseline string, at the same
        //    element and attribute, byte for byte through its key.
        // ------------------------------------------------------------------

        [Fact]
        public void LocXamlInventoryRoundTrip()
        {
            string repo = RepoRoot();
            var baseline = LoadBaseline(repo);
            var converted = ReadConvertedFiles(repo);
            var common = ReadListFile(Path.Combine(repo, ToolsRel, "common-keys.txt"));
            var en = LoadEnglish(repo, out List<string> enWarns);

            var failures = new List<string>(enWarns.Select(w => "en.json: " + w));
            foreach (var entry in converted.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                string rel = entry.Key;
                HashSet<string> specs = entry.Value;   // empty: the whole file is converted
                string abs = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) { failures.Add(rel + ": in converted-files.txt but not on disk"); continue; }
                if (!baseline.TryGetValue(rel, out List<CsvRow> baseRows))
                {
                    failures.Add(rel + ": in converted-files.txt but has no baseline rows, so the round trip cannot be checked");
                    continue;
                }

                var rows = WalkXaml(abs, rel, common, out XDocument doc);
                var ctx = ScopeContextOf(doc);

                // Baseline side: every row's value at its normalized location;
                // in a scoped file only the rows whose element lies in a listed
                // scope, test 3 keeps watching the rest.
                var expected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var b in baseRows)
                {
                    if (specs.Count > 0 && !InScope(rel, LocateBaselineElement(doc, b), specs, ctx)) continue;
                    AddAt(expected, NormalizeLocation(LocalName(b.ElementType), b.XName, b.Attribute), b.Value);
                }

                // Converted side: resolve {loc:T Key} through en.json, then
                // take the matching baseline entry.
                foreach (var r in rows)
                {
                    if (specs.Count > 0 && !InScope(rel, r.Element, specs, ctx)) continue;
                    string value = r.Value;
                    string key = null;
                    var m = LocRefWhole.Match(r.Value);
                    if (m.Success)
                    {
                        key = m.Groups[1].Value;
                        if (!en.TryGetValue(key, out value))
                        {
                            failures.Add(rel + ":" + Inv(r.Line) + " " + r.Attribute + " on " + r.XName + " = {loc:T " + key + "}: the key is not in en.json");
                            continue;
                        }
                    }
                    string location = NormalizeLocation(r.Element.Name.LocalName, r.XName, r.Attribute);
                    if (TakeAt(expected, location, value)) continue;

                    string via = key != null ? " (via {loc:T " + key + "})" : "";
                    if (expected.TryGetValue(location, out List<string> others) && others.Count > 0)
                        failures.Add(rel + ":" + Inv(r.Line) + " " + location + " [" + value + "]" + via
                            + " but the baseline recorded [" + string.Join("] or [", others) + "] there");
                    else
                        failures.Add(rel + ":" + Inv(r.Line) + " " + location + " [" + value + "]" + via
                            + " has no baseline row at that element path and attribute");
                }
                foreach (var kv in expected.OrderBy(k => k.Key, StringComparer.Ordinal))
                    foreach (string v in kv.Value)
                        failures.Add(rel + " " + kv.Key + " baseline string [" + v + "] is no longer in the file (moved, deleted or its key's English text differs)");
            }
            AssertNoFailures(failures);
        }

        // ------------------------------------------------------------------
        // 5. Nothing letter-bearing is left as a literal in a converted file.
        // ------------------------------------------------------------------

        [Fact]
        public void LocNoXamlLiteralsRemain()
        {
            string repo = RepoRoot();
            var converted = ReadConvertedFiles(repo);
            var common = ReadListFile(Path.Combine(repo, ToolsRel, "common-keys.txt"));
            var keep = ReadListFile(Path.Combine(repo, ToolsRel, "xaml-keep-literal.txt"));

            var failures = new List<string>();
            foreach (var entry in converted.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                string rel = entry.Key;
                HashSet<string> specs = entry.Value;   // empty: the whole file is converted
                string abs = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) { failures.Add(rel + ": in converted-files.txt but not on disk"); continue; }
                var rows = WalkXaml(abs, rel, common, out XDocument doc);
                var ctx = ScopeContextOf(doc);
                foreach (var r in rows)
                {
                    if (specs.Count > 0 && !InScope(rel, r.Element, specs, ctx)) continue;   // not converted yet
                    if (r.SkipReason != "") continue;              // binding, numeric, glyph, identifier, empty
                    if (keep.Contains(r.Value)) continue;          // allowed verbatim
                    if (IsInputText(r)) continue;                  // a TextBox default the code parses back
                    failures.Add(rel + ":" + Inv(r.Line) + " " + r.XName + " " + r.Attribute + "=\"" + r.Value + "\" is still a literal"
                        + " (give it a key, or list the value in tools/loc/xaml-keep-literal.txt)");
                }
            }
            // A file that carries {loc:T} references but has no
            // converted-files.txt line is checked by nobody: not here, not by
            // the round trip, and test 3 reads it as unconverted.
            foreach (string rel in PluginXamlFiles(repo))
            {
                if (converted.ContainsKey(rel)) continue;
                string abs = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
                if (ReadUtf8(abs).Contains("{loc:T "))
                    failures.Add(rel + ": has {loc:T} references but is not listed in tools/loc/converted-files.txt");
            }
            AssertNoFailures(failures);
        }

        // ------------------------------------------------------------------
        // 6. Every key used exists; every key that exists is used.
        // ------------------------------------------------------------------

        [Fact]
        public void LocKeysResolve()
        {
            string repo = RepoRoot();
            var en = LoadEnglish(repo, out List<string> enWarns);
            var failures = new List<string>(enWarns.Select(w => "en.json: " + w));
            foreach (string k in en.Keys)
                if (!KeyRegex.IsMatch(k)) failures.Add("en.json: key '" + k + "' does not match " + KeyRegex);

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in XamlRefs(repo))
            {
                if (!x.Valid) { failures.Add(x.Where + ": malformed {loc:T " + x.Key + "}; a key is " + KeyRegex); continue; }
                referenced.Add(x.Key);
                if (!en.ContainsKey(x.Key)) failures.Add(x.Where + ": {loc:T " + x.Key + "} is not in en.json");
            }
            foreach (var c in CsCalls(repo))
            {
                if (!KeyRegex.IsMatch(c.Key)) { failures.Add(c.Where + ": Loc." + c.Kind + "(\"" + c.Key + "\") is not a key; a key is " + KeyRegex); continue; }
                if (c.Kind == "N")
                {
                    foreach (string suffix in new[] { ".one", ".other" })
                    {
                        string form = c.Key + suffix;
                        referenced.Add(form);
                        if (!en.ContainsKey(form)) failures.Add(c.Where + ": Loc.N(\"" + c.Key + "\") needs " + form + " in en.json");
                    }
                    continue;
                }
                referenced.Add(c.Key);
                if (!en.ContainsKey(c.Key)) failures.Add(c.Where + ": Loc." + c.Kind + "(\"" + c.Key + "\") is not in en.json");
            }
            foreach (var b in CsBindings(repo))
            {
                referenced.Add(b.Key);
                if (!en.ContainsKey(b.Key))
                {
                    failures.Add(b.Where + ": new Binding(\"[" + b.Key + "]\") is not in en.json");
                    continue;
                }
                // A bound path carries no arguments, so a value with
                // placeholders would reach the label as "{0}".
                int holes = MaxPlaceholder(en[b.Key]) + 1;
                if (holes > 0)
                    failures.Add(b.Where + ": new Binding(\"[" + b.Key + "]\") binds a string with "
                        + Inv(holes) + " placeholder(s); bind a key without placeholders");
            }
            foreach (string k in en.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (referenced.Contains(k) || DynamicFamily.IsMatch(k)) continue;
                failures.Add("en.json: '" + k + "' is referenced by no {loc:T} in XAML, no Loc.T/F/N literal "
                    + "in C# and no new Binding(\"[key]\") to the store "
                    + "(only Effect_*_Name and EngineLayout_* are looked up dynamically)");
            }
            AssertNoFailures(failures);
        }

        // ------------------------------------------------------------------
        // 7. Format arity: arguments passed equal placeholders expected.
        // ------------------------------------------------------------------

        // Appended to every argument-count failure: CountArgsAfter reads a
        // generic argument by its shape, and an expression it cannot read
        // belongs in a local.
        private const string ArityHint = "; if an argument is a generic expression, assign it to a local first";

        [Fact]
        public void LocFormatArity()
        {
            string repo = RepoRoot();
            var en = LoadEnglish(repo, out List<string> enWarns);
            var failures = new List<string>(enWarns.Select(w => "en.json: " + w));

            foreach (var c in CsCalls(repo))
            {
                if (c.Kind == "N")
                {
                    // LocStore.N(key, n, args): the first argument after the key
                    // is the count and is NOT inserted into args, so only what
                    // follows it reaches string.Format. Each plural form may use
                    // at most that many placeholders and the wider form uses
                    // exactly that many (".one" may leave the number out).
                    if (c.ArgsAfter < 1) { failures.Add(c.Where + ": Loc.N(\"" + c.Key + "\") has no count argument"); continue; }
                    int values = c.ArgsAfter - 1;
                    int widest = -1;
                    bool any = false;
                    foreach (string suffix in new[] { ".one", ".other" })
                    {
                        if (!en.TryGetValue(c.Key + suffix, out string form)) continue;   // LocKeysResolve reports it
                        any = true;
                        int need = MaxPlaceholder(form) + 1;
                        widest = Math.Max(widest, need);
                        if (need > values)
                            failures.Add(c.Where + ": Loc.N(\"" + c.Key + "\") passes " + values + " value(s) after the count but "
                                + c.Key + suffix + " uses " + need + " placeholder(s): [" + form + "]" + ArityHint);
                    }
                    if (any && widest != values)
                        failures.Add(c.Where + ": Loc.N(\"" + c.Key + "\") passes " + values + " value(s) after the count but its plural forms use at most "
                            + widest + " placeholder(s)" + ArityHint);
                    continue;
                }

                if (!en.TryGetValue(c.Key, out string english)) continue;   // LocKeysResolve reports it
                int needed = MaxPlaceholder(english) + 1;
                if (c.Kind == "F")
                {
                    if (needed != c.ArgsAfter)
                        failures.Add(c.Where + ": Loc.F(\"" + c.Key + "\") passes " + c.ArgsAfter + " argument(s) but the English text uses "
                            + needed + " placeholder(s): [" + english + "]" + ArityHint);
                }
                else if (needed > 0)
                {
                    failures.Add(c.Where + ": Loc.T(\"" + c.Key + "\") returns a string with " + needed + " placeholder(s) raw; use Loc.F: [" + english + "]");
                }
            }

            foreach (var x in XamlRefs(repo))
            {
                if (!x.Valid || !en.TryGetValue(x.Key, out string english)) continue;
                if (MaxPlaceholder(english) >= 0)
                    failures.Add(x.Where + ": {loc:T " + x.Key + "} binds a string with placeholders raw; a formatted string needs a C# sink with Loc.F: [" + english + "]");
            }
            AssertNoFailures(failures);
        }

        // ==================================================================
        // 8. Edge whitespace is deliberate in English and preserved in
        //    translations. A value that ends in a space sits against its
        //    neighbor in one sentence, so a translation that trims it renders
        //    two words joined, and nothing else in the pipeline can see that:
        //    the pseudo-locale pads every value, and placeholder parity does
        //    not look at whitespace.
        // ==================================================================

        // English keys whose leading or trailing whitespace carries meaning.
        // Add one only after checking that the space belongs to the value
        // rather than to sloppy XAML, and say what it butts against.
        private static readonly Dictionary<string, string> EdgeSpaceKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Followed immediately by the Hyperlink "Lovely Sim Racing" in the
            // light-pattern credit line inside the LIGHTSYNC tab itself.
            { "Lightsync_LightPatternsPerCar", "runs into the Lovely Sim Racing hyperlink" },
            // The second copy of that credit line, the one declared on the
            // Telemetry FFB tab under the per-car rev light option. Its lead-in
            // names the rev light data rather than the patterns, so it is its own
            // value, and it runs into the same hyperlink.
            { "TelemetryFfb_LovelyPerCarRevLightTiming", "runs into the Lovely Sim Racing hyperlink" },
            // The sign-in window builds one sentence from a Run and a
            // Hyperlink, and one row from a label and a link button, so each
            // lead fragment ends where its link begins.
            { "SignIn_PrivacyLead", "runs into the Privacy policy hyperlink" },
            { "SignIn_DidntGetIt", "runs into the Resend code link" },
            { "Plugin_IracingNoticeStartWithPattern", "a paragraph of the iRacing notice, ends its blank line" },
            { "Plugin_IracingNoticeStart", "a paragraph of the iRacing notice, ends its blank line" },
            // Every one of these is a fragment of a paragraph that is built in
            // pieces: a sentence that runs into a hyperlink, a suffix appended
            // after a name, or a clause added to a line already on screen. The
            // space is the separator, and folding it into the code would mean
            // building the paragraph differently.
            { "Plugin_CloseWhateverElseDrives", "StandDownCopy.Before, runs into the guide link" },
            { "Plugin_IRacingClosedSetLoadTrueForceAPI", "StandDownCopy.Before, runs into the guide link" },
            { "Plugin_RunPluginSTrueforce", "StandDownCopy.Before, runs into the guide link" },
            { "Plugin_RunningMAIRATF4ALLSame", "StandDownCopy.Before, runs into the guide link" },
            { "Plugin_Guides", "StandDownCopy.After, continues past the guide link" },
            { "Plugin_IRacingGuide", "StandDownCopy.After, continues past the guide link" },
            { "Plugin_UnconfirmedModel", "appended to the wheel model in WheelStatus" },
            { "Plugin_FAILProbeCrashed_Fmt", "a probe report line, appended to the lines before it" },
            { "Account_PatreonHasDiscordConnected", "appended to the Patreon link result" },
            { "Account_ReCommunityDiscordToo", "appended to the Patreon link result" },
            { "Guides_DataComesStraightGame", "appended to the live telemetry rate line" },
            { "Settings_Telemetry", "a source suffix after a car or engine name" },
            { "Settings_BuiltCarList", "a source suffix after a car or engine name" },
            { "Settings_CachedEarlierSession", "a source suffix after a car or engine name" },
            { "Settings_CommunityConfirmed", "a source suffix after a car or engine name" },
            { "Settings_Heuristic_Fmt", "a source suffix after a car or engine name" },
            { "Settings_GripCalibrationStillSettling", "appended to the learning status line" },
            { "Settings_LimitWhatCanMeasure", "appended to the learning status line" },
            { "Settings_ThenActivateBelow", "appended to the game notice when the feature is off" },
            { "Settings_CurrentLibraryMovedPre", "a paragraph appended to the restore dialog body" },
            // The import preview's toggle row is built from separate elements:
            // a caption, then "all" and "none" as links, so the caption and the
            // bullet-prefixed second caption carry the spacing between them.
            { "ImportPreview_Include", "runs into the all and none links" },
            { "ImportPreview_Default", "separates the row and runs into its links" },
            // Each of these three is a mode note that runs into a hyperlink in
            // the same paragraph, so the sentence ends with its separating space.
            { "Settings_LightsyncOnlySessionMAIRA", "runs into the guide link" },
            { "Settings_LightsyncOnlySessionStreaming_Fmt", "runs into the guide link" },
            { "Settings_IRacingSupportsTrueforceNatively", "runs into the notice link" },
            // Appended as its own Run to the mode note, after a sentence
            // already in that paragraph, so it opens with the separating space.
            { "Settings_NormalModeAlsoUnlocks", "continues the mode note paragraph" },
            // The two help lines under the usage-statistics and community
            // checkboxes on the Settings tab. Each is a Run followed by the
            // "Privacy policy" Hyperlink in the same sentence.
            { "Settings_DiagnosticsHelpsDecideWhatTo", "runs into the Privacy policy hyperlink" },
            { "Settings_NetworkSignInIsOnlyNeeded", "runs into the Privacy policy hyperlink" },
            // The two indented sub-option labels under the Engine pulse
            // checkboxes on the Effects tab. Two leading spaces put each
            // slider label under the checkbox it belongs to.
            { "Effects_EngineStrength", "two leading spaces indent it under the Low-end body checkbox" },
            { "Effects_EngineAmount", "two leading spaces indent it under the High-RPM boost checkbox" },
        };

        [Fact]
        public void LocEdgeWhitespaceIsDeliberateAndPreserved()
        {
            string repo = RepoRoot();
            var en = LoadEnglish(repo, out List<string> enWarns);
            var failures = new List<string>(enWarns.Select(w => "en.json: " + w));

            foreach (var kv in en)
            {
                if (kv.Key == LocStore.MetaMember) continue;
                if (kv.Value == null || kv.Value == kv.Value.Trim()) continue;
                if (EdgeSpaceKeys.ContainsKey(kv.Key)) continue;
                failures.Add("en.json: '" + kv.Key + "' begins or ends with whitespace: [" + kv.Value
                    + "]. A translator cannot see that space and a JSON editor may trim it. Either fold the space"
                    + " into the surrounding XAML, or add the key to EdgeSpaceKeys with a note saying what it runs into.");
            }
            foreach (var kv in EdgeSpaceKeys)
            {
                if (!en.TryGetValue(kv.Key, out string text))
                {
                    failures.Add("EdgeSpaceKeys names '" + kv.Key + "' (" + kv.Value + "), which en.json no longer holds");
                    continue;
                }
                if (text == text.Trim())
                    failures.Add("EdgeSpaceKeys names '" + kv.Key + "' (" + kv.Value
                        + "), whose value no longer carries edge whitespace; drop the entry");
            }

            foreach (var lang in LanguageFiles(repo))
            {
                if (lang.Tag == LocStore.EnglishTag) continue;
                foreach (var kv in lang.Table)
                {
                    if (kv.Key == LocStore.MetaMember) continue;
                    if (!en.TryGetValue(kv.Key, out string english)) continue;   // the validator reports unknown keys
                    string want = EdgeSpace(english);
                    string got = EdgeSpace(kv.Value);
                    if (want != got)
                        failures.Add(lang.Tag + ".json: '" + kv.Key + "' leading and trailing whitespace differs from English: English "
                            + want + ", translation " + got + ". English [" + english + "], translation [" + kv.Value + "]");
                }
            }
            AssertNoFailures(failures);
        }

        // ==================================================================
        // 9. A caption that exists as both a plain key and a _Fmt sibling must
        //    read the same in every language. The plain key is the XAML default
        //    the panel shows before code writes the caption, the _Fmt key is
        //    what code writes afterwards, so a translation that renders them
        //    differently changes the wording of one label mid-session.
        // ==================================================================

        // plain key, format key, and the arguments that reproduce the plain
        // English text through the format string.
        private static readonly object[][] PairedCaptions =
        {
            new object[] { "Lightsync_PatternSelectedLed", "Lightsync_PatternSelectedLed_Fmt", new object[] { 1 } },
        };

        [Fact]
        public void LocPairedCaptionsAgree()
        {
            string repo = RepoRoot();
            var en = LoadEnglish(repo, out List<string> enWarns);
            var failures = new List<string>(enWarns.Select(w => "en.json: " + w));

            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in PairedCaptions) listed.Add((string)pair[0]);
            foreach (var kv in en)
            {
                if (kv.Key == LocStore.MetaMember || kv.Key.EndsWith("_Fmt", StringComparison.Ordinal)) continue;
                if (!en.ContainsKey(kv.Key + "_Fmt") || listed.Contains(kv.Key)) continue;
                failures.Add("en.json: '" + kv.Key + "' and '" + kv.Key + "_Fmt' are one caption under two keys, which no"
                    + " other check ties together. Add it to PairedCaptions with the arguments that reproduce the plain text,"
                    + " or give the label a single key.");
            }

            var cultures = new List<LanguageFile>(LanguageFiles(repo));
            foreach (var pair in PairedCaptions)
            {
                string plainKey = (string)pair[0];
                string fmtKey = (string)pair[1];
                var args = (object[])pair[2];
                foreach (var lang in cultures)
                {
                    string plain = Resolve(lang, en, plainKey);
                    string fmt = Resolve(lang, en, fmtKey);
                    if (plain == null) { failures.Add(lang.Tag + ".json: PairedCaptions names '" + plainKey + "', which nothing defines"); continue; }
                    if (fmt == null) { failures.Add(lang.Tag + ".json: PairedCaptions names '" + fmtKey + "', which nothing defines"); continue; }
                    string rendered;
                    try
                    {
                        rendered = string.Format(CultureInfo.GetCultureInfo("en-US"), fmt, args);
                    }
                    catch (FormatException ex)
                    {
                        failures.Add(lang.Tag + ".json: '" + fmtKey + "' does not format: [" + fmt + "] " + ex.Message);
                        continue;
                    }
                    if (!string.Equals(plain, rendered, StringComparison.Ordinal))
                        failures.Add(lang.Tag + ".json: '" + plainKey + "' reads [" + plain + "] but '" + fmtKey
                            + "' renders [" + rendered + "]. They are one label, so they must agree.");
                }
            }
            AssertNoFailures(failures);
        }

        // ==================================================================
        // 10. The C# ratchet: nothing a user reads is added in English again.
        //
        //     LocKeysResolve and validate.ps1 ask whether every key a call
        //     names exists. This asks the opposite, and it is the question
        //     Phase 2 of docs/localization-plan.md is measured by: is any string
        //     a user reads still a bare English literal in code? Three data
        //     files under tools/loc say what that means, and
        //     tools/loc/sweep-cs.ps1 computes the same numbers from the same
        //     three with its own walker, the way Get-LocInventory and the XAML
        //     walk above mirror each other. A rule added to one belongs in the
        //     other.
        //
        //       cs-ui-sinks.txt       what a UI sink is: the properties, the
        //                             constructors, the calls and their text
        //                             arguments, the calls whose literals never
        //                             reach a user, and the receivers whose
        //                             every method must be classified
        //       cs-keep-literal.txt   what stays English, by file, by value or
        //                             by the type of the initializer it sits in
        //       cs-literal-budget.txt today's count per file
        //
        //     What makes this test fail:
        //       - a file holds MORE than its budget: a label was written in
        //         English, or moved into a file whose number was lower
        //       - a file holds FEWER than its budget: a slice converted labels
        //         without lowering its line, so the number stops being true.
        //         Run sweep-cs.ps1 -WriteBudget and include the diff.
        //       - a file with no budget line holds any at all
        //       - the budget names a file that is no longer on disk
        //       - a method on a watched receiver (TrueforceDialog) is neither a
        //         call sink nor a nonsink, which would be a blind spot rather
        //         than a pass
        //       - one of the three data files is missing or malformed
        //
        //     Phase 2 is provably done the day the budget file holds no entries.
        // ==================================================================

        [Fact]
        public void LocCsLiteralBudget()
        {
            string repo = RepoRoot();
            var rules = ReadCsSinkRules(Path.Combine(repo, ToolsRel, "cs-ui-sinks.txt"));
            var allow = ReadCsAllowlist(Path.Combine(repo, ToolsRel, "cs-keep-literal.txt"));
            var common = ReadListFile(Path.Combine(repo, ToolsRel, "common-keys.txt"));
            string budgetPath = Path.Combine(repo, ToolsRel, "cs-literal-budget.txt");

            var failures = new List<string>();

            // The numbers ratchet, so the rules that produce them have to be
            // pinned too: widening a sink rule or the allowlist would lower the
            // remainder without routing a single label, and both checkers would
            // still pass because both read the weakened files. Changing any of
            // these counts means editing this test in the same commit, which is
            // what puts it in front of a reviewer.
            if (allow.Files.Count != 2
                || !allow.Files.Contains("src/TrueforceForAll.Plugin/SettingsControl.DevCodes.cs")
                || !allow.Files.Contains("src/TrueforceForAll.Plugin/TestCodesWindow.cs"))
                failures.Add("cs-keep-literal.txt: the whole-file exemptions must be exactly the two the plan's "
                    + "Phase 2 exit criterion names, DevCodes.cs and TestCodesWindow.cs; a third one hides a "
                    + "file's labels with no warning");
            // prop went from 7 to 18 when the sweep learned about display text that
            // never touches a control directly: a sentence assigned to a local or a
            // controller field and only shown later. textmember and recordprop grew
            // for the same reason, so all three are pinned now rather than one.
            if (rules.PropNames.Count != 18)
                failures.Add("cs-ui-sinks.txt: expected 18 prop rules, found " + Inv(rules.PropNames.Count)
                    + ". A property rule decides what counts as a label write: adding or removing one moves every "
                    + "budget number, so update this test in the same commit and say why in the message");
            if (rules.TextMembers.Count != 41)
                failures.Add("cs-ui-sinks.txt: expected 41 textmember rules, found " + Inv(rules.TextMembers.Count)
                    + ". Each one says a member returns display text, which is a judgement about that member, so "
                    + "adding or removing one belongs in a commit that says which member and why");
            // Counted by property name, not by line: RecordProps maps a property to
            // the types that declare it, so eleven lines over nine names read as nine.
            if (rules.RecordProps.Count != 9)
                failures.Add("cs-ui-sinks.txt: expected 9 recordprop property name(s), found " + Inv(rules.RecordProps.Count)
                    + ". Same reasoning as textmember: the rule names one property of one of our own records");

            var found = new Dictionary<string, List<CsFinding>>(StringComparer.Ordinal);
            foreach (string abs in PluginFiles(repo, "*.cs"))
            {
                string rel = RelPath(repo, abs);
                if (allow.Files.Contains(rel)) continue;
                var hits = SweepCsFile(ReadUtf8(abs), rel, rules, allow, common, failures);
                if (hits.Count > 0) found[rel] = hits;
            }

            if (!File.Exists(budgetPath))
            {
                failures.Add("tools/loc/cs-literal-budget.txt does not exist; run tools/loc/sweep-cs.ps1 -WriteBudget to create it");
                AssertNoFailures(failures);
                return;
            }
            var budget = ReadCsBudget(budgetPath);

            foreach (var kv in budget.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string rel = kv.Key;
                int want = kv.Value;
                int got = found.TryGetValue(rel, out List<CsFinding> hits) ? hits.Count : 0;
                if (!File.Exists(Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    failures.Add(rel + ": named in tools/loc/cs-literal-budget.txt but not on disk; drop the line");
                    continue;
                }
                if (got > want)
                    failures.Add(rel + ": " + Inv(got) + " bare English UI literal(s), budget " + Inv(want)
                        + ". Route the new one through Loc.T/F/N (the budget only goes down). " + Examples(hits));
                else if (got < want)
                    failures.Add(rel + ": " + Inv(got) + " bare English UI literal(s), budget " + Inv(want)
                        + ". Lower the budget line to " + Inv(got) + " in this commit (tools/loc/sweep-cs.ps1 -WriteBudget).");
            }
            foreach (var kv in found.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (budget.ContainsKey(kv.Key)) continue;
                failures.Add(kv.Key + ": " + Inv(kv.Value.Count) + " bare English UI literal(s) and no budget line."
                    + " Give them keys, or add a line with tools/loc/sweep-cs.ps1 -WriteBudget. " + Examples(kv.Value));
            }
            AssertNoFailures(failures);
        }

        private static string Examples(List<CsFinding> hits)
        {
            if (hits == null || hits.Count == 0) return "";
            var parts = hits.Take(3).Select(h => h.File + ":" + Inv(h.Line) + " " + h.Sink + " [" + h.Value.Replace("\n", "\\n") + "]");
            return "For example: " + string.Join("; ", parts);
        }

        // ==================================================================
        // Helpers: the C# UI-sink sweep (mirror of tools/loc/sweep-cs.ps1)
        // ==================================================================

        private sealed class CsFinding
        {
            public string File, Sink, Frame, Value;
            public int Line;
        }

        // One rule from cs-ui-sinks.txt that matches a call or a constructor,
        // with the arguments it says are UI text: positions, parameter names,
        // or both, because the same overload is called both ways.
        private sealed class CsCallRule
        {
            public string Name;
            public Regex Re;
            public readonly HashSet<int> Positions = new HashSet<int>();
            public readonly HashSet<string> ParamNames = new HashSet<string>(StringComparer.Ordinal);

            public bool Takes(int index, string name)
                => Positions.Contains(index) || (name.Length > 0 && ParamNames.Contains(name));
        }

        private sealed class CsSinkRules
        {
            public Regex PropRe;
            public readonly List<CsCallRule> Calls = new List<CsCallRule>();
            public readonly List<CsCallRule> Ctors = new List<CsCallRule>();
            public readonly List<Regex> Skips = new List<Regex>();
            public readonly List<Regex> Texts = new List<Regex>();
            public readonly List<KeyValuePair<string, Regex>> Watches = new List<KeyValuePair<string, Regex>>();
            public readonly HashSet<string> DottedCalls = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> NonSinks = new HashSet<string>(StringComparer.Ordinal);
            // A region rule names a member whose literals are all display text,
            // or a declared collection whose entries are all labels. A record
            // property counts only inside its own type's initializer.
            public readonly List<string> TextMembers = new List<string>();
            public readonly List<string> Labels = new List<string>();
            public readonly Dictionary<string, HashSet<string>> RecordProps =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            public Regex RecordPropRe;

            // The property names behind PropRe, kept so LocCsLiteralBudget can
            // pin how many there are: a prop rule decides what counts as a
            // label write, so adding one silently moves every budget number.
            public readonly List<string> PropNames = new List<string>();
        }

        private sealed class CsAllowlist
        {
            public readonly HashSet<string> Files = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Values = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Types = new HashSet<string>(StringComparer.Ordinal);
        }

        // "Loc.T" matches that dotted name; ".ToString" matches it on any
        // receiver. The match ends at the '(' so its last character is the paren.
        private static Regex CsNamedCallRegex(string name)
        {
            string lead = @"(?<![A-Za-z0-9_.])";
            string body = name;
            if (name.StartsWith(".", StringComparison.Ordinal)) { lead = @"\.\s*"; body = name.Substring(1); }
            string parts = string.Join(@"\s*\.\s*", body.Split('.').Select(Regex.Escape));
            return new Regex(lead + parts + @"(?![A-Za-z0-9_])\s*\(");
        }

        private static CsSinkRules ReadCsSinkRules(string path)
        {
            if (!File.Exists(path)) throw new InvalidOperationException("Missing sink rules: " + path);
            var rules = new CsSinkRules();
            var props = new List<string>();
            int n = 0;
            foreach (string raw in ReadUtf8(path).Split('\n'))
            {
                n++;
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string where = "cs-ui-sinks.txt:" + Inv(n);
                if (parts.Length < 2) throw new InvalidOperationException(where + " has a directive with no name: [" + line + "]");
                string kind = parts[0], name = parts[1];
                string sel = parts.Length >= 3 ? parts[2] : "";
                switch (kind)
                {
                    case "prop":
                        props.Add(name);
                        break;
                    case "ctor":
                        // An optional namespace qualifier, as in
                        // "new System.Windows.Documents.Run(...)".
                        rules.Ctors.Add(MakeCsCallRule(name, sel, where,
                            new Regex(@"\bnew\s+(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*" + Regex.Escape(name) + @"(?![A-Za-z0-9_])\s*\(")));
                        break;
                    case "call":
                    {
                        // A dotted name is matched as written; a one-part name
                        // matches the method on any receiver, so the lookbehind
                        // lets a dot through.
                        string[] bits = name.Split('.');
                        string lead = bits.Length == 1 ? @"(?<![A-Za-z0-9_])" : @"(?<![A-Za-z0-9_.])";
                        string body = string.Join(@"\s*\.\s*", bits.Select(Regex.Escape));
                        rules.Calls.Add(MakeCsCallRule(name, sel, where, new Regex(lead + body + @"(?![A-Za-z0-9_])\s*\(")));
                        if (bits.Length > 1) rules.DottedCalls.Add(name);
                        break;
                    }
                    case "callre":
                        // The method name is captured so a finding names the
                        // helper rather than the pattern that matched it.
                        rules.Calls.Add(MakeCsCallRule(name, sel, where,
                            new Regex(@"(?<![A-Za-z0-9_])(?<mname>" + name + @")(?![A-Za-z0-9_])\s*\(")));
                        break;
                    case "skipcall":
                        rules.Skips.Add(CsNamedCallRegex(name));
                        break;
                    case "textcall":
                        rules.Texts.Add(CsNamedCallRegex(name));
                        break;
                    case "watch":
                        rules.Watches.Add(new KeyValuePair<string, Regex>(name,
                            new Regex(@"(?<![A-Za-z0-9_.])" + Regex.Escape(name) + @"\s*\.\s*(?<m>[A-Za-z_][A-Za-z0-9_]*)\s*\(")));
                        break;
                    case "nonsink":
                        rules.NonSinks.Add(name);
                        break;
                    case "textmember":
                        rules.TextMembers.Add(name);
                        break;
                    case "labels":
                        rules.Labels.Add(name);
                        break;
                    case "recordprop":
                    {
                        // <Type>.<Prop>, so GuideEntry.ActionLabel is a label and
                        // a field called ActionLabel on anything else is not.
                        int dot = name.LastIndexOf('.');
                        if (dot < 1 || dot == name.Length - 1)
                            throw new InvalidOperationException(where + " recordprop wants <Type>.<Prop>: [" + line + "]");
                        string rpType = name.Substring(0, dot);
                        string rpProp = name.Substring(dot + 1);
                        if (!rules.RecordProps.TryGetValue(rpProp, out HashSet<string> types))
                        {
                            types = new HashSet<string>(StringComparer.Ordinal);
                            rules.RecordProps[rpProp] = types;
                        }
                        types.Add(rpType);
                        break;
                    }
                    default:
                        throw new InvalidOperationException(where + " has an unknown directive [" + kind + "]");
                }
            }
            if (props.Count == 0) throw new InvalidOperationException("cs-ui-sinks.txt names no property sink.");
            rules.PropNames.AddRange(props);
            string alt = string.Join("|", props.OrderByDescending(p => p.Length).Select(Regex.Escape));
            rules.PropRe = new Regex(@"(?:(?<dot>\.)|(?<![A-Za-z0-9_.]))(?<name>" + alt + @")(?![A-Za-z0-9_])\s*(?<plus>\+)?=(?![=>])");
            if (rules.RecordProps.Count > 0)
            {
                string ralt = string.Join("|", rules.RecordProps.Keys.OrderByDescending(p => p.Length).Select(Regex.Escape));
                rules.RecordPropRe = new Regex(@"(?:(?<dot>\.)|(?<![A-Za-z0-9_.]))(?<name>" + ralt + @")(?![A-Za-z0-9_])\s*(?<plus>\+)?=(?![=>])");
            }
            return rules;
        }

        private static CsCallRule MakeCsCallRule(string name, string sel, string where, Regex re)
        {
            var rule = new CsCallRule { Name = name, Re = re };
            foreach (string token in sel.Split(','))
            {
                string t = token.Trim();
                if (t.Length == 0) continue;
                if (t.All(char.IsDigit)) rule.Positions.Add(int.Parse(t, CultureInfo.InvariantCulture));
                else rule.ParamNames.Add(t);
            }
            if (rule.Positions.Count == 0 && rule.ParamNames.Count == 0)
                throw new InvalidOperationException(where + " selects no argument for " + name);
            return rule;
        }

        private static CsAllowlist ReadCsAllowlist(string path)
        {
            var allow = new CsAllowlist();
            foreach (string entry in ReadListFile(path))
            {
                int colon = entry.IndexOf(':');
                if (colon < 1) throw new InvalidOperationException("cs-keep-literal.txt: [" + entry + "] is not kind:value");
                string kind = entry.Substring(0, colon);
                string val = entry.Substring(colon + 1);
                if (kind == "file") allow.Files.Add(val.Replace('\\', '/'));
                else if (kind == "value") allow.Values.Add(val);
                else if (kind == "type") allow.Types.Add(val);
                else throw new InvalidOperationException("cs-keep-literal.txt: unknown kind [" + kind + "] in [" + entry + "]");
            }
            return allow;
        }

        private static Dictionary<string, int> ReadCsBudget(string path)
        {
            var budget = new Dictionary<string, int>(StringComparer.Ordinal);
            int n = 0;
            foreach (string raw in ReadUtf8(path).Split('\n'))
            {
                n++;
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                    throw new InvalidOperationException("cs-literal-budget.txt:" + Inv(n) + " is not '<path> <count>': [" + line + "]");
                string rel = parts[0].Replace('\\', '/');
                if (budget.ContainsKey(rel)) throw new InvalidOperationException("cs-literal-budget.txt lists " + rel + " twice.");
                budget[rel] = int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            return budget;
        }

        private sealed class CsSite
        {
            public bool Arrow;
            public int Index, Body;
            // The end of a textmember's or labels region's span. Unused by the
            // other kinds, which derive their end from the expression.
            public int RegionEnd;
            public string Kind, Sink;
            public CsCallRule Rule;
        }

        private sealed class CsSpan
        {
            public int Start, End;
            public string Name = "";
            public int Index;
        }

        // Index just past the ')', ']' or '}' that closes the group opened at
        // open. Literals are stepped over whole.
        private static int CsBalancedEnd(string s, int open)
        {
            int depth = 0, i = open;
            while (i < s.Length)
            {
                if (IsLiteralStart(s, i)) { i = SkipLiteral(s, i); continue; }
                char c = s[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth <= 0) return i + 1;
                }
                i++;
            }
            return s.Length;
        }

        // The end of the expression that starts at start: the first ';' or ','
        // at depth zero, or the close of the group that encloses it. That covers
        // a statement, an object initializer entry, and an initializer written
        // inside an argument list.
        private static int CsExprEnd(string s, int start)
        {
            int depth = 0, i = start;
            while (i < s.Length)
            {
                if (IsLiteralStart(s, i)) { i = SkipLiteral(s, i); continue; }
                char c = s[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0) return i;
                    depth--;
                }
                else if (depth == 0 && (c == ';' || c == ',')) return i;
                i++;
            }
            return s.Length;
        }

        // The arguments of the call whose '(' is at open, with the parameter
        // name a named argument gives them ("okLabel: \"Got it\"").
        private static List<CsSpan> SplitCsArgs(string s, int open)
        {
            var raw = new List<CsSpan>();
            int depth = 0, i = open, argStart = open + 1;
            while (i < s.Length)
            {
                if (i > open && IsLiteralStart(s, i)) { i = SkipLiteral(s, i); continue; }
                char c = s[i];
                if (c == '(' || c == '[' || c == '{') { depth++; i++; continue; }
                if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (i > argStart) raw.Add(new CsSpan { Start = argStart, End = i });
                        break;
                    }
                    i++;
                    continue;
                }
                if (c == ',' && depth == 1)
                {
                    raw.Add(new CsSpan { Start = argStart, End = i });
                    argStart = i + 1;
                    i++;
                    continue;
                }
                i++;
            }
            var result = new List<CsSpan>();
            int index = 0;
            foreach (var span in raw)
            {
                string body = s.Substring(span.Start, span.End - span.Start);
                if (body.Trim().Length == 0) { index++; continue; }
                var m = Regex.Match(body, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:(?!:)");
                span.Name = m.Success ? m.Groups[1].Value : "";
                span.Index = index++;
                result.Add(span);
            }
            return result;
        }

        // The value of the C# literal at [start,end): escapes resolved in a
        // regular string, doubled quotes folded in a verbatim one, and an
        // interpolation hole rendered as "{}" so the letters outside it are what
        // the real-string rule weighs.
        private static string DecodeCsLiteral(string s, int start, int end)
        {
            int i = start;
            bool verbatim = false, interpolated = false;
            while (i < end && (s[i] == '@' || s[i] == '$'))
            {
                if (s[i] == '@') verbatim = true; else interpolated = true;
                i++;
            }
            if (i >= end) return "";
            char quote = s[i];
            i++;
            int stop = end > start && s[end - 1] == quote ? end - 1 : end;
            var sb = new StringBuilder();
            while (i < stop)
            {
                char c = s[i];
                if (verbatim && c == quote && i + 1 < stop && s[i + 1] == quote) { sb.Append(quote); i += 2; continue; }
                if (!verbatim && c == '\\' && i + 1 < stop)
                {
                    char e = s[i + 1];
                    i += 2;
                    if (e == 'n') sb.Append('\n');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 't') sb.Append('\t');
                    else if (e == '0') sb.Append('\0');
                    else if (e == 'a' || e == 'b' || e == 'f' || e == 'v') sb.Append(' ');
                    else if (e == 'u' && i + 4 <= stop) { sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; }
                    else if (e == 'U' && i + 8 <= stop) { sb.Append(char.ConvertFromUtf32(Convert.ToInt32(s.Substring(i, 8), 16))); i += 8; }
                    else if (e == 'x')
                    {
                        var hex = new StringBuilder();
                        while (hex.Length < 4 && i < stop && Uri.IsHexDigit(s[i])) { hex.Append(s[i]); i++; }
                        if (hex.Length > 0) sb.Append((char)Convert.ToInt32(hex.ToString(), 16));
                    }
                    else sb.Append(e);
                    continue;
                }
                if (interpolated && c == '{')
                {
                    if (i + 1 < stop && s[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                    int holeEnd = CsBalancedEnd(s, i);
                    sb.Append("{}");
                    i = holeEnd;
                    continue;
                }
                if (interpolated && c == '}' && i + 1 < stop && s[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // "" means a real UI string, otherwise numeric, glyph or empty. The XAML
        // rule (SkipReason above) asks whether the attribute and element are
        // display before letting a two-letter value through; a C# sink write is
        // display by definition, so two letters is the threshold here and the
        // rest of the rule is the same. One rule the XAML side does not need:
        // placeholders come out before the letters are counted, because XAML
        // holds none. "{0} items" keeps " items" and stays real, while
        // "{0:X2}{1:X2}{2:X2}" is left with nothing and is a number format.
        private static string CsSkipReason(string value, HashSet<string> common)
        {
            if (value == null) return "empty";
            string t = Regex.Replace(value, @"\{[^{}]*\}", "").Trim();
            if (t.Length == 0) return "empty";
            int letters = 0, digits = 0;
            foreach (char ch in t)
            {
                if (char.IsLetter(ch)) letters++;
                else if (char.IsDigit(ch)) digits++;
            }
            if (letters == 0) return digits > 0 ? "numeric" : "glyph";
            if (Regex.IsMatch(t, @"\s")) return "";
            if (letters >= 2) return "";
            if (common != null && common.Contains(value)) return "";
            return "glyph";
        }

        // True when the literal at [start,end) is being compared rather than
        // shown: 'mode == "mine"', '"mine" == mode', 'is "x"', 'case "x":', or
        // the pattern side of a switch arm. Such a literal sits at the top level
        // of a sink expression ('Label.Text = kind == "car" ? A : B') and would
        // otherwise read as the value; the value there is A or B.
        private static bool CsTestOperand(string s, int start, int end)
        {
            int k = start - 1;
            while (k >= 0 && char.IsWhiteSpace(s[k])) k--;
            if (k >= 1)
            {
                string two = s.Substring(k - 1, 2);
                if (two == "==" || two == "!=") return true;
            }
            if (k >= 0 && IsIdentifierChar(s[k]))
            {
                int w = k;
                while (w >= 0 && IsIdentifierChar(s[w])) w--;
                string word = s.Substring(w + 1, k - w);
                if (word == "is" || word == "case") return true;
            }
            int j = end;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j + 1 < s.Length)
            {
                string two = s.Substring(j, 2);
                if (two == "==" || two == "!=" || two == "=>") return true;
            }
            return false;
        }

        // Two maps over every "new" in the file. types: brace index -> the type
        // of the object or collection initializer it opens, "" for an implicit
        // array; everything else is a statement block, which is how a bare
        // "Title = ..." on a window is told from an entry in a data record's
        // initializer. ends: the index of "new" -> just past the whole
        // construction, which a sink expression steps over because any UI text
        // inside it sits on a sink of its own.
        private static void CsNewSpans(string s, out Dictionary<int, string> types, out Dictionary<int, int> ends)
        {
            types = new Dictionary<int, string>();
            ends = new Dictionary<int, int>();
            foreach (Match m in Regex.Matches(s, @"\bnew\b"))
            {
                int j = m.Index + 3;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                string type = "";
                if (j < s.Length && s[j] != '[' && s[j] != '{')
                {
                    int nameStart = j;
                    while (j < s.Length && (IsIdentifierChar(s[j]) || s[j] == '.')) j++;
                    if (j == nameStart) continue;
                    type = s.Substring(nameStart, j - nameStart);
                }
                while (j < s.Length)
                {
                    while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                    if (j >= s.Length) break;
                    char c = s[j];
                    if (c == '<')
                    {
                        int e = GenericListEnd(s, j);
                        if (e < 0) break;
                        j = e;
                        continue;
                    }
                    if (c == '(' || c == '[') { j = CsBalancedEnd(s, j); continue; }
                    break;
                }
                int end = j;
                while (end < s.Length && char.IsWhiteSpace(s[end])) end++;
                if (end < s.Length && s[end] == '{')
                {
                    types[end] = type;
                    ends[m.Index] = CsBalancedEnd(s, end);
                }
                else
                {
                    ends[m.Index] = j;
                }
            }
        }

        // One pass over the file, jumping between the characters that matter, to
        // learn for each site whether the code reaches it at all (a site matched
        // inside a string literal does not count) and which object initializer,
        // if any, encloses it. "" means a statement block; a site with no entry
        // is dead.
        private static Dictionary<int, string> CsSiteFrames(string s, List<CsSite> sites, Dictionary<int, string> initTypes)
        {
            var frames = new Dictionary<int, string>();
            if (sites.Count == 0) return frames;
            var stack = new List<string>();
            char[] stops = { '"', '\'', '@', '$', '{', '}' };
            int i = 0, k = 0;
            while (i < s.Length)
            {
                int n = s.IndexOfAny(stops, i);
                if (n < 0) n = s.Length;
                while (k < sites.Count && sites[k].Index < n)
                {
                    frames[sites[k].Index] = stack.Count > 0 ? stack[stack.Count - 1] : "";
                    k++;
                }
                if (n >= s.Length) break;
                i = n;
                if (IsLiteralStart(s, i))
                {
                    int litEnd = SkipLiteral(s, i);
                    while (k < sites.Count && sites[k].Index < litEnd) k++;   // matched inside a literal
                    i = litEnd;
                    continue;
                }
                char c = s[i];
                if (c == '{') stack.Add(initTypes.TryGetValue(i, out string t) ? t : "");
                else if (c == '}' && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                i++;
            }
            return frames;
        }

        // Every string literal in [start,end) that a user actually reads.
        // Stepped over whole: a call the rules skip (a key, a format specifier),
        // any "new ..." construction, and any other call or indexer, because a
        // literal handed to a method or used as a lookup key is not display
        // text. Descended into: a grouping parenthesis, a ternary, a
        // concatenation, and the calls the rules mark as text composers.
        // The interior of each interpolation hole in the literal at [start,end).
        // "{{" is an escaped brace, a nested brace is tracked by depth, and a
        // literal inside a hole is stepped over so a '}' in its text cannot close
        // the hole. Mirrors Get-LocCsHoleSpans in sweep-cs.ps1.
        private static List<CsSpan> CsHoleSpans(string s, int start, int end)
        {
            var found = new List<CsSpan>();
            int i = start;
            while (i < end && s[i] != '"') i++;   // past the opening quote
            i++;
            while (i < end)
            {
                if (s[i] != '{') { i++; continue; }
                if (i + 1 < end && s[i + 1] == '{') { i += 2; continue; }
                int depth = 1, j = i + 1, holeStart = j;
                while (j < end && depth > 0)
                {
                    if (IsLiteralStart(s, j)) { j = SkipLiteral(s, j); continue; }
                    if (s[j] == '{') depth++;
                    else if (s[j] == '}') { depth--; if (depth == 0) break; }
                    j++;
                }
                if (j > holeStart) found.Add(new CsSpan { Start = holeStart, End = j });
                i = j + 1;
            }
            return found;
        }

        private static List<CsSpan> CsLiteralsIn(string s, int start, int end,
            Dictionary<int, int> skipAt, Dictionary<int, int> textAt, Dictionary<int, int> newEnd)
        {
            var found = new List<CsSpan>();
            int i = start;
            while (i < end)
            {
                if (skipAt.TryGetValue(i, out int skipParen)) { i = CsBalancedEnd(s, skipParen); continue; }
                if (newEnd.TryGetValue(i, out int afterNew)) { i = afterNew; continue; }
                if (textAt.TryGetValue(i, out int textParen)) { i = textParen + 1; continue; }
                if (IsLiteralStart(s, i))
                {
                    int litEnd = SkipLiteral(s, i);
                    if (s[i] != '\'' && !CsTestOperand(s, i, litEnd))
                        found.Add(new CsSpan { Start = i, End = litEnd });
                    // An interpolated string's holes hold code, and that code can
                    // hold display text of its own. The same walk applies inside,
                    // so a Loc call or a lookup key in a hole is stepped over too.
                    bool isInterp = false;
                    for (int p = i; p < litEnd && (s[p] == '@' || s[p] == '$'); p++)
                        if (s[p] == '$') isInterp = true;
                    if (isInterp)
                        foreach (var hole in CsHoleSpans(s, i, litEnd))
                            found.AddRange(CsLiteralsIn(s, hole.Start, hole.End, skipAt, textAt, newEnd));
                    i = litEnd;
                    continue;
                }
                char c = s[i];
                if (c == '(' || c == '[')
                {
                    int k = i - 1;
                    while (k >= 0 && char.IsWhiteSpace(s[k])) k--;
                    bool isCall = k >= 0 && (IsIdentifierChar(s[k]) || s[k] == ')' || s[k] == ']');
                    if (isCall) { i = CsBalancedEnd(s, i); continue; }
                }
                i++;
            }
            return found;
        }

        // The span of each named region. A textmember's body is the balanced
        // braces after its declaration, or everything up to the ';' when it is an
        // expression body; a labels region is its initializer's braces. Matched
        // at a declaration, so a call to the member and a read of the collection
        // are untouched. Mirrors Get-LocCsRegionSpans in sweep-cs.ps1.
        private static List<CsRegion> CsRegionSpans(string s, List<string> names, string kind)
        {
            var found = new List<CsRegion>();
            foreach (string nm in names)
            {
                string esc = Regex.Escape(nm);
                var re = kind == "labels"
                    ? new Regex(@"(?<![A-Za-z0-9_.])" + esc + @"\s*=\s*(?:new\b[^={;]*)?\{")
                    : new Regex(@"(?<![A-Za-z0-9_.])" + esc + @"\s*(?:\([^()]*\))?\s*(?:=>|\{)");
                foreach (Match m in re.Matches(s))
                {
                    bool isArrow = s.Substring(m.Index, m.Length).EndsWith("=>", StringComparison.Ordinal);
                    int start, end;
                    if (isArrow)
                    {
                        start = m.Index + m.Length;
                        end = CsExprEnd(s, start);
                    }
                    else
                    {
                        start = m.Index + m.Length - 1;
                        if (start >= s.Length || s[start] != '{') continue;
                        end = CsBalancedEnd(s, start);
                    }
                    // Arrow: the region IS the returned expression, so a textmember
                    // has no inner "return" to look for and the region is the span.
                    if (end > start) found.Add(new CsRegion { Start = start, End = end, Name = nm, Arrow = isArrow });
                }
            }
            return found;
        }

        // The spans a member actually returns: what follows "return" or "=>" up to
        // the end of that expression. Mirrors Get-LocCsReturnSpans in sweep-cs.ps1.
        private static readonly Regex CsReturnRe = new Regex(@"(?<![A-Za-z0-9_])return(?![A-Za-z0-9_])|=>");

        private static List<CsSpan> CsReturnSpans(string s, int start, int end)
        {
            var found = new List<CsSpan>();
            foreach (Match m in CsReturnRe.Matches(s))
            {
                if (m.Index < start || m.Index >= end) continue;
                int from = m.Index + m.Length;
                int to = Math.Min(CsExprEnd(s, from), end);
                if (to > from) found.Add(new CsSpan { Start = from, End = to });
            }
            return found;
        }

        private sealed class CsRegion
        {
            public bool Arrow;
            public int Start;
            public int End;
            public string Name;
        }

        private static int[] CsLineStarts(string s)
        {
            var list = new List<int> { 0 };
            for (int i = s.IndexOf('\n'); i >= 0; i = s.IndexOf('\n', i + 1)) list.Add(i + 1);
            return list.ToArray();
        }

        private static int CsLineAt(int[] lineStarts, int index)
        {
            int lo = 0, hi = lineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lineStarts[mid] <= index) lo = mid; else hi = mid - 1;
            }
            return lo + 1;
        }

        private static List<CsFinding> SweepCsFile(string raw, string rel, CsSinkRules rules,
            CsAllowlist allow, HashSet<string> common, List<string> failures)
        {
            // Comments are blanked first (newlines kept, so line numbers hold),
            // so a sink quoted in one is never counted.
            string text = StripComments(raw);

            var sites = new List<CsSite>();
            foreach (Match m in rules.PropRe.Matches(text))
                sites.Add(new CsSite { Index = m.Index, Kind = "prop", Sink = m.Groups["name"].Value, Body = m.Index + m.Length });
            foreach (var rule in rules.Calls)
                foreach (Match m in rule.Re.Matches(text))
                    sites.Add(new CsSite
                    {
                        Index = m.Index, Kind = "call", Body = m.Index + m.Length - 1, Rule = rule,
                        Sink = m.Groups["mname"].Success ? m.Groups["mname"].Value : rule.Name,
                    });
            foreach (var rule in rules.Ctors)
                foreach (Match m in rule.Re.Matches(text))
                    sites.Add(new CsSite { Index = m.Index, Kind = "ctor", Sink = "new " + rule.Name, Body = m.Index + m.Length - 1, Rule = rule });
            if (rules.RecordPropRe != null)
                foreach (Match m in rules.RecordPropRe.Matches(text))
                    sites.Add(new CsSite { Index = m.Index, Kind = "recordprop", Sink = m.Groups["name"].Value, Body = m.Index + m.Length });
            foreach (var r in CsRegionSpans(text, rules.TextMembers, "textmember"))
                sites.Add(new CsSite { Index = r.Start, Kind = "textmember", Sink = r.Name, Body = r.Start, RegionEnd = r.End, Arrow = r.Arrow });
            foreach (var r in CsRegionSpans(text, rules.Labels, "labels"))
                sites.Add(new CsSite { Index = r.Start, Kind = "labels", Sink = r.Name, Body = r.Start, RegionEnd = r.End });
            sites = sites.OrderBy(s => s.Index).ToList();

            var skipAt = new Dictionary<int, int>();
            foreach (var re in rules.Skips)
                foreach (Match m in re.Matches(text)) skipAt[m.Index] = m.Index + m.Length - 1;
            var textAt = new Dictionary<int, int>();
            foreach (var re in rules.Texts)
                foreach (Match m in re.Matches(text)) textAt[m.Index] = m.Index + m.Length - 1;

            CsNewSpans(text, out Dictionary<int, string> newTypes, out Dictionary<int, int> newEnds);
            var frames = CsSiteFrames(text, sites, newTypes);
            int[] lineStarts = CsLineStarts(text);

            var findings = new List<CsFinding>();
            foreach (var site in sites)
            {
                if (!frames.TryGetValue(site.Index, out string frame)) continue;   // matched inside a literal
                var spans = new List<CsSpan>();
                if (site.Kind == "textmember")
                {
                    if (site.Arrow) spans.Add(new CsSpan { Start = site.Body, End = site.RegionEnd });
                    else spans.AddRange(CsReturnSpans(text, site.Body, site.RegionEnd));
                }
                else if (site.Kind == "labels")
                    spans.Add(new CsSpan { Start = site.Body, End = site.RegionEnd });
                else if (site.Kind == "recordprop")
                {
                    // Only inside an initializer of one of the types the rule named.
                    if (frame.Length == 0 || !rules.RecordProps[site.Sink].Contains(frame)) continue;
                    spans.Add(new CsSpan { Start = site.Body, End = CsExprEnd(text, site.Body) });
                }
                else if (site.Kind == "prop")
                    spans.Add(new CsSpan { Start = site.Body, End = CsExprEnd(text, site.Body) });
                else
                    foreach (var arg in SplitCsArgs(text, site.Body))
                        if (site.Rule.Takes(arg.Index, arg.Name)) spans.Add(arg);

                foreach (var span in spans)
                    foreach (var lit in CsLiteralsIn(text, span.Start, span.End, skipAt, textAt, newEnds))
                    {
                        string value = DecodeCsLiteral(text, lit.Start, lit.End);
                        if (CsSkipReason(value, common) != "") continue;
                        if (allow.Values.Contains(value)) continue;
                        if (frame.Length > 0 && allow.Types.Contains(frame)) continue;
                        findings.Add(new CsFinding
                        {
                            File = rel, Line = CsLineAt(lineStarts, lit.Start),
                            Sink = site.Sink, Frame = frame, Value = value,
                        });
                    }
            }

            // A method on a watched receiver that nobody has classified is a
            // blind spot, not a pass.
            foreach (var watch in rules.Watches)
                foreach (Match m in watch.Value.Matches(text))
                {
                    string full = watch.Key + "." + m.Groups["m"].Value;
                    if (rules.DottedCalls.Contains(full) || rules.NonSinks.Contains(full)) continue;
                    failures.Add(rel + ":" + Inv(CsLineAt(lineStarts, m.Index)) + " " + full
                        + "(...) is neither a call sink nor a nonsink in tools/loc/cs-ui-sinks.txt");
                }

            return findings;
        }

        // ==================================================================
        // Helpers: language files
        // ==================================================================

        private sealed class LanguageFile
        {
            public string Tag;
            public Dictionary<string, string> Table;
        }

        // Every Languages\*.json in the plugin, English included. Only the
        // files themselves: a value a file leaves out falls back to English
        // here, as LocStore does, but a parent-culture chain (es-MX over es)
        // is not walked, so a pair split across a parent and its child is
        // outside what these two tests can see.
        private static IEnumerable<LanguageFile> LanguageFiles(string repo)
        {
            string dir = Path.Combine(repo, PluginRel.Replace('/', Path.DirectorySeparatorChar), "Languages");
            if (!Directory.Exists(dir)) yield break;
            foreach (string path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                string tag = Path.GetFileNameWithoutExtension(path);
                Dictionary<string, string> table;
                try
                {
                    table = LocStore.ParseLanguageJson(ReadUtf8(path), out _, _ => { });
                }
                catch (JsonException)
                {
                    continue;   // LocStore's own parse test owns malformed files
                }
                yield return new LanguageFile { Tag = tag, Table = table };
            }
        }

        private static string Resolve(LanguageFile lang, Dictionary<string, string> en, string key)
        {
            if (lang.Table.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v)) return v;
            return en.TryGetValue(key, out string e) ? e : null;
        }

        // The leading and trailing whitespace of a value, printed so a failure
        // message can show what is otherwise invisible.
        private static string EdgeSpace(string text)
        {
            if (text == null) return "(null)";
            int lead = 0;
            while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;
            int trail = 0;
            while (trail < text.Length - lead && char.IsWhiteSpace(text[text.Length - 1 - trail])) trail++;
            return "lead " + Inv(lead) + ", trail " + Inv(trail);
        }

        // ==================================================================
        // Helpers: repo files
        // ==================================================================

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "Directory.Build.props"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Repo root (a folder holding src/Directory.Build.props) not found above " + AppContext.BaseDirectory);
        }

        private static string ReadUtf8(string path)
            => File.ReadAllText(path, new UTF8Encoding(false, true));   // a BOM is detected and dropped

        private static string Inv(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static string RelPath(string repo, string abs)
        {
            string full = Path.GetFullPath(abs);
            string root = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
            return rel.Replace('\\', '/');
        }

        private static bool UnderBinOrObj(string path)
        {
            foreach (string seg in path.Split('\\', '/'))
                if (seg.Equals("bin", StringComparison.OrdinalIgnoreCase) || seg.Equals("obj", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static IEnumerable<string> PluginFiles(string repo, string pattern)
        {
            string pluginDir = Path.Combine(repo, PluginRel.Replace('/', Path.DirectorySeparatorChar));
            return Directory.EnumerateFiles(pluginDir, pattern, SearchOption.AllDirectories)
                .Where(p => !UnderBinOrObj(RelPath(repo, p)))
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        private static IEnumerable<string> PluginXamlFiles(string repo)
            => PluginFiles(repo, "*.xaml").Select(p => RelPath(repo, p));

        // Mirrors Read-LocListFile: one value per line, verbatim; blank lines
        // and '#' lines ignored.
        private static HashSet<string> ReadListFile(string path)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return set;
            foreach (string line in ReadUtf8(path).Split('\n'))
            {
                string t = line.TrimEnd('\r');
                if (t.Trim().Length == 0) continue;
                if (t.StartsWith("#", StringComparison.Ordinal)) continue;
                set.Add(t);
            }
            return set;
        }

        // tools/loc/converted-files.txt (decision B). A line is a repo-relative
        // XAML path, meaning the whole file is converted, or
        // "<path>|<spec>[,<spec>...]" naming the converted parts of it, each
        // spec one of resources:<file>, header:<file>, tab:<xName> and
        // trailer:<file> (see InScope). The result maps the path to its spec
        // set, empty for a whole file. A path listed twice is an error, as it
        // is in Read-LocConvertedList (the converter merges a file's slices
        // into one line); the raw lines are read here, not ReadListFile's set,
        // which would hide two identical lines. Kinds are lower-cased the way
        // Test-LocInScope reads them; arguments are kept as written.
        private static Dictionary<string, HashSet<string>> ReadConvertedFiles(string repo)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            string listPath = Path.Combine(repo, ToolsRel, "converted-files.txt");
            if (!File.Exists(listPath)) return map;
            foreach (string rawLine in ReadUtf8(listPath).Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                string path = line;
                string specText = null;
                int bar = line.IndexOf('|');
                if (bar >= 0) { path = line.Substring(0, bar); specText = line.Substring(bar + 1); }
                path = path.Trim().Replace('\\', '/');
                if (path.Length == 0) throw new InvalidOperationException("converted-files.txt: a line without a path: [" + line + "]");
                if (map.ContainsKey(path)) throw new InvalidOperationException("converted-files.txt lists " + path + " twice.");
                var specs = new HashSet<string>(StringComparer.Ordinal);
                map[path] = specs;
                if (specText == null) continue;
                int added = 0;
                foreach (string raw in specText.Split(','))
                {
                    string spec = raw.Trim();
                    if (spec.Length == 0) continue;
                    int colon = spec.IndexOf(':');
                    if (colon < 1 || colon == spec.Length - 1)
                        throw new InvalidOperationException("converted-files.txt: bad scope spec [" + spec + "] on " + path + " (expected kind:value)");
                    string kind = spec.Substring(0, colon).ToLowerInvariant();
                    if (kind != "resources" && kind != "header" && kind != "tab" && kind != "trailer")
                        throw new InvalidOperationException("converted-files.txt: unknown scope kind [" + kind + "] on " + path
                            + " (resources, header, tab or trailer; a whole file is a bare path)");
                    specs.Add(kind + ":" + spec.Substring(colon + 1));
                    added++;
                }
                if (added == 0) throw new InvalidOperationException("converted-files.txt: no scope spec after '|' on " + path);
            }
            return map;
        }

        // en.json through the runtime's own parser. Warnings (duplicate key,
        // bad key, non-string value, missing _meta) come back to the caller,
        // who fails on them: a file the runtime has to repair is a defect.
        private static Dictionary<string, string> LoadEnglish(string repo, out List<string> warns)
        {
            string path = Path.Combine(repo, PluginRel.Replace('/', Path.DirectorySeparatorChar), "Languages", "en.json");
            warns = new List<string>();
            if (!File.Exists(path))
            {
                warns.Add("missing at " + path);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return LocStore.ParseLanguageJson(ReadUtf8(path), out _, warns.Add);
        }

        private static void AssertNoFailures(List<string> failures)
        {
            if (failures.Count == 0) return;
            const int cap = 80;
            var shown = failures.Take(cap).ToList();
            if (failures.Count > cap) shown.Add("... and " + (failures.Count - cap) + " more");
            Assert.Fail(failures.Count + " problem(s):\n" + string.Join("\n", shown));
        }

        // Multiset difference of two row streams, reported both ways. Only
        // test 3 calls it, so the header names that test's fix.
        private static void Diff(List<string> failures, string what, IEnumerable<string> expected, IEnumerable<string> actual)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string e in expected) counts[e] = counts.TryGetValue(e, out int n) ? n + 1 : 1;
            var extra = new List<string>();
            foreach (string a in actual)
            {
                if (counts.TryGetValue(a, out int n) && n > 0) { if (n == 1) counts.Remove(a); else counts[a] = n - 1; }
                else extra.Add(a);
            }
            var missing = counts.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (missing.Count == 0 && extra.Count == 0) return;
            failures.Add(what + ": " + missing.Count + " baseline row(s) the C# walk did not produce, " + extra.Count + " C# row(s) the baseline lacks"
                + "; if the change is intended, regenerate tools/loc/baseline with inventory.ps1 -ResolveWith");
            foreach (string m in missing.Take(20)) failures.Add("    baseline only: " + m);
            foreach (string x in extra.Take(20)) failures.Add("    C# walk only:  " + x);
        }

        // ==================================================================
        // Helpers: the baseline CSV
        // ==================================================================

        private sealed class CsvRow
        {
            public string File, Line, ElementType, XName, Attribute, Value, SkipReason;
        }

        private static readonly string[] BaselineColumns = { "file", "line", "elementType", "xName", "attribute", "value", "skipReason" };

        // Every *.csv under tools/loc/baseline, merged by file. A XAML file
        // must appear in exactly one CSV, so a new file gets its own dated
        // baseline before it is converted.
        private static Dictionary<string, List<CsvRow>> LoadBaseline(string repo)
        {
            string dir = Path.Combine(repo, ToolsRel.Replace('/', Path.DirectorySeparatorChar), "baseline");
            var result = new Dictionary<string, List<CsvRow>>(StringComparer.Ordinal);
            var owner = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(dir)) throw new InvalidOperationException("No baseline folder at " + dir);
            foreach (string csv in Directory.GetFiles(dir, "*.csv").OrderBy(p => p, StringComparer.Ordinal))
            {
                var records = ReadCsv(csv);
                if (records.Count == 0) throw new InvalidOperationException(csv + " is empty");
                if (!records[0].SequenceEqual(BaselineColumns, StringComparer.Ordinal))
                    throw new InvalidOperationException(csv + " header is [" + string.Join(",", records[0]) + "], expected [" + string.Join(",", BaselineColumns) + "]");
                var seenHere = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 1; i < records.Count; i++)
                {
                    string[] f = records[i];
                    if (f.Length != BaselineColumns.Length)
                        throw new InvalidOperationException(csv + " row " + (i + 1) + " has " + f.Length + " fields");
                    var row = new CsvRow { File = f[0], Line = f[1], ElementType = f[2], XName = f[3], Attribute = f[4], Value = f[5], SkipReason = f[6] };
                    if (seenHere.Add(row.File))
                    {
                        if (owner.TryGetValue(row.File, out string other))
                            throw new InvalidOperationException(row.File + " has baseline rows in both " + Path.GetFileName(other) + " and " + Path.GetFileName(csv));
                        owner[row.File] = csv;
                    }
                    if (!result.TryGetValue(row.File, out List<CsvRow> list)) result[row.File] = list = new List<CsvRow>();
                    list.Add(row);
                }
            }
            return result;
        }

        // RFC 4180: quoted fields may hold commas, doubled quotes and line breaks.
        private static List<string[]> ReadCsv(string path)
        {
            string text = ReadUtf8(path);
            var rows = new List<string[]>();
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false, pending = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        inQuotes = false; i++; continue;
                    }
                    sb.Append(c); i++; continue;
                }
                if (c == '"') { inQuotes = true; pending = true; i++; continue; }
                if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); pending = true; i++; continue; }
                if (c == '\r') { i++; continue; }
                if (c == '\n')
                {
                    fields.Add(sb.ToString()); sb.Clear();
                    rows.Add(fields.ToArray()); fields.Clear();
                    pending = false; i++; continue;
                }
                sb.Append(c); pending = true; i++;
            }
            if (pending || fields.Count > 0) { fields.Add(sb.ToString()); rows.Add(fields.ToArray()); }
            return rows;
        }

        // ==================================================================
        // Helpers: the XAML walk (mirror of Get-LocInventory in _common.ps1)
        // ==================================================================

        private sealed class XamlRow
        {
            public string File, ElementType, XName, Attribute, Value, SkipReason;
            public int Line;
            public XElement Element;
        }

        private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        // Identifier and layout attributes named in the tooling contract
        // ($LocDenyContract) plus the additions found on the real files
        // ($LocDenyAdded). Same spelling, same case sensitivity (ordinal).
        private static readonly HashSet<string> DenySet = new HashSet<string>(StringComparer.Ordinal)
        {
            "x:Name", "x:Key", "x:Class", "x:Uid", "Tag", "GroupName", "Name", "Property", "TargetType", "TargetName",
            "Style", "BasedOn", "Key", "SharedSizeGroup",
            "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "KeyDown", "LostFocus", "Loaded",
            "MouseDown", "PreviewMouseDown", "ValueChanged", "Expanded", "Collapsed", "Closed", "RequestNavigate",
            "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "Margin", "Padding", "Width", "Height",
            "MinWidth", "MaxWidth", "MinHeight", "MaxHeight", "FontSize", "FontWeight", "FontFamily", "Opacity",
            "Foreground", "Background", "BorderBrush", "BorderThickness", "CornerRadius", "Fill", "Stroke", "Stretch",
            "Orientation", "HorizontalAlignment", "VerticalAlignment", "Visibility", "IsEnabled", "IsChecked",
            "IsExpanded", "SelectedIndex", "Minimum", "Maximum", "Value", "TickFrequency", "Interval", "Cursor",
            "Focusable", "IsTabStop", "TextWrapping", "TextTrimming", "TextAlignment", "Source", "Data", "Points",
            "StrokeThickness", "RenderTransformOrigin", "Panel.ZIndex", "Command", "CommandParameter", "ItemsSource",
            "DisplayMemberPath", "SelectedValuePath", "Binding", "Path", "ElementName", "UpdateSourceTrigger", "Mode",
            "Converter",
            // $LocDenyAdded
            "Unloaded", "MouseMove", "MouseLeave", "MouseEnter", "MouseLeftButtonUp", "MouseLeftButtonDown",
            "MouseRightButtonUp", "MouseDoubleClick", "LostKeyboardFocus", "GotFocus", "GotKeyboardFocus",
            "DropDownClosed", "DropDownOpened", "LoadingRow", "KeyUp", "PreviewKeyDown", "PreviewKeyUp", "Initialized",
            "ActionName", "FontStyle", "FontStretch", "DockPanel.Dock", "SelectionMode", "SelectionUnit",
            "HeadersVisibility", "GridLinesVisibility", "WindowStartupLocation", "ResizeMode", "SizeToContent",
            "Placement", "Color", "TextDecorations", "TextElement.Foreground", "FocusVisualStyle", "CaretBrush", "mc:Ignorable",
            "d:DesignHeight", "d:DesignWidth", "SmallChange", "LargeChange", "Angle", "MaxLength", "MaxDropDownHeight",
            "RowHeaderWidth", "BlurRadius", "ShadowDepth", "Columns", "Rows",
        };

        // Test-LocDenied.
        private static bool IsDenied(string attributeName)
        {
            if (DenySet.Contains(attributeName)) return true;
            if (attributeName.StartsWith("xmlns", StringComparison.Ordinal)) return true;
            if (attributeName.EndsWith("Changed", StringComparison.Ordinal)) return true;
            if (attributeName.EndsWith("Alignment", StringComparison.Ordinal)) return true;
            if (attributeName.EndsWith("Visibility", StringComparison.Ordinal)) return true;
            if (attributeName.EndsWith("Brush", StringComparison.Ordinal)) return true;
            if (attributeName.EndsWith("Style", StringComparison.Ordinal)) return true;
            if (attributeName.EndsWith("Thickness", StringComparison.Ordinal)) return true;
            if (attributeName.StartsWith("ToolTipService.", StringComparison.Ordinal)) return true;
            if (attributeName.StartsWith("ScrollViewer.", StringComparison.Ordinal)) return true;
            return false;
        }

        // Get-LocSkipReason: "" is a real string; otherwise binding, numeric,
        // glyph, identifier or empty. Decision A, the short-token rule: a value
        // with at least one letter is real when it contains whitespace, has
        // four or more letters, is listed verbatim in common-keys.txt (whatever
        // its length: "NEW", "OK"), or has two or more letters on a display
        // attribute (ShortTokenAttributes: Content, Header, Text, Title or
        // element text) of an element that shows text (ShortTokenElements). A
        // single letter stays a glyph. The letter count comes first, so a
        // letterless value is never real, common-keys.txt or not. PowerShell's
        // -eq is case-insensitive, so the True/False test is too; the two name
        // sets are ordinal there ([StringComparer]::Ordinal) and here.
        private static readonly HashSet<string> ShortTokenElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "ComboBoxItem", "RadioButton", "Button", "CheckBox", "TextBlock", "Label", "Run", "Hyperlink",
            "MenuItem", "TabItem", "SHTabItem", "GroupBox", "Expander",
        };

        private static readonly HashSet<string> ShortTokenAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Content", "Header", "Text", "Title", "#text",
        };

        private static string SkipReason(string value, string elementLocalName, string attribute, HashSet<string> commonSet)
        {
            if (value == null) return "empty";
            string t = value.Trim();
            if (t.Length == 0) return "empty";
            if (t.StartsWith("{", StringComparison.Ordinal)) return "binding";
            if (EqI(t, "True") || EqI(t, "False")) return "identifier";
            int letters = 0, digits = 0;
            foreach (char ch in t)
            {
                if (char.IsLetter(ch)) letters++;
                else if (char.IsDigit(ch)) digits++;
            }
            if (letters == 0) return digits > 0 ? "numeric" : "glyph";
            if (Regex.IsMatch(t, @"\s")) return "";
            if (letters >= 4) return "";
            if (commonSet != null && commonSet.Contains(value)) return "";
            if (letters >= 2 && ShortTokenAttributes.Contains(attribute) && ShortTokenElements.Contains(elementLocalName)) return "";
            return "glyph";
        }

        private static bool EqI(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ConvertTo-LocCollapsedText: what WPF shows for element text.
        private static string Collapse(string text)
            => Regex.Replace(text.Replace("\r\n", "\n"), @"\s+", " ").Trim();

        private static string TypeName(XElement el)
        {
            string prefix = el.GetPrefixOfNamespace(el.Name.Namespace);
            return string.IsNullOrEmpty(prefix) ? el.Name.LocalName : prefix + ":" + el.Name.LocalName;
        }

        private static string AttributeName(XElement el, XAttribute a)
        {
            if (a.Name.Namespace == XNamespace.None) return a.Name.LocalName;
            string prefix = el.GetPrefixOfNamespace(a.Name.Namespace);
            return string.IsNullOrEmpty(prefix) ? a.Name.LocalName : prefix + ":" + a.Name.LocalName;
        }

        // x:Name, else x:Key (Styles), else null.
        private static string OwnName(XElement el)
        {
            var n = el.Attribute(XamlNs + "Name");
            if (n != null) return n.Value;
            var k = el.Attribute(XamlNs + "Key");
            return k?.Value;
        }

        private static string OrdinalStep(XElement el)
        {
            int i = 1;
            foreach (var s in el.ElementsBeforeSelf()) if (s.Name == el.Name) i++;
            return TypeName(el) + "[" + Inv(i) + "]";
        }

        // Get-LocElementPath: the element's own name, else the nearest named
        // ancestor plus an ordinal path down to the element.
        private static string ElementPath(XElement el)
        {
            string own = OwnName(el);
            if (own != null) return own;
            var steps = new List<string>();
            XElement cur = el;
            while (cur != null)
            {
                if (!ReferenceEquals(cur, el))
                {
                    string n = OwnName(cur);
                    if (n != null) { steps.Insert(0, n); break; }
                }
                steps.Insert(0, OrdinalStep(cur));
                cur = cur.Parent;
            }
            return string.Join("/", steps);
        }

        private static string LocalName(string elementType)
        {
            int colon = elementType.IndexOf(':');
            return colon >= 0 ? elementType.Substring(colon + 1) : elementType;
        }

        // The rows plus the parsed document, which the scope checks need.
        private static List<XamlRow> WalkXaml(string absPath, string relPath, HashSet<string> commonSet, out XDocument doc)
        {
            doc = XDocument.Parse(ReadUtf8(absPath), LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            var rows = new List<XamlRow>();
            foreach (var el in doc.Root.DescendantsAndSelf())
            {
                string type = TypeName(el);
                string xname = ElementPath(el);
                bool isSetter = EqI(el.Name.LocalName, "Setter");
                string setterProp = isSetter ? el.Attribute("Property")?.Value : null;

                foreach (var a in el.Attributes())
                {
                    string aname = a.IsNamespaceDeclaration ? "xmlns" : AttributeName(el, a);
                    string col = aname;
                    bool deny;
                    if (isSetter && EqI(a.Name.LocalName, "Value") && a.Name.Namespace == XNamespace.None && setterProp != null)
                    {
                        col = "Setter:" + setterProp;
                        deny = IsDenied(setterProp);
                    }
                    else
                    {
                        deny = IsDenied(aname);
                    }
                    if (deny) continue;
                    rows.Add(new XamlRow
                    {
                        File = relPath, Line = ((IXmlLineInfo)a).LineNumber, ElementType = type, XName = xname,
                        Attribute = col, Value = a.Value, SkipReason = SkipReason(a.Value, el.Name.LocalName, col, commonSet), Element = el,
                    });
                }

                foreach (var n in el.Nodes())
                {
                    if (n.NodeType != XmlNodeType.Text) continue;
                    string raw = ((XText)n).Value;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string collapsed = Collapse(raw);
                    // Line of the first visible character, not of the node start.
                    int lead = raw.Length - raw.TrimStart().Length;
                    int line = ((IXmlLineInfo)n).LineNumber + raw.Substring(0, lead).Count(ch => ch == '\n');
                    rows.Add(new XamlRow
                    {
                        File = relPath, Line = line, ElementType = type, XName = xname,
                        Attribute = "#text", Value = collapsed, SkipReason = SkipReason(collapsed, el.Name.LocalName, "#text", commonSet), Element = el,
                    });
                }
            }
            return rows;
        }

        // validate.ps1's exception: the Text of an input box is a default the
        // code parses back ("2.5 s", "250 ms"), never a translation target.
        private static bool IsInputText(XamlRow r)
        {
            if (r.Attribute != "Text") return false;
            string ln = r.Element.Name.LocalName;
            return EqI(ln, "TextBox") || EqI(ln, "PasswordBox") || EqI(ln, "RichTextBox") || EqI(ln, "ComboBox");
        }

        // ==================================================================
        // Helpers: scopes (decision B, mirror of Test-LocInScope)
        // ==================================================================

        private sealed class ScopeContext
        {
            public XElement FirstTab, LastTab;   // the first and last SHTabItem in document order, or null
        }

        // Get-LocScopeContext.
        private static ScopeContext ScopeContextOf(XDocument doc)
        {
            var ctx = new ScopeContext();
            foreach (var el in doc.Root.Descendants())
            {
                if (!EqI(el.Name.LocalName, "SHTabItem")) continue;
                if (ctx.FirstTab == null) ctx.FirstTab = el;
                ctx.LastTab = el;
            }
            return ctx;
        }

        // Test-LocInResources: under an element whose local name ends with
        // ".Resources" (UserControl.Resources, Grid.Resources).
        private static bool InResources(XElement el)
        {
            foreach (var a in el.Ancestors())
                if (a.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)) return true;
            return false;
        }

        // Get-LocTabItem: the nearest SHTabItem, the element itself included.
        private static XElement TabItemOf(XElement el)
        {
            foreach (var a in el.AncestorsAndSelf())
                if (EqI(a.Name.LocalName, "SHTabItem")) return a;
            return null;
        }

        // Decision B: does this element lie in one of a scoped file's converted
        // scopes? An empty spec set is the whole file. resources:<file> holds
        // every element inside a .Resources element; header:<file> every
        // element before the first SHTabItem in document order (the tab's own
        // ancestors included) that is not inside Resources, or everything
        // outside Resources when the file has no tab; tab:<xName> the
        // SHTabItem of that x:Name and its descendants, a nested tab excluded
        // because the nearest tab wins; trailer:<file> every element after
        // the last SHTabItem that is inside no tab and not inside Resources.
        // The file argument must name the row's file, by file name or
        // repo-relative path. PowerShell compares file names with -eq
        // (case-insensitive) and the tab x:Name with -ceq (case-sensitive),
        // so this does too. A null element (a baseline row whose path no
        // longer resolves) is in no scope.
        private static bool InScope(string fileRel, XElement el, HashSet<string> specs, ScopeContext ctx)
        {
            if (specs.Count == 0) return true;
            if (el == null) return false;
            string fileName = fileRel.Substring(fileRel.LastIndexOf('/') + 1);
            foreach (string spec in specs)
            {
                int colon = spec.IndexOf(':');
                string kind = spec.Substring(0, colon);
                string arg = spec.Substring(colon + 1);
                bool fileMatch = EqI(fileName, arg) || EqI(fileRel, arg.Replace('\\', '/'));
                switch (kind)
                {
                    case "resources":
                        if (fileMatch && InResources(el)) return true;
                        break;
                    case "header":
                        if (fileMatch && !InResources(el) && (ctx.FirstTab == null || el.IsBefore(ctx.FirstTab))) return true;
                        break;
                    case "trailer":
                        // Get-LocSection classifies resources, then tab, then
                        // header or trailer, so a descendant of the last tab
                        // (after it in document order) is a tab row, never a
                        // trailer row.
                        if (fileMatch && ctx.LastTab != null && !InResources(el) && TabItemOf(el) == null && el.IsAfter(ctx.LastTab)) return true;
                        break;
                    case "tab":
                        var tab = TabItemOf(el);
                        string tabName = tab == null ? null : OwnName(tab);
                        if (tabName != null && string.Equals(tabName, arg, StringComparison.Ordinal)) return true;
                        break;
                }
            }
            return false;
        }

        private static readonly Regex OrdinalStepRegex = new Regex(@"^(.+)\[(\d+)\]$");

        // The current element of a baseline row, found by its recorded path
        // (Get-LocElementPath): the first step is an x:Name or x:Key, or the
        // root's "Type[1]"; each further step is "Type[n]" among the
        // children. A template reuses a name ("Bd"), so of several elements
        // carrying the first step's name the one nearest the recorded line is
        // taken. Conversion keeps the tree shape apart from the Run it adds
        // under a Hyperlink, so a step that no longer resolves stops at the
        // deepest element found, whose scope the missing descendant shared.
        // Null when even the first step is gone: the row is then in no scope
        // and test 3 reports it.
        private static XElement LocateBaselineElement(XDocument doc, CsvRow row)
        {
            string[] steps = row.XName.Split('/');
            XElement cur = null;
            var first = OrdinalStepRegex.Match(steps[0]);
            if (first.Success)
            {
                if (TypeName(doc.Root) == first.Groups[1].Value && first.Groups[2].Value == "1") cur = doc.Root;
            }
            else
            {
                int.TryParse(row.Line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int line);
                int best = int.MaxValue;
                foreach (var el in doc.Root.DescendantsAndSelf())
                {
                    if (OwnName(el) != steps[0]) continue;
                    int distance = Math.Abs(((IXmlLineInfo)el).LineNumber - line);
                    if (distance < best) { best = distance; cur = el; }
                }
            }
            if (cur == null) return null;
            for (int s = 1; s < steps.Length; s++)
            {
                var m = OrdinalStepRegex.Match(steps[s]);
                if (!m.Success) break;
                string type = m.Groups[1].Value;
                int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                XElement next = null;
                int i = 0;
                foreach (var child in cur.Elements())
                {
                    if (TypeName(child) != type) continue;
                    if (++i == n) { next = child; break; }
                }
                if (next == null) break;
                cur = next;
            }
            return cur;
        }

        // Where a string lives, independent of the shape convert-xaml.ps1 or a
        // hand conversion leaves behind. Element text becomes a Text attribute
        // on a Run, a TextBlock or a Hyperlink and a Content attribute
        // elsewhere; a Hyperlink's #text keeps the element's own path. Run
        // text, the Run's own text node or its Text attribute alike, is folded
        // into the parent's bucket by stripping the trailing /Run[n], so the
        // parent's #text baseline row and the <Run Text="{loc:T Key}"/> that
        // replaces it under a TextBlock or a Hyperlink land at the same
        // location, and sibling Runs compare as a multiset under their parent
        // whatever ordinal an inserted Run gives them. Applied to both sides.
        private static string NormalizeLocation(string localName, string xName, string attribute)
        {
            string path = xName;
            string attr = attribute;
            if (attr == "#text")
                attr = EqI(localName, "Run") || EqI(localName, "TextBlock") || EqI(localName, "Hyperlink") ? "Text" : "Content";
            if (EqI(localName, "Run") && attr == "Text")
            {
                var m = Regex.Match(path, @"^(.*)/Run\[\d+\]$");
                if (m.Success) path = m.Groups[1].Value;
            }
            return attr + " on " + path;
        }

        private static void AddAt(Dictionary<string, List<string>> map, string location, string value)
        {
            if (!map.TryGetValue(location, out List<string> list)) map[location] = list = new List<string>();
            list.Add(value);
        }

        private static bool TakeAt(Dictionary<string, List<string>> map, string location, string value)
        {
            if (!map.TryGetValue(location, out List<string> list)) return false;
            int i = list.FindIndex(v => string.Equals(v, value, StringComparison.Ordinal));
            if (i < 0) return false;
            list.RemoveAt(i);
            if (list.Count == 0) map.Remove(location);
            return true;
        }

        // ==================================================================
        // Helpers: references in XAML and C#
        // ==================================================================

        private sealed class XamlRef
        {
            public string Where, Key;
            public bool Valid;
        }

        private static List<XamlRef> XamlRefs(string repo)
        {
            var refs = new List<XamlRef>();
            foreach (string abs in PluginFiles(repo, "*.xaml"))
            {
                string rel = RelPath(repo, abs);
                string text = ReadUtf8(abs);
                foreach (Match m in LocRefAny.Matches(text))
                {
                    string key = m.Groups[1].Value.Trim();
                    if (key.StartsWith("Key", StringComparison.Ordinal))
                    {
                        var p = Regex.Match(key, @"^Key\s*=\s*(.*)$");
                        if (p.Success) key = p.Groups[1].Value.Trim();
                    }
                    refs.Add(new XamlRef { Where = rel + ":" + Inv(LineOf(text, m.Index)), Key = key, Valid = KeyRegex.IsMatch(key) });
                }
            }
            return refs;
        }

        private sealed class CsCall
        {
            public string Where, Kind, Key;
            public int ArgsAfter;   // arguments after the key literal, up to the call's closing parenthesis
        }

        private static List<CsCall> CsCalls(string repo)
        {
            var calls = new List<CsCall>();
            foreach (string abs in PluginFiles(repo, "*.cs"))
            {
                string rel = RelPath(repo, abs);
                string code = StripComments(ReadUtf8(abs));
                foreach (Match m in CallRegex.Matches(code))
                {
                    calls.Add(new CsCall
                    {
                        Where = rel + ":" + Inv(LineOf(code, m.Index)),
                        Kind = m.Groups[1].Value,
                        Key = m.Groups[2].Value,
                        ArgsAfter = CountArgsAfter(code, m.Index + m.Length),
                    });
                }
            }
            return calls;
        }

        private sealed class CsBinding
        {
            public string Where, Key;
        }

        // Every new Binding("[Key]") in the plugin's C#, the store-indexer form
        // a code-built label uses instead of assigning a Loc.T result.
        private static List<CsBinding> CsBindings(string repo)
        {
            var binds = new List<CsBinding>();
            foreach (string abs in PluginFiles(repo, "*.cs"))
            {
                string rel = RelPath(repo, abs);
                string code = StripComments(ReadUtf8(abs));
                foreach (Match m in BindingRegex.Matches(code))
                    binds.Add(new CsBinding { Where = rel + ":" + Inv(LineOf(code, m.Index)), Key = m.Groups[1].Value });
            }
            return binds;
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }

        // The highest {n} in a string.Format pattern, or -1 for none. Escaped
        // braces ({{ and }}) are literal; alignment ({0,-8}) and format
        // ({0:F1}) are allowed after the index.
        private static int MaxPlaceholder(string s)
        {
            int max = -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{') { i++; continue; }
                    int j = i + 1, n = 0;
                    bool digits = false;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9') { n = n * 10 + (s[j] - '0'); j++; digits = true; }
                    if (digits && j < s.Length && (s[j] == '}' || s[j] == ',' || s[j] == ':')) max = Math.Max(max, n);
                }
                else if (s[i] == '}' && i + 1 < s.Length && s[i + 1] == '}')
                {
                    i++;
                }
            }
            return max;
        }

        // ---- a lexer-lite for C#: literals and comments, enough to count
        // ---- top-level commas inside one argument list.

        private static bool IsLiteralStart(string s, int i)
        {
            char c = s[i];
            if (c == '"' || c == '\'') return true;
            if (c == '@' || c == '$')
            {
                int j = i;
                while (j < s.Length && (s[j] == '@' || s[j] == '$')) j++;
                return j - i <= 2 && j < s.Length && s[j] == '"';
            }
            return false;
        }

        // Index just past the literal starting at i: a char literal, a regular
        // string (backslash escapes), a verbatim string (doubled quotes) or an
        // interpolated string, whose holes may hold code with literals of
        // their own.
        private static int SkipLiteral(string s, int i)
        {
            bool verbatim = false, interpolated = false;
            while (i < s.Length && (s[i] == '@' || s[i] == '$'))
            {
                if (s[i] == '@') verbatim = true; else interpolated = true;
                i++;
            }
            if (i >= s.Length) return i;
            char quote = s[i];
            i++;
            if (quote == '\'')
            {
                while (i < s.Length && s[i] != '\'')
                {
                    if (s[i] == '\\') i++;
                    i++;
                }
                return Math.Min(i + 1, s.Length);
            }
            if (quote != '"') return i;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                    return i + 1;
                }
                if (c == '\\' && !verbatim) { i += 2; continue; }
                if (interpolated && c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }
                    i++;
                    int depth = 0;
                    while (i < s.Length)
                    {
                        if (IsLiteralStart(s, i)) { i = SkipLiteral(s, i); continue; }
                        char h = s[i];
                        if (h == '{' || h == '(' || h == '[') depth++;
                        else if (h == ')' || h == ']') depth--;
                        else if (h == '}')
                        {
                            if (depth == 0) { i++; break; }
                            depth--;
                        }
                        i++;
                    }
                    continue;
                }
                i++;
            }
            return i;
        }

        // Comments become spaces (newlines kept, so line numbers hold); string
        // and char literals pass through untouched.
        private static string StripComments(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (IsLiteralStart(s, i))
                {
                    int end = SkipLiteral(s, i);
                    sb.Append(s, i, end - i);
                    i = end;
                    continue;
                }
                char c = s[i];
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? s.Length : end + 2;
                    for (; i < end; i++) sb.Append(s[i] == '\n' ? '\n' : ' ');
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // Arguments that follow position pos (just past the key literal) up to
        // the call's closing parenthesis: one per top-level comma. Nested
        // parentheses, brackets, braces and literals are skipped whole, and so
        // is a generic type argument list ("Foo<Dictionary<string, int>>()"):
        // a '<' that directly follows an identifier character opens one when
        // everything up to the matching '>' is type-list text (letters,
        // digits, '_', '.', ',', '?', nested angle brackets, square brackets
        // and whitespace). A params array passed as one variable counts as one
        // argument, and a comparison written without spaces ("a<b, c>d")
        // reads as a generic list; the arity message says to assign such an
        // argument to a local first.
        private static int CountArgsAfter(string code, int pos)
        {
            int depth = 0, count = 0, i = pos;
            while (i < code.Length)
            {
                if (IsLiteralStart(code, i)) { i = SkipLiteral(code, i); continue; }
                char c = code[i];
                if (c == '<' && i > 0 && IsIdentifierChar(code[i - 1]))
                {
                    int end = GenericListEnd(code, i);
                    if (end > 0) { i = end; continue; }
                }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (c == ',' && depth == 0) count++;
                i++;
            }
            return count;
        }

        private static bool IsIdentifierChar(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

        private static bool IsTypeListChar(char c)
            => IsIdentifierChar(c) || c == '.' || c == ',' || c == '?' || c == '[' || c == ']' || char.IsWhiteSpace(c);

        // Index just past the '>' that closes the type argument list opened at
        // open, or -1 when the text up to it is not a type list.
        private static int GenericListEnd(string code, int open)
        {
            int depth = 0;
            for (int j = open; j < code.Length; j++)
            {
                char c = code[j];
                if (c == '<') { depth++; continue; }
                if (c == '>') { depth--; if (depth == 0) return j + 1; continue; }
                if (!IsTypeListChar(c)) return -1;
            }
            return -1;
        }

        // ==================================================================
        // Helpers: test data
        // ==================================================================

        private static string LangJson(string culture, params (string Key, string Value)[] pairs)
        {
            var o = new JObject
            {
                [LocStore.MetaMember] = new JObject { ["culture"] = culture, ["name"] = "Test " + culture },
            };
            foreach (var p in pairs) o[p.Key] = p.Value;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
