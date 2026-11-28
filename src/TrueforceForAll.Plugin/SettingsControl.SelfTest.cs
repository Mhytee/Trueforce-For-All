// The self-test's result, drawn as a checklist. SelfTest_Click still builds
// the result as tagged text, one check per line ("[OK]   USBPcap installed",
// "[FAIL] ...", "[skip] ...", an indented line continuing the one above),
// because that text is what Copy puts on the clipboard for a bug report and
// what every translation already carries. This file reads that text back and
// draws it: an icon per check, its follow-up lines under it, a fix button
// where the panel already has one, and the overall verdict as a callout.
//
// The tags are recognized in the current language (Plugin_OK, Plugin_FAIL,
// Settings_Skip), so a translated result is drawn the same way. Any other
// bracketed tag (the "[..]" of the live FFB watch) draws as in progress.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        private enum CheckState { Ok, Fail, Skip, Pending }

        private static readonly SolidColorBrush CheckOkBrush      = MakeBrush("#66BB6A");
        private static readonly SolidColorBrush CheckFailBrush    = MakeBrush("#EF5350");
        private static readonly SolidColorBrush CheckSkipBrush    = MakeBrush("#9E9E9E");
        private static readonly SolidColorBrush CheckPendingBrush = MakeBrush("#FFB300");
        private static readonly SolidColorBrush VerdictOkBg       = MakeBrush("#332E7D32");
        private static readonly SolidColorBrush VerdictIssueBg    = MakeBrush("#33B26A00");

        private string _selfTestPlainText;
        private DispatcherTimer _selfTestCopiedTimer;

        /// <summary>Show a self-test result (the tagged text SelfTest_Click
        /// builds) as the checklist.</summary>
        private void ShowSelfTestResult(string plain)
        {
            _selfTestPlainText = plain ?? "";
            if (SelfTestRowsPanel == null || SelfTestResultHost == null) return;
            SelfTestRowsPanel.Children.Clear();
            if (SelfTestCopiedText != null) SelfTestCopiedText.Visibility = Visibility.Collapsed;

            string okTag   = Loc.T("Plugin_OK").Trim();
            string failTag = Loc.T("Plugin_FAIL").Trim();
            string skipTag = Loc.T("Settings_Skip").Trim();
            string healthy = Loc.T("Settings_OverallHealthy").Trim();
            // The verdict line's lead-in, in this language: format it around a
            // marker and keep what comes before the marker.
            const string Marker = "\u0001";
            string blockingFmt = Loc.F("Settings_MostBlockingIssue_Fmt", Marker);
            int at = blockingFmt.IndexOf(Marker, StringComparison.Ordinal);
            string blockingPrefix = (at > 0 ? blockingFmt.Substring(0, at) : "").Trim();

            StackPanel lastRowBody = null;
            foreach (string raw in _selfTestPlainText.Replace("\r", "").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(raw)) { lastRowBody = null; continue; }

                // An indented line continues the check above it.
                if (char.IsWhiteSpace(raw[0]) && lastRowBody != null)
                {
                    lastRowBody.Children.Add(DetailLine(raw.Trim()));
                    continue;
                }

                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = line.IndexOf(']');
                    if (close > 0)
                    {
                        string tag  = line.Substring(0, close + 1);
                        string text = line.Substring(close + 1).Trim();
                        CheckState state = tag == okTag ? CheckState.Ok
                                         : tag == failTag ? CheckState.Fail
                                         : tag == skipTag ? CheckState.Skip
                                         : CheckState.Pending;
                        lastRowBody = AddCheckRow(state, text, FixFor(raw.TrimEnd()));
                        continue;
                    }
                }

                if (line == healthy || (blockingPrefix.Length > 0 && line.StartsWith(blockingPrefix, StringComparison.Ordinal)))
                {
                    AddVerdict(line, line == healthy);
                    lastRowBody = null;
                    continue;
                }

                // Anything else is a note: the rumble line, the probe heading.
                SelfTestRowsPanel.Children.Add(new TextBlock
                {
                    Text = line,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                    Margin = new Thickness(0, 6, 0, 2),
                });
                lastRowBody = null;
            }

            SelfTestResultHost.Visibility = Visibility.Visible;
        }

        private StackPanel AddCheckRow(CheckState state, string text, UIElement fix)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            string glyph; SolidColorBrush brush;
            switch (state)
            {
                case CheckState.Ok:   glyph = "✓"; brush = CheckOkBrush;      break;
                case CheckState.Fail: glyph = "✗"; brush = CheckFailBrush;    break;
                case CheckState.Skip: glyph = "–"; brush = CheckSkipBrush;    break;
                default:              glyph = "…"; brush = CheckPendingBrush; break;
            }
            var icon = new TextBlock
            {
                Text = glyph, Foreground = brush, FontWeight = FontWeights.Bold, FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Opacity = state == CheckState.Skip ? 0.75 : 1.0,
                Margin = new Thickness(0, 1, 0, 0),
            });
            if (fix != null) body.Children.Add(fix);
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);

            SelfTestRowsPanel.Children.Add(grid);
            return body;
        }

        private static TextBlock DetailLine(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
        };

        private void AddVerdict(string text, bool healthy)
        {
            SelfTestRowsPanel.Children.Add(new Border
            {
                Background = healthy ? VerdictOkBg : VerdictIssueBg,
                BorderBrush = healthy ? CheckOkBrush : CheckPendingBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 6, 0, 4),
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold },
            });
        }

        /// <summary>The fix button for a failing check, when the panel already
        /// has the action for it. Matched on the exact line the check printed,
        /// in the current language. Both actions confirm before doing anything.</summary>
        private UIElement FixFor(string line)
        {
            if (_plugin == null) return null;
            // Literal binding paths, one per action, so the localization checks
            // can see each key (a concatenated key is invisible to them).
            Binding label;
            RoutedEventHandler act;
            if (line == Loc.T("Settings_FAILUSBPcapNotInstalled") || line == Loc.T("Settings_FAILUSBPcapInstalledBut"))
            {
                label = new Binding("[Settings_SelfTestFixReinstallUsbPcap]");
                act = UsbPcapReinstall_Click;
            }
            else if (line == Loc.T("Settings_FAILUSBPcapNotRegistered") && _plugin.UsbPcapUninstallerPath != null)
            {
                label = new Binding("[Settings_UsbPcapUninstall]");
                act = UsbPcapUninstall_Click;
            }
            else return null;

            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 5, 0, 0),
            };
            label.Source = Loc.Instance;
            label.Mode = BindingMode.OneWay;
            b.SetBinding(ContentControl.ContentProperty, label);
            b.Click += act;
            return b;
        }

        private void SelfTestCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selfTestPlainText)) return;
            try { Clipboard.SetText(_selfTestPlainText.TrimEnd()); }
            catch (Exception ex)
            {
                // The clipboard can be held by another process for a moment.
                SimHub.Logging.Current.Warn("[TF4ALL] Self-test copy failed: " + ex.Message);
                return;
            }
            if (SelfTestCopiedText == null) return;
            SelfTestCopiedText.Visibility = Visibility.Visible;
            if (_selfTestCopiedTimer == null)
            {
                _selfTestCopiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _selfTestCopiedTimer.Tick += (s, a) =>
                {
                    _selfTestCopiedTimer.Stop();
                    if (SelfTestCopiedText != null) SelfTestCopiedText.Visibility = Visibility.Collapsed;
                };
            }
            _selfTestCopiedTimer.Stop();
            _selfTestCopiedTimer.Start();
        }
    }
}
