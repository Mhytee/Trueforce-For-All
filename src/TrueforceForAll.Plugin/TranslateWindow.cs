// The window a translator works in: English on the left, their language on the
// right, and the panel behind it re-rendering as each row is finished.
//
// It exists because the alternative is a 2,000-key JSON file in Notepad, where a
// missing quote costs the whole language and nothing tells you which of the keys
// you have not reached yet. Here a row is a row, the filter shows what is left,
// and a save is always a valid file.
//
// What it writes is the root override, <languagesRoot>\<tag>.json, which is the
// highest-precedence layer: whatever a build ships or the community sends, the
// file a person edited on their own machine still wins. Saving reloads the store,
// so the edit reaches the panel without a restart (docs/localization-plan.md,
// Phase 2). The community Send half is Phase 3b; this window's Send explains how
// to hand the file over by issue or on Discord.
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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    internal sealed class TranslateWindow : Window
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
        private readonly string _tag;
        private readonly string _root;
        private readonly Action<string> _log;
        private readonly List<Row> _rows = new List<Row>();
        private readonly DataGrid _grid;
        private readonly TextBox _search;
        private readonly TextBox _name;
        private readonly CheckBox _untranslatedOnly;
        private readonly CheckBox _showKeys;
        private readonly CheckBox _problemsOnly;
        private readonly TextBlock _status;
        private readonly DataGridTextColumn _keyColumn;

        /// <summary>One English string and the translation being written for it.
        /// The row is the unit a translator sees, so it also carries whether its
        /// placeholders still match, which is the one mistake that silently costs
        /// the whole string at runtime.</summary>
        internal sealed class Row : INotifyPropertyChanged
        {
            private string _text;
            public string Key { get; set; }
            public string English { get; set; }

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

            /// <summary>Roughly how many characters fit, worked out from this row's own
            /// English rather than an alphabet-wide average: the string in hand is the
            /// better guide, and a translator can act on characters where pixels mean
            /// nothing.</summary>
            private int FitChars
            {
                get
                {
                    if (Room <= 0 || string.IsNullOrEmpty(English)) return 0;
                    double w = MeasureWidth(English);
                    if (w <= 0) return 0;
                    return Math.Max(1, (int)(Room / (w / English.Length)));
                }
            }

            public string Text
            {
                get => _text;
                set
                {
                    _text = value;
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

            /// <summary>Which plural form this row is, in words, or empty. A plural
            /// key ends in .one or .other, and with the key column hidden the pair
            /// reads as the same English twice unless the window says which is
            /// which.</summary>
            public string PluralForm
            {
                get
                {
                    if (Key == null) return "";
                    if (Key.EndsWith(".one", StringComparison.Ordinal)) return Loc.T("Translate_PluralOne");
                    if (Key.EndsWith(".other", StringComparison.Ordinal)) return Loc.T("Translate_PluralOther");
                    return "";
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
                    if (Room > 0) return Loc.F("Translate_TightFit_Fmt", FitChars);
                    return PluralForm;
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

        internal TranslateWindow(LocStore store, string tag, string languageName, Action<string> log)
        {
            _store = store;
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
            head.Children.Add(new TextBlock
            {
                Text = heading,
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
            });
            head.Children.Add(new TextBlock
            {
                Text = Loc.T("Translate_Intro"),
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
                Width = 220, Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(5, 3, 5, 3), FontSize = 12,
            };
            _search.TextChanged += (s, e) => ApplyFilter();
            bar.Children.Add(_search);

            _untranslatedOnly = new CheckBox
            {
                Content = Loc.T("Translate_UntranslatedOnly"), Foreground = TextFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            };
            _untranslatedOnly.Checked += (s, e) => ApplyFilter();
            _untranslatedOnly.Unchecked += (s, e) => ApplyFilter();
            bar.Children.Add(_untranslatedOnly);

            // Off by default: a key column turns a sentence into a name, and a
            // translator has no use for it until they are reporting one.
            _showKeys = new CheckBox
            {
                Content = Loc.T("Translate_ShowKeys"), Foreground = TextFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
                IsChecked = false,
            };
            _showKeys.Checked += (s, e) => UpdateKeyColumn();
            _showKeys.Unchecked += (s, e) => UpdateKeyColumn();
            bar.Children.Add(_showKeys);

            // The placeholder problems, on their own. A translator can clear them
            // before sending instead of finding out from a fallback later.
            _problemsOnly = new CheckBox
            {
                Content = Loc.T("Translate_NeedsFix"), Foreground = TextFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            };
            _problemsOnly.Checked += (s, e) => ApplyFilter();
            _problemsOnly.Unchecked += (s, e) => ApplyFilter();
            bar.Children.Add(_problemsOnly);

            bar.Children.Add(new TextBlock
            {
                Text = Loc.T("Translate_LanguageName"), Foreground = MutedFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0),
            });
            _name = new TextBox
            {
                Width = 150, Text = languageName ?? tag, MaxLength = 64,
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(5, 3, 5, 3), FontSize = 12,
                ToolTip = Loc.T("Translate_LanguageName_Tip"),
            };
            bar.Children.Add(_name);
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                Background = PanelBg,
                Foreground = TextFg,
                RowBackground = PanelBg,
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)),
                HorizontalGridLinesBrush = BorderFg,
                BorderBrush = BorderFg,
                EnableRowVirtualization = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                SelectionMode = DataGridSelectionMode.Single,
            };
            _keyColumn = new DataGridTextColumn
            {
                Header = Loc.T("Translate_ColumnKey"),
                Binding = new Binding(nameof(Row.Key)),
                IsReadOnly = true,
                Width = new DataGridLength(200),
                Visibility = Visibility.Collapsed,
            };
            _grid.Columns.Add(_keyColumn);
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
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Loc.F("Translate_ColumnYours_Fmt", tag),
                Binding = new Binding(nameof(Row.Text)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                ElementStyle = WrapStyle(),
                EditingElementStyle = EditStyle(),
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Loc.T("Translate_ColumnNote"),
                Binding = new Binding(nameof(Row.Note)),
                IsReadOnly = true,
                Width = new DataGridLength(190),
                ElementStyle = NoteStyle(),
            });
            // A committed cell is a finished row: write the file and reload, which
            // is what makes the panel behind this window change as you work.
            _grid.CellEditEnding += (s, e) =>
            {
                if (e.EditAction != DataGridEditAction.Commit) return;
                var box = e.EditingElement as TextBox;
                var row = e.Row?.Item as Row;
                if (box != null && row != null) row.Text = box.Text;
                Dispatcher.BeginInvoke(new Action(() => Save(quiet: true)));
            };
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            _status = new TextBlock
            {
                Text = "", Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            Grid.SetRow(_status, 3);
            root.Children.Add(_status);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            buttons.Children.Add(MakeButton(Loc.T("Translate_CopyEnglish"), (s, e) => CopyEnglish()));
            buttons.Children.Add(MakeButton(Loc.T("Translate_Send"), (s, e) => Send()));
            buttons.Children.Add(MakeButton(Loc.T("Translate_OpenFolder"), (s, e) => OpenFolder()));
            buttons.Children.Add(MakeButton(Loc.T("Translate_Save"), (s, e) => Save(quiet: false)));
            var close = MakeButton(Loc.T("Settings_Close"), (s, e) => Close());
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
            };

            LoadRows();
            ApplyFilter();
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

        private Button MakeButton(string text, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(12, 5, 12, 5), MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0), Background = InputBg, Foreground = TextFg,
                BorderBrush = BorderFg,
            };
            b.Click += click;
            return b;
        }

        /// <summary>Every English key, with whatever this language already says for
        /// it. The English side is the table the build ships, so a key that English
        /// dropped cannot appear here and a key it added always does.</summary>
        private void LoadRows()
        {
            _rows.Clear();
            if (_store == null) return;
            var existing = ReadExisting();
            foreach (string key in _store.EnglishKeys)
            {
                string english;
                if (!_store.TryGetEnglish(key, out english)) continue;
                string mine;
                existing.TryGetValue(key, out mine);
                _rows.Add(new Row
                {
                    Key = key, English = english, Text = mine ?? "",
                    Room = LocFitBudget.Room(key),
                });
            }
        }

        private static readonly Typeface FitFace = new Typeface("Segoe UI");

        /// <summary>Width in device-independent pixels at the size the settings panel
        /// draws at, which is the unit LocFitBudget's numbers are in. Characters would
        /// not do: "Iiii" and "MMMM" are both four.</summary>
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
            try { _grid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }
            row.Text = row.English;
            Save(quiet: true);
        }

        private void UpdateKeyColumn()
            => _keyColumn.Visibility = _showKeys.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        private void ApplyFilter()
        {
            string needle = (_search.Text ?? "").Trim();
            bool onlyEmpty = _untranslatedOnly.IsChecked == true;
            IEnumerable<Row> view = _rows;
            if (onlyEmpty) view = view.Where(r => string.IsNullOrWhiteSpace(r.Text));
            if (_problemsOnly.IsChecked == true) view = view.Where(r => r.Warning.Length > 0);
            if (needle.Length > 0)
                view = view.Where(r =>
                    (r.English ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.Text ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.Key ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = view.ToList();
            _grid.ItemsSource = list;
            UpdateStatus(list.Count);
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

        private void OpenFolder()
        {
            try
            {
                string path = FilePath();
                if (path != null && File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (!string.IsNullOrEmpty(_root))
                    Process.Start(new ProcessStartInfo(_root) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translate: opening the languages folder failed: " + ex.Message);
            }
        }

        /// <summary>Phase 2's hand-over: save, show the translator where the file
        /// is and what to do with it. Phase 3b replaces this with a submit to the
        /// community service, at which point this text becomes the fallback for
        /// someone who would rather not sign in.</summary>
        private void Send()
        {
            Save(quiet: true);
            string path = FilePath();
            bool go = TrueforceDialog.Show(this,
                Loc.T("Translate_SendTitle"),
                Loc.F("Translate_SendBody_Fmt", path ?? _tag + ".json"),
                DialogKind.Info,
                Loc.T("Translate_SendOpenIssue"), Loc.T("Common_Cancel"), goldOk: true) == true;
            OpenFolder();
            if (!go) return;
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/Mhytee/Trueforce-For-All/issues/new")
                { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log("[TF4ALL] Translate: opening the issue page failed: " + ex.Message);
            }
        }
    }
}
