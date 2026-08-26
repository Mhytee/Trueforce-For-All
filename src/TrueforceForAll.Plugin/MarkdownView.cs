// The Markdown renderer, shared by everything in the plugin that shows authored
// text: the update dialog's release notes, the bundled changelog, and the guides
// behind the header's "?" button.
//
// Lifted out of SettingsControl.xaml.cs unchanged (bar the rename) when the
// guides started needing it. It had been sitting in a 13,000-line file as a
// private helper of the settings panel, which is not where a second caller can
// find it, and a second renderer is how two parts of one app start disagreeing
// about what bold means.
//
// Deliberately not a real Markdown parser and deliberately no dependency: net48
// plugin land, and the syntax authored here is a known, small set. What it
// knows: headings 1-3, bullet lists (with a two-tier **headline:** description
// shape), ordered lists, blockquote callouts including GitHub's alert markers,
// inline bold, and links. Image lines are elided with one pointer, since this
// renders text only. Anything else falls through as plain text rather than
// showing raw markup.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal static class MarkdownView
    {
        // Render a GitHub-flavored Markdown release body as a stack of styled
        // TextBlocks. Supports headings (#..######) and bullets (- / *); other
        // syntax falls through as plain text. We don't pull in a real markdown
        // parser because the release notes only ever use these two constructs
        // and we want zero added dependencies in net48 plugin land.
        internal static StackPanel Render(string body, Action<string> onGuideLink = null,
                                          double scale = 1.0)
        {
            var panel = new StackPanel();
            if (string.IsNullOrWhiteSpace(body))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "(No release notes published.)",
                    FontSize = 12 * scale,
                    Opacity = 0.7,
                });
                return panel;
            }

            // Normalize line endings: GitHub bodies usually arrive with \r\n.
            string[] lines = body.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            bool prevWasBlank = false;
            bool imageNoteShown = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? "";
                string trimmed = raw.TrimStart();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // Collapse runs of blank lines into a single small gap.
                    if (!prevWasBlank && panel.Children.Count > 0)
                    {
                        panel.Children.Add(new TextBlock { Height = 6 });
                        prevWasBlank = true;
                    }
                    continue;
                }
                prevWasBlank = false;

                // Markdown images (![alt](url), or link-wrapped [![...]) and
                // the HTML ones GitHub writes when you drag a file into the
                // release editor (<img src="...user-attachments/...">, and
                // <video> / <picture> for the same reason).
                // This renderer is text-only (and WPF wouldn't animate a
                // release GIF anyway), so image lines are dropped instead of
                // showing as raw markup. One dim pointer per body tells
                // in-app readers where the visuals live.
                if (trimmed.StartsWith("![", StringComparison.Ordinal)
                    || trimmed.StartsWith("[![", StringComparison.Ordinal)
                    || trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("<video", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("<picture", StringComparison.OrdinalIgnoreCase))
                {
                    if (!imageNoteShown)
                    {
                        imageNoteShown = true;
                        panel.Children.Add(new TextBlock
                        {
                            Text = "(screenshots on the GitHub release page)",
                            FontSize = 11 * scale,
                            Opacity = 0.55,
                            Margin = new Thickness(0, 0, 0, 2),
                        });
                    }
                    continue;
                }

                // Blockquote ("> ..." lines), including GitHub alert callouts
                // ("> [!WARNING]" etc.). Consecutive quote lines collapse into
                // one left-accented callout box; the quoted content re-enters
                // this renderer, so headers/bullets/bold inside it work.
                if (trimmed[0] == '>')
                {
                    var quoted = new System.Collections.Generic.List<string>();
                    int j = i;
                    while (j < lines.Length)
                    {
                        string q = (lines[j] ?? "").TrimStart();
                        if (q.Length == 0 || q[0] != '>') break;
                        string innerLine = q.Substring(1);
                        if (innerLine.StartsWith(" ", StringComparison.Ordinal))
                            innerLine = innerLine.Substring(1);
                        quoted.Add(innerLine);
                        j++;
                    }
                    panel.Children.Add(BuildQuoteCallout(quoted, onGuideLink, scale));
                    i = j - 1;   // loop ++ lands on the first non-quote line
                    continue;
                }

                // Heading levels 1..3 (deeper levels fall through to plain).
                int hashCount = 0;
                while (hashCount < trimmed.Length && trimmed[hashCount] == '#') hashCount++;
                if (hashCount >= 1 && hashCount <= 3
                    && hashCount < trimmed.Length && trimmed[hashCount] == ' ')
                {
                    string text = trimmed.Substring(hashCount + 1).Trim();
                    double size = (hashCount == 1 ? 16 : hashCount == 2 ? 14 : 13) * scale;
                    var hdr = new TextBlock
                    {
                        FontSize = size,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 10, 0, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    // Through the inline renderer like every other branch, rather
                    // than Text = text. Release notes never put markup in a heading
                    // so this went unnoticed for as long as this was only a
                    // release-notes renderer; a guide heading carrying a link
                    // printed its own markdown on screen.
                    AppendInlineMarkdown(hdr, text, onGuideLink);
                    // Gold the section (###) headers to match the bundled
                    // changelog's grouped look; keep the title (#/##) default.
                    if (hashCount >= 3)
                        hdr.Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x86, 0x0B));
                    panel.Children.Add(hdr);
                    continue;
                }

                // Bullet rows ("- foo" / "* foo"). Use a real bullet glyph
                // indented one step. Inline **bold** spans get rendered as
                // bold runs so release notes like "- **Headline.** desc"
                // don't show literal asterisks.
                if (trimmed.Length >= 2
                    && (trimmed[0] == '-' || trimmed[0] == '*')
                    && trimmed[1] == ' ')
                {
                    string content = trimmed.Substring(2);
                    // Two-tier: when the bullet opens with a **bold** lead-in
                    // (our "- **Headline:** description" shape), render the
                    // headline as a bulleted bold line and the rest as a dimmed,
                    // indented description line, echoing the bundled changelog.
                    //
                    // The bold run must CLOSE on ':' or '.', which is what makes it
                    // a headline rather than a bolded first few words. Without that
                    // test this fired on any bullet opening with emphasis, and a
                    // guide line like "- **Forza Horizon 4, 5 and 6**. Opt-in, per
                    // game." lost half its sentence to a dimmed 11px footnote,
                    // sometimes starting on a stranded comma. Release notes are
                    // unaffected: the convention there puts the punctuation inside
                    // the bold, which is exactly what this now requires.
                    if (content.StartsWith("**", StringComparison.Ordinal))
                    {
                        int close = content.IndexOf("**", 2, StringComparison.Ordinal);
                        if (close > 2
                            && (content[close - 1] == ':' || content[close - 1] == '.'))
                        {
                            string headline = content.Substring(2, close - 2);
                            string desc = content.Substring(close + 2).TrimStart();
                            var hl = new TextBlock
                            {
                                FontSize = 12 * scale,
                                Margin = new Thickness(8, 4, 0, 0),
                                TextWrapping = TextWrapping.Wrap,
                            };
                            hl.Inlines.Add(new Run("• "));
                            // Through the inline renderer (re-wrapped in **
                            // so it keeps the bold weight) instead of a raw
                            // bold Run: headlines can carry links, like the
                            // bold Patreon link in the v0.2.1 warning.
                            AppendInlineMarkdown(hl, "**" + headline + "**", onGuideLink);
                            panel.Children.Add(hl);
                            if (desc.Length > 0)
                            {
                                var db = new TextBlock
                                {
                                    FontSize = 11 * scale,
                                    Opacity = 0.7,
                                    Margin = new Thickness(22, 2, 0, 0),
                                    TextWrapping = TextWrapping.Wrap,
                                };
                                AppendInlineMarkdown(db, desc, onGuideLink);
                                panel.Children.Add(db);
                            }
                            continue;
                        }
                    }
                    var tb = new TextBlock
                    {
                        FontSize = 12 * scale,
                        Margin = new Thickness(8, 2, 0, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    tb.Inlines.Add(new Run("• "));
                    AppendInlineMarkdown(tb, content, onGuideLink);
                    panel.Children.Add(tb);
                    continue;
                }

                // Ordered rows ("1. foo"). The author's own number is kept
                // rather than recounted, so a list that continues after a
                // paragraph still reads 4, 5, 6. Same inline treatment as
                // bullets; the guides use these for step-by-step fixes.
                int dot = trimmed.IndexOf('.');
                if (dot > 0 && dot <= 3 && dot + 2 <= trimmed.Length - 1
                    && trimmed[dot + 1] == ' ' && AllDigits(trimmed, dot))
                {
                    var ol = new TextBlock
                    {
                        FontSize = 12 * scale,
                        Margin = new Thickness(8, 2, 0, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    ol.Inlines.Add(new Run(trimmed.Substring(0, dot + 1) + " "));
                    AppendInlineMarkdown(ol, trimmed.Substring(dot + 2), onGuideLink);
                    panel.Children.Add(ol);
                    continue;
                }

                // Plain paragraph line. Same **bold** treatment as bullets.
                var para = new TextBlock
                {
                    FontSize = 12 * scale,
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                };
                AppendInlineMarkdown(para, trimmed, onGuideLink);
                panel.Children.Add(para);
            }
            // Leading, set in one place rather than on every block above. WPF's
            // default line box is tight for anything longer than a release-note
            // bullet, and the guides are documents. Quote callouts pick it up by
            // re-entering Render.
            foreach (var child in panel.Children)
            {
                var tb = child as TextBlock;
                if (tb == null || tb.FontSize <= 0) continue;
                tb.LineHeight = Math.Round(tb.FontSize * 1.45);
                tb.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            }
            return panel;
        }

        // A "> quoted" block as a left-accented callout box. A GitHub alert
        // marker ("[!WARNING]" etc.) as the first quoted line picks the title
        // and accent color, echoing how GitHub renders it; a plain quote gets
        // a neutral bar and no title. Content re-enters Render,
        // so everything the renderer knows works inside the box too.
        private static Border BuildQuoteCallout(System.Collections.Generic.List<string> quoted, Action<string> onGuideLink,
                                                double scale)
        {
            string title  = null;
            Color  accent = Color.FromRgb(0x88, 0x88, 0x88);
            int start = 0;
            string first = null;
            for (int k = 0; k < quoted.Count; k++)
            {
                if (!string.IsNullOrWhiteSpace(quoted[k])) { first = quoted[k].Trim(); start = k; break; }
            }
            if (first != null
                && first.StartsWith("[!", StringComparison.Ordinal)
                && first.EndsWith("]", StringComparison.Ordinal))
            {
                switch (first.Substring(2, first.Length - 3).ToUpperInvariant())
                {
                    case "WARNING":   title = "Warning";   accent = Color.FromRgb(0xE5, 0xC0, 0x4A); break;
                    case "CAUTION":   title = "Caution";   accent = Color.FromRgb(0xE0, 0x62, 0x5A); break;
                    case "IMPORTANT": title = "Important"; accent = Color.FromRgb(0xB0, 0x87, 0xE8); break;
                    case "NOTE":      title = "Note";      accent = Color.FromRgb(0x6C, 0xA0, 0xDD); break;
                    case "TIP":       title = "Tip";       accent = Color.FromRgb(0x5F, 0xB8, 0x6A); break;
                }
                if (title != null) start++;   // the marker line itself isn't content
            }

            StackPanel inner;
            string bodyText = string.Join("\n", quoted.Skip(start));
            inner = string.IsNullOrWhiteSpace(bodyText) ? new StackPanel() : Render(bodyText, onGuideLink, scale);
            if (title != null)
            {
                inner.Children.Insert(0, new TextBlock
                {
                    Text = title,
                    FontSize = 12 * scale,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(accent),
                    Margin = new Thickness(0, 0, 0, 2),
                });
            }
            return new Border
            {
                BorderBrush = new SolidColorBrush(accent),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x16, accent.R, accent.G, accent.B)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 6, 0, 6),
                Child = inner,
            };
        }

        // Append `text` to a TextBlock's Inlines, rendering **bold** runs in
        // bold and [label](https://url) as clickable links. Anything outside
        // those is plain. An unclosed `**` stays literal rather than being
        // dropped, so a body that opens bold without closing degrades
        // gracefully; a malformed or non-http link stays literal too. Links
        // may sit inside bold spans (and carry the bold weight); bold markers
        // inside a link label are consumed as styling, not shown.
        internal static void AppendInlineMarkdown(TextBlock tb, string text, Action<string> onGuideLink = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool bold = false;
            var sb = new System.Text.StringBuilder();
            void Flush()
            {
                if (sb.Length == 0) return;
                tb.Inlines.Add(new Run(sb.ToString())
                {
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                });
                sb.Clear();
            }
            int i = 0;
            while (i < text.Length)
            {
                // `code spans`, in a monospace face. Added for the guides, which
                // quote things the reader has to type exactly: a file path, a
                // config line, a launch option. Without this the backticks
                // rendered on screen, sitting either side of the one string most
                // likely to be copied by hand. An unclosed backtick stays literal,
                // same rule as an unclosed bold marker.
                if (text[i] == '`')
                {
                    int closeTick = text.IndexOf('`', i + 1);
                    if (closeTick > i + 1)
                    {
                        Flush();
                        tb.Inlines.Add(new Run(text.Substring(i + 1, closeTick - i - 1))
                        {
                            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                            // Bold survives across a code span, so a **bold `path`**
                            // reads as one phrase rather than losing weight midway.
                            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xC6, 0x9A)),
                        });
                        i = closeTick + 1;
                        continue;
                    }
                }
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    // Opening bold needs a closer somewhere ahead; otherwise
                    // the marker is literal text.
                    if (!bold && text.IndexOf("**", i + 2, StringComparison.Ordinal) < 0)
                    {
                        sb.Append("**");
                        i += 2;
                        continue;
                    }
                    Flush();
                    bold = !bold;
                    i += 2;
                    continue;
                }
                if (text[i] == '[')
                {
                    int closeBracket = text.IndexOf(']', i + 1);
                    if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket + 2);
                        if (closeParen > closeBracket)
                        {
                            string label = text.Substring(i + 1, closeBracket - i - 1).Replace("**", "");
                            string url   = text.Substring(closeBracket + 2, closeParen - closeBracket - 2);
                            // [label](guide:some-key): a cross-reference to another
                            // guide, which the browser turns into a selection rather
                            // than a navigation. Guides refer to each other
                            // constantly ("the iRacing setup guide walks through
                            // both switches"), and a reference the reader has to go
                            // and find by hand is a reference they will not follow.
                            //
                            // Without a handler (release notes, or the step lists
                            // rendered into the settings panel) the label renders as
                            // plain text: still a readable sentence, just not
                            // clickable, which is the right degradation for a
                            // context where there is nothing to navigate to.
                            if (label.Length > 0
                                && url.StartsWith("guide:", StringComparison.OrdinalIgnoreCase))
                            {
                                Flush();
                                string key = url.Substring(6);
                                if (onGuideLink == null)
                                {
                                    tb.Inlines.Add(new Run(label)
                                    {
                                        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                                    });
                                }
                                else
                                {
                                    var glink = new System.Windows.Documents.Hyperlink(new Run(label)
                                    {
                                        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                                    })
                                    {
                                        Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xEE)),
                                    };
                                    glink.Click += (s, e) => onGuideLink(key);
                                    tb.Inlines.Add(glink);
                                }
                                i = closeParen + 1;
                                continue;
                            }
                            if (label.Length > 0
                                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                            {
                                Flush();
                                var link = new System.Windows.Documents.Hyperlink(new Run(label)
                                {
                                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                                })
                                {
                                    NavigateUri = uri,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xEE)),
                                };
                                link.RequestNavigate += (s, e) =>
                                {
                                    try
                                    {
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                                            e.Uri.AbsoluteUri) { UseShellExecute = true });
                                    }
                                    catch { }
                                };
                                tb.Inlines.Add(link);
                                i = closeParen + 1;
                                continue;
                            }
                        }
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            Flush();
        }

        // True when text[0..count) is all digits. Guards the ordered-list match
        // so a sentence opening "Mr. Smith" or "v1. something" is not a list.
        private static bool AllDigits(string text, int count)
        {
            for (int k = 0; k < count; k++)
                if (text[k] < '0' || text[k] > '9') return false;
            return true;
        }
    }
}
