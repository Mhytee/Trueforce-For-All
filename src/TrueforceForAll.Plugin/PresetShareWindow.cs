// Modal that fires from the preset manager's "Share..." button. Shows the
// preset being uploaded (car, game, preset name, which effect sections it
// touches), lets the user confirm an Author (pre-filled from
// Settings.SharingAuthor) + optional Description, then uploads through
// PresetSharingClient. On success the new preset id is returned via the
// UploadedPresetId property so the caller can record it / surface a
// success toast.
//
// Styled to match CarFactsShareWindow / CarNameInputWindow.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class PresetShareWindow : Window
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

        public string UploadedPresetId { get; private set; }

        private readonly TrueforcePlugin _plugin;
        private readonly string _kind;        // "car" or "game"
        private readonly string _presetName;
        private readonly string _game;
        private readonly string _carId;       // "" for game presets
        private readonly string _carDisplay;  // "" for game presets
        private readonly Newtonsoft.Json.Linq.JObject _body;
        private readonly List<string> _effectTags;

        // Well-known SimHub game identifiers we seed the target-games picker
        // with. Mirrors IsNativeTrueforceGame + a few non-native titles
        // common in the user base. The free-text Add input lets users name
        // anything not on the list.
        private static readonly string[] WellKnownGames = new[]
        {
            "AssettoCorsa",
            "AssettoCorsaCompetizione",
            "AssettoCorsaRally",
            "AssettoCorsaEVO",
            "Automobilista2",
            "BeamNgDrive",
            "CodemastersGrid2019",
            "EAWRC23",
            "F12022",
            "F12023",
            "F12024",
            "F12025",
            "FM8",
            "ForzaHorizon5",
            "ForzaHorizon6",
            "IRacing",
            "LMU",
            "PCars2",
            "PCars3",
            "RaceRoom",
            "TDUSC",
            "WRC10",
            "WRCGenerations",
        };

        /// <summary>Modal for sharing a car preset. Body is the
        /// CarOverride payload + any referenced CustomEngineDefs.</summary>
        public static PresetShareWindow ForCar(
            TrueforcePlugin plugin,
            string presetName, string game, string carId, string carDisplay,
            Newtonsoft.Json.Linq.JObject body, List<string> effectTags)
            => new PresetShareWindow(plugin, "car",
                presetName, game, carId, carDisplay, body, effectTags);

        /// <summary>Modal for sharing a game preset (a whole-game
        /// settings snapshot). Body is a serialized
        /// GameSettingsSnapshot.</summary>
        public static PresetShareWindow ForGame(
            TrueforcePlugin plugin,
            string presetName, string game,
            Newtonsoft.Json.Linq.JObject body, List<string> effectTags)
            => new PresetShareWindow(plugin, "game",
                presetName, game, "", "", body, effectTags);

        /// <summary>Modal for sharing a custom engine. Body is a
        /// serialized CustomEngineDef. Game-agnostic, so no game /
        /// car readout rows; just name + description + permission +
        /// the same auth-gate / sign-in flow.</summary>
        public static PresetShareWindow ForEngine(
            TrueforcePlugin plugin,
            string engineName,
            Newtonsoft.Json.Linq.JObject body)
            => new PresetShareWindow(plugin, "engine",
                engineName, "", "", "", body, new List<string>());

        // Back-compat: existing car call sites that haven't migrated
        // to ForCar yet still work via the legacy ctor signature.
        public PresetShareWindow(
            TrueforcePlugin plugin,
            string presetName, string game, string carId, string carDisplay,
            Newtonsoft.Json.Linq.JObject body, List<string> effectTags)
            : this(plugin, "car", presetName, game, carId, carDisplay, body, effectTags)
        { }

        private PresetShareWindow(
            TrueforcePlugin plugin, string kind,
            string presetName, string game, string carId, string carDisplay,
            Newtonsoft.Json.Linq.JObject body, List<string> effectTags)
        {
            _plugin     = plugin;
            string lk = (kind ?? "car").ToLowerInvariant();
            _kind       = lk == "game" || lk == "engine" ? lk : "car";
            _presetName = presetName ?? "";
            _game       = game ?? "";
            _carId      = carId ?? "";
            _carDisplay = string.IsNullOrEmpty(carDisplay) ? carId : carDisplay;
            _body       = body;
            _effectTags = effectTags ?? new List<string>();

            bool isGame   = _kind == "game";
            bool isEngine = _kind == "engine";
            Title         = isEngine
                ? "Share custom engine with the community"
                : isGame
                    ? "Share game preset with the community"
                    : "Share preset with the community";
            Width         = 480;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = isEngine
                    ? "Share custom engine with the community"
                    : isGame
                        ? "Share game preset with the community"
                        : "Share preset with the community",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // What we're sharing - a passive readout so the user can't
            // forget which preset is going up. Engines are game-agnostic
            // so neither Game nor Car nor Sections apply. Game presets
            // skip the Car row.
            root.Children.Add(MakeFactLine(isEngine ? "Engine" : "Preset", _presetName));
            if (!isEngine)
            {
                // Game presets aren't game-specific (per the data model -
                // target_games is a per-share assertion the user makes in
                // the picker below); the active-game readout was
                // misleading. Skip the Game line for game presets and let
                // the picker carry that decision.
                if (!isGame)
                    root.Children.Add(MakeFactLine("Game", _game));
                if (!isGame)
                    root.Children.Add(MakeFactLine("Car", _carDisplay));
                root.Children.Add(MakeFactLine("Sections", _effectTags.Count == 0
                    ? "(none)" : string.Join(", ", _effectTags)));
            }

            // Identity is server-authoritative: the server stamps
            // author = profiles.username from the signed-in user's
            // session. Read-only display so the user sees exactly how
            // the upload will be credited.
            bool signedIn = _plugin?.AuthIsSignedIn == true;
            string sharingAs = signedIn
                ? (_plugin?.Settings?.SharingAuthor ?? "(your username)")
                : "(sign in required)";
            root.Children.Add(MakeFactLine("Sharing as", sharingAs));

            root.Children.Add(new TextBlock {
                Text = "Description (optional, what makes this preset feel good):",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var descInput = new TextBox {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12, MaxLength = 1024,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 60,
                Margin = new Thickness(0, 0, 0, 12),
            };
            descInput.IsEnabled = signedIn;
            // Description is the only editable field on this modal -
            // grab focus on open so the user starts typing immediately.
            if (signedIn)
                descInput.Loaded += (s, e) => descInput.Focus();
            root.Children.Add(descInput);

            // Permission toggle: opt this preset in to being re-bundled
            // inside someone else's pack. Off by default (conservative).
            // Pack creators only see entries with this on (or items the
            // creator owns); built-ins and "no-redistribute" community
            // items never accidentally ride along.
            var allowInPacksCheck = new CheckBox
            {
                Content = "Allow others to include this in their packs",
                Foreground = TextFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                IsEnabled = signedIn,
            };
            root.Children.Add(allowInPacksCheck);

            // Target-games picker: game-preset uploads only. Universal
            // (no scope) by default; toggling Universal off reveals the
            // checkbox list of well-known games + free-text Add.
            CheckBox universalCheck = null;
            ListBox gamesList = null;
            TextBox addGameInput = null;
            Button addGameBtn = null;
            FrameworkElement gamesPanel = null;
            if (isGame)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "Which games is this tuned for?",
                    Foreground = MutedFg, FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4),
                });
                // Default the picker from the user's own GameDefaults: any
                // game keyed to this preset name reflects intent ("I use this
                // preset for X"). Active game is just whatever's running; not
                // a reliable signal of what the preset is tuned for. Universal
                // is the right default only when the user hasn't bound this
                // preset to any game.
                var defaultGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var gameDefaultsMap = _plugin?.Settings?.GameDefaults;
                if (gameDefaultsMap != null)
                {
                    foreach (var kv in gameDefaultsMap)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        if (string.Equals(kv.Value, _presetName, StringComparison.Ordinal))
                            defaultGames.Add(kv.Key.Trim());
                    }
                }
                bool startUniversal = defaultGames.Count == 0;
                universalCheck = new CheckBox
                {
                    Content = "Universal (any game)",
                    Foreground = TextFg, FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 6),
                    IsChecked = startUniversal,
                    IsEnabled = signedIn,
                };
                root.Children.Add(universalCheck);

                var pickerStack = new StackPanel
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    Visibility = startUniversal ? Visibility.Collapsed : Visibility.Visible,
                };
                gamesPanel = pickerStack;
                gamesList = new ListBox
                {
                    Foreground = TextFg, Background = InputBg,
                    BorderBrush = BorderFg,
                    Height = 140,
                    Margin = new Thickness(0, 0, 0, 6),
                    SelectionMode = SelectionMode.Multiple,
                };
                // Seed order: pre-checked default-bound games first (the
                // user's existing intent), then active game (unchecked - a
                // hint they can add it if they want), then well-known list.
                // All deduped case-insensitively against each other.
                var seedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in defaultGames)
                {
                    if (string.IsNullOrWhiteSpace(g)) continue;
                    if (!seedSeen.Add(g)) continue;
                    gamesList.Items.Add(MakeGameItem(g, true));
                }
                string activeGame = _plugin?.ActiveGame;
                if (!string.IsNullOrWhiteSpace(activeGame) && seedSeen.Add(activeGame.Trim()))
                    gamesList.Items.Add(MakeGameItem(activeGame.Trim(), false));
                foreach (var g in WellKnownGames)
                {
                    if (string.IsNullOrWhiteSpace(g)) continue;
                    if (!seedSeen.Add(g)) continue;
                    gamesList.Items.Add(MakeGameItem(g, false));
                }
                pickerStack.Children.Add(gamesList);

                var addRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                };
                addGameInput = new TextBox
                {
                    Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                    Padding = new Thickness(6, 4, 6, 4), FontSize = 12, MaxLength = 64,
                    Width = 260,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                addGameBtn = new Button
                {
                    Content = "Add", Padding = new Thickness(10, 3, 10, 3),
                    Foreground = TextFg, Background = PanelBg,
                };
                addRow.Children.Add(addGameInput);
                addRow.Children.Add(addGameBtn);
                pickerStack.Children.Add(addRow);

                // Wire Add: append a new pre-checked entry if not already present.
                addGameBtn.Click += (s, e) =>
                {
                    string raw = (addGameInput.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(raw)) return;
                    if (raw.Length > 64) raw = raw.Substring(0, 64);
                    foreach (CheckBox existing in gamesList.Items)
                    {
                        string txt = existing?.Content as string;
                        if (!string.IsNullOrEmpty(txt)
                            && string.Equals(txt, raw, StringComparison.OrdinalIgnoreCase))
                        {
                            existing.IsChecked = true;
                            addGameInput.Text = "";
                            return;
                        }
                    }
                    gamesList.Items.Add(MakeGameItem(raw, true));
                    addGameInput.Text = "";
                };

                root.Children.Add(pickerStack);

                // Toggle: Universal hides the picker; off shows it.
                universalCheck.Checked   += (s, e) => { pickerStack.Visibility = Visibility.Collapsed; };
                universalCheck.Unchecked += (s, e) => { pickerStack.Visibility = Visibility.Visible; };
            }

            // Status line for in-flight upload / errors. Wording shifts
            // based on whether the user is signed in.
            var statusText = new TextBlock {
                Foreground = signedIn ? MutedFg : ErrFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                Text = signedIn
                    ? "Uploads are credited to your username."
                    : "Sign in to share. Click \"Sign in...\" below.",
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(statusText);

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            // Sign-in button only renders when the user is signed out.
            // Clicking it opens SignInWindow modally; on success we
            // re-enable Upload + flip the status copy.
            Button signInBtn = null;
            if (!signedIn)
            {
                signInBtn = new Button {
                    Content = "Sign in...", Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(0, 0, 8, 0),
                    Foreground = TextFg, Background = PanelBg,
                };
                btnRow.Children.Add(signInBtn);
            }
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            // Empty-preset gate: car + game presets with no effect
            // sections selected would otherwise upload an empty body
            // that adds nothing for downloaders. Engines have no
            // sections concept, so they bypass this check (Upload is
            // enabled whenever signed in).
            bool hasSections = isEngine || _effectTags.Count > 0;
            var uploadBtn = new Button {
                Content = "Upload", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
                IsEnabled = signedIn && hasSections,
            };
            // Tooltip explains the disabled state when no sections
            // are picked. ShowOnDisabled=true so the user actually
            // sees it; WPF hides tooltips on disabled controls by
            // default.
            if (!isEngine && !hasSections)
            {
                uploadBtn.ToolTip = "Pick at least one effect section to share. An empty preset has nothing for downloaders to apply.";
                System.Windows.Controls.ToolTipService.SetShowOnDisabled(uploadBtn, true);
            }

            // Wire up the sign-in click after uploadBtn exists so we
            // can re-enable Upload + flip status when sign-in succeeds.
            if (signInBtn != null)
            {
                signInBtn.Click += async (s, e) =>
                {
                    var dlg = new SignInWindow(_plugin) { Owner = this };
                    bool? ok = dlg.ShowDialog();
                    // Check ok==true (matches every other SignInWindow
                    // caller) AND AuthIsSignedIn for defence-in-depth:
                    // ok==true is set only after VerifyOtpAsync succeeds
                    // AND SaveSession ran, so AuthIsSignedIn should agree;
                    // the belt-and-suspenders check guards against a
                    // future drift between dialog result and auth state.
                    if (ok == true && _plugin?.AuthIsSignedIn == true)
                    {
                        // Brand-new accounts have no profile.username
                        // yet; uploading would error out as "set a
                        // username first" from the server. Bootstrap
                        // it here so the inline sign-in stays parity
                        // with Settings panel sign-in.
                        try { await PickUsernameWindow.EnsureUsernameAsync(_plugin, this); }
                        catch { /* user cancelled / network blip; checked below */ }
                        // If the bootstrap couldn't populate a
                        // SharingAuthor (cancelled picker / network),
                        // surface a clear status and KEEP Upload
                        // disabled - otherwise the user clicks Upload
                        // and gets a raw "set a username first" from
                        // the server.
                        if (string.IsNullOrWhiteSpace(_plugin?.Settings?.SharingAuthor))
                        {
                            statusText.Foreground = ErrFg;
                            statusText.Text =
                                "Signed in, but no username yet. Pick one in Settings > Account & community, then re-open this dialog.";
                            return;
                        }
                        // Flip into signed-in state without re-opening
                        // the window. Update the readout, enable the
                        // form, drop the Sign in button. Re-apply the
                        // empty-preset gate so a fresh sign-in on a
                        // car/game preset with no sections doesn't
                        // suddenly enable Upload past that check.
                        descInput.IsEnabled = true;
                        uploadBtn.IsEnabled = hasSections;
                        allowInPacksCheck.IsEnabled = true;
                        if (universalCheck != null) universalCheck.IsEnabled = true;
                        statusText.Foreground = MutedFg;
                        statusText.Text = "Uploads are credited to your username.";
                        btnRow.Children.Remove(signInBtn);
                        // Rebuild the Sharing-as line in place. Cheap
                        // since the parent StackPanel preserves order.
                        for (int i = 0; i < root.Children.Count; i++)
                        {
                            if (root.Children[i] is Grid g
                                && g.Children.Count >= 1
                                && g.Children[0] is TextBlock label
                                && label.Text == "Sharing as")
                            {
                                root.Children.RemoveAt(i);
                                root.Children.Insert(i, MakeFactLine(
                                    "Sharing as",
                                    _plugin?.Settings?.SharingAuthor ?? "(your username)"));
                                break;
                            }
                        }
                    }
                };
            }
            uploadBtn.Click += async (s, e) =>
            {
                uploadBtn.IsEnabled = false;
                cancelBtn.IsEnabled = false;
                statusText.Foreground = MutedFg;
                statusText.Text = "Uploading...";

                string desc = (descInput.Text ?? "").Trim();
                // Author is server-authoritative now: profile.username if
                // signed in, null otherwise. We pass null and the server
                // stamps the right value. Routing branches on kind so
                // car presets hit upload_preset and game presets hit
                // upload_game_preset (separate table + RPC).
                bool allowPacks = allowInPacksCheck?.IsChecked == true;
                string newId = null;
                try
                {
                    if (isEngine)
                        newId = await _plugin.UploadCustomEngineToCommunityAsync(
                            _presetName, _body, desc, allowInPacks: allowPacks);
                    else if (isGame)
                    {
                        // Empty array = Universal; otherwise collect the
                        // checked-and-sanitized list from the picker.
                        string[] tg = CollectTargetGames(universalCheck, gamesList);
                        newId = await _plugin.UploadGamePresetToCommunityAsync(
                            _presetName, _game, _body, desc, _effectTags,
                            allowInPacks: allowPacks, targetGames: tg);
                    }
                    else
                        newId = await _plugin.UploadCarPresetToCommunityAsync(
                            _presetName, _game, _carId, _body,
                            null, desc, _effectTags,
                            allowInPacks: allowPacks);
                }
                catch (Exception ex)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Upload exception: " + ex.Message;
                    uploadBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }

                if (string.IsNullOrEmpty(newId))
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = _plugin.DescribeLastUploadError();
                    uploadBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }
                UploadedPresetId = newId;
                statusText.Foreground = OkFg;
                statusText.Text = "Uploaded. Thanks for contributing.";
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(uploadBtn);
            root.Children.Add(btnRow);
        }

        // Build a CheckBox row for one game entry in the target-games picker.
        private static CheckBox MakeGameItem(string gameName, bool preChecked)
        {
            return new CheckBox
            {
                Content    = gameName,
                IsChecked  = preChecked,
                Foreground = TextFg,
                FontSize   = 12,
                Margin     = new Thickness(2, 1, 2, 1),
            };
        }

        // Universal ON => empty array (universal). OFF => trimmed, deduped,
        // capped list. Server re-sanitizes; the cap mirrors server hygiene.
        private static string[] CollectTargetGames(CheckBox universalCheck, ListBox gamesList)
        {
            if (universalCheck == null || gamesList == null) return new string[0];
            if (universalCheck.IsChecked == true) return new string[0];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var picked = new List<string>();
            foreach (CheckBox cb in gamesList.Items)
            {
                if (cb?.IsChecked != true) continue;
                string raw = cb.Content as string;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string val = raw.Trim();
                if (val.Length > 64) val = val.Substring(0, 64);
                if (val.Length == 0) continue;
                if (!seen.Add(val)) continue;
                picked.Add(val);
                if (picked.Count >= 64) break;
            }
            return picked.ToArray();
        }

        private FrameworkElement MakeFactLine(string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblBlock = new TextBlock {
                Text = label, Foreground = MutedFg, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lblBlock, 0);
            grid.Children.Add(lblBlock);
            var valBlock = new TextBlock {
                Text = value ?? "", Foreground = TextFg, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(valBlock, 1);
            grid.Children.Add(valBlock);
            return grid;
        }
    }
}
