// Create + upload a community pack. Section picker over the user's
// library, filtered to entries the user is allowed to redistribute:
//   * Their own (non-built-in) game presets, car presets, and custom
//     engines are always eligible.
//   * Community-downloaded items are eligible only when the author
//     opted in (DownloadedPresetRecord.AllowInPacks captured at
//     download time).
//   * Built-ins are never eligible (they ship with the plugin).
//
// Bundles checked entries into the pack body and calls
// UploadPackToCommunityAsync. Mirrors the existing single-preset share
// modal in tone + sign-in gate.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class CreatePackWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush OkFg     = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        private static readonly Brush ErrFg    = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));

        public string UploadedPackId { get; private set; }

        private readonly TrueforcePlugin _plugin;
        private sealed class GameEntry { public string Name; public CheckBox Box; }
        private sealed class CarEntry  { public string CarId; public string GameName;
                                         public string PresetName; public CheckBox Box; }
        private sealed class EngineEntry { public CustomEngineDef Def; public CheckBox Box; }
        private readonly List<GameEntry>   _gameEntries   = new List<GameEntry>();
        private readonly List<CarEntry>    _carEntries    = new List<CarEntry>();
        private readonly List<EngineEntry> _engineEntries = new List<EngineEntry>();

        // The upload handler awaits a multi-second network call while only the
        // buttons are disabled; the title-bar X still closes the window. Once
        // closed, the continuation must not touch controls or set DialogResult
        // (which throws on a closed dialog, unhandled from an async void).
        // Same pattern as SignInWindow.
        private bool _closed;

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            base.OnClosed(e);
        }

        public CreatePackWindow(TrueforcePlugin plugin)
        {
            _plugin = plugin;

            Title         = "Create a community pack";
            Width         = 560;
            Height        = 640;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            // Title + sub.
            var headerStack = new StackPanel();
            DockPanel.SetDock(headerStack, Dock.Top);
            root.Children.Add(headerStack);

            headerStack.Children.Add(new TextBlock
            {
                Text = "Create a community pack",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = "Bundle game presets, car presets, and custom engines into one downloadable pack. Built-ins never appear here. Community items only appear when their author allowed re-bundling.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            // Metadata row: name + version.
            var metaGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(metaGrid, Dock.Top);
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            metaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameLbl = new TextBlock { Text = "Pack name", Foreground = MutedFg, FontSize = 11 };
            Grid.SetColumn(nameLbl, 0); Grid.SetRow(nameLbl, 0);
            metaGrid.Children.Add(nameLbl);
            var verLbl = new TextBlock { Text = "Version (optional)", Foreground = MutedFg, FontSize = 11 };
            Grid.SetColumn(verLbl, 2); Grid.SetRow(verLbl, 0);
            metaGrid.Children.Add(verLbl);

            var nameInput = new TextBox
            {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 13, MaxLength = 96, Margin = new Thickness(0, 2, 0, 0),
            };
            // Pack-name is the primary input; focus on open so the
            // user can start typing immediately.
            nameInput.Loaded += (s, e) => nameInput.Focus();
            Grid.SetColumn(nameInput, 0); Grid.SetRow(nameInput, 1);
            metaGrid.Children.Add(nameInput);
            var verInput = new TextBox
            {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 13, MaxLength = 32, Margin = new Thickness(0, 2, 0, 0),
            };
            Grid.SetColumn(verInput, 2); Grid.SetRow(verInput, 1);
            metaGrid.Children.Add(verInput);
            root.Children.Add(metaGrid);

            // Description.
            var descLbl = new TextBlock
            {
                Text = "Description (optional)",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2),
            };
            DockPanel.SetDock(descLbl, Dock.Top);
            root.Children.Add(descLbl);
            var descInput = new TextBox
            {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12, MaxLength = 1024,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 44,
                Margin = new Thickness(0, 0, 0, 10),
            };
            DockPanel.SetDock(descInput, Dock.Top);
            root.Children.Add(descInput);

            // Bottom action bar (declared before the scroll viewer so it
            // pins to the bottom; we wire its handlers after.)
            var statusText = new TextBlock
            {
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8),
                TextWrapping = TextWrapping.Wrap, Text = "",
            };
            DockPanel.SetDock(statusText, Dock.Bottom);
            root.Children.Add(statusText);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            DockPanel.SetDock(btnRow, Dock.Bottom);
            var cancelBtn = new Button
            {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);
            var uploadBtn = new Button
            {
                Content = "Upload pack", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            btnRow.Children.Add(uploadBtn);
            root.Children.Add(btnRow);

            // Scrollable section list (fills the remaining vertical space).
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var sections = new StackPanel();
            scroller.Content = sections;
            root.Children.Add(scroller);

            BuildGameSection(sections);
            BuildCarSection(sections);
            BuildEngineSection(sections);

            uploadBtn.Click += async (s, e) =>
            {
                string packName = (nameInput.Text ?? "").Trim();
                if (packName.Length < 2)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Pick a pack name (2 chars or more).";
                    return;
                }
                if (_plugin?.Settings?.CommunityEnabled != true)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Turn on 'Enable community features (online)' in Settings first.";
                    return;
                }
                if (!_plugin.AuthIsSignedIn)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Sign in first (Settings -> Account).";
                    return;
                }

                int entryCount = 0;
                var body = new JObject();
                // Each entry carries source provenance so downstream
                // pack importers can stamp DownloadedPresetRecord per
                // entry and the original author's allow_in_packs
                // setting follows the asset through the chain:
                //   source_id      -> uuid of the original community row
                //                     (null when the pack creator made it)
                //   source_author  -> the original author's username
                //   allow_in_packs -> author's redistribution choice
                // Look up via DownloadedPresetRecord (keyed by server id),
                // walking the local override/snapshot's CommunitySourceId.
                var dl = _plugin.Settings?.DownloadedCommunityPresets;

                var gameArr = new JArray();
                foreach (var ge in _gameEntries)
                    if (ge.Box.IsChecked == true
                        && _plugin.Settings?.Presets != null
                        && _plugin.Settings.Presets.TryGetValue(ge.Name, out var snap)
                        && snap != null)
                    {
                        var je = new JObject
                        {
                            ["name"]     = ge.Name,
                            ["snapshot"] = PresetSharingClient.BuildShareableSnapshotToken(snap),
                        };
                        string sid = snap.CommunitySourceId;
                        if (!string.IsNullOrEmpty(sid))
                        {
                            je["source_id"] = sid;
                            if (dl != null && dl.TryGetValue(sid, out var rec) && rec != null)
                                je["allow_in_packs"] = rec.AllowInPacks;
                            if (!string.IsNullOrEmpty(snap.Author))
                                je["source_author"] = snap.Author;
                        }
                        gameArr.Add(je);
                        entryCount++;
                    }
                if (gameArr.Count > 0) body["game_presets"] = gameArr;

                var carArr = new JArray();
                foreach (var ce in _carEntries)
                    if (ce.Box.IsChecked == true)
                    {
                        var perCar = _plugin.GetCarPresets(ce.CarId);
                        if (perCar != null && perCar.TryGetValue(ce.PresetName, out var entry)
                            && entry?.Override != null)
                        {
                            var je = new JObject
                            {
                                ["car_id"]      = ce.CarId,
                                ["preset_name"] = ce.PresetName,
                                ["game_name"]   = ce.GameName,
                                ["override"]    = JToken.FromObject(entry.Override),
                            };
                            string sid = entry.Override.CommunitySourceId;
                            if (!string.IsNullOrEmpty(sid))
                            {
                                je["source_id"] = sid;
                                if (dl != null && dl.TryGetValue(sid, out var rec) && rec != null)
                                    je["allow_in_packs"] = rec.AllowInPacks;
                                if (!string.IsNullOrEmpty(entry.Author))
                                    je["source_author"] = entry.Author;
                            }
                            carArr.Add(je);
                            entryCount++;
                        }
                    }
                if (carArr.Count > 0) body["car_presets"] = carArr;

                var engineArr = new JArray();
                foreach (var ee in _engineEntries)
                    if (ee.Box.IsChecked == true && ee.Def != null)
                    {
                        var je = JObject.FromObject(ee.Def);
                        string sid = ee.Def.CommunitySourceId;
                        if (!string.IsNullOrEmpty(sid))
                        {
                            je["source_id"] = sid;
                            if (dl != null && dl.TryGetValue(sid, out var rec) && rec != null)
                                je["allow_in_packs"] = rec.AllowInPacks;
                            if (!string.IsNullOrEmpty(ee.Def.Author))
                                je["source_author"] = ee.Def.Author;
                        }
                        engineArr.Add(je);
                        entryCount++;
                    }
                if (engineArr.Count > 0) body["custom_engines"] = engineArr;

                if (entryCount == 0)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Pick at least one entry to include.";
                    return;
                }

                uploadBtn.IsEnabled = false;
                cancelBtn.IsEnabled = false;
                statusText.Foreground = MutedFg;
                statusText.Text = $"Uploading {entryCount} entr{(entryCount == 1 ? "y" : "ies")}...";

                string newId = null;
                try
                {
                    newId = await _plugin.UploadPackToCommunityAsync(
                        packName, body,
                        descInput.Text?.Trim(),
                        verInput.Text?.Trim(),
                        entryCount);
                }
                catch (Exception ex)
                {
                    if (_closed) return;
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Upload exception: " + ex.Message;
                    uploadBtn.IsEnabled = true; cancelBtn.IsEnabled = true;
                    return;
                }
                if (_closed) return;
                if (string.IsNullOrEmpty(newId))
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = _plugin.DescribeLastUploadError();
                    uploadBtn.IsEnabled = true; cancelBtn.IsEnabled = true;
                    if (_plugin.LastUploadWasBlocked())
                        await ModerationNoticesWindow.MaybeShowAsync(_plugin, this, force: true).ConfigureAwait(true);
                    return;
                }
                UploadedPackId = newId;
                statusText.Foreground = OkFg;
                statusText.Text = "Uploaded. Thanks for contributing.";
                DialogResult = true;
                Close();
            };
        }

        private void BuildGameSection(StackPanel host)
        {
            var expander = new Expander
            {
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 6),
                Header = new TextBlock
                {
                    Text = "Game presets",
                    Foreground = TextFg, FontWeight = FontWeights.SemiBold, FontSize = 12,
                },
            };
            var list = new StackPanel { Margin = new Thickness(12, 4, 0, 8) };
            expander.Content = list;
            host.Children.Add(expander);

            if (_plugin?.Settings?.Presets == null) return;
            foreach (var kv in _plugin.Settings.Presets.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string name = kv.Key;
                var snap = kv.Value;
                if (_plugin.IsBuiltinPreset(name)) continue;
                if (!IsRedistributable(snap?.CommunitySourceId, snap?.CommunityAllowInPacks, snap?.CommunityUploadedByUserId))
                {
                    list.Children.Add(MakeIneligibleRow(UiContentSanitizer.SafeDisplayText(name, 128) + "  (community item, not redistributable)"));
                    continue;
                }
                var cb = new CheckBox
                {
                    Content = UiContentSanitizer.SafeDisplayText(name, 128),
                    Foreground = TextFg, FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                list.Children.Add(cb);
                _gameEntries.Add(new GameEntry { Name = name, Box = cb });
            }
            if (_gameEntries.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "(no eligible game presets)",
                    Foreground = MutedFg, FontStyle = FontStyles.Italic, FontSize = 11,
                });
        }

        private void BuildCarSection(StackPanel host)
        {
            var expander = new Expander
            {
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 6),
                Header = new TextBlock
                {
                    Text = "Car presets",
                    Foreground = TextFg, FontWeight = FontWeights.SemiBold, FontSize = 12,
                },
            };
            var list = new StackPanel { Margin = new Thickness(12, 4, 0, 8) };
            expander.Content = list;
            host.Children.Add(expander);

            var allCars = _plugin?.GetAllCarPresets();
            if (allCars == null) return;
            foreach (var carKv in allCars.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string carId = carKv.Key;
                if (carKv.Value == null) continue;
                foreach (var entry in carKv.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string presetName = entry.Key;
                    var carEntry = entry.Value;
                    if (carEntry == null) continue;
                    if (carEntry.IsBuiltin) continue;
                    string gameName = carEntry.GameName ?? "";
                    if (!IsRedistributable(carEntry.Override?.CommunitySourceId, carEntry.Override?.CommunityAllowInPacks, carEntry.Override?.CommunityUploadedByUserId))
                    {
                        list.Children.Add(MakeIneligibleRow($"{UiContentSanitizer.SafeDisplayText(carId, 96)} :: {UiContentSanitizer.SafeDisplayText(presetName, 96)}  (community item, not redistributable)"));
                        continue;
                    }
                    var cb = new CheckBox
                    {
                        Content = $"{UiContentSanitizer.SafeDisplayText(carId, 96)} :: {UiContentSanitizer.SafeDisplayText(presetName, 96)}",
                        Foreground = TextFg, FontSize = 12,
                        Margin = new Thickness(0, 1, 0, 1),
                    };
                    list.Children.Add(cb);
                    _carEntries.Add(new CarEntry
                    {
                        CarId = carId, GameName = gameName, PresetName = presetName, Box = cb,
                    });
                }
            }
            if (_carEntries.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "(no eligible car presets)",
                    Foreground = MutedFg, FontStyle = FontStyles.Italic, FontSize = 11,
                });
        }

        private void BuildEngineSection(StackPanel host)
        {
            var expander = new Expander
            {
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 6),
                Header = new TextBlock
                {
                    Text = "Custom engines",
                    Foreground = TextFg, FontWeight = FontWeights.SemiBold, FontSize = 12,
                },
            };
            var list = new StackPanel { Margin = new Thickness(12, 4, 0, 8) };
            expander.Content = list;
            host.Children.Add(expander);

            var engines = _plugin?.Settings?.CustomEngines;
            if (engines == null) return;
            foreach (var def in engines.OrderBy(d => d?.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (def == null || string.IsNullOrWhiteSpace(def.Name)) continue;
                if (!IsRedistributable(def.CommunitySourceId, def.CommunityAllowInPacks, def.CommunityUploadedByUserId))
                {
                    list.Children.Add(MakeIneligibleRow(UiContentSanitizer.SafeDisplayText(def.Name, 128) + "  (community engine, not redistributable)"));
                    continue;
                }
                var cb = new CheckBox
                {
                    Content = UiContentSanitizer.SafeDisplayText(def.Name, 128),
                    Foreground = TextFg, FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                list.Children.Add(cb);
                _engineEntries.Add(new EngineEntry { Def = def, Box = cb });
            }
            if (_engineEntries.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "(no eligible custom engines)",
                    Foreground = MutedFg, FontStyle = FontStyles.Italic, FontSize = 11,
                });
        }

        private FrameworkElement MakeIneligibleRow(string label)
            => new TextBlock
            {
                Text = label,
                Foreground = MutedFg, FontStyle = FontStyles.Italic, FontSize = 11,
                Margin = new Thickness(0, 1, 0, 1),
            };

        /// <summary>True when the item is either user-owned (no
        /// CommunitySourceId stamped) or community-sourced with
        /// AllowInPacks=true. Identity is by stable server uuid, not
        /// local name, so rename / duplicate / re-import don't open
        /// permission holes. Built-ins are pre-filtered by the section
        /// builders. communitySourceId=null means the user authored
        /// this item locally.</summary>
        private bool IsRedistributable(string communitySourceId, bool? presetAllowInPacks,
            string communityUploadedByUserId)
        {
            // Self-uploaded short-circuit: if THIS user uploaded the
            // preset to community, they're the author and can re-bundle
            // their own work regardless of what AllowInPacks says (which
            // is a permission meant for OTHERS). Resolved via the local
            // CommunityUploadedByUserId stamp written by Stamp*AsUploaded.
            if (!string.IsNullOrEmpty(communityUploadedByUserId)
                && _plugin?.AuthIsSignedIn == true
                && string.Equals(communityUploadedByUserId,
                                 _plugin.AuthSignedInUserId,
                                 StringComparison.Ordinal))
                return true;

            // Locally authored, never uploaded, never imported from
            // anywhere -> the local user owns it outright.
            if (string.IsNullOrEmpty(communitySourceId)) return true;

            // Preset-level field is authoritative when set: it travels
            // with export/import and survives a missing tracker entry.
            // Stamped at download from PresetSummary.AllowInPacks and at
            // import from the carried JSON field.
            if (presetAllowInPacks.HasValue) return presetAllowInPacks.Value;
            // Legacy fallback for installs that pre-date the preset-level
            // field: read from DownloadedCommunityPresets where downloads
            // recorded the value at the time. Returns false on missing
            // tracker entries (conservative: unknown permission == deny).
            var map = _plugin?.Settings?.DownloadedCommunityPresets;
            if (map == null) return false;
            return map.TryGetValue(communitySourceId, out var rec)
                && rec != null
                && rec.AllowInPacks;
        }

        /// <summary>Pre-check car-preset entries by (carId, presetName).
        /// Called by the manager's bulk-share-pack path so the user's
        /// already-selected rows arrive in the window already ticked;
        /// rows that aren't pack-eligible (built-in, or non-redistributable
        /// community import) are skipped silently because they were never
        /// added to <see cref="_carEntries"/>.</summary>
        public void PrecheckCarPresets(IEnumerable<KeyValuePair<string, string>> targets)
        {
            if (targets == null) return;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in targets) set.Add(t.Key + "|" + t.Value);
            foreach (var e in _carEntries)
                if (e?.Box != null && set.Contains(e.CarId + "|" + e.PresetName))
                    e.Box.IsChecked = true;
        }

        /// <summary>Pre-check game-preset entries by name. Mirror of
        /// <see cref="PrecheckCarPresets"/> for the Games-segment bulk
        /// share-pack path.</summary>
        public void PrecheckGamePresets(IEnumerable<string> presetNames)
        {
            if (presetNames == null) return;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in presetNames) if (!string.IsNullOrEmpty(n)) set.Add(n);
            foreach (var e in _gameEntries)
                if (e?.Box != null && set.Contains(e.Name))
                    e.Box.IsChecked = true;
        }
    }
}
