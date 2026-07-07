// Owner-editor modal for a community preset. Lets you change name +
// description, and optionally replace the body with the JSON of one of
// your local car presets (so an update to your tune travels without
// losing votes + downloads on the existing row).
//
// Body replacement is opt-in: if you don't pick anything from the
// "Replace body with..." dropdown, only metadata is sent.
//
// Game-preset variant adds a target-games picker (Universal vs explicit
// list); the picker state is always emitted on save so the user's
// chosen state is preserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal sealed class EditCommunityPresetWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        // Action-button palette (matches the rest of the plugin): Save = the
        // amber primary (SharePrimaryButton), Cancel = destructive red
        // (TrueforceDialog's DestructiveBg - backing out discards unapplied
        // edits), Add = green (additive action; PillGreen family).
        private static readonly Brush PrimaryBg   = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush PrimaryFg   = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        private static readonly Brush CancelBg    = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E));
        private static readonly Brush CancelFg    = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xE0));
        private static readonly Brush AddBg       = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0x40));
        private static readonly Brush AddFg       = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));

        // Same well-known SimHub games list as PresetShareWindow uses.
        // Strings match what SimHub emits as ActiveGame. Native-Trueforce
        // titles are excluded since the plugin auto-yields for them; the
        // free-text Add input still lets users name anything off-list.
        private static readonly string[] WellKnownGames = new[]
        {
            "AssettoCorsa",
            "FH5",
            "FH6",
            "FM7",
            "PCars2",
            "Wreckfest2",
        };

        public string  NewName        { get; private set; }
        public string  NewDescription { get; private set; }
        public JObject NewBody        { get; private set; }   // null = leave body alone
        public List<string> NewEffectTags { get; private set; }   // null = leave alone
        // null = no change (leave server's current value alone); bool
        // = flip the row's allow_in_packs. update_preset honors this
        // semantics on the server side.
        public bool?   NewAllowInPacks { get; private set; }
        // Game-preset only. null = no change; empty array = Universal;
        // populated = tuned-for list. Game-preset ctor always sets this
        // on save to the picker's current state per the spec.
        public string[] NewTargetGames { get; private set; }

        public EditCommunityPresetWindow(
            string presetName, string presetDescription, string carId,
            List<CarPresetEntry> userPresetsForCar,
            System.Func<CarOverride, JObject> bodyBuilder,
            System.Func<CarOverride, List<string>> tagsBuilder,
            bool currentAllowInPacks = false)
        {
            Title         = "Edit your community preset";
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
                Text = "Edit your community preset",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "Update the name or description, and optionally replace the body with one of your local presets. Existing votes + downloads stay on the preset.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(new TextBlock { Text = "Name", Foreground = MutedFg, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            var nameInput = new TextBox {
                Text = presetName ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4), FontSize = 13, MaxLength = 96,
                Margin = new Thickness(0, 0, 0, 10),
            };
            // Grab focus + select-all on open so the user can start
            // editing immediately, matching the pattern in
            // PickUsernameWindow / CarNameInputWindow.
            nameInput.Loaded += (s, e) => { nameInput.Focus(); nameInput.SelectAll(); };
            root.Children.Add(nameInput);

            root.Children.Add(new TextBlock { Text = "Description", Foreground = MutedFg, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            var descInput = new TextBox {
                Text = presetDescription ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4), FontSize = 12, MaxLength = 1024,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(descInput);

            root.Children.Add(new TextBlock {
                Text = "Replace body with…", Foreground = MutedFg, FontSize = 11, Margin = new Thickness(0, 0, 0, 2),
            });
            var combo = new ComboBox {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Height = 26, Margin = new Thickness(0, 0, 0, 4),
            };
            combo.Items.Add(new ComboBoxItem { Content = "(leave body alone)", Tag = null, Foreground = TextFg });
            if (userPresetsForCar != null)
                foreach (var p in userPresetsForCar.OrderBy(p => p.PresetName))
                    combo.Items.Add(new ComboBoxItem {
                        Content = p.PresetName + "   (" + (p.GameName ?? "") + ")",
                        Tag = p, Foreground = TextFg,
                    });
            combo.SelectedIndex = 0;
            root.Children.Add(combo);

            var helpText = new TextBlock {
                Text = (userPresetsForCar?.Count ?? 0) == 0
                    ? "(You don't have local presets for car '" + carId + "' yet. Save one first to enable body replacement.)"
                    : "Body comes from the preset you pick. Custom-engine defs travel along.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(helpText);

            // Pack-inclusion permission. Initial state mirrors the
            // server's current setting; only sent on save when the
            // user actually toggled it (so unchecked-but-already-false
            // doesn't burn a content_version bump).
            var allowInPacksCheck = new CheckBox
            {
                Content = "Allow others to include this in their packs",
                Foreground = TextFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                IsChecked = currentAllowInPacks,
            };
            root.Children.Add(allowInPacksCheck);

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = CancelFg, Background = CancelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var saveBtn = new Button {
                Content = "Save", Padding = new Thickness(12, 5, 12, 5),
                Foreground = PrimaryFg, Background = PrimaryBg, IsDefault = true,
                FontWeight = FontWeights.Bold,
            };
            saveBtn.Click += (s, e) =>
            {
                string n = (nameInput.Text ?? "").Trim();
                if (n.Length < 2 || n.Length > 96)
                {
                    TrueforceDialog.Show(this, "Edit preset", "Name must be 2-96 characters.",
                        DialogKind.Warning);
                    return;
                }
                NewName = n;
                NewDescription = (descInput.Text ?? "").Trim();
                if (combo.SelectedItem is ComboBoxItem ci && ci.Tag is CarPresetEntry entry
                    && entry.Override != null)
                {
                    NewBody = bodyBuilder?.Invoke(entry.Override);
                    NewEffectTags = tagsBuilder?.Invoke(entry.Override);
                }
                // Only emit NewAllowInPacks when the user actually
                // changed the checkbox; matches the update_preset RPC
                // "null = no change" semantics so we don't bump
                // content_version on a no-op edit.
                bool nowChecked = allowInPacksCheck.IsChecked == true;
                if (nowChecked != currentAllowInPacks)
                    NewAllowInPacks = nowChecked;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);
        }

        /// <summary>Game-preset edit mode. No body-replace picker (game
        /// presets are whole-game snapshots, not per-car overrides);
        /// adds a target-games picker pre-populated from the row.</summary>
        public static EditCommunityPresetWindow ForGamePreset(
            TrueforcePlugin plugin,
            string presetName, string presetDescription,
            string[] currentTargetGames, string activeGameSeed,
            bool currentAllowInPacks = false)
        {
            return new EditCommunityPresetWindow(plugin, presetName, presetDescription,
                currentTargetGames ?? new string[0], activeGameSeed,
                currentAllowInPacks);
        }

        private EditCommunityPresetWindow(
            TrueforcePlugin plugin,
            string presetName, string presetDescription,
            string[] currentTargetGames, string activeGameSeed,
            bool currentAllowInPacks)
        {
            Title         = "Edit your community game preset";
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
                Text = "Edit your community game preset",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "Update the name, description, or which games this preset is tuned for. Existing votes and downloads stay on the preset.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(new TextBlock { Text = "Name", Foreground = MutedFg, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            var nameInput = new TextBox {
                Text = presetName ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4), FontSize = 13, MaxLength = 96,
                Margin = new Thickness(0, 0, 0, 10),
            };
            nameInput.Loaded += (s, e) => { nameInput.Focus(); nameInput.SelectAll(); };
            root.Children.Add(nameInput);

            root.Children.Add(new TextBlock { Text = "Description", Foreground = MutedFg, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            var descInput = new TextBox {
                Text = presetDescription ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4), FontSize = 12, MaxLength = 1024,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(descInput);

            // Pack-inclusion permission (same semantics as the car ctor).
            var allowInPacksCheck = new CheckBox
            {
                Content = "Allow others to include this in their packs",
                Foreground = TextFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                IsChecked = currentAllowInPacks,
            };
            root.Children.Add(allowInPacksCheck);

            // Target-games picker. Universal ON when the row currently
            // has no targets; otherwise OFF with the existing entries
            // pre-checked. Seed merges current + active + well-known
            // (de-duped) so the user can add/remove without retyping.
            root.Children.Add(new TextBlock
            {
                Text = "Which games is this tuned for?",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4),
            });
            bool startsUniversal = currentTargetGames == null || currentTargetGames.Length == 0;
            var universalCheck = new CheckBox
            {
                Content = "Universal (any game)",
                Foreground = TextFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6),
                IsChecked = startsUniversal,
            };
            root.Children.Add(universalCheck);

            var pickerStack = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 12),
                Visibility = startsUniversal ? Visibility.Collapsed : Visibility.Visible,
            };
            var gamesList = new ListBox
            {
                Foreground = TextFg, Background = InputBg,
                BorderBrush = BorderFg,
                Height = 140,
                Margin = new Thickness(0, 0, 0, 6),
                SelectionMode = SelectionMode.Multiple,
            };
            // Candidate set sourced from SimHub-known games on this
            // install + the WellKnownGames fallback. Native-Trueforce
            // titles are partitioned (not removed) so the toggle below
            // can hide them without losing user selections. Free-text
            // Add is the escape hatch for anything off-list.
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (plugin?.Settings?.GameDefaults != null)
                foreach (var k in plugin.Settings.GameDefaults.Keys)
                    if (!string.IsNullOrWhiteSpace(k)) candidates.Add(k.Trim());
            if (plugin?.Settings?.GameEnabled != null)
                foreach (var k in plugin.Settings.GameEnabled.Keys)
                    if (!string.IsNullOrWhiteSpace(k)) candidates.Add(k.Trim());
            if (!string.IsNullOrWhiteSpace(activeGameSeed))
                candidates.Add(activeGameSeed.Trim());
            foreach (var g in WellKnownGames)
                if (!string.IsNullOrWhiteSpace(g)) candidates.Add(g);

            // Per-game check-state cache that survives toggle rebuilds.
            // Seeded with currentTargetGames (the row's existing intent)
            // so they always show pre-checked, even if the native-TF
            // toggle is off and they happen to be native.
            var checkedState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (currentTargetGames != null)
                foreach (var g in currentTargetGames)
                    if (!string.IsNullOrWhiteSpace(g))
                        checkedState[g.Trim()] = true;
            if (startsUniversal && !string.IsNullOrWhiteSpace(activeGameSeed))
                checkedState[activeGameSeed.Trim()] = true;

            var currentTargets = new HashSet<string>(
                (currentTargetGames ?? new string[0]).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            Action<bool> rebuildGames = null;
            rebuildGames = showNative =>
            {
                foreach (CheckBox cb in gamesList.Items)
                    if (cb?.Content is string s2)
                        checkedState[s2] = cb.IsChecked == true;
                gamesList.Items.Clear();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Existing target games first (always shown - user's
                // intent wins over the native-TF filter).
                foreach (var g in currentTargets.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
                {
                    if (!seen.Add(g)) continue;
                    bool c = !checkedState.TryGetValue(g, out var v) || v;
                    gamesList.Items.Add(MakeGameItem(g, c));
                }
                // Non-native candidates next, sorted.
                foreach (var g in candidates
                            .Where(g => !seen.Contains(g) && !TrueforcePlugin.IsNativeTrueforceGame(g))
                            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
                {
                    if (!seen.Add(g)) continue;
                    bool c = checkedState.TryGetValue(g, out var v) && v;
                    gamesList.Items.Add(MakeGameItem(g, c));
                }
                // Native-TF games last, only when the toggle is on.
                if (showNative)
                {
                    foreach (var g in candidates
                                .Where(g => !seen.Contains(g) && TrueforcePlugin.IsNativeTrueforceGame(g))
                                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!seen.Add(g)) continue;
                        bool c = checkedState.TryGetValue(g, out var v) && v;
                        gamesList.Items.Add(MakeGameItem(g, c));
                    }
                }
            };
            rebuildGames(false);
            pickerStack.Children.Add(gamesList);

            var showNativeCheck = new CheckBox
            {
                Content = "Show games with native Trueforce (advanced)",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6),
                IsChecked = false,
                ToolTip = "Trueforce ships in some games directly (iRacing, ACC, F1 22+, etc) and the plugin yields to those. Check this if your preset targets a co-operative setup like MAIRA in iRacing.",
            };
            showNativeCheck.Checked   += (s, e) => rebuildGames(true);
            showNativeCheck.Unchecked += (s, e) => rebuildGames(false);
            pickerStack.Children.Add(showNativeCheck);

            var addRow = new StackPanel { Orientation = Orientation.Horizontal };
            var addGameInput = new TextBox
            {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4), FontSize = 12, MaxLength = 64,
                Width = 260, Margin = new Thickness(0, 0, 6, 0),
            };
            var addGameBtn = new Button
            {
                Content = "Add", Padding = new Thickness(10, 3, 10, 3),
                Foreground = AddFg, Background = AddBg,
            };
            addRow.Children.Add(addGameInput);
            addRow.Children.Add(addGameBtn);
            pickerStack.Children.Add(addRow);

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

            universalCheck.Checked   += (s, e) => { pickerStack.Visibility = Visibility.Collapsed; };
            universalCheck.Unchecked += (s, e) => { pickerStack.Visibility = Visibility.Visible; };

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = CancelFg, Background = CancelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var saveBtn = new Button {
                Content = "Save", Padding = new Thickness(12, 5, 12, 5),
                Foreground = PrimaryFg, Background = PrimaryBg, IsDefault = true,
                FontWeight = FontWeights.Bold,
            };
            saveBtn.Click += (s, e) =>
            {
                string n = (nameInput.Text ?? "").Trim();
                if (n.Length < 2 || n.Length > 96)
                {
                    TrueforceDialog.Show(this, "Edit preset", "Name must be 2-96 characters.",
                        DialogKind.Warning);
                    return;
                }
                NewName = n;
                NewDescription = (descInput.Text ?? "").Trim();
                bool nowChecked = allowInPacksCheck.IsChecked == true;
                if (nowChecked != currentAllowInPacks)
                    NewAllowInPacks = nowChecked;
                // Always emit picker state per spec: empty array when
                // Universal, populated list otherwise. Server treats empty
                // as "reset to universal" and the list as a replace.
                NewTargetGames = CollectTargetGames(universalCheck, gamesList);
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);
        }

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

        // Universal ON => empty array; OFF => trimmed/deduped/capped list.
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
    }
}
