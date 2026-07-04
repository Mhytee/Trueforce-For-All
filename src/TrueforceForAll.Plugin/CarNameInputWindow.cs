// Inline modal for naming a car. Fires from the header's "Name this car"
// affordance. The user types a name; on submit, the caller writes it
// locally (via TrueforcePlugin.WriteCarNameFact) and optionally opens the
// share-flow modal to contribute to the community.
//
// Single text field; the styling mirrors CarFactsShareWindow so the two
// modals feel like the same family.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class CarNameInputWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush WarnFg   = new SolidColorBrush(Color.FromRgb(0xE2, 0x6D, 0x5A));

        public string EnteredName { get; private set; }

        // The "2 to 96 characters" hint; recolored to a warning on a rejected
        // submit so Save doesn't look dead when the length is out of range.
        private TextBlock _hint;

        /// <param name="carId">Identifier the plugin sees for this car
        /// (e.g. "Car_2267" or "ks_mazda_rx7"). Shown below the header so
        /// the user knows exactly which car they're naming.</param>
        /// <param name="currentName">Existing name (User or community) to
        /// pre-fill the textbox with. Empty when no name is on file.</param>
        public CarNameInputWindow(string carId, string currentName)
        {
            Title         = "Name this car";
            Width         = 440;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = string.IsNullOrEmpty(currentName) ? "Name this car" : "Rename this car",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = carId, Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            var input = new TextBox {
                Text = currentName ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 14, MaxLength = 96,
            };
            input.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
            root.Children.Add(input);

            _hint = new TextBlock {
                Text = "2 to 96 characters.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 14),
            };
            root.Children.Add(_hint);

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var saveBtn = new Button {
                Content = "Save", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            saveBtn.Click += (s, e) => TryCommit(input);
            btnRow.Children.Add(saveBtn);

            root.Children.Add(btnRow);

            // Enter inside the textbox commits via IsDefault on saveBtn;
            // Escape dismisses the modal via the cancel button's
            // IsCancel = true (mirrors every other modal in the plugin).
            cancelBtn.IsCancel = true;

            // Keyboard shortcut: Ctrl+Enter also saves (some users have
            // muscle memory from other dialogs).
            input.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    TryCommit(input);
                    e.Handled = true;
                }
            };
        }

        private void TryCommit(TextBox input)
        {
            string v = (input.Text ?? "").Trim();
            if (v.Length < 2 || v.Length > 96)
            {
                // Don't fail silently: flag the length hint so the user sees why
                // Save did nothing.
                _hint.Text = "Enter 2 to 96 characters.";
                _hint.Foreground = WarnFg;
                input.Focus();
                return;
            }
            EnteredName = v;
            DialogResult = true;
            Close();
        }
    }
}
