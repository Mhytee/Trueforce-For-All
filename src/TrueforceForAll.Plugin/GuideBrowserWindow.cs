// The help browser behind the header's "?" button.
//
// One window, list on the left, guide on the right. It replaced a context menu
// whose entries were of two different kinds: some opened a document, some
// silently navigated the panel somewhere. Both arrived through the same click,
// which meant the menu could not be browsed. You had to already know which
// entries were safe to look at.
//
// So every entry is a document now, and going somewhere is always the SECOND
// step: read the guide, then press its action button. Nothing navigates behind
// the reader's back, and the action runs after the window closes so the panel
// is not being rearranged underneath an open modal.
//
// Guides are Markdown, embedded as Guides\*.md and rendered by MarkdownView.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    /// <summary>One row in the help browser. <see cref="Key"/> names the
    /// Guides\{key}.md that supplies the text.</summary>
    internal sealed class GuideEntry
    {
        public string Key;
        public string Group;
        public string Title;

        /// <summary>Null for a guide with nowhere to go. When set, the button
        /// appears under the guide and <see cref="Action"/> runs after the
        /// window has closed.</summary>
        public string ActionLabel;
        public Action Action;

        /// <summary>Null means always listed. Used for guides whose subject is
        /// not present on this install, which would otherwise be a document
        /// about controls the reader cannot open.</summary>
        public Func<bool> Visible;

        /// <summary>Appended to the file's text, for the lines a document cannot
        /// know (a live telemetry rate, for instance).</summary>
        public Func<string> Extra;
    }

    internal sealed class GuideBrowserWindow : Window
    {
        private static readonly Brush WindowBg     = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg      = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
        private static readonly Brush TextFg       = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg      = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFgGold = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush RowHover     = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        private static readonly Brush RowSelected  = new SolidColorBrush(Color.FromArgb(0x28, 0xE5, 0xC0, 0x4A));

        private readonly List<GuideEntry> _entries;
        /// <summary>What the list is showing right now: all of _entries, or the
        /// subset matching the search box. Arrow keys walk THIS, so they never
        /// step onto a row that is filtered out.</summary>
        private readonly List<GuideEntry> _shown = new List<GuideEntry>();
        /// <summary>Guide text, loaded once, so searching is a string scan rather
        /// than thirteen resource reads per keystroke.</summary>
        private readonly Dictionary<GuideEntry, string> _bodies = new Dictionary<GuideEntry, string>();
        private readonly Dictionary<GuideEntry, Border> _rows = new Dictionary<GuideEntry, Border>();
        private StackPanel _listPanel;
        private TextBlock _noMatches;
        private readonly TextBlock _title;
        private readonly ScrollViewer _bodyScroll;
        private readonly Button _actionButton;
        private GuideEntry _selected;

        /// <summary>Guides render a notch larger than release notes: 13px body
        /// against the renderer's 12. These are documents in a 620px column, not
        /// bullets in a 460px dialog.</summary>
        private const double GuideTextScale = 13.0 / 12.0;

        /// <summary>The action the reader chose, or null. Run by the CALLER after
        /// this returns, so navigation happens with the window already gone.</summary>
        private Action _chosen;

        /// <summary>Open the browser. <paramref name="initialKey"/> deep-links to
        /// one guide (the "why?" links beside individual settings); omit it to
        /// land on the first entry.</summary>
        internal static void Show(Window owner, IEnumerable<GuideEntry> entries, string initialKey = null)
        {
            var list = new List<GuideEntry>();
            foreach (var e in entries)
                if (e != null && (e.Visible == null || e.Visible())) list.Add(e);
            if (list.Count == 0) return;

            var w = new GuideBrowserWindow(list, initialKey);
            if (owner != null) w.Owner = owner;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowDialog();
            // After the window is down, never while it is up.
            try { w._chosen?.Invoke(); } catch { }
        }

        private GuideBrowserWindow(List<GuideEntry> entries, string initialKey)
        {
            _entries = entries;
            Title = "Trueforce For All: guides";
            Background = WindowBg;
            Foreground = TextFg;
            Width = 880;
            Height = Math.Min(660, Math.Max(420, SystemParameters.WorkArea.Height * 0.8));
            MinWidth = 620;
            MinHeight = 380;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(268) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Content = root;

            // ---- left: search box, then the list --------------------------------
            var left = new Grid { Background = PanelBg };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(left, 0);
            root.Children.Add(left);

            // Searches titles AND bodies, which is the whole point: the titles are
            // topic-shaped and the questions people arrive with are symptom-shaped.
            // Nobody types "rev lights and wheel screen"; they type G HUB, app.ini,
            // USBPcap, lime, 5300, and every one of those lives in a body.
            var searchHost = new Grid { Margin = new Thickness(10, 10, 10, 6) };
            Grid.SetRow(searchHost, 0);
            left.Children.Add(searchHost);

            var search = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                Foreground = TextFg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 4, 7, 4),
                FontSize = 12,
                CaretBrush = TextFg,
            };
            var watermark = new TextBlock
            {
                Text = "Search guides",
                Foreground = MutedFg,
                FontSize = 12,
                Margin = new Thickness(9, 5, 0, 0),
                IsHitTestVisible = false,
            };
            search.TextChanged += (s2, e2) =>
            {
                watermark.Visibility = search.Text.Length == 0
                    ? Visibility.Visible : Visibility.Collapsed;
                ApplyFilter(search.Text);
            };
            searchHost.Children.Add(search);
            searchHost.Children.Add(watermark);

            var listScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 4, 0, 10),
            };
            Grid.SetRow(listScroll, 1);
            left.Children.Add(listScroll);

            _listPanel = new StackPanel();
            listScroll.Content = _listPanel;

            _noMatches = new TextBlock
            {
                Foreground = MutedFg,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 8, 14, 0),
                Visibility = Visibility.Collapsed,
            };

            foreach (var e in _entries)
                _bodies[e] = GuideText.Load(e.Key) ?? "";
            ApplyFilter(null);

            // ---- right: the guide ----------------------------------------------
            var right = new Grid { Margin = new Thickness(20, 16, 20, 14) };
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            _title = new TextBlock
            {
                Foreground = HeaderFgGold,
                FontWeight = FontWeights.SemiBold,
                FontSize = 17,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            };
            Grid.SetRow(_title, 0);
            right.Children.Add(_title);

            _bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Foreground = TextFg,
            };
            Grid.SetRow(_bodyScroll, 1);
            right.Children.Add(_bodyScroll);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            Grid.SetRow(btnRow, 2);
            right.Children.Add(btnRow);

            _actionButton = new Button
            {
                MinWidth = 130,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold,
                Visibility = Visibility.Collapsed,
            };
            // Through the shared theme rather than by setting Background here.
            // WPF's default button template ignores a plain Background on hover
            // and paints its own system chrome, so a gold button turned grey the
            // moment the pointer touched it. ModalButtonTheme swaps the template
            // for a flat one that keeps the colour and dims on hover instead.
            ModalButtonTheme.Primary(_actionButton);
            _actionButton.Click += (s, e) =>
            {
                _chosen = _selected?.Action;
                Close();
            };
            btnRow.Children.Add(_actionButton);

            var close = new Button
            {
                Content = "Close",
                MinWidth = 90,
                Padding = new Thickness(14, 5, 14, 5),
                Cursor = Cursors.Hand,
                IsCancel = true,
            };
            ModalButtonTheme.Secondary(close);
            close.Click += (s, e) => Close();
            btnRow.Children.Add(close);

            GuideEntry start = null;
            if (!string.IsNullOrEmpty(initialKey))
                start = _entries.Find(e => string.Equals(e.Key, initialKey, StringComparison.Ordinal));
            Select(start ?? _entries[0]);
        }

        /// <summary>Rebuild the list for a search term, matching title AND body.
        ///
        /// Filtering never changes which guide is on screen. Someone half way
        /// through a document who types a word to look something else up should
        /// not have the document taken away from them; the list narrows, the
        /// reading stays put until they pick something.</summary>
        private void ApplyFilter(string query)
        {
            _shown.Clear();
            string q = (query ?? "").Trim();
            foreach (var e in _entries)
            {
                if (q.Length == 0
                    || e.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (_bodies.TryGetValue(e, out string body)
                        && body.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                    _shown.Add(e);
            }

            _rows.Clear();
            _listPanel.Children.Clear();
            string group = null;
            foreach (var e in _shown)
            {
                if (!string.Equals(group, e.Group, StringComparison.Ordinal))
                {
                    group = e.Group;
                    _listPanel.Children.Add(new TextBlock
                    {
                        Text = (group ?? "").ToUpperInvariant(),
                        Foreground = MutedFg,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(14, _listPanel.Children.Count == 0 ? 2 : 14, 14, 4),
                    });
                }
                _listPanel.Children.Add(BuildRow(e));
            }

            if (_shown.Count == 0)
            {
                _noMatches.Text = "No guide mentions “" + q + "”.";
                _noMatches.Visibility = Visibility.Visible;
                _listPanel.Children.Add(_noMatches);
            }
            else
            {
                _noMatches.Visibility = Visibility.Collapsed;
            }

            // The selected row is rebuilt too, so re-apply its highlight.
            if (_selected != null && _rows.TryGetValue(_selected, out var row))
                row.Background = RowSelected;
        }

        private Border BuildRow(GuideEntry e)
        {
            var text = new TextBlock
            {
                Text = e.Title,
                Foreground = TextFg,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            var row = new Border
            {
                Padding = new Thickness(14, 7, 12, 7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = text,
                // NOT focusable, deliberately. An earlier version made every row a
                // tab stop that selected itself on GotKeyboardFocus, which meant
                // the first Tab press replaced whatever the reader was in the
                // middle of with entry one, and undid any arrow-key walk. It also
                // put fifteen stops between the reader and the action button.
                // Selection is the arrow keys and the mouse; Tab is for the
                // buttons and the links inside the guide.
                Focusable = false,
            };
            row.MouseLeftButtonUp += (s, ev) => Select(e);
            row.MouseEnter += (s, ev) => { if (_selected != e) row.Background = RowHover; };
            row.MouseLeave += (s, ev) => { if (_selected != e) row.Background = Brushes.Transparent; };
            _rows[e] = row;
            return row;
        }

        /// <summary>Follow a [label](guide:key) cross-reference. Guides refer to
        /// each other constantly, and a reference the reader has to go and find in
        /// the list by hand is one they will not follow.</summary>
        private void GoToGuide(string key)
        {
            var target = _entries.Find(x => string.Equals(x.Key, key, StringComparison.Ordinal));
            if (target != null) Select(target);
        }

        /// <summary>Turn cross-references to guides that are NOT in this list back
        /// into plain text before rendering.
        ///
        /// Entries can be hidden (the light-patterns guide is, on a wheel whose
        /// strip has a fixed look), and a link that silently does nothing when
        /// clicked is worse than a sentence that simply does not offer one. The
        /// label survives either way, so the prose still reads.</summary>
        private string DisarmMissingGuideLinks(string md)
        {
            if (string.IsNullOrEmpty(md) || md.IndexOf("](guide:", StringComparison.OrdinalIgnoreCase) < 0)
                return md;
            var sb = new System.Text.StringBuilder(md.Length);
            int i = 0;
            while (i < md.Length)
            {
                int open = md.IndexOf('[', i);
                if (open < 0) { sb.Append(md, i, md.Length - i); break; }
                int close = md.IndexOf(']', open + 1);
                if (close < 0 || close + 1 >= md.Length || md[close + 1] != '(')
                { sb.Append(md, i, open - i + 1); i = open + 1; continue; }
                int paren = md.IndexOf(')', close + 2);
                if (paren < 0) { sb.Append(md, i, open - i + 1); i = open + 1; continue; }

                string url = md.Substring(close + 2, paren - close - 2);
                if (url.StartsWith("guide:", StringComparison.OrdinalIgnoreCase))
                {
                    string key = url.Substring(6);
                    bool known = _entries.Exists(x => string.Equals(x.Key, key, StringComparison.Ordinal));
                    sb.Append(md, i, open - i);
                    if (known) sb.Append(md, open, paren - open + 1);
                    else sb.Append(md, open + 1, close - open - 1);   // the label alone
                    i = paren + 1;
                    continue;
                }
                sb.Append(md, i, paren - i + 1);
                i = paren + 1;
            }
            return sb.ToString();
        }

        /// <summary>Up and down walk the list wherever focus happens to be, so the
        /// reader never has to find the list first. Everything else (Tab, Space,
        /// Escape) is WPF's own.</summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                int i = _shown.IndexOf(_selected);
                if (i >= 0)
                {
                    int next = i + (e.Key == Key.Down ? 1 : -1);
                    if (next >= 0 && next < _shown.Count)
                    {
                        Select(_shown[next]);
                        e.Handled = true;
                    }
                }
            }
            base.OnPreviewKeyDown(e);
        }

        private void Select(GuideEntry e)
        {
            if (e == null || ReferenceEquals(e, _selected)) return;
            _selected = e;
            foreach (var kv in _rows)
                kv.Value.Background = ReferenceEquals(kv.Key, e) ? RowSelected : Brushes.Transparent;
            // Arrow keys and crosslinks can both land on a row below the fold, and
            // a selection nobody can see reads as nothing having happened.
            if (_rows.TryGetValue(e, out var selectedRow)) selectedRow.BringIntoView();

            _title.Text = e.Title;

            string md = GuideText.Load(e.Key);
            if (e.Extra != null)
            {
                string extra = null;
                try { extra = e.Extra(); } catch { }
                if (!string.IsNullOrEmpty(extra)) md = md.TrimEnd() + "\n\n" + extra;
            }
            // Capped measure, and a notch larger than the release-note default.
            // Uncapped, a maximised window ran lines past 200 characters, which is
            // what actually makes a guide tiring to read; the point size was the
            // lesser half of it.
            var rendered = MarkdownView.Render(DisarmMissingGuideLinks(md), GoToGuide, GuideTextScale);
            rendered.MaxWidth = 680;
            rendered.HorizontalAlignment = HorizontalAlignment.Left;
            _bodyScroll.Content = rendered;
            _bodyScroll.ScrollToTop();

            if (string.IsNullOrEmpty(e.ActionLabel) || e.Action == null)
            {
                _actionButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                _actionButton.Content = e.ActionLabel;
                _actionButton.Visibility = Visibility.Visible;
            }
        }
    }
}
