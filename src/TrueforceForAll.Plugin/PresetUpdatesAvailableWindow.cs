// Modal shown on plugin load when one or more community presets the
// user previously downloaded have a newer version on the server.
// Per-row Update / Skip plus bulk Apply / Dismiss all at the bottom.
//
// Update: re-fetch the body, run the same section picker as a fresh
// download, save under the same local name (overwriting), and bump
// SeenContentVersion. Skip: just bump SeenContentVersion so the row
// stops appearing until the next real edit.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class PresetUpdatesAvailableWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush ValueBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush OkFg     = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));

        // Per-row decision the caller acts on after the window closes.
        public enum RowAction { None, Update, Skip }

        public sealed class RowOutcome
        {
            public string Id { get; set; }
            public int ServerContentVersion { get; set; }
            public RowAction Action { get; set; }
            public string LocalPresetName { get; set; }
        }

        public List<RowOutcome> Outcomes { get; } = new List<RowOutcome>();

        public PresetUpdatesAvailableWindow(
            List<(PresetSummary Server, DownloadedPresetRecord Local)> updates)
        {
            updates = updates ?? new List<(PresetSummary Server, DownloadedPresetRecord Local)>();
            Title         = "Community preset updates";
            Width         = 540;
            Height        = 460;
            MinWidth      = 440;
            MinHeight     = 320;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.CanResize;

            var grid = new Grid { Margin = new Thickness(18, 16, 18, 14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = grid;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(new TextBlock {
                Text = updates.Count == 1
                    ? "A community preset you downloaded has been updated."
                    : updates.Count + " community presets you downloaded have updates.",
                Foreground = HeaderFg, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });
            header.Children.Add(new TextBlock {
                Text = "Update to replace your local copy with the new version, or Skip to keep what you have. Skipped rows won't reappear until the next edit.",
                Foreground = MutedFg, FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel();
            scroll.Content = stack;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            // Build one outcome per row so we can mutate it as the user
            // clicks buttons.
            foreach (var (server, local) in updates)
            {
                var outcome = new RowOutcome
                {
                    Id = server.Id,
                    ServerContentVersion = server.ContentVersion,
                    Action = RowAction.None,
                    LocalPresetName = local.LocalPresetName,
                };
                Outcomes.Add(outcome);
                stack.Children.Add(BuildRow(server, local, outcome));
            }

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            // Cancel: dismisses the modal without committing any per-
            // row outcome. ShowDialog returns false so the caller skips
            // the apply loop. Escape also fires this via IsCancel=true.
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);
            var skipAllBtn = new Button {
                Content = "Skip all", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
            };
            skipAllBtn.Click += (s, e) =>
            {
                foreach (var o in Outcomes) if (o.Action == RowAction.None) o.Action = RowAction.Skip;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(skipAllBtn);

            var updateAllBtn = new Button {
                Content = "Update all", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            updateAllBtn.Click += (s, e) =>
            {
                foreach (var o in Outcomes) if (o.Action == RowAction.None) o.Action = RowAction.Update;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(updateAllBtn);

            Grid.SetRow(btnRow, 2);
            grid.Children.Add(btnRow);
        }

        private FrameworkElement BuildRow(PresetSummary server,
            DownloadedPresetRecord local, RowOutcome outcome)
        {
            var border = new Border {
                Background = ValueBg, BorderBrush = BorderFg,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            border.Child = rowGrid;

            var info = new StackPanel();
            info.Children.Add(new TextBlock {
                Text = UiContentSanitizer.SafeDisplayText(server.Name, 128) ?? "(unnamed)", Foreground = TextFg, FontSize = 13, FontWeight = FontWeights.SemiBold,
            });
            string by = string.IsNullOrEmpty(server.Author) ? "(anonymous)" : "by " + UiContentSanitizer.SafeDisplayText(server.Author, 96);
            string ver = "v" + local.SeenContentVersion + " → v" + server.ContentVersion;
            info.Children.Add(new TextBlock {
                Text = by + "   |   " + server.Game + "   " + server.CarId + "   |   " + ver,
                Foreground = MutedFg, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            info.Children.Add(new TextBlock {
                Text = "Local name: " + (local.LocalPresetName ?? "(unknown)"),
                Foreground = MutedFg, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(info, 0);
            rowGrid.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock {
                Foreground = OkFg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), Text = "",
            };
            var updateBtn = new Button {
                Content = "Update", Padding = new Thickness(10, 4, 10, 4),
                Foreground = TextFg, Background = PanelBg, Margin = new Thickness(0, 0, 6, 0),
            };
            var skipBtn = new Button {
                Content = "Skip", Padding = new Thickness(10, 4, 10, 4),
                Foreground = TextFg, Background = PanelBg,
            };
            updateBtn.Click += (s, e) =>
            {
                outcome.Action = RowAction.Update;
                status.Text = "Will update";
                updateBtn.IsEnabled = false;
                skipBtn.IsEnabled   = true;
            };
            skipBtn.Click += (s, e) =>
            {
                outcome.Action = RowAction.Skip;
                status.Text = "Will skip";
                updateBtn.IsEnabled = true;
                skipBtn.IsEnabled   = false;
            };
            actions.Children.Add(status);
            actions.Children.Add(updateBtn);
            actions.Children.Add(skipBtn);
            Grid.SetColumn(actions, 1);
            rowGrid.Children.Add(actions);
            return border;
        }
    }
}
