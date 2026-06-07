// Discoverability popover: when a user is in a car and the community
// has presets for it, the header surfaces a "* N community" chip. Click
// opens this window - a tight list of the top 3-5 presets with vote
// arrows, score, author, and a one-click Apply button per row. Apply
// downloads + imports + sets the preset as the car's active preset (no
// section picker - that's the slower-path Preset Manager flow).
//
// "See all" link at the bottom switches the host into the full
// Preset Manager Cars-Community view.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class CommunityForCarPopover : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush RowBg    = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush OkFg     = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        private static readonly Brush ErrFg    = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));
        private static readonly Brush LinkFg   = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xD8));

        /// <summary>Set to true when the user clicks "See all..." so
        /// the caller can switch the host UI to the full Cars-Community
        /// view instead of just closing the popover.</summary>
        public bool SeeAllRequested { get; private set; }

        private readonly TrueforcePlugin _plugin;
        private readonly string _activeCarId;
        private readonly string _activeGame;

        public CommunityForCarPopover(TrueforcePlugin plugin,
            string activeCarId, string activeGame, string carDisplay,
            IList<PresetSummary> topPresets)
        {
            _plugin      = plugin;
            _activeCarId = activeCarId ?? "";
            _activeGame  = activeGame  ?? "";

            Title         = "Community presets for this car";
            Width         = 520;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock
            {
                Text = "Top community presets",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(carDisplay)
                    ? $"For {activeCarId} in {activeGame}."
                    : $"For {carDisplay} ({activeCarId}) in {activeGame}.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            // Status line for in-flight apply / errors. Shared across rows.
            var statusText = new TextBlock
            {
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "",
                TextWrapping = TextWrapping.Wrap,
            };

            if (topPresets == null || topPresets.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "(no community presets available right now)",
                    Foreground = MutedFg, FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 0, 0, 12),
                });
            }
            else
            {
                foreach (var p in topPresets)
                    root.Children.Add(BuildRow(p, statusText));
            }

            root.Children.Add(statusText);

            // Footer: "See all in Preset Manager" link + Close.
            var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var seeAllBtn = new Button
            {
                Content = "See all in Preset Manager...",
                Padding = new Thickness(0), Margin = new Thickness(0),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                Foreground = LinkFg, FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            seeAllBtn.Click += (s, e) =>
            {
                SeeAllRequested = true;
                DialogResult = true;
                Close();
            };
            DockPanel.SetDock(seeAllBtn, Dock.Left);
            footer.Children.Add(seeAllBtn);

            var closeBtn = new Button
            {
                Content = "Close", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            closeBtn.Click += (s, e) => { DialogResult = false; Close(); };
            footer.Children.Add(closeBtn);
            root.Children.Add(footer);
        }

        private FrameworkElement BuildRow(PresetSummary p, TextBlock statusText)
        {
            var row = new Border
            {
                Background = RowBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.Child = grid;

            var meta = new StackPanel();
            Grid.SetColumn(meta, 0);
            grid.Children.Add(meta);
            meta.Children.Add(new TextBlock
            {
                Text = UiContentSanitizer.SafeDisplayText(p.Name, 128) ?? "(unnamed)",
                Foreground = TextFg, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var subParts = new List<string>(3);
            if (!string.IsNullOrEmpty(p.Author)) subParts.Add("by " + UiContentSanitizer.SafeDisplayText(p.Author, 96));
            if (p.Downloads > 0)                  subParts.Add(p.Downloads + " downloads");
            if (p.EffectTags != null && p.EffectTags.Count > 0)
                subParts.Add(string.Join(", ", p.EffectTags));
            if (subParts.Count > 0)
            {
                meta.Children.Add(new TextBlock
                {
                    Text = string.Join("  *  ", subParts),
                    Foreground = MutedFg, FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            int score = p.Upvotes - p.Downvotes;
            var scoreText = new TextBlock
            {
                Text = (score > 0 ? "+" : "") + score,
                Foreground = score >= 0 ? OkFg : ErrFg,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(scoreText, 1);
            grid.Children.Add(scoreText);

            var applyBtn = new Button
            {
                Content = "Apply",
                Padding = new Thickness(10, 4, 10, 4),
                Foreground = TextFg, Background = PanelBg,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(applyBtn, 2);
            grid.Children.Add(applyBtn);

            // Per-row status line so a later row's failure doesn't
            // overwrite an earlier row's success. Lives directly under
            // the row, hidden until something happens.
            var rowStatus = new TextBlock
            {
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            var rowStack = new StackPanel();
            rowStack.Children.Add(row);
            rowStack.Children.Add(rowStatus);

            applyBtn.Click += async (s, e) =>
            {
                applyBtn.IsEnabled = false;
                rowStatus.Foreground = MutedFg;
                rowStatus.Text = $"Applying '{UiContentSanitizer.SafeDisplayText(p.Name, 96) ?? "preset"}'...";
                rowStatus.Visibility = Visibility.Visible;
                bool ok = await ApplyOneAsync(p, statusText);
                if (ok)
                {
                    rowStatus.Foreground = OkFg;
                    rowStatus.Text = $"Applied '{UiContentSanitizer.SafeDisplayText(p.Name, 96) ?? "preset"}'. This is now the car's active preset.";
                    applyBtn.Content   = "Re-apply";
                    applyBtn.IsEnabled = true;
                }
                else
                {
                    rowStatus.Foreground = ErrFg;
                    rowStatus.Text = "Apply failed. Check the status line below for details.";
                    applyBtn.IsEnabled = true;
                }
            };
            return rowStack;
        }

        // Download + import + set-as-active for one preset. Returns
        // false on any failure (status text is already set).
        private async Task<bool> ApplyOneAsync(PresetSummary p, TextBlock statusText)
        {
            if (_plugin == null || p?.Id == null) return false;
            PresetFull full;
            try
            {
                full = await Task.Run(() => _plugin.FetchCommunityPresetBody(p.Id));
            }
            catch (Exception ex)
            {
                statusText.Foreground = ErrFg;
                statusText.Text = "Download failed: " + ex.Message;
                return false;
            }
            if (full?.Body == null || full.Summary == null)
            {
                statusText.Foreground = ErrFg;
                statusText.Text = "Download returned no body.";
                return false;
            }

            // Body shape: { override: {...}, custom_engines: [...] }
            CarOverride ovr = null;
            List<CustomEngineDef> customs = null;
            try
            {
                var ovrToken = full.Body["override"];
                if (ovrToken != null) ovr = ovrToken.ToObject<CarOverride>();
                var ceToken = full.Body["custom_engines"];
                if (ceToken is Newtonsoft.Json.Linq.JArray ja)
                    customs = ja.ToObject<List<CustomEngineDef>>();
            }
            catch (Exception ex)
            {
                statusText.Foreground = ErrFg;
                statusText.Text = "Body parse failed: " + ex.Message;
                return false;
            }
            if (ovr == null)
            {
                statusText.Foreground = ErrFg;
                statusText.Text = "Body had no override section.";
                return false;
            }

            if (customs != null && customs.Count > 0)
                _plugin.ImportCommunityCustomEngines(customs);

            // Dedup: if the user already has this preset locally for
            // this car (matching CommunitySourceId), just switch to
            // it instead of saving a duplicate.
            var existing = _plugin.GetCarPresets(_activeCarId);
            string existingName = null;
            if (existing != null)
                foreach (var kv in existing)
                    if (kv.Value?.Override != null
                        && string.Equals(kv.Value.Override.CommunitySourceId, full.Summary.Id,
                                         StringComparison.Ordinal))
                    { existingName = kv.Key; break; }
            if (!string.IsNullOrEmpty(existingName))
            {
                try
                {
                    _plugin.SwitchActiveCarPreset(_activeCarId, existingName);
                }
                catch (Exception ex)
                {
                    // The preset save+import already worked locally;
                    // only the "make it active" call failed. Tell the
                    // user instead of pretending it's set as active.
                    SimHub.Logging.Current.Info(
                        "[Trueforce] Popover switch-to-existing failed: " + ex.Message);
                    statusText.Foreground = ErrFg;
                    statusText.Text =
                        $"Found '{existingName}' in your library but couldn't make it active. Set it in Presets > Car presets.";
                    return false;
                }
                statusText.Foreground = OkFg;
                statusText.Text = $"Switched this car to '{existingName}' (already in your library).";
                return true;
            }

            // Pick a non-colliding preset name in the active car's library.
            string baseName = (full.Summary.Name ?? "Community preset") + " (community)";
            string presetName = baseName;
            int n = 2;
            while (existing != null && existing.ContainsKey(presetName))
            {
                presetName = baseName + " " + n;
                n++;
            }

            _plugin.SaveImportedCommunityCarPreset(
                _activeCarId, presetName, _activeGame, ovr,
                full.Summary.Author, full.Summary.Description,
                communitySourceId: full.Summary.Id);

            // Make it the active preset for this car BEFORE recording
            // the download. If the switch fails we don't want the
            // server-side download counter inflated for a flow the user
            // didn't actually get a working preset out of, and we don't
            // want a permanent local tracker row claiming "downloaded"
            // for a preset that isn't bound to the car.
            try
            {
                _plugin.SwitchActiveCarPreset(_activeCarId, presetName);
            }
            catch (Exception ex)
            {
                // Import succeeded but the car-binding write failed.
                // The preset is in the user's library; they just need
                // to set it active manually. Don't claim full success.
                SimHub.Logging.Current.Info(
                    "[Trueforce] Popover switch-after-save failed: " + ex.Message);
                statusText.Foreground = ErrFg;
                statusText.Text =
                    $"Saved '{presetName}' to your library but couldn't make it active. Set it in Presets > Car presets.";
                return false;
            }

            // Switch worked. Record now so the server counter + local
            // tracker reflect a real, completed download.
            _plugin.RecordCommunityPresetDownload(p.Id);
            _plugin.RecordDownloadedCommunityPreset(
                full.Summary.Id, presetName, _activeCarId, _activeGame,
                full.Summary.ContentVersion, kind: "car",
                allowInPacks: full.Summary.AllowInPacks);
            return true;
        }
    }
}
