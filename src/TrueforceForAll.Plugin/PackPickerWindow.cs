// Modal dialog used by the "Export" flow: lets the user pick which presets
// and car presets to bundle into a .tfpack zip. Two side-by-side CheckBox
// lists with Select all / Select none controls; OK gates on at least one
// item being selected.
//
// The caller is responsible for filtering built-ins out of the lists before
// constructing this window (built-ins ship with the plugin, so every
// recipient already has them). Pass preferCarId to pre-check just the
// presets belonging to the active car; everything else defaults unchecked.
//
// The car list is grouped by GameName with bold separators. Checking a
// preset on the left filters the car list to only the games that preset
// is the default for (from BuiltinPresets.GameDefaultBindings + the user's
// Settings.GameDefaults, passed in via presetGameMappings). When no
// presets are checked, every group stays visible.
//
// Built in code (not XAML) because the dialog is single-use and the plugin's
// XAML build pipeline doesn't auto-pick up new .xaml files in this folder
// without a corresponding .csproj change. A few dozen lines of WPF here is
// cheaper than wiring a whole new compiled-XAML resource.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class PackPickerWindow : Window
    {
        // Palette. Hard-coded because this window is its own top-level WPF
        // Window and so doesn't inherit SimHub's panel-level dark theme; if
        // we leave foregrounds at their system defaults the text comes
        // out black-on-dark and is unreadable.
        private static readonly Brush WindowBg   = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg    = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg     = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg    = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush BorderFg   = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush GroupHeaderFg = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));

        public enum ExportOutputMode { Pack, FolderOfFiles }

        public List<string> SelectedPresetNames { get; private set; } = new List<string>();
        public List<CarPresetEntry> SelectedCarPresets { get; private set; } = new List<CarPresetEntry>();
        // Set on OK click in export mode. Pack name is captured only when
        // OutputMode == Pack (validated non-empty before DialogResult=true).
        // In import mode both fields stay at their defaults.
        public ExportOutputMode SelectedOutputMode { get; private set; } = ExportOutputMode.Pack;
        public string PackName { get; private set; }

        private readonly List<CheckBox> _presetChecks = new List<CheckBox>();
        private readonly List<(CheckBox Cb, CarPresetEntry Entry)> _carChecks = new List<(CheckBox, CarPresetEntry)>();

        // (game key, header TextBlock, content StackPanel). Game key is
        // GameName lowercased; empty key marks the "(other)" group at the
        // bottom for cars without an associated game.
        private readonly List<(string GameKey, FrameworkElement Header, FrameworkElement Content)> _carGameGroups
            = new List<(string, FrameworkElement, FrameworkElement)>();

        // preset name -> games (lowercased) it's bound to as a default.
        // Drives the "filter car panel by checked presets" interaction.
        private readonly Dictionary<string, HashSet<string>> _presetGameMap;

        private TextBlock _carCountText;

        public PackPickerWindow(List<string> presets, List<CarPresetEntry> cars, bool exportMode, string preferCarId = null,
            IReadOnlyDictionary<string, IReadOnlyList<string>> presetGameMappings = null)
        {
            // Normalize the mapping to a case-insensitive HashSet-per-preset
            // so filter checks stay O(1) and don't trip over GameName casing.
            _presetGameMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (presetGameMappings != null)
            {
                foreach (var kv in presetGameMappings)
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (kv.Value != null) foreach (var g in kv.Value) if (!string.IsNullOrEmpty(g)) set.Add(g);
                    _presetGameMap[kv.Key] = set;
                }
            }

            Title = exportMode ? "Export" : "Import";
            Width = 760;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 560;
            MinHeight = 360;
            Background = WindowBg;
            Foreground = TextFg;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                            // header text
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });       // picker
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                            // output-mode (export only)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                            // button row

            var header = new TextBlock
            {
                Text = "Pick the presets to bundle into the pack. Recipients can import the whole pack at once.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = TextFg,
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var twoCol = new Grid();
            twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(twoCol, 1);
            root.Children.Add(twoCol);

            twoCol.Children.Add(BuildPresetPanel(presets));
            var carPanel = BuildCarPanel(cars, preferCarId);
            Grid.SetColumn(carPanel, 1);
            twoCol.Children.Add(carPanel);

            // Output-mode selector + pack name input (export mode only).
            // Two radios: bundle as one .tfpack archive, OR write loose
            // .tfpreset.json / .tfcar.json files into a folder. The pack
            // name TextBox is enabled only while the Pack radio is selected
            // (it tags every contained file with the pack identity).
            RadioButton rbPack = null;
            RadioButton rbFolder = null;
            TextBox tbPackName = null;
            if (exportMode)
            {
                var modeRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                modeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                modeRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                rbPack = new RadioButton
                {
                    Content    = "Bundle as pack (.tfpack)",
                    GroupName  = "ExportOutputMode",
                    IsChecked  = true,
                    Foreground = TextFg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                rbFolder = new RadioButton
                {
                    Content    = "Loose files in a folder",
                    GroupName  = "ExportOutputMode",
                    Foreground = TextFg,
                    Margin     = new Thickness(0, 6, 0, 0),
                };

                // Pack radio + Pack-name input share one row so the
                // dependency between them reads at a glance. TextBox
                // enables/disables to mirror the selected mode.
                var packNameLbl = new TextBlock
                {
                    Text              = "Pack name:",
                    Foreground        = TextFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(16, 0, 8, 0),
                };
                tbPackName = new TextBox
                {
                    Background        = PanelBg,
                    Foreground        = TextFg,
                    BorderBrush       = BorderFg,
                    Padding           = new Thickness(4, 2, 4, 2),
                    MinWidth          = 220,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var topRow = new Grid();
                topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(rbPack,      0); topRow.Children.Add(rbPack);
                Grid.SetColumn(packNameLbl, 1); topRow.Children.Add(packNameLbl);
                Grid.SetColumn(tbPackName,  2); topRow.Children.Add(tbPackName);
                Grid.SetRow(topRow, 0);
                modeRow.Children.Add(topRow);

                Grid.SetRow(rbFolder, 1);
                modeRow.Children.Add(rbFolder);

                // Enable/disable pack-name TextBox to match selected mode.
                rbPack.Checked   += (s, e) => { tbPackName.IsEnabled = true; };
                rbFolder.Checked += (s, e) => { tbPackName.IsEnabled = false; };

                Grid.SetRow(modeRow, 2);
                root.Children.Add(modeRow);
            }

            // OK / Cancel row.
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok = new Button { Content = exportMode ? "Export…" : "Import", Width = 110, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            Grid.SetRow(btnRow, 3);
            root.Children.Add(btnRow);

            ok.Click += (s, e) =>
            {
                SelectedPresetNames = new List<string>();
                foreach (var cb in _presetChecks)
                    if (cb.IsChecked == true) SelectedPresetNames.Add((string)cb.Tag);

                SelectedCarPresets = new List<CarPresetEntry>();
                foreach (var (cb, entry) in _carChecks)
                    if (cb.IsChecked == true) SelectedCarPresets.Add(entry);

                if (SelectedPresetNames.Count == 0 && SelectedCarPresets.Count == 0)
                {
                    MessageBox.Show(this, "Pick at least one preset or car preset to include.",
                                    "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (exportMode)
                {
                    SelectedOutputMode = rbFolder?.IsChecked == true
                        ? ExportOutputMode.FolderOfFiles
                        : ExportOutputMode.Pack;
                    if (SelectedOutputMode == ExportOutputMode.Pack)
                    {
                        var name = (tbPackName?.Text ?? "").Trim();
                        if (string.IsNullOrEmpty(name))
                        {
                            MessageBox.Show(this, "Pack name is required when bundling as a pack.",
                                            "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
                            tbPackName?.Focus();
                            return;
                        }
                        PackName = name;
                    }
                    else
                    {
                        PackName = null;
                    }
                }

                DialogResult = true;
            };

            Content = root;

            // Initial pass: any pre-checked preset (none today, but cheap)
            // would seed the filter immediately.
            UpdateCarGroupVisibility();
        }

        private UIElement BuildPresetPanel(List<string> presets)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 0, 6, 0) };

            var header = BuildPanelHeader($"Presets ({presets.Count})");
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);

            var toggleRow = BuildToggleRow(
                onAll:  () => { foreach (var cb in _presetChecks) cb.IsChecked = true;  },
                onNone: () => { foreach (var cb in _presetChecks) cb.IsChecked = false; });
            DockPanel.SetDock(toggleRow, Dock.Top);
            panel.Children.Add(toggleRow);

            var listSp = new StackPanel();
            if (presets.Count == 0)
            {
                listSp.Children.Add(new TextBlock
                {
                    Text = "No user presets in your library yet.",
                    Foreground = MutedFg,
                    Margin = new Thickness(4, 6, 0, 0),
                });
            }
            else
            {
                foreach (var name in presets)
                {
                    var cb = new CheckBox
                    {
                        Content = name,
                        Tag = name,
                        Margin = new Thickness(2, 3, 2, 3),
                        IsChecked = false,
                        Foreground = TextFg,
                    };
                    cb.Checked   += (s, e) => UpdateCarGroupVisibility();
                    cb.Unchecked += (s, e) => UpdateCarGroupVisibility();
                    _presetChecks.Add(cb);
                    listSp.Children.Add(cb);
                }
            }
            panel.Children.Add(WrapInScrolledBorder(listSp));
            return panel;
        }

        private UIElement BuildCarPanel(List<CarPresetEntry> cars, string preferCarId)
        {
            var panel = new DockPanel { Margin = new Thickness(6, 0, 0, 0) };

            _carCountText = new TextBlock
            {
                Text = $"Car presets ({cars.Count})",
                FontWeight = FontWeights.SemiBold,
                Foreground = TextFg,
                Margin = new Thickness(2, 0, 2, 6),
            };
            DockPanel.SetDock(_carCountText, Dock.Top);
            panel.Children.Add(_carCountText);

            var toggleRow = BuildToggleRow(
                onAll:  () => { foreach (var pair in _carChecks) if (IsEffectivelyVisible(pair.Cb)) pair.Cb.IsChecked = true;  },
                onNone: () => { foreach (var pair in _carChecks) if (IsEffectivelyVisible(pair.Cb)) pair.Cb.IsChecked = false; });
            DockPanel.SetDock(toggleRow, Dock.Top);
            panel.Children.Add(toggleRow);

            var listSp = new StackPanel();
            if (cars.Count == 0)
            {
                listSp.Children.Add(new TextBlock
                {
                    Text = "No car presets saved yet.",
                    Foreground = MutedFg,
                    Margin = new Thickness(4, 6, 0, 0),
                });
                panel.Children.Add(WrapInScrolledBorder(listSp));
                return panel;
            }

            // Group cars by GameName. Empty / null GameName lands in the
            // "Other" group, rendered at the bottom.
            var groups = new Dictionary<string, List<CarPresetEntry>>(StringComparer.OrdinalIgnoreCase);
            var noGame = new List<CarPresetEntry>();
            foreach (var entry in cars)
            {
                string g = entry.GameName;
                if (string.IsNullOrEmpty(g)) { noGame.Add(entry); continue; }
                if (!groups.TryGetValue(g, out var list))
                {
                    list = new List<CarPresetEntry>();
                    groups[g] = list;
                }
                list.Add(entry);
            }

            var orderedGames = new List<string>(groups.Keys);
            orderedGames.Sort(StringComparer.OrdinalIgnoreCase);

            bool hasPrefer = !string.IsNullOrEmpty(preferCarId);

            void RenderGroup(string gameKey, string headerLabel, List<CarPresetEntry> entries)
            {
                var groupHeader = new TextBlock
                {
                    Text = headerLabel,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GroupHeaderFg,
                    Margin = new Thickness(2, _carGameGroups.Count == 0 ? 0 : 10, 2, 4),
                };
                var content = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
                foreach (var entry in entries)
                {
                    string label = $"{entry.CarId} · {entry.PresetName}";
                    bool preselect = hasPrefer && string.Equals(entry.CarId, preferCarId, StringComparison.OrdinalIgnoreCase);
                    var cb = new CheckBox
                    {
                        Content = label,
                        Margin = new Thickness(2, 3, 2, 3),
                        IsChecked = preselect,
                        Foreground = TextFg,
                    };
                    _carChecks.Add((cb, entry));
                    content.Children.Add(cb);
                }
                listSp.Children.Add(groupHeader);
                listSp.Children.Add(content);
                _carGameGroups.Add((gameKey, groupHeader, content));
            }

            foreach (var gameName in orderedGames)
                RenderGroup(gameName.ToLowerInvariant(), gameName, groups[gameName]);

            // No-game cars: render last with the "" key so the filter logic
            // hides them when a specific game is selected (they don't match
            // any game). Users can clear the filter to bring them back.
            if (noGame.Count > 0)
                RenderGroup("", "Other", noGame);

            panel.Children.Add(WrapInScrolledBorder(listSp));
            return panel;
        }

        // Recompute the "selected games" set from the preset checkboxes and
        // hide / show car-group panels accordingly. With no presets checked,
        // every group stays visible.
        private void UpdateCarGroupVisibility()
        {
            var selectedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cb in _presetChecks)
            {
                if (cb.IsChecked != true) continue;
                if (cb.Tag is string name
                    && _presetGameMap.TryGetValue(name, out var games))
                {
                    foreach (var g in games) selectedGames.Add(g);
                }
            }

            bool filterActive = selectedGames.Count > 0;
            int visibleCount = 0;
            foreach (var (gameKey, header, content) in _carGameGroups)
            {
                bool show = !filterActive || (!string.IsNullOrEmpty(gameKey) && selectedGames.Contains(gameKey));
                var want = show ? Visibility.Visible : Visibility.Collapsed;
                if (header.Visibility != want)  header.Visibility  = want;
                if (content.Visibility != want) content.Visibility = want;
                if (show)
                {
                    if (content is StackPanel sp)
                        visibleCount += sp.Children.Count;
                }
            }

            if (_carCountText != null)
            {
                int total = _carChecks.Count;
                _carCountText.Text = filterActive
                    ? $"Car presets ({visibleCount} of {total})"
                    : $"Car presets ({total})";
            }
        }

        // Used by Select all / Select none in the car panel: only toggle
        // boxes whose group is currently visible. Walks up to the group's
        // StackPanel for the visibility check.
        private static bool IsEffectivelyVisible(FrameworkElement el)
        {
            for (var cur = el; cur != null; cur = cur.Parent as FrameworkElement)
                if (cur.Visibility != Visibility.Visible) return false;
            return true;
        }

        private static UIElement WrapInScrolledBorder(UIElement child)
        {
            var sv = new ScrollViewer
            {
                Content = child,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8, 4, 8, 4),
                Background = PanelBg,
            };
            return new Border
            {
                Child = sv,
                Background = PanelBg,
                BorderBrush = BorderFg,
                BorderThickness = new Thickness(1),
            };
        }

        private static UIElement BuildPanelHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextFg,
                Margin = new Thickness(2, 0, 2, 6),
            };
        }

        // The Select all / Select none row. Buttons sized to fit their text
        // cleanly (the earlier 22 px height clipped descenders on some fonts).
        private static UIElement BuildToggleRow(Action onAll, Action onNone)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var all  = new Button { Content = "Select all",  Width = 96, Height = 26, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
            var none = new Button { Content = "Select none", Width = 96, Height = 26, FontSize = 11 };
            all.Click  += (s, e) => onAll();
            none.Click += (s, e) => onNone();
            row.Children.Add(all);
            row.Children.Add(none);
            return row;
        }
    }
}
