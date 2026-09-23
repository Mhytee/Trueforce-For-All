// Phase 2, M5: the in-plugin Achievement tracker window. A small modal destination reached
// from the header-card crown, the account dropdown, or by clicking a celebration toast.
// Lists every achievement (earned + locked) with the caller's progress. When the user has
// earned something but hasn't linked Discord, a "link to claim" button swaps the window to a
// "check your browser" view and, once the link completes, reloads itself back to the tracker.
// Data + the link/reload/toggle callbacks are passed in; the window orchestrates the swap.
// Programmatic WPF, mirroring BackupConflictWindow.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class AchievementsWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush EarnedBg = new SolidColorBrush(Color.FromArgb(0x22, 0xE5, 0xC0, 0x4A));
        private static readonly Brush FgText   = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        private static readonly Brush DimText  = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8));
        private static readonly Brush Gold     = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush LockedFg = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        private static readonly Brush Blurple  = new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2));

        // Official Discord logo (mascot), viewBox 0 0 127.14 96.36.
        private const string DiscordLogoPath = "M107.7,8.07A105.15,105.15,0,0,0,81.47,0a72.06,72.06,0,0,0-3.36,6.83A97.68,97.68,0,0,0,49,6.83,72.37,72.37,0,0,0,45.64,0,105.89,105.89,0,0,0,19.39,8.09C2.79,32.65-1.71,56.6.54,80.21h0A105.73,105.73,0,0,0,32.71,96.36,77.7,77.7,0,0,0,39.6,85.25a68.42,68.42,0,0,1-10.85-5.18c.91-.66,1.8-1.34,2.66-2a75.57,75.57,0,0,0,64.32,0c.87.71,1.76,1.39,2.66,2a68.68,68.68,0,0,1-10.87,5.19,77,77,0,0,0,6.89,11.1A105.25,105.25,0,0,0,126.6,80.22h0C129.24,52.84,122.09,29.11,107.7,8.07ZM42.45,65.69C36.18,65.69,31,60,31,53s5-12.74,11.43-12.74S54,46,53.89,53,48.84,65.69,42.45,65.69Zm42.24,0C78.41,65.69,73.25,60,73.25,53s5-12.74,11.44-12.74S96.23,46,96.12,53,91.08,65.69,84.69,65.69Z";

        private readonly Func<Task<bool>> _onLinkAsync;
        private readonly Func<Task<(List<AchievementClient.AchievementRow> list, bool linked)>> _onReloadAsync;
        private readonly Border _host = new Border();   // swappable center content
        private TextBlock _countText;
        private readonly List<AchievementClient.AchievementRow> _initialList;
        private readonly bool _initialLinked;

        public AchievementsWindow(
            List<AchievementClient.AchievementRow> achievements,
            bool discordLinked,
            bool showCelebrations,
            Func<Task<bool>> onLinkAsync,
            Func<Task<(List<AchievementClient.AchievementRow> list, bool linked)>> onReloadAsync,
            Action<bool> onToggleCelebrations,
            bool autoStartLink = false)
        {
            _onLinkAsync = onLinkAsync;
            _onReloadAsync = onReloadAsync;
            _initialList = achievements ?? new List<AchievementClient.AchievementRow>();
            _initialLinked = discordLinked;

            Title = "Achievements";
            Width = 540;
            Height = 600;
            Background = WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock { Text = "🏆", Foreground = Brushes.White, FontSize = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = "Achievements", Foreground = FgText, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            _countText = new TextBlock { Foreground = DimText, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(_countText);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var celebToggle = new CheckBox
            {
                Content = "Show achievement celebrations", Foreground = DimText,
                IsChecked = showCelebrations, VerticalAlignment = VerticalAlignment.Center,
            };
            celebToggle.Checked   += (s, e) => onToggleCelebrations?.Invoke(true);
            celebToggle.Unchecked += (s, e) => onToggleCelebrations?.Invoke(false);
            DockPanel.SetDock(celebToggle, Dock.Left);
            footer.Children.Add(celebToggle);
            var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Background = PanelBg, Foreground = FgText };
            close.Click += (s, e) => Close();
            footer.Children.Add(close);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            root.Children.Add(_host);   // last, undocked => fills center
            Content = root;

            BuildListView(_initialList, _initialLinked);

            // Opened straight from the toast's "Link" button: jump into the link flow (which
            // shows the "check your browser" view) as soon as the window is up.
            if (autoStartLink && onLinkAsync != null)
                Loaded += (s, e) => { _ = DoLinkFlow(); };
        }

        private void UpdateCount(List<AchievementClient.AchievementRow> list)
        {
            int earned = 0; foreach (var a in list) if (a.Earned) earned++;
            if (_countText != null) _countText.Text = "   " + earned + " / " + list.Count;
        }

        private void BuildListView(List<AchievementClient.AchievementRow> achievements, bool discordLinked)
        {
            achievements = achievements ?? new List<AchievementClient.AchievementRow>();
            UpdateCount(achievements);
            int earnedCount = 0; foreach (var a in achievements) if (a.Earned) earnedCount++;

            var inner = new DockPanel();
            if (earnedCount > 0 && !discordLinked)
            {
                var claim = MakeClaimButton(earnedCount);
                claim.Click += async (s, e) => await DoLinkFlow();
                DockPanel.SetDock(claim, Dock.Top);
                inner.Children.Add(claim);
            }
            var listPanel = new StackPanel();
            foreach (var a in achievements) listPanel.Children.Add(MakeRow(a));
            inner.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel });
            _host.Child = inner;
        }

        // Shown after the user clicks the claim button: tells them to finish in the browser.
        private void BuildLinkingView()
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
            sp.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(DiscordLogoPath), Fill = Blurple, Stretch = Stretch.Uniform,
                Width = 48, Height = 36, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16),
            });
            sp.Children.Add(new TextBlock
            {
                Text = "Check your browser to finish linking Discord.",
                Foreground = FgText, FontSize = 15, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });
            sp.Children.Add(new TextBlock
            {
                Text = "This window updates automatically once you're done.",
                Foreground = DimText, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            _host.Child = sp;
        }

        // Show "check your browser", run the link, then reload fresh state and rebuild the list
        // (now linked, with roles claimed) or fall back to the claim view if it didn't complete.
        private async Task DoLinkFlow()
        {
            BuildLinkingView();
            bool ok = false;
            try { ok = _onLinkAsync != null && await _onLinkAsync(); } catch { ok = false; }

            var list = _initialList;
            bool linked = _initialLinked || ok;
            if (_onReloadAsync != null)
            {
                try { var (l, lk) = await _onReloadAsync(); if (l != null) { list = l; linked = lk; } } catch { }
            }
            BuildListView(list, linked);
        }

        private Button MakeClaimButton(int earnedCount)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(DiscordLogoPath), Fill = Brushes.White, Stretch = Stretch.Uniform,
                Width = 22, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
            });
            content.Children.Add(new TextBlock
            {
                Text = "Join Discord to claim your role" + (earnedCount > 1 ? "s" : ""),
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            return new Button
            {
                Padding = new Thickness(13, 8, 15, 8), Margin = new Thickness(0, 0, 0, 10),
                Background = Blurple, BorderThickness = new Thickness(0), Foreground = Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
                Content = content,
            };
        }

        private static Border MakeRow(AchievementClient.AchievementRow a)
        {
            var card = new Border
            {
                Background = a.Earned ? EarnedBg : PanelBg,
                BorderBrush = a.Earned ? Gold : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6),
            };
            var sp = new StackPanel();

            var titleRow = new DockPanel();
            titleRow.Children.Add(new TextBlock
            {
                Text = a.Earned ? "✓" : "○",
                Foreground = a.Earned ? Gold : LockedFg, FontSize = 14, FontWeight = FontWeights.Bold,
                Width = 20, VerticalAlignment = VerticalAlignment.Center,
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = a.Label ?? a.Key, Foreground = a.Earned ? FgText : DimText,
                FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            });
            var status = new TextBlock
            {
                Text = StatusText(a), Foreground = a.Earned ? Gold : LockedFg, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(status, Dock.Right);
            titleRow.Children.Add(status);
            sp.Children.Add(titleRow);

            sp.Children.Add(new TextBlock
            {
                Text = a.Description ?? "", Foreground = DimText, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0),
            });

            if (!a.Earned && a.Kind == "threshold" && a.Threshold > 0)
            {
                double frac = Math.Max(0, Math.Min(1, a.CurrentValue / a.Threshold));
                sp.Children.Add(new ProgressBar
                {
                    Minimum = 0, Maximum = 1, Value = frac, Height = 4,
                    Margin = new Thickness(20, 6, 0, 0),
                    Foreground = Gold, Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(0),
                });
            }

            card.Child = sp;
            return card;
        }

        private static string StatusText(AchievementClient.AchievementRow a)
        {
            if (a.Earned) return "Earned";
            if (a.Kind == "percentile") return "Top " + Fmt(a.Threshold) + "%";
            return Fmt(a.CurrentValue) + " / " + Fmt(a.Threshold);
        }

        private static string Fmt(double v)
        {
            return (Math.Abs(v - Math.Round(v)) < 0.0001)
                ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
