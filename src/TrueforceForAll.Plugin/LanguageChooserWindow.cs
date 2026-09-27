// Which language to translate, out of every language Windows knows.
//
// The Settings picker lists the languages this install HAS, which is the right
// list for choosing what to read the panel in. It is the wrong list for deciding
// what to translate: on a fresh install it holds English and nothing else, so a
// German speaker who would happily write German has no way to see that German is
// open. This window is that list. It is not a curated set of languages we have
// blessed; it is the 310 neutral cultures the framework enumerates, plus a box for
// any other tag, because who translates the plugin is not ours to decide.
//
// The percentage beside each one is what THIS PC has: a file in the languages
// folder, or one the build ships. It cannot say whether someone else has started a
// language, and the window says so rather than implying otherwise. That answer
// needs the community service (docs/localization-plan.md, Phase 3b).
//
// Progress is only computed for a language that has a layer. Describe() on the
// other 300 would build a 2,900-name missing list each time to tell us what we
// already know from its absence.

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

        /// <summary>The tag the user chose, or null when they closed the window.</summary>
        public string ChosenTag { get; private set; }

        /// <summary>The name that language calls itself, to prefill the Translate
        /// window's name box. Null when the tag is one Windows does not know.</summary>
        public string ChosenName { get; private set; }

        private sealed class Row
        {
            public string Tag { get; set; }
            public string Native { get; set; }
            public string English { get; set; }
            public string Progress { get; set; }
            public string Note { get; set; }
            // Sort key, not shown: languages with work in them first, then the
            // reader's own Windows language, then everything else by its own name.
            public int Rank { get; set; }
            public int Done { get; set; }
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly DataGrid _grid;
        private readonly TextBox _search;
        private readonly TextBox _otherTag;

        internal LanguageChooserWindow(LocStore store, string windowsLang)
        {
            Title = Loc.T("LangPicker_Title");
            Width = 760;
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
                Width = 240, Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
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
            AddColumn(Loc.T("LangPicker_ColumnLanguage"), nameof(Row.Native), 180);
            AddColumn(Loc.T("LangPicker_ColumnEnglishName"), nameof(Row.English), 170);
            AddColumn(Loc.T("LangPicker_ColumnTag"), nameof(Row.Tag), 90);
            AddColumn(Loc.T("LangPicker_ColumnProgress"), nameof(Row.Progress), 130);
            AddColumn(Loc.T("LangPicker_ColumnNote"), nameof(Row.Note), 150);
            // A double-click is how a list of 310 rows is used; the button is for
            // anyone who reaches it by keyboard.
            _grid.MouseDoubleClick += (s, e) => Accept();
            _grid.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            // The tag box on the left, the buttons where a dialog's buttons go.
            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tagRow = new StackPanel { Orientation = Orientation.Horizontal };
            tagRow.Children.Add(new TextBlock
            {
                Text = Loc.T("LangPicker_OtherTag"), Foreground = MutedFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            _otherTag = new TextBox
            {
                Width = 140, MaxLength = 32, Foreground = TextFg, Background = InputBg,
                BorderBrush = BorderFg, Padding = new Thickness(5, 3, 5, 3), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.T("LangPicker_OtherTag_Tip"),
            };
            tagRow.Children.Add(_otherTag);
            Grid.SetColumn(tagRow, 0);
            bottom.Children.Add(tagRow);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var open = MakeButton(Loc.T("LangPicker_Open"), (s, e) => Accept());
            open.IsDefault = true;
            buttons.Children.Add(open);
            var close = MakeButton(Loc.T("Settings_Close"), (s, e) => Close());
            close.IsCancel = true;
            buttons.Children.Add(close);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);
            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            Build(store, windowsLang);
            ApplyFilter();
        }

        private void AddColumn(string header, string path, double width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, TextFg));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = new DataGridLength(width),
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

        /// <summary>One row per neutral culture, plus any tag that has a file here
        /// and is not one of them (a regional file someone wrote, or a test
        /// locale). Progress is read only for the tags that have a layer.</summary>
        private void Build(LocStore store, string windowsLang)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string t in LocDiagnostics.KnownTags(store)) known.Add(t);
            }
            catch { }

            int total = store == null ? 0 : store.EnglishKeyCount;
            string active = store == null ? LocStore.EnglishTag : store.ActiveTag;
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
                _rows.Add(MakeRow(store, c.Name, c.NativeName, c.EnglishName, known, total, active, windowsLang));
            }
            // A tag with a file here that is not a neutral culture still belongs in
            // the list, or the one language this PC is already translating could be
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
                _rows.Add(MakeRow(store, tag, native, english, known, total, active, windowsLang));
            }

            _rows.Sort((a, b) =>
            {
                if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
                if (a.Done != b.Done) return b.Done.CompareTo(a.Done);
                return string.Compare(a.Native, b.Native, StringComparison.OrdinalIgnoreCase);
            });
        }

        private Row MakeRow(LocStore store, string tag, string native, string english,
                            HashSet<string> known, int total, string active, string windowsLang)
        {
            var row = new Row
            {
                Tag = tag,
                Native = string.IsNullOrEmpty(native) ? tag : native,
                English = english ?? "",
                Progress = Loc.T("LangPicker_NotStarted"),
                Note = "",
                Rank = 2,
            };
            if (known.Contains(tag) && store != null)
            {
                try
                {
                    var s = store.Describe(tag);
                    int defined = s.DefinedCount;
                    int all = defined + s.Missing.Count;
                    if (all > 0)
                    {
                        row.Done = defined;
                        row.Progress = Loc.F("LangPicker_Progress_Fmt",
                            (int)Math.Round(100.0 * defined / all), defined, all);
                        row.Rank = 0;
                    }
                }
                catch { }
            }
            bool isActive = string.Equals(tag, active, StringComparison.OrdinalIgnoreCase);
            bool isWindows = !string.IsNullOrEmpty(windowsLang)
                && string.Equals(tag, windowsLang, StringComparison.OrdinalIgnoreCase);
            if (isActive) row.Note = Loc.T("LangPicker_NoteShowingNow");
            else if (isWindows) row.Note = Loc.T("LangPicker_NoteYourWindowsLanguage");
            if (isWindows && row.Rank == 2) row.Rank = 1;
            return row;
        }

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

        /// <summary>The typed tag wins over the selected row, since someone who
        /// typed one is asking for a language the list does not offer.</summary>
        private void Accept()
        {
            string typed = (_otherTag.Text ?? "").Trim();
            if (typed.Length > 0)
            {
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
                return;
            }
            var row = _grid.SelectedItem as Row;
            if (row == null) return;
            ChosenTag = row.Tag;
            ChosenName = row.Native;
            DialogResult = true;
        }
    }
}
