// Which language to translate: a list of languages.
//
// The Settings picker lists the languages this install HAS, which is the right
// list for choosing what to read the panel in. It is the wrong list for deciding
// what to translate: on a fresh install it holds English and nothing else, so a
// German speaker who would happily write German has no way to see that German is
// open. This window is that list, and it is not a curated set we have blessed: it
// is every language the framework enumerates, because who translates the plugin is
// not ours to decide.
//
// Two columns, because two things are being asked: which language, and how much of
// it exists already. A language appears under its own name and nothing else:
// someone who speaks Portuguese is not looking for "Portuguese", and the English
// name would spend the column on a reader who is not the one choosing. The search
// box still matches it, so typing "German" finds Deutsch.
//
// The code (de, pt-BR) is deliberately not a column either; it is what the search
// box accepts and what the Translate window's title shows. One box does both jobs:
// it filters the list, and when it matches nothing it is taken as the code of a
// language the list does not offer, which is how pt-BR or a test locale is reached
// without a second control.
//
// The percentage is what THIS PC has: a file in the languages folder, or one the
// build ships. It cannot say whether someone else has started a language, and the
// window says so rather than implying otherwise. That answer needs the community
// service (docs/localization-plan.md, Phase 3b).
//
// Progress is only computed for a language that has a layer. Describe() on the
// other three hundred would build a 2,900-name missing list each time to tell us
// what we already know from its absence.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    internal sealed class LanguageChooserWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

        /// <summary>The language the user chose, as its code, or null when they
        /// closed the window.</summary>
        public string ChosenTag { get; private set; }

        /// <summary>The name that language calls itself, to prefill the Translate
        /// window's name box. Null when it is a code Windows does not know.</summary>
        public string ChosenName { get; private set; }

        private sealed class Row
        {
            public string Tag { get; set; }
            public string Native { get; set; }
            /// <summary>Not shown. The search box matches it, so someone who
            /// knows a language by its English name can still find it.</summary>
            public string English { get; set; }
            public string Progress { get; set; }

            /// <summary>What this PC's files define, as a percentage. Kept so the
            /// server's answer can be merged in without rebuilding every row from
            /// scratch.</summary>
            public double Local { get; set; }
            // Sort key, not shown: languages with work in them first, then the
            // reader's own Windows language, then everything else by its own name.
            public int Rank { get; set; }
            public int Done { get; set; }
        }

        private readonly List<Row> _rows = new List<Row>();
        /// <summary>The service client, or null when there is none. Only used to ask
        /// what each language already has.</summary>
        private readonly LocCommunity _community;
        /// <summary>Tag to percentage, as the server reports it. Empty until the one
        /// call answers, and empty forever with no backend or no consent.</summary>
        private Dictionary<string, double> _sent =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly DataGrid _grid;
        private readonly TextBox _search;

        internal LanguageChooserWindow(LocStore store, string windowsLang,
                                       LocCommunity community = null)
        {
            Title = Loc.T("LangPicker_Title");
            Width = 620;
            Height = 560;
            Background = WindowBg;
            Foreground = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(16, 14, 16, 12) };
            for (int i = 0; i < 4; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            Content = root;

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock
            {
                Text = Loc.T("LangPicker_Title"),
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
            });
            head.Children.Add(new TextBlock
            {
                Text = Loc.T("LangPicker_Intro"),
                Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            bar.Children.Add(new TextBlock
            {
                Text = Loc.T("Translate_Search"), Foreground = MutedFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            _search = new TextBox
            {
                Width = 260, Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(5, 3, 5, 3), FontSize = 12,
                ToolTip = Loc.T("LangPicker_Search_Tip"),
            };
            _search.TextChanged += (s, e) => ApplyFilter();
            _search.Loaded += (s, e) => _search.Focus();
            bar.Children.Add(_search);
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                Background = PanelBg,
                Foreground = TextFg,
                RowBackground = PanelBg,
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)),
                HorizontalGridLinesBrush = BorderFg,
                BorderBrush = BorderFg,
                EnableRowVirtualization = true,
                SelectionMode = DataGridSelectionMode.Single,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            AddColumn(Loc.T("LangPicker_ColumnLanguage"), nameof(Row.Native),
                      new DataGridLength(1, DataGridLengthUnitType.Star));
            AddColumn(Loc.T("LangPicker_ColumnProgress"), nameof(Row.Progress),
                      new DataGridLength(170));
            // A double-click is how a long list is used; Enter is for the keyboard.
            _grid.MouseDoubleClick += (s, e) => Accept();
            _grid.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var open = MakeButton(Loc.T("LangPicker_Open"), (s, e) => Accept());
            open.IsDefault = true;
            buttons.Children.Add(open);
            var close = MakeButton(Loc.T("Settings_Close"), (s, e) => Close());
            close.IsCancel = true;
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            _community = community;
            Build(store, windowsLang);
            ApplyFilter();
            // Then ask what each language already has and say so. One call, made because
            // a person opened this window; the rows are already correct without it, just
            // smaller for a language this PC has never fetched.
            LoadSentPercentagesAsync();
        }

        private void AddColumn(string header, string path, DataGridLength width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, TextFg));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = width,
                ElementStyle = style,
            });
        }

        private Button MakeButton(string text, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(12, 5, 12, 5), MinWidth = 110,
                Margin = new Thickness(8, 0, 0, 0), Background = InputBg, Foreground = TextFg,
                BorderBrush = BorderFg,
            };
            b.Click += click;
            return b;
        }

        /// <summary>One row per language the framework knows, plus any code that has
        /// a file here and is not one of them (a regional file someone wrote, or a
        /// test locale). Progress is read only for codes that have a layer.</summary>
        private void Build(LocStore store, string windowsLang)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string t in LocDiagnostics.KnownTags(store)) known.Add(t);
            }
            catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cultures = new List<CultureInfo>();
            try
            {
                cultures.AddRange(CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                    .Where(c => !string.IsNullOrEmpty(c.Name)));
            }
            catch { }

            foreach (var c in cultures)
            {
                seen.Add(c.Name);
                _rows.Add(MakeRow(store, c.Name, c.NativeName, c.EnglishName, known, windowsLang));
            }
            // A code with a file here that is not one of those still belongs in the
            // list, or the one language this PC is already translating could be
            // missing from it.
            foreach (string tag in known)
            {
                if (seen.Contains(tag)) continue;
                string native = null, english = null;
                try
                {
                    var ci = CultureInfo.GetCultureInfo(tag);
                    native = ci.NativeName;
                    english = ci.EnglishName;
                }
                catch { }
                _rows.Add(MakeRow(store, tag, native, english, known, windowsLang));
            }

            _rows.Sort(CompareRows);
        }

        private static int CompareRows(Row a, Row b)
        {
            if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
            if (a.Done != b.Done) return b.Done.CompareTo(a.Done);
            return string.Compare(a.Native, b.Native, StringComparison.OrdinalIgnoreCase);
        }

        private Row MakeRow(LocStore store, string tag, string native, string english,
                            HashSet<string> known, string windowsLang)
        {
            string ownName = string.IsNullOrEmpty(native) ? tag : native;
            var row = new Row
            {
                Tag = tag,
                Native = ownName,
                English = english ?? "",
                Progress = Loc.T("LangPicker_NotStarted"),
                Rank = 2,
            };
            if (known.Contains(tag) && store != null)
            {
                try
                {
                    var s = store.Describe(tag);
                    int defined = s.DefinedCount;
                    int all = defined + s.Missing.Count;
                    if (all > 0) row.Local = 100.0 * defined / all;
                }
                catch { }
            }
            ApplyPercent(row);
            if (row.Rank == 2 && !string.IsNullOrEmpty(windowsLang)
                && string.Equals(tag, windowsLang, StringComparison.OrdinalIgnoreCase))
                row.Rank = 1;
            return row;
        }

        /// <summary>One percentage, from whichever side has more of the language. The
        /// local figure wins when it is higher, which is the case that matters most: a
        /// translator with rows written and not yet sent must not see their own work read
        /// as whatever the server happens to hold. A row with nothing anywhere says so in
        /// words instead of showing a zero.</summary>
        private void ApplyPercent(Row row)
        {
            double sent;
            if (!_sent.TryGetValue(row.Tag ?? "", out sent)) sent = 0;
            double percent = Math.Max(row.Local, sent);
            if (percent <= 0)
            {
                row.Progress = Loc.T("LangPicker_NotStarted");
                if (row.Rank == 0) row.Rank = 2;
                row.Done = 0;
                return;
            }
            row.Progress = Loc.F("LangPicker_Progress_Fmt", (int)Math.Round(percent));
            row.Done = (int)Math.Round(percent);
            row.Rank = 0;
        }

        /// <summary>Ask the server what every language has, then say so. One call, when a
        /// person opens this window, and the rows are rebuilt if it changes anything.
        /// Failure is silent by design: the numbers the rows already carry are true, just
        /// smaller.</summary>
        private async void LoadSentPercentagesAsync()
        {
            if (_community == null) return;
            Dictionary<string, double> sent;
            try { sent = await _community.GetProgressAsync(); }
            catch { return; }
            if (sent == null || sent.Count == 0) return;
            _sent = sent;
            foreach (var row in _rows) ApplyPercent(row);
            _rows.Sort(CompareRows);
            ApplyFilter();
        }

        /// <summary>The search box matches a language's own name, its English name
        /// and its code, so typing "de", "German" or "Deutsch" all find German.</summary>
        private void ApplyFilter()
        {
            string needle = (_search.Text ?? "").Trim();
            IEnumerable<Row> view = _rows;
            if (needle.Length > 0)
                view = view.Where(r =>
                    (r.Native ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.English ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.Tag ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            _grid.ItemsSource = view.ToList();
            if (_grid.Items.Count > 0) _grid.SelectedIndex = 0;
        }

        /// <summary>A selected row wins. When the search matched nothing, what was
        /// typed is taken as the code of a language the list does not offer, which
        /// is how a regional code like pt-BR is reached with no second control.</summary>
        private void Accept()
        {
            var row = _grid.SelectedItem as Row;
            if (row != null)
            {
                ChosenTag = row.Tag;
                ChosenName = row.Native;
                DialogResult = true;
                return;
            }
            string typed = (_search.Text ?? "").Trim();
            if (typed.Length == 0) return;
            if (!SettingsControl.IsCultureTagShape(typed))
            {
                TrueforceDialog.Show(this, Loc.T("LangPicker_Title"),
                    Loc.T("Settings_LanguageBadTag"), DialogKind.Warning);
                return;
            }
            ChosenTag = typed;
            try { ChosenName = CultureInfo.GetCultureInfo(typed).NativeName; }
            catch { ChosenName = null; }
            DialogResult = true;
        }
    }
}
