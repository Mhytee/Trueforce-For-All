// Read-only preview of a community preset's body. Opens from the
// Community segment's Preview button so the user can see what a preset
// actually contains before committing to a download.
//
// Renders: identity (name, author, car), description, section list with
// key values pulled from the CarOverride. No body mutation; if the user
// likes it, they hit Download in the parent window.

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class PresetPreviewWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush ValueBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

        public bool DownloadRequested { get; private set; }

        public PresetPreviewWindow(PresetSummary summary, JObject body)
        {
            Title         = "Preview: " + (summary?.Name ?? "preset");
            Width         = 540;
            Height        = 560;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.CanResize;
            MinWidth      = 440;
            MinHeight     = 400;

            var grid = new Grid { Margin = new Thickness(18, 16, 18, 14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // body
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons
            Content = grid;

            // ---- Header block ----
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock {
                Text = summary?.Name ?? "(unnamed)",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 16,
                Margin = new Thickness(0, 0, 0, 2),
            });
            string subtitle = (string.IsNullOrEmpty(summary?.Author) ? "(anonymous)" : "by " + summary.Author)
                + "   |   " + (summary?.Game ?? "") + "   "
                + (summary?.CarId ?? "");
            head.Children.Add(new TextBlock {
                Text = subtitle, Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4),
            });
            int score = (summary?.Upvotes ?? 0) - (summary?.Downvotes ?? 0);
            head.Children.Add(new TextBlock {
                Text = $"Score {score} (▲ {summary?.Upvotes ?? 0} / ▼ {summary?.Downvotes ?? 0})   |   {summary?.Downloads ?? 0} downloads",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
            });
            if (!string.IsNullOrWhiteSpace(summary?.Description))
            {
                head.Children.Add(new Border {
                    Background = ValueBg, BorderBrush = BorderFg,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = new TextBlock {
                        Text = summary.Description,
                        Foreground = TextFg, FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // ---- Body: section breakdown ----
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var bodyPanel = new StackPanel();
            scroll.Content = bodyPanel;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var ovr = body?["override"] as JObject;
            var customs = body?["custom_engines"] as JArray;

            if (ovr == null)
            {
                bodyPanel.Children.Add(new TextBlock {
                    Text = "(no body data)",
                    Foreground = MutedFg, FontSize = 12,
                });
            }
            else
            {
                // Section ordered to match the upload effect_tags whitelist.
                AddSection(bodyPanel, ovr, "EnginePulse",  "Engine pulse");
                AddSection(bodyPanel, ovr, "RevLimiter",   "Rev limiter");
                AddSection(bodyPanel, ovr, "RoadBumps",    "Road bumps");
                AddSection(bodyPanel, ovr, "TractionLoss", "Traction loss");
                AddSection(bodyPanel, ovr, "GearShift",    "Gear shift");
                AddSection(bodyPanel, ovr, "AbsClick",     "ABS click");
                AddSection(bodyPanel, ovr, "PitLimiter",   "Pit limiter");
                AddSection(bodyPanel, ovr, "Drs",          "DRS");
                AddSection(bodyPanel, ovr, "Collision",    "Collision");
                AddSection(bodyPanel, ovr, "AudioCapture", "Audio capture");
                AddSection(bodyPanel, ovr, "Airborne",     "Airborne ducking");
            }

            if (customs != null && customs.Count > 0)
            {
                bodyPanel.Children.Add(new TextBlock {
                    Text = $"Bundled custom engines ({customs.Count})",
                    Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 10, 0, 4),
                });
                foreach (var c in customs.OfType<JObject>())
                {
                    string name = c?["Name"]?.ToString() ?? "(unnamed)";
                    bool ev = c?["IsElectric"]?.ToObject<bool>() ?? false;
                    bodyPanel.Children.Add(new TextBlock {
                        Text = "• " + name + (ev ? "  (electric)" : ""),
                        Foreground = TextFg, FontSize = 12,
                        Margin = new Thickness(8, 1, 0, 1),
                    });
                }
            }

            // ---- Buttons ----
            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var closeBtn = new Button {
                Content = "Close", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            closeBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(closeBtn);

            var dlBtn = new Button {
                Content = "Download…", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            dlBtn.Click += (s, e) =>
            {
                DownloadRequested = true;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(dlBtn);
            Grid.SetRow(btnRow, 2);
            grid.Children.Add(btnRow);
        }

        private void AddSection(StackPanel host, JObject ovr, string key, string label)
        {
            var sect = ovr[key];
            if (sect == null || sect.Type == JTokenType.Null) return;

            host.Children.Add(new TextBlock {
                Text = label, Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 2),
            });
            // Show a handful of useful numeric / enum fields per section
            // so the reader gets a quick sense of intensity / character.
            var summary = SummariseSection(sect as JObject);
            host.Children.Add(new TextBlock {
                Text = summary, Foreground = TextFg, FontSize = 11,
                Margin = new Thickness(8, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        private static string SummariseSection(JObject section)
        {
            if (section == null) return "(present)";
            var parts = new List<string>();
            // Prefer common headline fields if present.
            foreach (var headline in new[] {
                "Enabled","Gain","Pitch","LowpassHz","HighpassHz",
                "Freq","PulseFreq","DutyCycle","ActiveAmp","Threshold",
                "RedlineOffsetRpm","EngageMode","Waveform","Sensitivity",
                "Reduction","Layout","ElectricMode","CustomEngineId",
            })
            {
                var t = section[headline];
                if (t == null || t.Type == JTokenType.Null) continue;
                string val = t.Type == JTokenType.Float
                    ? t.ToObject<double>().ToString("0.###")
                    : t.ToString();
                parts.Add(headline + ": " + val);
                if (parts.Count >= 6) break;
            }
            if (parts.Count == 0)
            {
                // Fall back to a property count so empty-section confusion
                // doesn't surface.
                int n = section.Properties().Count();
                return n + " field" + (n == 1 ? "" : "s");
            }
            return string.Join("   |   ", parts);
        }
    }
}
