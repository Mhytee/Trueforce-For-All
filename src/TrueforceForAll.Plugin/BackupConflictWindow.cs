// Phase 2 backup/sync, M2: the divergence conflict dialog. Shown when a manual
// "Back up now" finds the cloud backup changed on another PC since this one last
// synced. Offers Smart Merge (default; union presets newest-wins + pick a settings
// side) plus explicit Use-this-PC / Use-cloud overrides. Programmatic WPF (no XAML),
// mirroring UpdateVsNewChooserWindow's pattern.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueforceForAll.Plugin.Localization;

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
            Title = Loc.T("BackupConflict_BackupConflict");
            Width = 500;
            SizeToContent = SizeToContent.Height;
            Background = WindowBg;
            Foreground = FgText;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = Loc.T("BackupConflict_CloudBackupChangedSince"),
                Foreground = FgText, FontSize = 14, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });

            string src = string.IsNullOrEmpty(cloudDeviceLabel) ? Loc.T("BackupConflict_AnotherPc") : cloudDeviceLabel;
            root.Children.Add(new TextBlock
            {
                // Two whole sentences rather than one glued to " on ": a
                // translator can put the date where their language wants it.
                Text = string.IsNullOrEmpty(cloudWhenLocal)
                    ? Loc.F("BackupConflict_CloudLastBackedUpBy_Fmt", src)
                    : Loc.F("BackupConflict_CloudLastBackedUpByOn_Fmt", src, cloudWhenLocal),
                Foreground = DimText, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            var keepCloud = new CheckBox
            {
                Content = Loc.T("BackupConflict_MergeKeepCloudS"),
                Foreground = FgText, Margin = new Thickness(0, 0, 0, 12), IsChecked = false,
            };
            root.Children.Add(keepCloud);

            var smartBtn = MakeChoiceButton(
                Loc.T("BackupConflict_SmartMergeRecommended"),
                Loc.T("BackupConflict_KeepEveryPresetNewest"),
                () => { KeepCloudSettings = keepCloud.IsChecked == true; Choice = BackupConflictChoice.SmartMerge; });
            smartBtn.IsDefault = true;   // Enter = the recommended, non-destructive choice
            root.Children.Add(smartBtn);

            root.Children.Add(MakeChoiceButton(
                Loc.T("BackupConflict_UsePCOverwriteCloud"),
                Loc.T("BackupConflict_ReplaceCloudBackupPC"),
                () => { Choice = BackupConflictChoice.UseThisPc; }));

            root.Children.Add(MakeChoiceButton(
                Loc.T("BackupConflict_UseCloudApplyPC"),
                Loc.T("BackupConflict_PullCloudSetupOnto"),
                () => { Choice = BackupConflictChoice.UseCloud; }));

            var cancel = new Button
            {
                Content = Loc.T("Common_Cancel"), Width = 90, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 4, 10, 4),
                Background = PanelBg, Foreground = FgText, IsCancel = true,
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
