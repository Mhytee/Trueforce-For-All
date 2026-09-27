// Modal shown when a user reports a community item (preset / game preset /
// custom engine / pack) for moderator review. Replaces the old bare yes/no
// confirm: it captures WHY, which is what reaches the #preset-reports Discord
// channel so a moderator can act on the report with context.
//
// A reason category is required (radio list, no default so the pick is
// deliberate); the note is optional free text. The category KEYS here must
// match the server vocabulary validated in migration 0080's report_* RPCs
// (broken / inappropriate / spam / wrong_data / stolen / other).

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    internal sealed class ReportDialog : Window
    {
        // Server vocabulary key (e.g. "broken"); null until a reason is picked.
        public string SelectedCategory { get; private set; }
        // Optional free-text note; empty string when left blank.
        public string Note { get; private set; } = "";

        // Keep keys in lockstep with migration 0080's category CHECK constraint.
        private static readonly (string Key, string Label)[] Categories =
        {
            ("broken",        "Report_CategoryBroken"),
            ("inappropriate", "Report_CategoryInappropriate"),
            ("spam",          "Report_CategorySpam"),
            ("wrong_data",    "Report_CategoryWrongData"),
            ("stolen",        "Report_CategoryStolen"),
            ("other",         "Report_CategoryOther"),
        };

        public ReportDialog(string targetName, string subjectKind)
        {
            Title = Loc.T("Report_ReportModeratorReview");
            Width = 460;
            Height = 470;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SettingsControl.ApplyDarkTheme(this);

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(targetName)
                    ? Loc.F("Report_ReportThisForReview_Fmt", subjectKind)
                    : Loc.F("Report_ReportModeratorReview_Fmt2", subjectKind, targetName),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
            });

            sp.Children.Add(new TextBlock
            {
                Text = Loc.T("Report_WhyReporting"),
                Margin = new Thickness(0, 0, 0, 6),
                FontWeight = FontWeights.SemiBold,
            });

            // Radio buttons over a ComboBox: a fixed short set reads clearer and
            // sidesteps WPF's dark-theme ComboBox popup rendering quirks.
            var radios = new List<RadioButton>();
            foreach (var c in Categories)
            {
                var rb = new RadioButton
                {
                    Content = Loc.T(c.Label),   // the table holds a key name
                    Tag = c.Key,
                    GroupName = "ReportReason",
                    Margin = new Thickness(2, 2, 0, 2),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)),
                };
                radios.Add(rb);
                sp.Children.Add(rb);
            }

            sp.Children.Add(new TextBlock
            {
                Text = Loc.T("Report_AnythingElseOptional"),
                Margin = new Thickness(0, 10, 0, 4),
                FontWeight = FontWeights.SemiBold,
            });
            var tbNote = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(4, 2, 4, 2),
                Height = 70,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxLength = 1000,
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)),
            };
            sp.Children.Add(tbNote);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancel = new Button { Content = Loc.T("Common_Cancel"), Width = 90, Height = 28, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var report = new Button { Content = Loc.T("PresetManager_CommunityReport"), Width = 110, Height = 28, IsDefault = true };
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(report);
            sp.Children.Add(btnRow);

            report.Click += (s, e) =>
            {
                string picked = null;
                foreach (var rb in radios)
                {
                    if (rb.IsChecked == true) { picked = rb.Tag as string; break; }
                }
                if (picked == null)
                {
                    TrueforceDialog.Show(this, Loc.T("Report_ReportModeratorReview"),
                        Loc.T("Report_PleasePickReasonReport"), DialogKind.Info);
                    return;
                }
                SelectedCategory = picked;
                Note = (tbNote.Text ?? "").Trim();
                DialogResult = true;
            };

            Content = sp;
        }
    }
}
