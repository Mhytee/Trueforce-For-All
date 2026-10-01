// The window a translator works in: English on the left, their language on the
// right, and the panel behind it re-rendering as each row is finished.
//
// It exists because the alternative is a 2,000-key JSON file in Notepad, where a
// missing quote costs the whole language and nothing tells you which of the keys
// you have not reached yet. Here a row is a row, the headings say how much of
// each area is left, and a save is always a valid file.
//
// What it writes is the root override, <languagesRoot>\<tag>.json, which is the
// highest-precedence layer: whatever a build ships or the community sends, the
// file a person edited on their own machine still wins. Saving reloads the store,
// so the edit reaches the panel without a restart (docs/localization-plan.md,
// Phase 2). There is no Send: a row goes to the service a moment after it is
// typed, because a typo is better than no translation at all and a button that
// has to be found is a language that never arrives.
//
// A counted string is one row per form THIS language has, not per form English
// has, so a Russian translator is given the four forms Russian needs and a
// Japanese one the single form Japanese needs.
//
// Keys are hidden by default. A translator does not need them, and a key column
// invites treating the English as a name rather than as a sentence to translate.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    internal sealed partial class TranslateWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush WarnFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));

        private static readonly Regex Placeholder = new Regex(@"\{(\d+)(?::[^{}]*)?\}");

        private readonly LocStore _store;
        /// <summary>The service client, or null when the runtime did not start or the
        /// window was opened without one. Send falls back to the folder and an issue
        /// when this is null, which is also what happens signed out.</summary>
        private readonly LocCommunity _community;
        private readonly string _tag;
        private readonly string _root;
        private readonly Action<string> _log;
        private readonly List<Row> _rows = new List<Row>();
        private readonly DataGrid _grid;
        private readonly TextBox _search;
        private readonly TextBox _name;
        private readonly StackPanel _nameRow;
        private readonly TextBlock _status;
        private readonly TextBlock _problemLink;
        /// <summary>Keys committed since the last flush. A set, so editing one row five
        /// times sends it once.</summary>
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Keys the server refused this session, so a row it will not take is not
        /// offered again every few seconds. Cleared when the row is edited, because the
        /// edit may be the fix.</summary>
        private readonly HashSet<string> _refused = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Rows WPF has built, counted so the log can say whether this window
        /// virtualizes. A screenful is a few dozen; the whole table is near 3,000.</summary>
        private Border _progressTrack;
        private Border _progressFill;
        private double _progressDone;
        private int _rowsBuilt;
        private bool _costReported;
        private DispatcherTimer _publishTimer;
        private bool _publishing;
        /// <summary>When the queue may be offered again. The server's caps are counted
        /// per hour, so a refusal means waiting, not retrying every five seconds: a
        /// translator who fills a language in one sitting would otherwise spend the rest
        /// of the hour being turned down at the same rate they were typing.</summary>
        private DateTime _retryAfter = DateTime.MinValue;

        /// <summary>Whether the list is showing only the rows with something wrong.
        /// Reached by clicking the count rather than by a checkbox that sits there
        /// being unticked: the condition is rare and the count is already read.</summary>
        private bool _onlyProblems;

        /// <summary>One tab or window's worth of strings, named by the part of the
        /// key before its first underscore. It is the group heading, and it carries its
        /// own progress so a translator can finish an area and see it finished. The
        /// label is the prefix with its words separated, not a translated name: it
        /// names where the strings live, and inventing 50 localized area names to say
        /// so would be more copy than the strings themselves.</summary>
        internal sealed class AreaGroup : INotifyPropertyChanged
        {
            public string Prefix { get; set; }
            public string Label { get; set; }
            public int Rank { get; set; }
            public int Total { get; set; }

            private int _done;
            public int Done
            {
                get => _done;
                set
                {
                    if (_done == value) return;
                    _done = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Done)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));
                }
            }

            public string Header => Loc.F("Translate_GroupProgress_Fmt", Label, Done, Total);

            public event PropertyChangedEventHandler PropertyChanged;
        }

        /// <summary>One English string and the translation being written for it.
        /// The row is the unit a translator sees, so it also carries whether its
        /// placeholders still match, which is the one mistake that silently costs
        /// the whole string at runtime.</summary>
        internal sealed class Row : INotifyPropertyChanged
        {
            private string _text;
            public string Key { get; set; }
            public string English { get; set; }

            /// <summary>Which heading this row sits under. Set once when the rows are
            /// built; the row reports into it as it is filled in.</summary>
            public AreaGroup Area { get; set; }

            /// <summary>The language being written, which is rarely the one the window
            /// itself is drawn in. Only the plural note needs it, and it needs it because
            /// which counts a form covers is a fact about this language.</summary>
            public string Language { get; set; }

            /// <summary>What this row sorts under: its own key, or the base key when it
            /// is one form of a counted string, so a set stays in one place.</summary>
            public string SortKey { get; set; }

            /// <summary>Where this form sits inside its set, and 0 for a row that is not
            /// one of a set.</summary>
            public int FormRank { get; set; }

            /// <summary>Whether this row, or any form of the set it belongs to, was empty
            /// when the rows were built. Fixed at build time: it is what puts the work
            /// first without moving a row the moment it is typed into.</summary>
            public bool SortEmpty { get; set; }

            /// <summary>Pixels this string has when the panel draws it in a control
            /// with a fixed width, or 0 when nothing constrains it. From LocFitBudget,
            /// generated off the XAML, because the window cannot see the layout a
            /// string ends up in.</summary>
            public double Room { get; set; }

            /// <summary>How far past its control the translation reaches, in pixels,
            /// or 0 when it fits or nothing constrains it.</summary>
            private double Overflow
            {
                get
                {
                    if (Room <= 0 || string.IsNullOrEmpty(_text)) return 0;
                    double w = MeasureWidth(_text);
                    return w > Room ? w - Room : 0;
                }
            }

            /// <summary>Roughly how many characters fit. Measured from the translation
            /// once there is one, and from the English until then: a Chinese character is
            /// about twice the width of a Latin letter, so counting in English letters
            /// would promise a Chinese translator twice the room they have. A translator
            /// can act on characters where pixels mean nothing, which is why this is not
            /// just reported in pixels.</summary>
            private int FitChars
            {
                get
                {
                    if (Room <= 0) return 0;
                    string gauge = string.IsNullOrEmpty(_text) ? English : _text;
                    if (string.IsNullOrEmpty(gauge)) return 0;
                    double w = MeasureWidth(gauge);
                    if (w <= 0) return 0;
                    return Math.Max(1, (int)(Room / (w / gauge.Length)));
                }
            }

            public string Text
            {
                get => _text;
                set
                {
                    bool was = !string.IsNullOrWhiteSpace(_text);
                    _text = value;
                    bool now = !string.IsNullOrWhiteSpace(_text);
                    if (Area != null && was != now) Area.Done += now ? 1 : -1;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Warning)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoteBrush)));
                }
            }

            /// <summary>What the English cell says on hover. Through Loc.F, not
            /// WPF's StringFormat: a value holding a raw {0} that reaches a label is
            /// exactly what the placeholder check refuses, and it is right to.</summary>
            public string KeyTip => Loc.F("Translate_KeyTip_Fmt", Key);

            /// <summary>Which plural form this row is, or empty. Said in counts rather
            /// than by naming the form: with the key column hidden a set reads as the
            /// same English two or three times over, and "used for 2, 3, 4, 22" tells a
            /// translator which one they are writing where "few" does not. The counts
            /// come from the rule itself, so they are this language's and not
            /// English's.</summary>
            public string PluralForm
            {
                get
                {
                    string baseKey, form;
                    if (!LocPlurals.TrySplit(Key, out baseKey, out form)) return "";
                    bool more;
                    var counts = LocPlurals.ExampleCounts(Language, form, 4, out more);
                    if (counts.Count == 0) return "";
                    var sb = new StringBuilder();
                    for (int i = 0; i < counts.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(counts[i].ToString(CultureInfo.InvariantCulture));
                    }
                    if (more) sb.Append("…");
                    return Loc.F("Translate_PluralCounts_Fmt", sb.ToString());
                }
            }

            /// <summary>Anything a translator should know about this row: the
            /// placeholder problem when there is one, else which plural form it is.
            /// One column rather than two, since a row rarely has both to say.</summary>
            public string Note
            {
                get
                {
                    string w = Warning;
                    if (w.Length > 0) return w;
                    // Which form ahead of how much room: the forms of one counted string
                    // carry the same English, so without this note a set of three reads
                    // as the same row three times and the fit is the lesser thing to
                    // know about it.
                    string plural = PluralForm;
                    if (plural.Length > 0) return plural;
                    if (Room > 0) return Loc.F("Translate_TightFit_Fmt", FitChars);
                    return "";
                }
            }

            /// <summary>Red for a problem, muted for a note. Bound per row, which is
            /// what lets one column carry both without reading as an error.</summary>
            public Brush NoteBrush => Warning.Length > 0 ? WarnFg : MutedFg;

            /// <summary>What is wrong with this row, worst first: a dropped or
            /// invented placeholder, which would make string.Format throw and cost the
            /// whole string, then text that reaches past the control it is drawn in,
            /// which costs the end of the label.</summary>
            public string Warning
            {
                get
                {
                    if (string.IsNullOrEmpty(_text)) return "";
                    if (HasPlaceholderProblem)
                        return Loc.F("Translate_PlaceholdersDoNotMatch_Fmt",
                            string.Join(", ", Numbers(English).OrderBy(n => n).Select(n => "{" + n + "}")));
                    double over = Overflow;
                    if (over > 0) return Loc.F("Translate_TooWide_Fmt", (int)Math.Ceiling(over));
                    return "";
                }
            }

            /// <summary>Whether the translation drops or invents a placeholder. Apart
            /// from Warning so the status line can count the two problems separately:
            /// one costs the whole string, the other costs the end of a label.</summary>
            public bool HasPlaceholderProblem
                => !string.IsNullOrEmpty(_text) && !Numbers(_text).SetEquals(Numbers(English));

            /// <summary>Whether the translation reaches past its control.</summary>
            public bool IsTooWide => !HasPlaceholderProblem && Overflow > 0;

            private static HashSet<int> Numbers(string s)
            {
                var set = new HashSet<int>();
                if (s != null)
                    foreach (Match m in Placeholder.Matches(s)) set.Add(int.Parse(m.Groups[1].Value));
                return set;
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        internal TranslateWindow(LocStore store, string tag, string languageName, Action<string> log,
                                 LocCommunity community = null)
        {
            _store = store;
            _community = community;
            _tag = tag;
            _root = store?.LanguagesRoot;
            _log = log ?? (m => { });

            // The code alone tells a translator nothing, so the language's own
            // name leads and the code follows it.
            string heading = string.IsNullOrWhiteSpace(languageName) || languageName == tag
                ? Loc.F("Translate_Title_Fmt", tag)
                : Loc.F("Translate_TitleNamed_Fmt", languageName, tag);
            Title = heading;
            Width = 1040;
            Height = 660;
            Background = WindowBg;
            Foreground = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(16, 14, 16, 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var title = new TextBlock
            {
                Text = heading,
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Cursor = Cursors.Hand,
                ToolTip = Loc.T("Translate_RenameLanguage_Tip"),
            };
            title.MouseLeftButtonUp += (s, e) =>
            {
                _nameRow.Visibility = Visibility.Visible;
                _name.Focus();
                _name.SelectAll();
            };
            head.Children.Add(title);
            head.Children.Add(new TextBlock
            {
                Text = Loc.T("Translate_Intro"),
                Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            // How far along, as a bar rather than only as a sentence at the bottom of the
            // window. 2,974 rows is a long way, and a number read once does not give the
            // same sense of movement as a line that grows.
            _progressFill = new Border { Background = HeaderFg, HorizontalAlignment = HorizontalAlignment.Left };
            _progressTrack = new Border
            {
                Background = BorderFg, Height = 4, Margin = new Thickness(0, 10, 0, 0),
                Child = _progressFill,
            };
            _progressTrack.SizeChanged += (s, e) => PaintProgressBar();
            head.Children.Add(_progressTrack);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            // The box says what it searches, in it, rather than a "Search" label sitting
            // beside an empty box saying less.
            _search = new TextBox
            {
                Width = 300, Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(5, 3, 5, 3), FontSize = 12,
            };
            var searchHint = new TextBlock
            {
                Text = Loc.T("Translate_SearchHint"),
                Foreground = MutedFg, FontSize = 12, IsHitTestVisible = false,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _search.TextChanged += (s, e) =>
            {
                searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                ApplyFilter();
            };
            var searchCell = new Grid();
            searchCell.Children.Add(_search);
            searchCell.Children.Add(searchHint);
            bar.Children.Add(searchCell);

            // The name is set once. It shows while it is unset, which is when it
            // matters, and afterwards it lives in the title with the heading offering
            // to bring this row back.
            _nameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _nameRow.Children.Add(new TextBlock
            {
                Text = Loc.T("Translate_LanguageName"), Foreground = MutedFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            _name = new TextBox
            {
                Width = 150, Text = languageName ?? tag, MaxLength = 64,
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(5, 3, 5, 3), FontSize = 12,
                ToolTip = Loc.T("Translate_LanguageName_Tip"),
            };
            _nameRow.Children.Add(_name);
            bool named = !(string.IsNullOrWhiteSpace(languageName) || languageName == tag);
            if (named) _nameRow.Visibility = Visibility.Collapsed;
            bar.Children.Add(_nameRow);
            bar.Children.Add(BuildViewSwitch());
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                // No lines and no stripes. Both are what made this read as a
                // spreadsheet; the boxes and the spacing separate the rows now.
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                Background = PanelBg,
                Foreground = TextFg,
                RowBackground = PanelBg,
                HorizontalGridLinesBrush = BorderFg,
                BorderBrush = BorderFg,
                EnableRowVirtualization = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                SelectionMode = DataGridSelectionMode.Single,
            };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Loc.T("Translate_ColumnEnglish"),
                Binding = new Binding(nameof(Row.English)),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                // The key on hover: "Source" or "Apply" says nothing on its own, and
                // the key names the tab it lives in without a column spent on it.
                ElementStyle = EnglishStyle(),
            });
            // A real input box on every row, always there. This was a DataGridTextColumn,
            // which draws a TextBlock until the cell is clicked: an untranslated row
            // showed nothing at all on the right, and the only hint it could be typed
            // into was finding out that clicking changed it (owner, 2026-09-30: "its not
            // particularly clear a user can or is supposed to type into the row next to
            // english"). A template column has no edit mode to enter, so the box is the
            // cell and one click puts the caret in it.
            //
            // IsReadOnly is about the grid, not the box: it stops the DataGrid trying to
            // start an edit over the top of a control that is already editing.
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = Loc.F("Translate_ColumnYours_Fmt", tag),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = TranslationBoxTemplate(),
                IsReadOnly = true,
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Loc.T("Translate_ColumnNote"),
                Binding = new Binding(nameof(Row.Note)),
                IsReadOnly = true,
                Width = new DataGridLength(190),
                ElementStyle = NoteStyle(),
            });
            // Grouped, and still virtualized. Three things are needed and this had one of
            // them, which is why opening the window built all 2,974 rows and took 49
            // seconds: long enough that Windows called SimHub unresponsive and offered to
            // close it.
            //
            // IsVirtualizingWhenGrouping is the permission. It is not the mechanism: a
            // grouped list puts each group's rows inside the group's own items panel, and
            // the default there is a plain StackPanel, which has no notion of only
            // building what shows. Every group therefore built every row it held. The
            // panel below is the part that actually virtualizes, and recycling keeps the
            // row containers rather than throwing them away on each scroll.
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(_grid, true);
            VirtualizingPanel.SetVirtualizationMode(_grid, VirtualizationMode.Recycling);
            // The one that actually mattered. A ScrollViewer with CanContentScroll false
            // scrolls its content by pixels, so it measures that content at unlimited
            // height and every row has to exist before anything can be drawn. No amount
            // of virtualization settings underneath can survive that, which is why the
            // three above changed nothing on their own: the window still built all 2,974
            // rows and took 33 seconds to do it.
            //
            // DataGrid's own default is true. It arrives false here, so something in the
            // styling this window inherits from SimHub turns it off; setting it on the
            // grid beats an inherited style. Scrolling becomes row by row rather than
            // smooth, which is what every virtualized list does and the price of the
            // window opening at all.
            ScrollViewer.SetCanContentScroll(_grid, true);
            VirtualizingPanel.SetScrollUnit(_grid, ScrollUnit.Item);
            // How many rows WPF actually built, said out loud. Opening this window stalled
            // the UI thread for over 1.6 seconds, long enough for Windows to call SimHub
            // unresponsive, and the dump showed the time going into arranging DataGrid
            // cells. Whether that is a screenful or all of them is the whole question, and
            // it is not a thing to guess at twice.
            _grid.LoadingRow += (s, e) => _rowsBuilt++;
            var groupStyle = new GroupStyle();
            var groupHeading = new FrameworkElementFactory(typeof(TextBlock));
            groupHeading.SetBinding(TextBlock.TextProperty, new Binding("Name.Header"));
            groupHeading.SetValue(TextBlock.ForegroundProperty, HeaderFg);
            groupHeading.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            groupHeading.SetValue(TextBlock.FontSizeProperty, 12.0);
            groupHeading.SetValue(TextBlock.MarginProperty, new Thickness(2, 10, 0, 4));
            var headerTemplate = new DataTemplate { VisualTree = groupHeading };
            // The panel the rows of one group are laid out by. Without this the group
            // builds all of them.
            groupStyle.Panel = new ItemsPanelTemplate(
                new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            // Each area folds. 62 of them, all permanently open, left scrolling as the
            // only way to get anywhere; the headings already say how much of each is
            // done, so shutting the finished ones turns the rest into a short list.
            // Expanded to start, because a translator opening this window should see
            // work rather than a wall of closed headings.
            var container = new Style(typeof(GroupItem));
            var shell = new ControlTemplate(typeof(GroupItem));
            var expander = new FrameworkElementFactory(typeof(Expander));
            expander.SetValue(Expander.IsExpandedProperty, true);
            expander.SetValue(Expander.ForegroundProperty, HeaderFg);
            expander.SetValue(Expander.BorderThicknessProperty, new Thickness(0));
            expander.SetValue(Expander.MarginProperty, new Thickness(0, 2, 0, 2));
            // The group itself is the header, drawn by the template built above.
            expander.SetBinding(Expander.HeaderProperty, new Binding());
            expander.SetValue(Expander.HeaderTemplateProperty, headerTemplate);
            expander.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            shell.VisualTree = expander;
            container.Setters.Add(new Setter(GroupItem.TemplateProperty, shell));
            groupStyle.ContainerStyle = container;
            _grid.GroupStyle.Add(groupStyle);

            // Copying the English into a row is a row action, so it lives on the row.
            var menu = new ContextMenu();
            var copyItem = new MenuItem { Header = Loc.T("Translate_CopyEnglish") };
            copyItem.Click += (s, e) => CopyEnglish();
            menu.Items.Add(copyItem);
            _grid.ContextMenu = menu;
            // WPF does not select on right-click, so without this the menu would copy
            // the English into whichever row happened to be selected before.
            _grid.PreviewMouseRightButtonDown += (s, e) =>
            {
                var hit = e.OriginalSource as DependencyObject;
                while (hit != null && !(hit is DataGridRow)) hit = VisualTreeHelper.GetParent(hit);
                var hitRow = hit as DataGridRow;
                if (hitRow != null) _grid.SelectedItem = hitRow.Item;
            };
            InputBindings.Add(new KeyBinding(new DelegateCommand(CopyEnglish), Key.D, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new DelegateCommand(() => Save(quiet: false)), Key.S, ModifierKeys.Control));

            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);
            // One string at a time, in the same row as the list: TranslateWindow.FocusMode.cs
            // swaps which of the two is visible. Same rows, same file, same publish queue.
            var focus = BuildFocusPanel(tag);
            Grid.SetRow(focus, 2);
            root.Children.Add(focus);

            var statusRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0),
            };
            _status = new TextBlock
            {
                Text = "", Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            statusRow.Children.Add(_status);
            _problemLink = LinkText("", WarnFg, (s, e) =>
            {
                _onlyProblems = !_onlyProblems;
                ApplyFilter();
            });
            _problemLink.Margin = new Thickness(8, 0, 0, 0);
            statusRow.Children.Add(_problemLink);
            Grid.SetRow(statusRow, 3);
            root.Children.Add(statusRow);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var close = MakeButton(Loc.T("Settings_Close"), (s, e) => Close());
            ModalButtonTheme.Secondary(close);
            close.IsCancel = true;
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            // A cell still in edit mode is work the translator typed. Committing it
            // here is the difference between saving that row and losing it.
            Closing += (s, e) =>
            {
                try { _grid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }
                Save(quiet: true);
                try { _publishTimer?.Stop(); } catch { }
                // One last flush, not awaited: the window is closing and the queue is a
                // file either way, so what does not make it goes up next time.
                if (_community != null) _ = FlushAsync();
            };

            LoadRows();
            ApplyFilter();

            // Publishing runs on a timer rather than per keystroke: a translator moving
            // down a column commits a row a second, and one request per row would spend
            // the hourly cap on traffic instead of translations.
            if (_community != null)
            {
                _publishTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _publishTimer.Tick += (s, e) => { _ = FlushAsync(); };
                _publishTimer.Start();
                // Anything typed before this window last closed, or typed offline, or
                // written straight into the file by hand. The cache says what the server
                // has; everything else is unsent.
                QueueEverythingUnsent();
            }
        }

        /// <summary>Every row this translator wrote that the server does not have, which
        /// is how work done offline or in an earlier session reaches it without anyone
        /// pressing anything.
        ///
        /// Wrote, not holds: a row that still says what the build ships is not theirs to
        /// publish, and on a language the plugin already translates that is almost every
        /// row. Sending those would claim the shipped translation as their work and spend
        /// ten hours of the server's hourly cap saying nothing new.</summary>
        private void QueueEverythingUnsent()
        {
            var published = _community.Published(_tag);
            foreach (var row in _rows)
                if (IsMine(row, published)) _pending.Add(row.Key);
            if (_pending.Count > 0) UpdateStatus(_grid.Items.Count);
        }

        /// <summary>Whether this row is something this translator wrote and the server has
        /// not got: not empty, not what the build already says, not what the server already
        /// holds.</summary>
        private bool IsMine(Row row, Dictionary<string, string> published)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Text)) return false;
            if (string.Equals(row.Text, _store.ResolveShipped(_tag, row.Key), StringComparison.Ordinal))
                return false;
            string there;
            if (published != null && published.TryGetValue(row.Key, out there)
                && string.Equals(there, row.Text, StringComparison.Ordinal)) return false;
            return true;
        }

        private static Style WrapStyle()
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            st.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
            st.Setters.Add(new Setter(TextBlock.ForegroundProperty, TextFg));
            return st;
        }

        private static Style EnglishStyle()
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            st.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
            st.Setters.Add(new Setter(TextBlock.ForegroundProperty, TextFg));
            st.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding(nameof(Row.KeyTip))));
            return st;
        }

        private static Style NoteStyle()
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            st.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
            st.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11.0));
            // Bound, not fixed: red when the note is a problem, muted when it is
            // telling you which plural form the row is.
            st.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(Row.NoteBrush))));
            return st;
        }

        /// <summary>The input box a row is translated in. One per visible row, which is
        /// a few dozen rather than all 2,974 of them: the window virtualizes properly
        /// since ScrollViewer.CanContentScroll was set back to true on the grid.</summary>
        private DataTemplate TranslationBoxTemplate()
        {
            var box = new FrameworkElementFactory(typeof(TextBox));
            // One way, with the write back done by hand on LostFocus. A two way binding
            // would update the row at the same moment as the handler that has to queue
            // the row for publishing and save the file, and the order of the two is not
            // something to depend on.
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(Row.Text)) { Mode = BindingMode.OneWay });
            box.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
            // Thirty-two of the strings carry a line break: the dialog bodies, the
            // numbered setup steps, the game notices. Without this a translator can read
            // those breaks and not type one, so the longest strings in the plugin cannot
            // be translated. The box takes the Enter key before the grid sees it, which
            // leaves Tab and clicking away as the ways to finish a row.
            box.SetValue(TextBox.AcceptsReturnProperty, true);
            box.SetValue(TextBox.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            // The longest English string is 769 characters; past this the box scrolls
            // rather than pushing the rest of the grid off screen.
            box.SetValue(TextBox.MaxHeightProperty, 180.0);
            // What makes it read as something to fill in rather than as a cell.
            box.SetValue(TextBox.BackgroundProperty, InputBg);
            box.SetValue(TextBox.ForegroundProperty, TextFg);
            box.SetValue(TextBox.BorderBrushProperty, BorderFg);
            box.SetValue(TextBox.BorderThicknessProperty, new Thickness(1));
            box.SetValue(TextBox.PaddingProperty, new Thickness(5, 3, 5, 3));
            box.SetValue(TextBox.MarginProperty, new Thickness(2, 3, 2, 3));
            box.SetValue(TextBox.MinHeightProperty, 26.0);
            box.AddHandler(TextBox.LostFocusEvent, new RoutedEventHandler(TranslationBox_LostFocus));
            // Tab belongs to the column of boxes, not to the grid's cells. Left alone it
            // walks into the English cell and the note beside it, which is three presses
            // per row to do the one thing this window is for.
            box.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(TranslationBox_PreviewKeyDown));
            return new DataTemplate { VisualTree = box };
        }

        /// <summary>A box that has been left is a finished row: write the file and reload,
        /// which is what makes the panel behind this window change as you work. This was
        /// DataGrid.CellEditEnding, which cannot fire any more because the column no
        /// longer has an edit mode to end.</summary>
        /// <summary>Tab to the next row's box, Shift+Tab to the one before. The box the
        /// focus leaves is committed on the way out by LostFocus, as any other way of
        /// leaving it would be.</summary>
        private void TranslationBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab) return;
            var row = (sender as TextBox)?.DataContext as Row;
            if (row == null) return;
            e.Handled = true;
            MoveToRowBox(row, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        }

        /// <summary>Select the row one step away and put the caret in its box. The row has
        /// to be scrolled to first: with virtualization working, the container for a row
        /// off screen does not exist yet, which is why the focus happens a beat later.</summary>
        private void MoveToRowBox(Row from, int delta)
        {
            var items = _grid.Items;
            int at = items.IndexOf(from);
            if (at < 0) return;
            int next = at + delta;
            if (next < 0 || next >= items.Count) return;
            object target = items[next];
            _grid.SelectedItem = target;
            _grid.ScrollIntoView(target);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var container = _grid.ItemContainerGenerator.ContainerFromItem(target) as DataGridRow;
                var box = container == null ? null : FindDescendant<TextBox>(container);
                if (box != null) { box.Focus(); box.CaretIndex = (box.Text ?? "").Length; }
            }), DispatcherPriority.Background);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var hit = child as T ?? FindDescendant<T>(child);
                if (hit != null) return hit;
            }
            return null;
        }

        private void TranslationBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var box = sender as TextBox;
            var row = box?.DataContext as Row;
            if (box == null || row == null) return;
            if (string.Equals(row.Text ?? "", box.Text ?? "", StringComparison.Ordinal)) return;
            row.Text = box.Text;
            // The edit may be the fix for whatever the server refused.
            _refused.Remove(row.Key);
            // Null rather than the cache: one row, and re-reading the file per keystroke
            // would be the wrong trade. A row that matches what the server already has is
            // answered already_approved, which costs one row of a batch and nothing else.
            if (IsMine(row, null)) _pending.Add(row.Key);
            Dispatcher.BeginInvoke(new Action(() => Save(quiet: true)));
        }

        private static Style EditStyle()
        {
            var st = new Style(typeof(TextBox));
            st.Setters.Add(new Setter(TextBox.TextWrappingProperty, TextWrapping.Wrap));
            // Thirty-two of the strings carry a line break: the dialog bodies, the
            // numbered setup steps, the game notices. Without this a translator can
            // read those breaks and not type one, so the longest strings in the
            // plugin cannot be translated. The box takes the Enter key before the
            // grid sees it, which leaves Tab and clicking away as the ways to
            // finish a row.
            st.Setters.Add(new Setter(TextBox.AcceptsReturnProperty, true));
            st.Setters.Add(new Setter(TextBox.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto));
            // The longest English string is 769 characters; past this the editor
            // scrolls rather than pushing the rest of the grid off screen.
            st.Setters.Add(new Setter(TextBox.MaxHeightProperty, 180.0));
            st.Setters.Add(new Setter(TextBox.BackgroundProperty, InputBg));
            st.Setters.Add(new Setter(TextBox.ForegroundProperty, TextFg));
            st.Setters.Add(new Setter(TextBox.BorderBrushProperty, BorderFg));
            return st;
        }

        /// <summary>Text that behaves like a link. Lighter than a button for the
        /// things a translator does once, or once they notice something.</summary>
        private static TextBlock LinkText(string text, Brush color, MouseButtonEventHandler click)
        {
            var t = new TextBlock
            {
                Text = text, Foreground = color, FontSize = 12, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = TextDecorations.Underline,
            };
            t.MouseLeftButtonUp += click;
            return t;
        }

        /// <summary>A KeyBinding needs an ICommand, and the window's actions are
        /// methods. One adapter is cheaper than a command per action.</summary>
        private sealed class DelegateCommand : ICommand
        {
            private readonly Action _run;
            public DelegateCommand(Action run) { _run = run; }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) { _run(); }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }

        /// <summary>A button in this window's theme. Through ModalButtonTheme, which
        /// replaces the template: setting Background and Foreground by hand is not
        /// enough, because WPF's own button template draws system chrome over them, so
        /// every button here rendered as a plain Windows button on a dark panel (owner,
        /// 2026-09-30: "none of the buttons on the translate pages are styled?"). The
        /// callers that want the gold accent say so with ModalButtonTheme.Primary.</summary>
        private Button MakeButton(string text, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(12, 5, 12, 5), MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
            };
            ModalButtonTheme.Secondary(b);
            b.Click += click;
            return b;
        }

        /// <summary>Every English key, with whatever this language already says for
        /// it. The English side is the table the build ships, so a key that English
        /// dropped cannot appear here and a key it added always does.
        ///
        /// Plural keys are the exception, because English's two forms are not every
        /// language's. A counted string turns into one row per form THIS language has:
        /// three for Russian, one for Japanese, six for Arabic. Both of the English rows
        /// for a base collapse into that set, so the rows follow the language being
        /// translated rather than the language it is being translated from.</summary>
        private void LoadRows()
        {
            _rows.Clear();
            if (_store == null) return;
            var existing = ReadExisting();
            var areas = new Dictionary<string, AreaGroup>(StringComparer.Ordinal);
            var built = new List<Row>();
            // key, English, room, the key the row sorts under, its place inside a plural
            // set, and whether the set it belongs to still has a hole in it.
            Action<string, string, double, string, int, bool> add =
                (key, english, room, sortKey, rank, groupEmpty) =>
            {
                string mine;
                existing.TryGetValue(key, out mine);
                var row = new Row
                {
                    Key = key, English = english,
                    Room = room,
                    Area = AreaFor(areas, key),
                    Language = _tag,
                    SortKey = sortKey, FormRank = rank, SortEmpty = groupEmpty,
                };
                row.Area.Total++;
                // Area before Text on purpose: the Text setter is what keeps Done, so
                // assigning it now counts this row exactly once.
                row.Text = mine ?? "";
                built.Add(row);
            };
            var pluralBases = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in _store.EnglishKeys)
            {
                string baseKey, form;
                if (LocPlurals.TrySplit(key, out baseKey, out form))
                {
                    // Once per base: English offers .one and .other and this would
                    // otherwise build the same set of rows twice.
                    if (!pluralBases.Add(baseKey)) continue;
                    // The budget is generated from the XAML, where a counted string is
                    // bound by its base, so every form shares the base's room.
                    double pluralRoom = LocFitBudget.Room(baseKey);
                    var forms = LocPlurals.FormsFor(_tag);
                    // One emptiness for the whole set, so a half-finished set stays
                    // together instead of one form sitting with the work and the other
                    // with the finished rows.
                    bool anyEmpty = false;
                    foreach (string f in forms)
                    {
                        string mine;
                        if (!existing.TryGetValue(baseKey + "." + f, out mine)
                            || string.IsNullOrWhiteSpace(mine)) { anyEmpty = true; break; }
                    }
                    for (int i = 0; i < forms.Count; i++)
                    {
                        string formKey = baseKey + "." + forms[i];
                        string formEnglish = _store.EnglishForPluralKey(formKey);
                        if (formEnglish == null) continue;
                        add(formKey, formEnglish, pluralRoom, baseKey, i, anyEmpty);
                    }
                    continue;
                }
                string english;
                if (!_store.TryGetEnglish(key, out english)) continue;
                string mineOne;
                existing.TryGetValue(key, out mineOne);
                add(key, english, LocFitBudget.Room(key), key, 0, string.IsNullOrWhiteSpace(mineOne));
            }
            // Ordered once, here, rather than by the view: a row that jumps the moment
            // it is filled in loses the translator their place. Untranslated first
            // inside each area, so the work is what you land on.
            // Every field compared here is fixed when the rows are built, which is what
            // makes the order hold still while a translator types in it.
            built.Sort((a, b) =>
            {
                int byArea = a.Area.Rank.CompareTo(b.Area.Rank);
                if (byArea != 0) return byArea;
                int byLabel = string.CompareOrdinal(a.Area.Label, b.Area.Label);
                if (byLabel != 0) return byLabel;
                if (a.SortEmpty != b.SortEmpty) return a.SortEmpty ? -1 : 1;
                int byKey = string.CompareOrdinal(a.SortKey, b.SortKey);
                if (byKey != 0) return byKey;
                // The forms of one counted string, in the order the language uses them:
                // the singular before the form for 2 to 4, which alphabetical order
                // would put the other way round.
                return a.FormRank.CompareTo(b.FormRank);
            });
            _rows.AddRange(built);
        }

        /// <summary>The areas in the order a translator meets them in the panel, so the
        /// first heading is the one they see first. Anything not named here follows,
        /// alphabetically, which is where the dialogs and the one-off windows land.</summary>
        private static readonly string[] AreaOrder =
        {
            "Header", "Effects", "TelemetryFfb", "Lightsync", "Presets", "PresetManager",
            "Settings", "Account", "Support", "SupportPrompt", "Welcome", "Trailer",
            "Common", "Guides", "GuideBrowser", "Translate", "LangPicker", "Plugin",
        };

        /// <summary>Words that are acronyms rather than words, so the split reads FFB
        /// and not Ffb.</summary>
        private static readonly Dictionary<string, string> AreaWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Ffb", "FFB" }, { "Oled", "OLED" }, { "Eq", "EQ" }, { "Rpm", "RPM" },
                { "Usb", "USB" }, { "Motd", "MOTD" }, { "Id8", "ID8" }, { "Abs", "ABS" },
                { "Lang", "Language" },
            };

        private static AreaGroup AreaFor(Dictionary<string, AreaGroup> areas, string key)
        {
            int cut = key.IndexOf('_');
            string prefix = cut > 0 ? key.Substring(0, cut) : key;
            AreaGroup area;
            if (areas.TryGetValue(prefix, out area)) return area;
            int rank = Array.IndexOf(AreaOrder, prefix);
            area = new AreaGroup
            {
                Prefix = prefix,
                Label = AreaLabel(prefix),
                Rank = rank >= 0 ? rank : AreaOrder.Length,
            };
            areas[prefix] = area;
            return area;
        }

        /// <summary>"TelemetryFfb" to "Telemetry FFB": the prefix with its words apart,
        /// which is as much of a name as a namespace can give.</summary>
        private static string AreaLabel(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return prefix;
            var words = new List<string>();
            var word = new StringBuilder();
            foreach (char c in prefix)
            {
                if (char.IsUpper(c) && word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
                word.Append(c);
            }
            if (word.Length > 0) words.Add(word.ToString());
            for (int i = 0; i < words.Count; i++)
            {
                string fixedUp;
                if (AreaWords.TryGetValue(words[i], out fixedUp)) words[i] = fixedUp;
                else if (i > 0) words[i] = words[i].ToLowerInvariant();
            }
            return string.Join(" ", words);
        }

        private static readonly Typeface FitFace = new Typeface("Segoe UI");

        /// <summary>Width in device-independent pixels at the size the settings panel
        /// draws at, which is the unit LocFitBudget's numbers are in. Characters would
        /// not do: "Iiii" and "MMMM" are both four.</summary>
        /// <summary>The filled part of the bar. Width in pixels rather than a ratio,
        /// because the track only knows how wide it is once it has been laid out, which
        /// is why this runs again on SizeChanged.</summary>
        private void PaintProgressBar()
        {
            if (_progressTrack == null || _progressFill == null) return;
            double w = _progressTrack.ActualWidth;
            _progressFill.Width = w <= 0 ? 0 : Math.Max(0, Math.Min(w, w * _progressDone));
        }

        private static double MeasureWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, FitFace, LocFitBudget.FontSize,
                Brushes.Black, 1.0);
            return ft.Width;
        }

        private Dictionary<string, string> ReadExisting()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string path = FilePath();
                if (path == null || !File.Exists(path)) return map;
                string name;
                var parsed = LocStore.ParseLanguageJson(File.ReadAllText(path, Encoding.UTF8), out name, m => { });
                foreach (var kv in parsed) map[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translate: could not read the existing " + _tag + " file: " + ex.Message);
            }
            return map;
        }

        private string FilePath()
            => string.IsNullOrEmpty(_root) ? null : Path.Combine(_root, _tag + ".json");

        /// <summary>Fill the selected row with the English, so a translator edits a
        /// sentence that already carries its placeholders rather than typing one from
        /// an empty box. Leaving a {0} out is the one mistake that costs the whole
        /// string at runtime, and this is the cheapest way not to make it.</summary>
        private void CopyEnglish()
        {
            var row = _grid.SelectedItem as Row;
            if (row == null) return;
            row.Text = row.English;
            // Queued here rather than left to the box losing focus: this writes the row
            // without the caret ever being in it, and the CommitEdit that used to carry
            // the queueing is gone with the grid's edit mode.
            _refused.Remove(row.Key);
            if (IsMine(row, null)) _pending.Add(row.Key);
            Save(quiet: true);
        }

        private void ApplyFilter()
        {
            string needle = (_search.Text ?? "").Trim();
            IEnumerable<Row> view = _rows;
            if (_onlyProblems) view = view.Where(r => r.Warning.Length > 0);
            if (needle.Length > 0)
                view = view.Where(r =>
                    (r.English ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.Text ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.Key ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = view.ToList();
            // Grouped by the area the rows already carry. No sort descriptions: the
            // list arrives in the order LoadRows chose and keeps it.
            var grouped = new ListCollectionView(list);
            grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Area)));
            _grid.ItemsSource = grouped;
            UpdateStatus(list.Count);
            ReportBuildCostOnce(list.Count);
        }

        /// <summary>Say once, after the window has drawn, how long it took and how many
        /// rows WPF built to do it. Opening this window froze SimHub for over 1.6 seconds
        /// and the dump put the time in DataGrid cell arrange, so the number that settles
        /// it is how many cells there were.</summary>
        private void ReportBuildCostOnce(int shown)
        {
            if (_costReported) return;
            _costReported = true;
            var started = DateTime.UtcNow;
            // At Loaded priority the first layout pass is done, so this measures the
            // stall rather than racing it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                double ms = (DateTime.UtcNow - started).TotalMilliseconds;
                _log("[TF4ALL] Translate window: " + shown + " row(s) shown, WPF built "
                     + _rowsBuilt + " of them, first layout " + ms.ToString("F0") + " ms."
                     + (_rowsBuilt > 200
                        ? " Row virtualization is NOT working: a screenful is a few dozen."
                        : ""));
            }), DispatcherPriority.Loaded);
        }

        private void UpdateStatus(int shown)
        {
            int done = _rows.Count(r => !string.IsNullOrWhiteSpace(r.Text));
            int holes = _rows.Count(r => r.HasPlaceholderProblem);
            int wide = _rows.Count(r => r.IsTooWide);
            string text = Loc.F("Translate_Progress_Fmt", done, _rows.Count, _rows.Count - done, shown);
            if (holes > 0) text += " " + Loc.N("Translate_RowsNeedPlaceholder", holes, holes);
            if (wide > 0) text += " " + Loc.N("Translate_RowsTooWide", wide, wide);
            _status.Text = text;
            _progressDone = _rows.Count == 0 ? 0 : (double)done / _rows.Count;
            PaintProgressBar();
            if (_onlyProblems)
            {
                _problemLink.Text = Loc.T("Translate_ShowAll");
                _problemLink.Visibility = Visibility.Visible;
            }
            else if (holes + wide > 0)
            {
                _problemLink.Text = Loc.T("Translate_NeedsFix");
                _problemLink.Visibility = Visibility.Visible;
            }
            else
            {
                _problemLink.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Write the root override and reload the store, so the panel
        /// behind this window is showing the edit a moment later. Quiet on the
        /// per-row saves, spoken on the button.</summary>
        private void Save(bool quiet)
        {
            string path = FilePath();
            if (path == null)
            {
                _status.Text = Loc.T("Translate_NoLanguagesFolder");
                return;
            }
            try
            {
                var obj = new JObject
                {
                    ["_meta"] = new JObject
                    {
                        ["culture"] = _tag,
                        ["name"] = string.IsNullOrWhiteSpace(_name.Text) ? _tag : _name.Text.Trim(),
                    },
                };
                // Only finished rows are written. An empty value would override
                // English with nothing and leave a blank label on screen.
                int written = 0;
                foreach (var row in _rows.OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(row.Text)) continue;
                    // A stray space at either end is invisible to whoever typed it and
                    // changes how the string renders. Trimmed, unless the English has
                    // one of its own, where the space is doing a job and theirs
                    // probably should too.
                    string value = row.Text;
                    if (row.English == null || row.English == row.English.Trim()) value = value.Trim();
                    obj[row.Key] = value;
                    written++;
                }
                // Opening this window and closing it should not leave a language file
                // holding nothing but its own name: that would show up in the picker
                // as a language at zero percent that nobody started.
                if (written == 0 && !File.Exists(path)) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // A save has to leave a readable file even if the write is
                // interrupted, so it lands beside the real one and is moved over.
                string temp = path + ".tmp";
                File.WriteAllText(temp, obj.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);

                _store.Load(_tag);
                UpdateStatus(_grid.Items.Count);
                if (!quiet) _status.Text = Loc.F("Translate_Saved_Fmt", Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translate: saving " + _tag + " failed: " + ex.Message);
                _status.Text = Loc.F("Translate_SaveFailed_Fmt", ex.Message);
            }
        }
        /// <summary>Send whatever is queued, in one batch of at most fifty. Runs every
        /// five seconds and does nothing when there is nothing to do, so a translator
        /// reading rather than typing makes no requests.
        ///
        /// A row the window already knows is wrong is held back rather than sent and
        /// refused: a mismatched placeholder would cost the whole string at runtime, and
        /// text too wide for its control would be cut off, and neither is a typo the
        /// server should have to explain.</summary>
        private async Task FlushAsync()
        {
            if (_publishing || _community == null || _pending.Count == 0) return;
            if (DateTime.UtcNow < _retryAfter) return;
            var byKey = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var r in _rows) byKey[r.Key] = r;

            var batch = new List<KeyValuePair<string, string>>();
            var taking = new List<string>();
            foreach (string key in _pending)
            {
                Row row;
                if (!byKey.TryGetValue(key, out row)) { taking.Add(key); continue; }
                if (_refused.Contains(key)) { taking.Add(key); continue; }
                if (string.IsNullOrWhiteSpace(row.Text) || row.Warning.Length > 0) continue;
                batch.Add(new KeyValuePair<string, string>(key, row.Text));
                taking.Add(key);
                if (batch.Count >= LocCommunity.SendChunk) break;
            }
            if (batch.Count == 0)
            {
                foreach (string key in taking) _pending.Remove(key);
                return;
            }

            _publishing = true;
            try
            {
                LocCommunity.SendResult result;
                try { result = await _community.SendAsync(_tag, batch); }
                catch (Exception ex)
                {
                    // Keep the queue and wait a little: a machine that is offline stays
                    // offline for longer than five seconds.
                    _status.Text = Loc.F("Translate_PublishFailed_Fmt", ex.Message);
                    _retryAfter = DateTime.UtcNow.AddMinutes(1);
                    return;
                }
                if (!result.Ok)
                {
                    // A cap, or a language closed for edits: the sentence says which, and
                    // the rows wait rather than being lost. Ten minutes, because every
                    // refusal at this level is counted per hour and asking again sooner
                    // cannot succeed.
                    _status.Text = result.Refusal ?? Loc.F("Translate_PublishFailed_Fmt", "");
                    _retryAfter = DateTime.UtcNow.AddMinutes(10);
                    return;
                }
                foreach (string key in taking) _pending.Remove(key);
                foreach (string e in result.Errors)
                {
                    _log("[TF4ALL] Translate: " + e);
                    int colon = e.IndexOf(':');
                    if (colon > 0) _refused.Add(e.Substring(0, colon));
                }
                string text = Loc.F("Translate_Published_Fmt", result.Accepted);
                if (result.Errors.Count > 0)
                    text += " " + Loc.N("Translate_PublishRefused", result.Errors.Count, result.Errors.Count);
                if (_pending.Count > 0)
                    text += " " + Loc.F("Translate_PublishQueued_Fmt", _pending.Count);
                _status.Text = text;
            }
            finally { _publishing = false; }
        }

    }
}
