// Read-only preview of a community submission's body. Opens from the
// Community segment's Preview button so the user can see what they are
// about to download before committing. Branches on summary.Kind:
//   * "car"    -> CarOverride section breakdown + bundled engines
//   * "game"   -> GameSettingsSnapshot section breakdown + bundled engines
//   * "engine" -> CustomEngineDef identity (pattern, layout, cylinders)
//   * "pack"   -> per-bucket list of entries (game presets / car presets
//                 / custom engines) so the user knows what installing the
//                 pack will pull into their library.
// No body mutation; if the user likes it, they hit Download.

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
            string kind = (summary?.Kind ?? "car").ToLowerInvariant();
            bool isGame   = kind == "game";
            bool isEngine = kind == "engine";
            bool isPack   = kind == "pack";

            string typeLabel = isPack   ? "pack"
                              : isEngine ? "engine"
                              : isGame   ? "game preset"
                              : "preset";

            Title         = "Preview: " + (summary?.Name ?? typeLabel);
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
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = grid;

            // ---- Header block ----
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock {
                Text = UiContentSanitizer.SafeDisplayText(summary?.Name, 128) ?? "(unnamed)",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 16,
                Margin = new Thickness(0, 0, 0, 2),
            });

            // Subtitle composition is kind-aware so we don't render an
            // empty CarId for game/engine/pack submissions.
            string author = string.IsNullOrEmpty(summary?.Author) ? "(anonymous)" : "by " + UiContentSanitizer.SafeDisplayText(summary.Author, 96);
            var subParts = new List<string> { author };
            if (!isEngine && !isPack && !string.IsNullOrEmpty(summary?.Game))
                subParts.Add(summary.Game);
            if (kind == "car" && !string.IsNullOrEmpty(summary?.CarId))
                subParts.Add(summary.CarId);
            if (isPack)
                subParts.Add("pack");
            head.Children.Add(new TextBlock {
                Text = string.Join("   |   ", subParts),
                Foreground = MutedFg, FontSize = 11,
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
                        Text = UiContentSanitizer.SafeMultiLineText(summary.Description, 1024, 10) ?? "(no description)",
                        Foreground = TextFg, FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // ---- Body ----
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var bodyPanel = new StackPanel();
            scroll.Content = bodyPanel;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            if (body == null)
            {
                bodyPanel.Children.Add(MutedLine("(no body data)"));
            }
            else if (isPack)
            {
                RenderPack(bodyPanel, body);
            }
            else if (isEngine)
            {
                RenderEngine(bodyPanel, body);
            }
            else if (isGame)
            {
                RenderGame(bodyPanel, body);
            }
            else
            {
                RenderCar(bodyPanel, body);
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

        // ---------------- Renderers ----------------

        private void RenderCar(StackPanel bodyPanel, JObject body)
        {
            var ovr = body["override"] as JObject;
            if (ovr == null)
                bodyPanel.Children.Add(MutedLine("(no override data)"));
            else
                AddOverrideSections(bodyPanel, ovr);
            AddBundledEngines(bodyPanel, body["custom_engines"] as JArray);
        }

        private void RenderGame(StackPanel bodyPanel, JObject body)
        {
            var snap = body["snapshot"] as JObject;
            if (snap == null)
                bodyPanel.Children.Add(MutedLine("(no snapshot data)"));
            else
            {
                // Game presets have flat top-level FFB settings
                // (MasterGain, FfbScale, FfbSmoothTimeConstantMs,
                // FfbInvertSign, etc) that aren't structured as
                // effect sections; show them up front so the reader
                // sees the preset's global tuning before drilling
                // into per-effect overrides.
                AddTopLevelGameSettings(bodyPanel, snap);
                // Per-effect sections share key names with CarOverride.
                AddOverrideSections(bodyPanel, snap);
            }
            AddBundledEngines(bodyPanel, body["custom_engines"] as JArray);
        }

        // Top-level FFB tuning fields on a GameSettingsSnapshot. Effect
        // sections are nested JObjects (handled by AddOverrideSections);
        // everything else flat at the snapshot root is rendered here.
        private void AddTopLevelGameSettings(StackPanel host, JObject snap)
        {
            // Anything that's a section header lives elsewhere; skip.
            var sectionKeys = new HashSet<string>(new[] {
                "EnginePulse","RevLimiter","RoadBumps","TractionLoss",
                "GearShift","AbsClick","PitLimiter","Drs","Collision",
                "AudioCapture","Airborne",
            });
            // CarOverrides on legacy snapshots is a dict, not a flat
            // field; also skip it from the headline overview.
            sectionKeys.Add("CarOverrides");

            var flat = new JObject();
            foreach (var prop in snap.Properties())
            {
                if (sectionKeys.Contains(prop.Name)) continue;
                if (prop.Value == null || prop.Value.Type == JTokenType.Null) continue;
                // Defensive: skip nested objects that aren't effect
                // sections (none should exist at this layer but a
                // future schema add wouldn't render well as a flat
                // value line).
                if (prop.Value.Type == JTokenType.Object) continue;
                flat[prop.Name] = prop.Value;
            }
            if (flat.Count == 0) return;
            host.Children.Add(new TextBlock {
                Text = "Global FFB settings",
                Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });
            host.Children.Add(BuildFieldsBlock(flat));
        }

        private void RenderEngine(StackPanel bodyPanel, JObject body)
        {
            // body is a serialized CustomEngineDef at the root. Headline
            // fields tell the reader what defines this engine without
            // making them scan raw JSON.
            string name      = body["Name"]?.ToString() ?? "(unnamed)";
            bool   electric  = body["IsElectric"]?.ToObject<bool>() ?? false;
            string layout    = body["Layout"]?.ToString();
            int?   cylinders = body["Cylinders"]?.ToObject<int?>();
            string pattern   = body["Pattern"]?.ToString();
            string author    = body["Author"]?.ToString();

            bodyPanel.Children.Add(new TextBlock {
                Text = "Engine", Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var parts = new List<string> { "Name: " + name };
            if (electric)                          parts.Add("electric");
            if (!string.IsNullOrEmpty(layout))     parts.Add("Layout: " + layout);
            if (cylinders.HasValue)                parts.Add("Cylinders: " + cylinders.Value);
            if (!string.IsNullOrEmpty(author))     parts.Add("Author: " + author);
            bodyPanel.Children.Add(new TextBlock {
                Text = string.Join("   |   ", parts),
                Foreground = TextFg, FontSize = 11,
                Margin = new Thickness(8, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });

            if (!string.IsNullOrWhiteSpace(pattern))
            {
                bodyPanel.Children.Add(new TextBlock {
                    Text = "Pattern", Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 2),
                });
                bodyPanel.Children.Add(new Border {
                    Background = ValueBg, BorderBrush = BorderFg,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = new TextBlock {
                        Text = pattern,
                        Foreground = TextFg, FontSize = 11,
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
        }

        private void RenderPack(StackPanel bodyPanel, JObject body)
        {
            var games   = body["game_presets"]   as JArray;
            var cars    = body["car_presets"]    as JArray;
            var engines = body["custom_engines"] as JArray;

            int total = (games?.Count ?? 0) + (cars?.Count ?? 0) + (engines?.Count ?? 0);
            bodyPanel.Children.Add(new TextBlock {
                Text = $"{total} entr{(total == 1 ? "y" : "ies")} bundled",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6),
            });

            if (games != null && games.Count > 0)
            {
                bodyPanel.Children.Add(SectionHeader($"Game presets ({games.Count})"));
                foreach (var je in games.OfType<JObject>())
                {
                    string nm = UiContentSanitizer.SafeDisplayText(je["name"]?.ToString(), 128) ?? "(unnamed)";
                    string au = UiContentSanitizer.SafeDisplayText(je["source_author"]?.ToString(), 96);
                    bodyPanel.Children.Add(EntryLine(nm,
                        string.IsNullOrEmpty(au) ? null : "by " + au));
                }
            }

            if (cars != null && cars.Count > 0)
            {
                bodyPanel.Children.Add(SectionHeader($"Car presets ({cars.Count})"));
                foreach (var je in cars.OfType<JObject>())
                {
                    string nm   = UiContentSanitizer.SafeDisplayText(je["preset_name"]?.ToString(), 128) ?? "(unnamed)";
                    string car  = UiContentSanitizer.SafeDisplayText(je["car_id"]?.ToString(), 96);
                    string game = UiContentSanitizer.SafeDisplayText(je["game_name"]?.ToString(), 96);
                    string au   = UiContentSanitizer.SafeDisplayText(je["source_author"]?.ToString(), 96);
                    var sub = new List<string>();
                    if (!string.IsNullOrEmpty(car))  sub.Add(car);
                    if (!string.IsNullOrEmpty(game)) sub.Add(game);
                    if (!string.IsNullOrEmpty(au))   sub.Add("by " + au);
                    bodyPanel.Children.Add(EntryLine(nm,
                        sub.Count == 0 ? null : string.Join("  |  ", sub)));
                }
            }

            if (engines != null && engines.Count > 0)
            {
                bodyPanel.Children.Add(SectionHeader($"Custom engines ({engines.Count})"));
                foreach (var je in engines.OfType<JObject>())
                {
                    string nm = UiContentSanitizer.SafeDisplayText(je["Name"]?.ToString(), 128) ?? "(unnamed)";
                    bool ev   = je["IsElectric"]?.ToObject<bool>() ?? false;
                    string au = UiContentSanitizer.SafeDisplayText(je["source_author"]?.ToString(), 96);
                    var sub = new List<string>();
                    if (ev) sub.Add("electric");
                    if (!string.IsNullOrEmpty(au)) sub.Add("by " + au);
                    bodyPanel.Children.Add(EntryLine(nm,
                        sub.Count == 0 ? null : string.Join("  |  ", sub)));
                }
            }

            if (total == 0)
                bodyPanel.Children.Add(MutedLine("(empty pack)"));
        }

        // ---------------- Shared helpers ----------------

        private void AddOverrideSections(StackPanel host, JObject ovr)
        {
            AddSection(host, ovr, "EnginePulse",  "Engine pulse");
            AddSection(host, ovr, "RevLimiter",   "Rev limiter");
            AddSection(host, ovr, "RoadBumps",    "Road bumps");
            AddSection(host, ovr, "TractionLoss", "Traction loss");
            AddSection(host, ovr, "GearShift",    "Gear shift");
            AddSection(host, ovr, "AbsClick",     "ABS click");
            AddSection(host, ovr, "PitLimiter",   "Pit limiter");
            AddSection(host, ovr, "Drs",          "DRS");
            AddSection(host, ovr, "Collision",    "Collision");
            AddSection(host, ovr, "AudioCapture", "Audio capture");
            AddSection(host, ovr, "Airborne",     "Airborne ducking");
        }

        private void AddBundledEngines(StackPanel host, JArray customs)
        {
            if (customs == null || customs.Count == 0) return;
            host.Children.Add(new TextBlock {
                Text = $"Bundled custom engines ({customs.Count})",
                Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (var c in customs.OfType<JObject>())
            {
                string name = UiContentSanitizer.SafeDisplayText(c["Name"]?.ToString(), 128) ?? "(unnamed)";
                bool ev = c["IsElectric"]?.ToObject<bool>() ?? false;
                host.Children.Add(new TextBlock {
                    Text = "• " + name + (ev ? "  (electric)" : ""),
                    Foreground = TextFg, FontSize = 12,
                    Margin = new Thickness(8, 1, 0, 1),
                });
            }
        }

        private TextBlock SectionHeader(string text) => new TextBlock {
            Text = text, Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 2),
        };

        private FrameworkElement EntryLine(string title, string subtitle)
        {
            var sp = new StackPanel { Margin = new Thickness(8, 1, 0, 3) };
            sp.Children.Add(new TextBlock {
                Text = "• " + title,
                Foreground = TextFg, FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(subtitle))
            {
                sp.Children.Add(new TextBlock {
                    Text = subtitle,
                    Foreground = MutedFg, FontSize = 11,
                    Margin = new Thickness(12, 0, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            return sp;
        }

        private TextBlock MutedLine(string text) => new TextBlock {
            Text = text, Foreground = MutedFg, FontSize = 12,
        };

        private void AddSection(StackPanel host, JObject ovr, string key, string label)
        {
            var sect = ovr[key] as JObject;
            if (sect == null) return;

            host.Children.Add(new TextBlock {
                Text = label, Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 2),
            });
            host.Children.Add(BuildFieldsBlock(sect));
        }

        // Two-column "Field    Value" listing for a JObject. Shows every
        // non-null primitive field the preset sets so the reader can see
        // the actual values the author tuned, not just a headline.
        // Nested objects + arrays render as a compact summary placeholder
        // rather than recursing (preset sections don't currently nest
        // beyond one level).
        private FrameworkElement BuildFieldsBlock(JObject section)
        {
            var grid = new Grid { Margin = new Thickness(8, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Surface enabled / disabled + gain first (the headline
            // tuning signal), then everything else alphabetically.
            var headline = new[] { "Enabled", "Gain", "Freq", "PulseFreq" };
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            int rendered = 0;
            foreach (var name in headline)
            {
                var t = section[name];
                if (t == null || t.Type == JTokenType.Null) continue;
                if (!IsRenderableValue(t)) continue;
                AppendFieldRow(grid, rendered++, name, t);
                seen.Add(name);
            }
            foreach (var prop in section.Properties()
                                          .Where(p => !seen.Contains(p.Name))
                                          .Where(p => p.Value != null && p.Value.Type != JTokenType.Null)
                                          .OrderBy(p => p.Name, System.StringComparer.OrdinalIgnoreCase))
            {
                if (!IsRenderableValue(prop.Value)) continue;
                AppendFieldRow(grid, rendered++, prop.Name, prop.Value);
            }
            if (rendered == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tb = new TextBlock {
                    Text = "(no fields set)", Foreground = MutedFg, FontSize = 11,
                };
                Grid.SetRow(tb, 0);
                Grid.SetColumn(tb, 0);
                Grid.SetColumnSpan(tb, 2);
                grid.Children.Add(tb);
            }
            return grid;
        }

        private void AppendFieldRow(Grid grid, int row, string name, JToken value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var nameBlock = new TextBlock {
                Text = name,
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 1, 8, 1),
            };
            Grid.SetRow(nameBlock, row);
            Grid.SetColumn(nameBlock, 0);
            grid.Children.Add(nameBlock);

            var valBlock = new TextBlock {
                Text = FormatValue(value),
                Foreground = TextFg, FontSize = 11,
                Margin = new Thickness(0, 1, 0, 1),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(valBlock, row);
            Grid.SetColumn(valBlock, 1);
            grid.Children.Add(valBlock);
        }

        // True when the token can be expressed on a single value line.
        // Objects don't fit (preset sections don't currently nest, but
        // a future schema add would degrade ungracefully without this
        // guard).
        private static bool IsRenderableValue(JToken t)
        {
            return t.Type != JTokenType.Object;
        }

        private static string FormatValue(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Float:
                    return t.ToObject<double>().ToString("0.###");
                case JTokenType.Boolean:
                    return t.ToObject<bool>() ? "Yes" : "No";
                case JTokenType.Array:
                    var arr = (JArray)t;
                    if (arr.Count == 0) return "(none)";
                    var first = arr.Take(6).Select(FormatValue);
                    string joined = string.Join(", ", first);
                    return arr.Count > 6 ? joined + " ... (" + arr.Count + ")" : joined;
                case JTokenType.Null:
                    return "(none)";
                default:
                    return t.ToString();
            }
        }
    }
}
