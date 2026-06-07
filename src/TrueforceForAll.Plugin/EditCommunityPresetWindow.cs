// Owner-editor modal for a community preset. Lets you change name +
// description, and optionally replace the body with the JSON of one of
// your local car presets (so an update to your tune travels without
// losing votes + downloads on the existing row).
//
// Body replacement is opt-in: if you don't pick anything from the
// "Replace body with..." dropdown, only metadata is sent.

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

        public string  NewName        { get; private set; }
        public string  NewDescription { get; private set; }
        public JObject NewBody        { get; private set; }   // null = leave body alone
        public List<string> NewEffectTags { get; private set; }   // null = leave alone
        // null = no change (leave server's current value alone); bool
        // = flip the row's allow_in_packs. update_preset honors this
        // semantics on the server side.
        public bool?   NewAllowInPacks { get; private set; }

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
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var saveBtn = new Button {
                Content = "Save", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            saveBtn.Click += (s, e) =>
            {
                string n = (nameInput.Text ?? "").Trim();
                if (n.Length < 2 || n.Length > 96)
                {
                    MessageBox.Show(this, "Name must be 2-96 characters.",
                        "Edit preset", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    }
}
