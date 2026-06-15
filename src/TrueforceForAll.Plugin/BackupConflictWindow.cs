// Phase 2 backup/sync, M2: the divergence conflict dialog. Shown when a manual
// "Back up now" finds the cloud backup changed on another PC since this one last
// synced. Offers Smart Merge (default; union presets newest-wins + pick a settings
// side) plus explicit Use-this-PC / Use-cloud overrides. Programmatic WPF (no XAML),
// mirroring UpdateVsNewChooserWindow's pattern.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class BackupConflictWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush FgText   = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        private static readonly Brush DimText  = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

        public BackupConflictChoice Choice { get; private set; } = BackupConflictChoice.Cancel;
        public bool KeepCloudSettings { get; private set; }

        public BackupConflictWindow(string cloudDeviceLabel, string cloudWhenLocal)
        {
            Title = "Backup conflict";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            Background = WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = "The cloud backup changed since this PC last synced.",
                Foreground = FgText, FontSize = 14, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });

            string src  = string.IsNullOrEmpty(cloudDeviceLabel) ? "another PC" : cloudDeviceLabel;
            string when = string.IsNullOrEmpty(cloudWhenLocal) ? "" : (" on " + cloudWhenLocal);
            root.Children.Add(new TextBlock
            {
                Text = "The cloud copy was last backed up by " + src + when + ".",
                Foreground = DimText, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            var keepCloud = new CheckBox
            {
                Content = "On merge, keep the cloud's global settings (instead of this PC's)",
                Foreground = FgText, Margin = new Thickness(0, 0, 0, 12), IsChecked = false,
            };
            root.Children.Add(keepCloud);

            root.Children.Add(MakeChoiceButton(
                "Smart merge (recommended)",
                "Keep every preset (the newest version of each wins) and pick one side's global settings.",
                () => { KeepCloudSettings = keepCloud.IsChecked == true; Choice = BackupConflictChoice.SmartMerge; }));

            root.Children.Add(MakeChoiceButton(
                "Use this PC (overwrite cloud)",
                "Replace the cloud backup with this PC's setup. The other PC's unsynced presets are lost.",
                () => { Choice = BackupConflictChoice.UseThisPc; }));

            root.Children.Add(MakeChoiceButton(
                "Use cloud (apply to this PC)",
                "Pull the cloud setup onto this PC. Your local-only presets are kept (restore is additive).",
                () => { Choice = BackupConflictChoice.UseCloud; }));

            var cancel = new Button
            {
                Content = "Cancel", Width = 90, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 4, 10, 4),
                Background = PanelBg, Foreground = FgText,
            };
            cancel.Click += (s, e) => { Choice = BackupConflictChoice.Cancel; DialogResult = false; Close(); };
            root.Children.Add(cancel);

            Content = root;
        }

        private Button MakeChoiceButton(string title, string subtitle, Action onClick)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, Foreground = FgText, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = subtitle, Foreground = DimText, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            var btn = new Button
            {
                Content = panel, Background = PanelBg, Foreground = FgText,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
                BorderThickness = new Thickness(0),
            };
            btn.Click += (s, e) => { onClick(); DialogResult = true; Close(); };
            return btn;
        }
    }
}
