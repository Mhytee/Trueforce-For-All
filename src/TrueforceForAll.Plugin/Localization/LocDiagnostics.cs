// What the language runtime says about itself: the startup report in
// SimHub.txt (one line per known language, one for the active one, a Warn
// when a translation is thin or an override names a key English never had),
// the missing-keys file the LOCREPORT access code writes for translators,
// and the PSEUDO / PSEUDOCJK walk that lengthens every visible string and
// measures what no longer fits.
//
// The pseudo walk is a developer tool. It rewrites the live panel's text in
// place and leaves it that way until the panel is rebuilt, which is why it
// only runs from an access code. It covers hard-coded and code-assigned text
// only: a property carrying a live Binding ({loc:T Key}) is never assigned,
// because that would detach the Binding. Bound text is exercised with
// tools\loc\pseudo.ps1 plus LOCLANG qps-ploc.
//
// Design and phases: docs/localization-plan.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin.Localization
{
    internal static class LocDiagnostics
    {
        // Above this share of English keys missing, the active language gets
        // a Warn rather than an Info at start.
        private const int MissingWarnPercent = 5;

        // Startup report.

        /// <summary>One Info line per known language, one for the active one,
        /// and a Warn when the active language misses more than 5 percent of
        /// the English keys or a root override carries a key English does not
        /// know. Known languages: every embedded one plus every root and
        /// shipped\ file on disk.</summary>
        internal static void StartupReport(Action<string> info, Action<string> warn)
        {
            var store = Loc.Instance;
            if (store == null)
            {
                Say(warn, "[TF4ALL] Language runtime is not initialized; the panel shows English.");
                return;
            }

            foreach (string tag in KnownTags(store))
            {
                LocStore.TagSummary s;
                try { s = store.Describe(tag); }
                catch (Exception ex)
                {
                    Say(warn, "[TF4ALL] Language " + tag + ": could not be inspected: " + ex.Message);
                    continue;
                }
                Say(info, string.Format(CultureInfo.InvariantCulture,
                    "[TF4ALL] Language {0}: {1}/{2} keys ({3} missing, {4} unknown to en), overrides {5} (root {0}.json), from {6}",
                    tag, s.DefinedCount, store.EnglishKeyCount, s.Missing.Count, s.Unknown.Count,
                    s.RootOverrides.Count, s.Layers));
                if (s.RootUnknown.Count > 0)
                    Say(warn, "[TF4ALL] Language " + tag + ": root " + tag + ".json names " + s.RootUnknown.Count
                        + " key(s) English does not have, so they can never show: " + FirstFew(s.RootUnknown, 5));
            }

            Say(info, "[TF4ALL] Language active=" + store.ActiveTag + " (source: " + store.ActiveSource + ")");

            try
            {
                if (!string.Equals(store.ActiveTag, LocStore.EnglishTag, StringComparison.OrdinalIgnoreCase)
                    && store.EnglishKeyCount > 0)
                {
                    int missing = store.MissingKeys(store.ActiveTag).Count;
                    if (missing * 100 > store.EnglishKeyCount * MissingWarnPercent)
                        Say(warn, "[TF4ALL] Language " + store.ActiveTag + " lacks " + missing + " of "
                            + store.EnglishKeyCount + " keys; those show in English. LOCREPORT lists them.");
                }
            }
            catch { }
        }

        /// <summary>Every tag worth a report line: the embedded ones, then
        /// whatever root and shipped\ files exist, deduplicated and sorted.</summary>
        internal static IReadOnlyList<string> KnownTags(LocStore store)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { LocStore.EnglishTag };
            try
            {
                foreach (string tag in LocSeed.EmbeddedTags(typeof(LocDiagnostics).Assembly)) set.Add(tag);
            }
            catch { }
            string root = store?.LanguagesRoot;
            if (!string.IsNullOrEmpty(root))
            {
                AddDiskTags(set, root);
                AddDiskTags(set, Path.Combine(root, LocStore.ShippedFolder));
            }
            return new List<string>(set);
        }

        private static void AddDiskTags(SortedSet<string> into, string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (string file in Directory.GetFiles(folder, "*.json"))
                {
                    string tag = Path.GetFileNameWithoutExtension(file);
                    if (LooksLikeTag(tag)) into.Add(tag);
                }
            }
            catch { }
        }

        private static bool LooksLikeTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Length > 32) return false;
            foreach (char c in tag)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) return false;
            }
            return tag[0] != '-' && tag[tag.Length - 1] != '-';
        }

        // Missing-keys report.

        /// <summary>Write missing-keys.&lt;tag&gt;.txt into the language folder:
        /// every English key the tag lacks, as JSON members carrying the
        /// English text, so a translator pastes them into &lt;tag&gt;.json and
        /// replaces the values. Returns the path written.</summary>
        internal static string WriteMissingReport(string tag)
        {
            var store = Loc.Instance;
            if (store == null) throw new InvalidOperationException("The language runtime is not initialized.");
            if (string.IsNullOrEmpty(store.LanguagesRoot)) throw new InvalidOperationException("The language folder is unknown.");

            var s = store.Describe(tag);
            var sb = new StringBuilder();
            sb.Append("Trueforce For All: keys missing from ").Append(s.Tag).AppendLine();
            sb.Append(s.Missing.Count).Append(" of ").Append(store.EnglishKeyCount)
              .Append(" English keys have no ").Append(s.Tag).Append(" text (layers: ").Append(s.Layers).AppendLine(").");
            sb.AppendLine("Each line below is the English text as a JSON member. Copy the lines you translate");
            sb.Append("into ").Append(s.Tag).AppendLine(".json in the folder above this file, inside the object,");
            sb.AppendLine("and replace the value. Keep {0}-style placeholders. Written by the LOCREPORT code.");
            sb.AppendLine();
            foreach (string key in s.Missing)
            {
                string english;
                if (!store.TryGetEnglish(key, out english)) english = string.Empty;
                sb.Append("  \"").Append(key).Append("\": ").Append(JsonString(english)).AppendLine(",");
            }
            if (s.Unknown.Count > 0)
            {
                sb.AppendLine();
                sb.Append(s.Unknown.Count).AppendLine(" key(s) this language defines that English does not (a typo, or a key that was renamed):");
                foreach (string key in s.Unknown) sb.Append("  ").AppendLine(key);
            }

            Directory.CreateDirectory(store.LanguagesRoot);
            string path = Path.Combine(store.LanguagesRoot, "missing-keys." + s.Tag + ".txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static string JsonString(string text)
        {
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Pseudo-localization walk.

        // Hangul syllables and Han characters, cycled to fill a string of the
        // same length: exercises font fallback and line height, which the
        // accented Latin walk cannot.
        private const string CjkSample = "한국어텍스트확인漢字測試字符串언어검사中文界面測試";

        /// <summary>Rewrite every visible string under root (TextBlock.Text,
        /// string Content, string Header, string ToolTip) that is not carried
        /// by a live Binding to a bracketed, lengthened pseudo-translation,
        /// then, once layout has settled,
        /// measure every TextBlock and ContentPresenter and log each one that
        /// no longer fits as "[TF4ALL] pseudo-clip ...", plus one summary line.
        /// Returns how many strings were rewritten; onMeasured receives
        /// (clipped, measured) when the deferred pass finishes.</summary>
        internal static int PseudoWalk(DependencyObject root, bool cjk, Action<string> log, Action<int, int> onMeasured)
        {
            if (root == null) return 0;
            int rewritten = 0;
            var visited = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!visited.Add(d)) continue;
                try { rewritten += Rewrite(d, cjk); } catch { }
                PushChildren(d, stack);
            }

            string mode = cjk ? "cjk" : "latin";
            int count = rewritten;
            Action measure = () =>
            {
                int clipped = 0, measured = 0;
                try
                {
                    Measure(root, log, ref clipped, ref measured);
                }
                catch (Exception ex)
                {
                    Say(log, "[TF4ALL] pseudo-clip measurement failed: " + ex.Message);
                }
                Say(log, string.Format(CultureInfo.InvariantCulture,
                    "[TF4ALL] pseudo-clip summary: {0} of {1} texts clipped ({2}, {3} strings rewritten)",
                    clipped, measured, mode, count));
                try { onMeasured?.Invoke(clipped, measured); } catch { }
            };
            try
            {
                root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, measure);
            }
            catch
            {
                measure();
            }
            return rewritten;
        }

        // The visual tree only holds what is realized (the selected tab); the
        // logical tree reaches the other tabs' content too, so both are
        // walked and deduplicated. Popups and context menus are neither.
        private static void PushChildren(DependencyObject d, Stack<DependencyObject> stack)
        {
            try
            {
                if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                {
                    int n = VisualTreeHelper.GetChildrenCount(d);
                    for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
                }
            }
            catch { }
            try
            {
                foreach (object child in LogicalTreeHelper.GetChildren(d))
                {
                    var cd = child as DependencyObject;
                    if (cd != null) stack.Push(cd);
                }
            }
            catch { }
        }

        private static int Rewrite(DependencyObject d, bool cjk)
        {
            int n = 0;
            if (IsGlyphFont(d)) return 0;

            var tb = d as TextBlock;
            if (tb != null && RewriteTextBlock(tb, cjk)) n++;

            var cc = d as ContentControl;
            if (cc != null)
            {
                var content = cc.Content as string;
                if (content != null && !ShouldSkip(content) && !IsBound(cc, ContentControl.ContentProperty))
                {
                    cc.Content = Wrap(content, cjk);
                    n++;
                }
                var hcc = d as HeaderedContentControl;
                var header = hcc?.Header as string;
                if (header != null && !ShouldSkip(header) && !IsBound(hcc, HeaderedContentControl.HeaderProperty))
                {
                    hcc.Header = Wrap(header, cjk);
                    n++;
                }
            }

            var fe = d as FrameworkElement;
            var tip = fe?.ToolTip as string;
            if (tip != null && !ShouldSkip(tip) && !IsBound(fe, FrameworkElement.ToolTipProperty))
            {
                fe.ToolTip = Wrap(tip, cjk);
                n++;
            }
            return n;
        }

        // A property carrying a live Binding ({loc:T Key}, or any other) is
        // left alone: assigning a value would detach the Binding and leave
        // that label stale until the panel is rebuilt. Bound text is
        // exercised with tools\loc\pseudo.ps1 plus LOCLANG qps-ploc instead.
        private static bool IsBound(DependencyObject d, DependencyProperty dp)
        {
            try
            {
                return System.Windows.Data.BindingOperations.GetBindingExpressionBase(d, dp) != null;
            }
            catch
            {
                return true;
            }
        }

        // A TextBlock with one Run is rewritten through Text. One with several
        // inlines (a Hyperlink, a Bold) keeps its structure: each Run is
        // transformed in place and the brackets go on the first and last.
        private static bool RewriteTextBlock(TextBlock tb, bool cjk)
        {
            var runs = new List<Run>();
            CollectRuns(tb.Inlines, runs);
            if (runs.Count <= 1)
            {
                // Setting Text replaces the inlines too, so a Binding on
                // either the TextBlock or its one Run keeps the whole block.
                if (IsBound(tb, TextBlock.TextProperty)) return false;
                if (runs.Count == 1 && IsBound(runs[0], Run.TextProperty)) return false;
                string text = tb.Text;
                if (ShouldSkip(text)) return false;
                tb.Text = Wrap(text, cjk);
                return true;
            }
            string whole = tb.Text;
            if (ShouldSkip(whole)) return false;
            // Bound and empty runs are skipped; the brackets go on the first
            // and last run that is actually rewritten.
            int first = -1, last = -1;
            for (int i = 0; i < runs.Count; i++)
            {
                if (string.IsNullOrEmpty(runs[i].Text) || IsBound(runs[i], Run.TextProperty)) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) return false;
            for (int i = first; i <= last; i++)
            {
                string t = runs[i].Text;
                if (string.IsNullOrEmpty(t) || IsBound(runs[i], Run.TextProperty)) continue;
                string p = string.IsNullOrWhiteSpace(t) ? t : Pseudo(t, cjk);
                if (i == first) p = "[" + p;
                if (i == last) p = p + "]";
                runs[i].Text = p;
            }
            return true;
        }

        private static void CollectRuns(InlineCollection inlines, List<Run> into)
        {
            if (inlines == null) return;
            foreach (Inline inline in inlines)
            {
                var run = inline as Run;
                if (run != null) { into.Add(run); continue; }
                var span = inline as Span;
                if (span != null) CollectRuns(span.Inlines, into);
            }
        }

        private static bool IsGlyphFont(DependencyObject d)
        {
            FontFamily family = null;
            var control = d as Control;
            if (control != null) family = control.FontFamily;
            var tb = d as TextBlock;
            if (tb != null) family = tb.FontFamily;
            string source = family?.Source;
            if (string.IsNullOrEmpty(source)) return false;
            return source.IndexOf("MDL2", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("Segoe Fluent", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Skip what is not prose: empty, a "{...}" placeholder or unresolved
        // markup, a "[...]" that is a missing key or an earlier pseudo pass, a
        // single character, or glyph-only text (icons drawn from a symbol font
        // that was not caught by name).
        private static bool ShouldSkip(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string t = text.Trim();
            if (t.Length <= 1) return true;
            if (t[0] == '{') return true;
            if (t[0] == '[' && t[t.Length - 1] == ']') return true;
            foreach (char c in t)
                if (char.IsLetterOrDigit(c)) return false;
            return true;
        }

        private static string Wrap(string text, bool cjk) => "[" + Pseudo(text, cjk) + "]";

        // Latin: accent swap plus "~" padding to 130 percent of the length, so a
        // label that only just fits in English shows its seams. CJK: a fill of
        // the same length from the sample, whitespace kept so words still wrap.
        internal static string Pseudo(string text, bool cjk)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length + text.Length / 3 + 2);
            if (cjk)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    sb.Append(char.IsWhiteSpace(c) ? c : CjkSample[i % CjkSample.Length]);
                }
                return sb.ToString();
            }
            foreach (char c in text)
            {
                switch (c)
                {
                    case 'a': sb.Append('á'); break;
                    case 'e': sb.Append('é'); break;
                    case 'i': sb.Append('í'); break;
                    case 'o': sb.Append('ó'); break;
                    case 'u': sb.Append('ú'); break;
                    case 'n': sb.Append('ñ'); break;
                    case 's': sb.Append('š'); break;
                    default: sb.Append(c); break;
                }
            }
            int target = (int)Math.Ceiling(text.Length * 1.3);
            if (target > text.Length) sb.Append('~', target - text.Length);
            return sb.ToString();
        }

        // Second pass, after layout: the tree is walked again because the
        // ContentPresenters regenerated their TextBlocks when Content changed.
        private static void Measure(DependencyObject root, Action<string> log, ref int clipped, ref int measured)
        {
            var visited = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!visited.Add(d)) continue;
                try
                {
                    var tb = d as TextBlock;
                    if (tb != null && tb.IsVisible && tb.ActualWidth > 0 && !string.IsNullOrEmpty(tb.Text))
                    {
                        measured++;
                        if (MeasureTextBlock(tb, log)) clipped++;
                    }
                    var cp = d as ContentPresenter;
                    if (cp != null && cp.IsVisible && cp.ActualWidth > 0)
                    {
                        double needed = cp.DesiredSize.Width - cp.Margin.Left - cp.Margin.Right;
                        if (needed > cp.ActualWidth + 0.5)
                        {
                            clipped++;
                            Say(log, string.Format(CultureInfo.InvariantCulture,
                                "[TF4ALL] pseudo-clip {0} width={1:F0} needed={2:F0} text={3}",
                                Describe(cp), cp.ActualWidth, needed, Snip(cp.Content as string ?? "")));
                        }
                    }
                }
                catch { }
                try
                {
                    if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    {
                        int n = VisualTreeHelper.GetChildrenCount(d);
                        for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
                    }
                }
                catch { }
            }
        }

        // The FormattedText pattern MotdStrip.IsBodyTextTrimmed uses: the
        // single-line width the text wants against the width the layout gave.
        // A wrapping TextBlock is judged on height instead, with the same
        // available width.
        private static bool MeasureTextBlock(TextBlock tb, Action<string> log)
        {
            string text = tb.Text ?? string.Empty;
            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            double pixelsPerDip = VisualTreeHelper.GetDpi(tb).PixelsPerDip;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                typeface, tb.FontSize, Brushes.Black, pixelsPerDip);
            double avail = Math.Max(0, tb.ActualWidth - tb.Padding.Left - tb.Padding.Right);

            if (tb.TextWrapping == TextWrapping.NoWrap)
            {
                double needed = ft.WidthIncludingTrailingWhitespace;
                if (needed <= avail + 0.5) return false;
                Say(log, string.Format(CultureInfo.InvariantCulture,
                    "[TF4ALL] pseudo-clip {0} width={1:F0} needed={2:F0} text={3}",
                    Describe(tb), avail, needed, Snip(text)));
                return true;
            }

            if (tb.ActualHeight <= 0) return false;
            ft.MaxTextWidth = Math.Max(1, avail);
            double availHeight = Math.Max(0, tb.ActualHeight - tb.Padding.Top - tb.Padding.Bottom);
            double neededHeight = ft.Height;
            if (neededHeight <= availHeight + 1.0) return false;
            Say(log, string.Format(CultureInfo.InvariantCulture,
                "[TF4ALL] pseudo-clip {0} width={1:F0} height={2:F0} needed={3:F0} (wraps) text={4}",
                Describe(tb), avail, availHeight, neededHeight, Snip(text)));
            return true;
        }

        // x:Name when the element has one, else the nearest named ancestor
        // and the type path down from it: "GainRow/StackPanel/TextBlock".
        private static string Describe(DependencyObject d)
        {
            var fe = d as FrameworkElement;
            if (fe != null && !string.IsNullOrEmpty(fe.Name)) return fe.Name;
            var parts = new List<string> { d.GetType().Name };
            DependencyObject cur = d;
            for (int hops = 0; hops < 8; hops++)
            {
                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur); } catch { }
                if (parent == null) break;
                var pfe = parent as FrameworkElement;
                if (pfe != null && !string.IsNullOrEmpty(pfe.Name))
                {
                    parts.Insert(0, pfe.Name);
                    break;
                }
                parts.Insert(0, parent.GetType().Name);
                cur = parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private static string Snip(string text)
        {
            string t = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return t.Length <= 60 ? t : t.Substring(0, 57) + "...";
        }

        private static string FirstFew(IReadOnlyList<string> keys, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count && i < max; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(keys[i]);
            }
            if (keys.Count > max) sb.Append(", ...");
            return sb.ToString();
        }

        private static void Say(Action<string> sink, string message)
        {
            try { sink?.Invoke(message); } catch { }
        }
    }
}
