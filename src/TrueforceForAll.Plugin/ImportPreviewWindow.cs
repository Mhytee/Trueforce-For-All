// Modal dialog used by the "Import" flow: shows the user what they're about
// to import (pack metadata + per-item checklist) and lets them deselect
// rows or toggle "set as default" before committing. Sister to
// PackPickerWindow on the export side; same dark palette + flat WPF layout
// because this is a top-level Window and doesn't inherit SimHub's panel
// theme.
//
// One candidate per picked file. Pack candidates show a metadata header
// (Pack name, Author, Version, Description) followed by their contained
// items grouped by Game/Car. Loose candidates have no header beyond the
// filename and one item. Unknown / SettingsBackup candidates are excluded
// upstream (RunImportFlow handles them via the existing dispatch).
//
// Per-row UI:
//   [Include] [Name / CarId / GameName] [Set as default]
// Set-as-default is currently honored only for car rows (CarId is in the
// file). Game rows show the column disabled with a tooltip explaining why
// (V1 limitation; the data model needs a Defaults manifest hint to enable).
//
// Returns via DialogResult=true plus public properties: SelectedItems
// per candidate carries the user's final Include + SetAsDefault choices.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class ImportPreviewWindow : Window
    {
        // Palette mirrors PackPickerWindow so the two modals feel like one
        // family.
        private static readonly Brush WindowBg     = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg       = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg      = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush BorderFg     = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush HeaderFg     = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));   // gold for pack name
        private static readonly Brush GroupHeaderFg = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));

        // The candidates the user is reviewing. The same list is returned via
        // the IsChecked / SetAsDefault flags on each Item after OK closes.
        public IReadOnlyList<SettingsControl.ImportCandidate> Candidates { get; }

        // Each (CheckBox, Item) so OK can read back the final state.
        private readonly List<(CheckBox Include, CheckBox SetDefault, SettingsControl.ImportPreviewItem Item)> _rowChecks
            = new List<(CheckBox, CheckBox, SettingsControl.ImportPreviewItem)>();

        public ImportPreviewWindow(IReadOnlyList<SettingsControl.ImportCandidate> candidates)
        {
            Candidates = candidates ?? new List<SettingsControl.ImportCandidate>();

            Title  = "Import";
            Width  = 760;
            Height = 560;
            MinWidth  = 580;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.CanResize;
            Background = WindowBg;
            Foreground = TextFg;

            // Root grid: header text / scrollable candidate list / button row.
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int totalItems = Candidates.Sum(c => c.Items?.Count ?? 0);
            string headerText = Candidates.Count == 1
                ? "Review what you're importing. Uncheck anything you'd rather skip."
                : $"Review {Candidates.Count} files ({totalItems} item(s) total). Uncheck anything you'd rather skip.";
            var header = new TextBlock
            {
                Text         = headerText,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10),
                Foreground   = TextFg,
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0),
            };
            Grid.SetRow(scroller, 1);
            root.Children.Add(scroller);

            var stack = new StackPanel();
            scroller.Content = stack;

            foreach (var c in Candidates)
            {
                stack.Children.Add(BuildCandidatePanel(c));
            }

            // Buttons.
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok     = new Button { Content = "Import",  Width = 110, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel",  Width =  90, Height = 28, IsCancel  = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            Grid.SetRow(btnRow, 2);
            root.Children.Add(btnRow);

            ok.Click += (s, e) =>
            {
                // Commit per-row state back to the model.
                foreach (var rc in _rowChecks)
                {
                    rc.Item.IsChecked    = rc.Include.IsChecked    == true;
                    rc.Item.SetAsDefault = rc.SetDefault != null && rc.SetDefault.IsChecked == true;
                }
                // Gate: at least one item checked across all candidates.
                int kept = Candidates.Sum(cand => cand.Items?.Count(it => it.IsChecked) ?? 0);
                if (kept == 0)
                {
                    TrueforceDialog.Show(this, "Trueforce For All", "No items selected for import.",
                        DialogKind.Info);
                    return;
                }
                DialogResult = true;
            };

            Content = root;
        }

        // Build one card per candidate. Pack candidates get a gold metadata
        // header; loose candidates get a single-line file label.
        private UIElement BuildCandidatePanel(SettingsControl.ImportCandidate c)
        {
            var panel = new Border
            {
                Background      = PanelBg,
                BorderBrush     = BorderFg,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(10),
                Margin          = new Thickness(0, 0, 0, 10),
            };
            var sp = new StackPanel();
            panel.Child = sp;

            string fileLabel = System.IO.Path.GetFileName(c.Path ?? "");
            sp.Children.Add(new TextBlock
            {
                Text       = fileLabel,
                Foreground = MutedFg,
                FontSize   = 11,
                Margin     = new Thickness(0, 0, 0, c.Kind == SettingsControl.ImportCandidateKind.Pack ? 6 : 8),
            });

            if (c.Kind == SettingsControl.ImportCandidateKind.Pack && c.Manifest != null)
            {
                sp.Children.Add(BuildPackHeader(c.Manifest));
            }

            if (c.Items == null || c.Items.Count == 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text       = "(no importable items inside this file)",
                    Foreground = MutedFg,
                    Margin     = new Thickness(0, 6, 0, 0),
                });
                return panel;
            }

            // Split into game + car groups when both present; suppress empty
            // group headers.
            var games = c.Items.Where(i => i.Kind == "game").ToList();
            var cars  = c.Items.Where(i => i.Kind == "car").ToList();

            if (games.Count > 0)
            {
                sp.Children.Add(BuildGroupHeader($"Game presets ({games.Count})", games));
                foreach (var it in games) sp.Children.Add(BuildItemRow(it));
            }
            if (cars.Count > 0)
            {
                sp.Children.Add(BuildGroupHeader($"Car presets ({cars.Count})", cars));
                foreach (var it in cars) sp.Children.Add(BuildItemRow(it));
            }

            return panel;
        }

        // Pack-level metadata: gold pack name + Author / Version / Description
        // as a small block. Empty fields are skipped.
        private UIElement BuildPackHeader(PresetPackManifest m)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            string label = string.IsNullOrEmpty(m.PackName) ? "(unnamed pack)" : m.PackName;
            sp.Children.Add(new TextBlock
            {
                Text       = label,
                Foreground = HeaderFg,
                FontWeight = FontWeights.SemiBold,
                FontSize   = 14,
            });
            var byline = new List<string>();
            if (!string.IsNullOrEmpty(m.Author))        byline.Add($"by {m.Author}");
            if (!string.IsNullOrEmpty(m.AuthorVersion)) byline.Add($"v{m.AuthorVersion}");
            if (byline.Count > 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text       = string.Join("  ", byline),
                    Foreground = MutedFg,
                    FontSize   = 12,
                    Margin     = new Thickness(0, 2, 0, 0),
                });
            }
            if (!string.IsNullOrEmpty(m.Description))
            {
                sp.Children.Add(new TextBlock
                {
                    Text         = m.Description,
                    Foreground   = TextFg,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 6, 0, 0),
                });
            }
            return sp;
        }

        // Group header with two pairs of inline toggle links so the user can
        // flip a whole group quickly without clicking each row. "Include
        // all / none" controls the leftmost (include) checkbox column.
        // "Default all / none" controls the rightmost (set-as-default)
        // column and is only shown for groups where at least one row's
        // Set-as-default checkbox is actually enabled (V1: car rows only).
        // Labels are spelled out so the user can't confuse which column the
        // link drives.
        private UIElement BuildGroupHeader(string title, List<SettingsControl.ImportPreviewItem> items)
        {
            bool anyDefaultableInGroup = items.Any(i => i.Kind == "car");

            var grid = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleTb = new TextBlock
            {
                Text              = title,
                Foreground        = GroupHeaderFg,
                FontWeight        = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(titleTb, 0);
            grid.Children.Add(titleTb);

            var togglePanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            togglePanel.Children.Add(new TextBlock
            {
                Text       = "Include ",
                Foreground = MutedFg, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            togglePanel.Children.Add(MakeLink("all",  () => SetGroupIncluded(items, true)));
            togglePanel.Children.Add(new TextBlock
            {
                Text = " / ", Foreground = MutedFg, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            togglePanel.Children.Add(MakeLink("none", () => SetGroupIncluded(items, false)));

            if (anyDefaultableInGroup)
            {
                togglePanel.Children.Add(new TextBlock
                {
                    Text       = "   •   Default ",   // bullet separator + label
                    Foreground = MutedFg, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                togglePanel.Children.Add(MakeLink("all",  () => SetGroupSetDefault(items, true)));
                togglePanel.Children.Add(new TextBlock
                {
                    Text = " / ", Foreground = MutedFg, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                togglePanel.Children.Add(MakeLink("none", () => SetGroupSetDefault(items, false)));
            }

            Grid.SetColumn(togglePanel, 1);
            grid.Children.Add(togglePanel);

            return grid;
        }

        private void SetGroupIncluded(List<SettingsControl.ImportPreviewItem> items, bool value)
        {
            foreach (var rc in _rowChecks)
            {
                if (items.Contains(rc.Item)) rc.Include.IsChecked = value;
            }
        }

        // Toggles every Set-as-default checkbox in the group that's actually
        // enabled. V1 leaves game rows disabled so this only affects car rows;
        // the no-op on disabled checkboxes is silent (WPF treats IsChecked
        // assignment on a disabled checkbox as a normal write).
        private void SetGroupSetDefault(List<SettingsControl.ImportPreviewItem> items, bool value)
        {
            foreach (var rc in _rowChecks)
            {
                if (rc.SetDefault == null) continue;
                if (!rc.SetDefault.IsEnabled) continue;
                if (items.Contains(rc.Item)) rc.SetDefault.IsChecked = value;
            }
        }

        // A focusable, keyboard-activatable link (Space/Enter) with a real hit
        // target, built as a chromeless Button so it reads as an underlined link
        // but is reachable by keyboard and shows a focus rect (unlike the old
        // bare TextBlock + MouseLeftButtonUp).
        private Button MakeLink(string text, Action onClick)
        {
            var tmpl = new ControlTemplate(typeof(Button));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            tmpl.VisualTree = cp;

            var btn = new Button
            {
                Template = tmpl,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = text, Foreground = HeaderFg, FontSize = 11,
                    TextDecorations = TextDecorations.Underline,
                },
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        // One row per item:
        //   [Include CB]  Name / sub-line  ............  [Set as default CB]
        // Game rows show the Set-as-default checkbox disabled with a tooltip
        // explaining V1 doesn't support it (data model gap).
        private UIElement BuildItemRow(SettingsControl.ImportPreviewItem item)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var include = new CheckBox
            {
                IsChecked         = item.IsChecked,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
                Foreground        = TextFg,
            };
            Grid.SetColumn(include, 0);
            grid.Children.Add(include);

            var labelSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labelSp.Children.Add(new TextBlock
            {
                Text       = item.Name ?? "(unnamed)",
                Foreground = TextFg,
            });
            var subParts = new List<string>();
            if (item.Kind == "car")
            {
                if (!string.IsNullOrEmpty(item.CarId))    subParts.Add($"car: {item.CarId}");
                if (!string.IsNullOrEmpty(item.GameName)) subParts.Add($"game: {item.GameName}");
            }
            else if (item.Kind == "game" && !string.IsNullOrEmpty(item.GameName))
            {
                subParts.Add($"game: {item.GameName}");
            }
            if (subParts.Count > 0)
            {
                labelSp.Children.Add(new TextBlock
                {
                    Text       = string.Join("    ", subParts),
                    Foreground = MutedFg,
                    FontSize   = 11,
                });
            }
            Grid.SetColumn(labelSp, 1);
            grid.Children.Add(labelSp);

            // Set-as-default checkbox. Car rows: enabled (CarId is known).
            // Game rows: disabled with tooltip (V1 limitation).
            CheckBox setDefault = null;
            if (item.Kind == "car")
            {
                setDefault = new CheckBox
                {
                    Content           = "Set as default",
                    IsChecked         = item.SetAsDefault,
                    Foreground        = TextFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 0, 0),
                    ToolTip           = "Make this the default preset for this car.",
                };
            }
            else
            {
                setDefault = new CheckBox
                {
                    Content     = "Set as default",
                    IsChecked   = false,
                    IsEnabled   = false,
                    Foreground  = MutedFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin      = new Thickness(8, 0, 0, 0),
                    ToolTip     = "Game presets aren't auto-bound on import yet. Set the game default manually in the Preset Manager after importing.",
                };
            }
            Grid.SetColumn(setDefault, 2);
            grid.Children.Add(setDefault);

            _rowChecks.Add((include, setDefault, item));
            return grid;
        }
    }
}
