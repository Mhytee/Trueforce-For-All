// Modal dialog used by the "Manage installed packs..." action in the Preset
// Manager. Lists every pack the user has imported (from installed-packs.json)
// and exposes two actions per pack: "Set pack as defaults" (walks every entry
// and writes game/car-default bindings; summary toast at the end) and "Remove
// pack" (destructive: deletes each on-disk entry whose hash still matches the
// install-time BaselineHash, leaves user-edited entries in place, then drops
// the pack record). Pack metadata is deliberately read-only here. allowing
// edits would let a user misattribute someone else's pack.
//
// Built in code (not XAML) for the same reason PackPickerWindow is: the XAML
// build pipeline doesn't auto-pick up new .xaml files without a csproj entry,
// and a few hundred lines of WPF here is cheaper than wiring a whole new
// compiled-XAML resource.
//
// Host coupling is via two delegates supplied by TrueforcePlugin: one to
// perform "set as defaults" for a given pack (returns a SetDefaultsSummary)
// and one to perform "remove pack" (returns a RemovePackSummary). The window
// reloads its list after each action so subsequent actions see fresh state.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class PackManagerWindow : Window
    {
        // Match PackPickerWindow's palette so the two dialogs read as one
        // family. Hard-coded because this window is its own top-level WPF
        // Window and doesn't inherit SimHub's panel-level dark theme.
        private static readonly Brush WindowBg     = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg       = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg     = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush BorderFg    = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush DangerFg    = new SolidColorBrush(Color.FromRgb(0xC8, 0x6A, 0x6A));

        private readonly Func<InstalledPacksFile> _loadPacks;
        private readonly Func<InstalledPack, SetDefaultsConflictPolicy, SetDefaultsSummary> _setAsDefaults;
        private readonly Func<InstalledPack, SetDefaultsPreview> _previewDefaults;
        private readonly Func<InstalledPack, RemovePackSummary>  _removePack;

        private readonly ListBox _packList;
        private readonly StackPanel _detailsPanel;
        private readonly Button _setDefaultsButton;
        private readonly Button _removeButton;
        private readonly TextBlock _statusText;

        public PackManagerWindow(
            Func<InstalledPacksFile> loadPacks,
            Func<InstalledPack, SetDefaultsConflictPolicy, SetDefaultsSummary> setAsDefaults,
            Func<InstalledPack, SetDefaultsPreview> previewDefaults,
            Func<InstalledPack, RemovePackSummary>  removePack)
        {
            _loadPacks       = loadPacks       ?? throw new ArgumentNullException(nameof(loadPacks));
            _setAsDefaults   = setAsDefaults   ?? throw new ArgumentNullException(nameof(setAsDefaults));
            _previewDefaults = previewDefaults ?? throw new ArgumentNullException(nameof(previewDefaults));
            _removePack      = removePack      ?? throw new ArgumentNullException(nameof(removePack));

            Title = "Manage installed packs";
            Width = 820;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 620;
            MinHeight = 380;
            Background = WindowBg;
            Foreground = TextFg;

            // Root: header, then a 2-column split (list / details), then a button row.
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "Installed packs",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextFg,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

            _packList = new ListBox
            {
                Background = PanelBg,
                Foreground = TextFg,
                BorderBrush = BorderFg,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            _packList.SelectionChanged += (s, e) => UpdateDetailsPanel();
            Grid.SetColumn(_packList, 0);
            split.Children.Add(_packList);

            var detailsBorder = new Border
            {
                Background = PanelBg,
                BorderBrush = BorderFg,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10),
            };
            _detailsPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                Content = _detailsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            detailsBorder.Child = scroll;
            Grid.SetColumn(detailsBorder, 1);
            split.Children.Add(detailsBorder);

            Grid.SetRow(split, 1);
            root.Children.Add(split);

            // Status line for the last action's summary. Wraps so a long
            // overwrote / kept / skipped summary stays fully readable instead of
            // being clipped with an ellipsis on a narrow window; the row is
            // Auto-height so it grows to fit.
            _statusText = new TextBlock
            {
                Foreground = MutedFg,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(_statusText, 2);
            root.Children.Add(_statusText);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            _setDefaultsButton = new Button
            {
                Content = "Set pack as defaults",
                Width = 170,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = PanelBg,
                Foreground = TextFg,
                BorderBrush = BorderFg,
                IsEnabled = false,
                ToolTip = "Walk every preset in the selected pack and set it as the default for its game / car. If any already have a default, you'll be asked whether to overwrite or keep them.",
            };
            _setDefaultsButton.Click += (s, e) => OnSetDefaultsClicked();

            _removeButton = new Button
            {
                Content = "Remove pack…",
                Width = 140,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = PanelBg,
                Foreground = DangerFg,
                BorderBrush = BorderFg,
                IsEnabled = false,
                ToolTip = "Destructive: deletes the pack record AND every preset / car preset it installed. Entries you have edited since import (hash mismatch) are kept.",
            };
            _removeButton.Click += (s, e) => OnRemoveClicked();

            var closeButton = new Button
            {
                Content = "Close",
                Width = 90,
                Height = 28,
                Background = PanelBg,
                Foreground = TextFg,
                BorderBrush = BorderFg,
            };
            closeButton.Click += (s, e) => { DialogResult = true; Close(); };

            buttonRow.Children.Add(_setDefaultsButton);
            buttonRow.Children.Add(_removeButton);
            buttonRow.Children.Add(closeButton);
            Grid.SetRow(buttonRow, 3);
            root.Children.Add(buttonRow);

            Content = root;
            RefreshList(selectAfter: null);
        }

        // Pull packs from disk and rebuild the ListBox items. selectAfter, if
        // non-null and still present, is re-selected so a refresh after an
        // action doesn't snap the user back to the top of the list (relevant
        // for "Set as defaults" which leaves the pack intact).
        private void RefreshList(InstalledPack selectAfter)
        {
            var file = _loadPacks();
            _packList.Items.Clear();
            if (file?.Packs != null)
            {
                foreach (var p in file.Packs.OrderByDescending(p => p.ImportedAt))
                    _packList.Items.Add(BuildPackRow(p));
            }
            if (_packList.Items.Count == 0)
            {
                // Empty placeholder: a non-selectable disabled item explains
                // why the action buttons are off rather than leaving a blank
                // panel.
                _packList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = "No packs installed yet. Import a .tfpack via the Preset Manager to populate this list.",
                        Foreground = MutedFg,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    IsEnabled = false,
                });
            }
            if (selectAfter != null)
            {
                foreach (var item in _packList.Items.OfType<ListBoxItem>())
                {
                    if (item.Tag is InstalledPack ip && ReferenceEquals(ip, selectAfter))
                    {
                        _packList.SelectedItem = item;
                        break;
                    }
                }
            }
            UpdateDetailsPanel();
        }

        private ListBoxItem BuildPackRow(InstalledPack p)
        {
            int gameCount = p.Entries?.Count(e => e?.Kind == InstalledPackEntry.KindGame) ?? 0;
            int carCount  = p.Entries?.Count(e => e?.Kind == InstalledPackEntry.KindCar)  ?? 0;
            string title  = string.IsNullOrEmpty(p.PackName) ? "(unnamed pack)" : p.PackName;
            string author = string.IsNullOrEmpty(p.Author) ? "" : $"  ·  {UiContentSanitizer.SafeDisplayText(p.Author, 96)}";
            string ver    = string.IsNullOrEmpty(p.AuthorVersion) ? "" : $"  ·  v{UiContentSanitizer.SafeDisplayText(p.AuthorVersion, 32)}";
            string counts = $"{gameCount} game · {carCount} car";

            var stack = new StackPanel { Margin = new Thickness(2) };
            stack.Children.Add(new TextBlock
            {
                Text = title + author + ver,
                Foreground = TextFg,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{p.ImportedAt:yyyy-MM-dd HH:mm}  ·  {counts}",
                Foreground = MutedFg,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return new ListBoxItem
            {
                Content = stack,
                Tag = p, // identifies the row in OnSetDefaults / OnRemove
                Padding = new Thickness(6),
                Foreground = TextFg,
            };
        }

        private InstalledPack SelectedPack()
        {
            if (_packList.SelectedItem is ListBoxItem item && item.Tag is InstalledPack p) return p;
            return null;
        }

        private void UpdateDetailsPanel()
        {
            _detailsPanel.Children.Clear();
            var p = SelectedPack();
            bool hasSel = p != null;
            _setDefaultsButton.IsEnabled = hasSel;
            _removeButton.IsEnabled      = hasSel;
            if (!hasSel)
            {
                _detailsPanel.Children.Add(new TextBlock
                {
                    Text = "Select a pack to see its contents.",
                    Foreground = MutedFg,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            // Header line (name + version, large).
            string headerText = string.IsNullOrEmpty(p.PackName) ? "(unnamed pack)" : UiContentSanitizer.SafeDisplayText(p.PackName, 128);
            if (!string.IsNullOrEmpty(p.AuthorVersion)) headerText += $"  v{UiContentSanitizer.SafeDisplayText(p.AuthorVersion, 32)}";
            _detailsPanel.Children.Add(new TextBlock
            {
                Text = headerText,
                Foreground = TextFg,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });

            AddDetailRow("Author",      string.IsNullOrEmpty(p.Author)        ? "(unknown)" : UiContentSanitizer.SafeDisplayText(p.Author, 96));
            AddDetailRow("Imported",    p.ImportedAt.ToString("yyyy-MM-dd HH:mm"));
            if (!string.IsNullOrEmpty(p.Description))
                AddDetailRow("Description", UiContentSanitizer.SafeMultiLineText(p.Description, 1024, 10) ?? "(no description)", wrap: true);

            int gameCount = p.Entries?.Count(e => e?.Kind == InstalledPackEntry.KindGame) ?? 0;
            int carCount  = p.Entries?.Count(e => e?.Kind == InstalledPackEntry.KindCar)  ?? 0;
            AddDetailRow("Contents", $"{gameCount} game preset(s), {carCount} car preset(s)");

            // List the actual entry names so the user knows what they're
            // about to remove or default-bind. Truncated visually if it grows
            // (ListBox parent is in a ScrollViewer).
            if (p.Entries != null && p.Entries.Count > 0)
            {
                var entryList = new TextBlock
                {
                    Foreground = MutedFg,
                    FontSize = 11,
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                foreach (var e in p.Entries)
                {
                    if (e == null) continue;
                    if (e.Kind == InstalledPackEntry.KindGame)
                        entryList.Inlines.Add(new Run($"game · {e.Name}\n"));
                    else if (e.Kind == InstalledPackEntry.KindCar)
                        entryList.Inlines.Add(new Run($"car · {e.CarId} / {e.PresetName}\n"));
                }
                _detailsPanel.Children.Add(entryList);
            }
        }

        // Two-column label / value row used by the details panel. wrap = true
        // is for long values like the pack description.
        private void AddDetailRow(string label, string value, bool wrap = false)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lab = new TextBlock { Text = label, Foreground = MutedFg, FontSize = 12 };
            Grid.SetColumn(lab, 0);
            row.Children.Add(lab);

            var val = new TextBlock
            {
                Text = value,
                Foreground = TextFg,
                FontSize = 12,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(val, 1);
            row.Children.Add(val);
            _detailsPanel.Children.Add(row);
        }

        private void OnSetDefaultsClicked()
        {
            var p = SelectedPack();
            if (p == null) return;

            // Warn before clobbering existing defaults: overwrite all, skip only
            // the conflicting ones, or cancel. No prompt when nothing conflicts.
            var preview = _previewDefaults(p);
            SetDefaultsConflictPolicy policy = SetDefaultsConflictPolicy.OverwriteAll;
            if (preview.ConflictCount > 0)
            {
                int total = preview.FreshCount + preview.ConflictCount;
                var choice = TrueforceDialog.ShowChoice(this,
                    "Set pack as defaults",
                    $"This pack sets {total} default binding(s). {preview.ConflictCount} of them already have a default for that game or car.\n\n"
                    + "\"Overwrite all\" replaces every existing default. \"Skip existing\" keeps your current defaults and only fills in the ones you haven't set yet.",
                    primaryLabel:   "Overwrite all",
                    secondaryLabel: "Skip existing",
                    cancelLabel:    "Cancel");
                if (choice == DialogChoice.Cancel) return;
                policy = choice == DialogChoice.Primary
                    ? SetDefaultsConflictPolicy.OverwriteAll
                    : SetDefaultsConflictPolicy.SkipConflicts;
            }

            try
            {
                var summary = _setAsDefaults(p, policy);
                _statusText.Text = FormatSetDefaultsSummary(summary);
                _statusText.Foreground = TextFg;
                RefreshList(selectAfter: p);
            }
            catch (Exception ex)
            {
                _statusText.Text = "Couldn't set the defaults. See the SimHub log, then try again.";
                TrueforceDialog.LogError("Set pack defaults", ex);
                _statusText.Foreground = DangerFg;
            }
        }

        private void OnRemoveClicked()
        {
            var p = SelectedPack();
            if (p == null) return;
            int gameCount = p.Entries?.Count(e => e?.Kind == InstalledPackEntry.KindGame) ?? 0;
            int carCount  = p.Entries?.Count(e => e?.Kind == InstalledPackEntry.KindCar)  ?? 0;
            string packLabel = string.IsNullOrEmpty(p.PackName) ? "this pack" : $"'{p.PackName}'";
            if (TrueforceDialog.Show(
                this,
                "Remove pack",
                $"Remove {packLabel} and delete every preset it installed " +
                $"({gameCount} game, {carCount} car)?\n\n" +
                $"Entries you have edited since import will be kept.\n\n" +
                $"This cannot be undone.",
                DialogKind.Destructive,
                okLabel: "Remove",
                cancelLabel: "Cancel") != true) return;

            try
            {
                var summary = _removePack(p);
                _statusText.Text = FormatRemoveSummary(p, summary);
                _statusText.Foreground = TextFg;
                RefreshList(selectAfter: null);
            }
            catch (Exception ex)
            {
                _statusText.Text = "Couldn't remove the pack. See the SimHub log, then try again.";
                TrueforceDialog.LogError("Remove pack", ex);
                _statusText.Foreground = DangerFg;
            }
        }

        private static string FormatSetDefaultsSummary(SetDefaultsSummary s)
        {
            if (s == null) return "";
            int gOver = s.GameDefaultsOverwritten;
            int cOver = s.CarDefaultsOverwritten;
            string overText = (gOver + cOver) > 0 ? $" (overwrote {gOver + cOver} existing)" : "";
            int kept = s.GameDefaultsSkippedConflict + s.CarDefaultsSkippedConflict;
            string keptText = kept > 0 ? $" · kept {kept} existing default(s)" : "";
            string skipText = s.GamePresetsSkipped > 0
                ? $" · skipped {s.GamePresetsSkipped} game preset(s) without a game mapping"
                : "";
            return $"Set defaults: {s.GameDefaultsSet} game, {s.CarDefaultsSet} car{overText}{keptText}{skipText}.";
        }

        private static string FormatRemoveSummary(InstalledPack p, RemovePackSummary s)
        {
            if (s == null) return "";
            string packLabel = string.IsNullOrEmpty(p.PackName) ? "pack" : $"'{p.PackName}'";
            string keptText = s.EntriesKept > 0
                ? $", kept {s.EntriesKept} edited"
                : "";
            return $"Removed {packLabel}: deleted {s.EntriesDeleted} preset(s){keptText}.";
        }
    }
}
