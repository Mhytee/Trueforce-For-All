// Styled modal fired after a user saves engine settings that diverge from
// what the resolver auto-detected. Asks if their correction should join the
// community database. Single Yes/No - the engine layout combo already
// bundles cyl + config into one assertion. Returns DialogResult=true on the
// affirmative button, false on cancel / window close.
//
// Copy adapts to the current community state so the user knows the actual
// impact of contributing:
//   None              -> "You're the first" (strong agency)
//   Match (confirm)   -> "X drivers agree. Confirm with them"
//   Diff (alternative) -> "Current community pick is X. Yours joins as an
//                          alternative"
// Auto-detected data is good but it needs validation OR correction; this
// framing makes both contribution types feel real.
//
// Built in code (not XAML) to match the existing plugin modal convention.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class CarFactsShareWindow : Window
    {
        // Palette matches the other plugin modals (PackPickerWindow et al).
        private static readonly Brush WindowBg  = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg   = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg    = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg   = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg  = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush ValueBg   = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush BorderFg  = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        // State accent: pioneer (gold), confirm (green-ish), alternative (orange).
        private static readonly Brush PioneerFg     = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush ConfirmFg     = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        private static readonly Brush AlternativeFg = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));

        public enum ShareState { First, Confirming, Alternative }

        /// <param name="carDisplayName">Human-readable car name. Falls back
        /// to carId when null.</param>
        /// <param name="carId">Identifier the plugin sees for this car.</param>
        /// <param name="userLayoutDisplay">"V8 cross-plane" / "Inline 6"
        /// etc. The fact the user is asserting.</param>
        /// <param name="state">Where the community is for this car/fact
        /// today - drives the lead copy.</param>
        /// <param name="consensusLayoutDisplay">Pretty-printed current
        /// consensus layout when state is Confirming / Alternative;
        /// ignored when First.</param>
        /// <param name="supportingSubmissions">Distinct submitter count
        /// backing the current consensus. Drives the "X drivers" line
        /// when non-zero.</param>
        public CarFactsShareWindow(
            string carDisplayName, string carId, string userLayoutDisplay,
            ShareState state, string consensusLayoutDisplay, int supportingSubmissions)
        {
            Title         = "Help the community";
            Width         = 460;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            string nameLine = string.IsNullOrEmpty(carDisplayName)
                ? carId
                : carDisplayName + "   " + carId;

            // State-dependent lead copy. One short line; the value chip
            // below carries the concrete data, so the body just supplies
            // the why.
            string leadHeader;
            Brush  leadColor;
            string bodyText;
            string driverWord = supportingSubmissions == 1 ? "driver" : "drivers";
            switch (state)
            {
                case ShareState.First:
                    leadHeader = "You're the first";
                    leadColor  = PioneerFg;
                    bodyText   = "Yours starts the record for this car.";
                    break;
                case ShareState.Confirming:
                    leadHeader = supportingSubmissions > 0
                                 ? $"Confirm with {supportingSubmissions} {driverWord}"
                                 : "Confirm community data";
                    leadColor  = ConfirmFg;
                    bodyText   = "Yours strengthens the existing record.";
                    break;
                default: // Alternative
                    leadHeader = "Disagree?";
                    leadColor  = AlternativeFg;
                    bodyText   = $"Community currently says \"{consensusLayoutDisplay}\""
                               + (supportingSubmissions > 0 ? $" ({supportingSubmissions} {driverWord})." : ".")
                               + " Yours joins as a counter.";
                    break;
            }

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = leadHeader, Foreground = leadColor,
                FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = nameLine, Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            // Concrete value chip - the user always sees what they're contributing.
            root.Children.Add(new Border {
                Background = ValueBg, BorderBrush = BorderFg,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new StackPanel { Children = {
                    new TextBlock {
                        Text = "You're marking this car as",
                        Foreground = MutedFg, FontSize = 11,
                        Margin = new Thickness(0, 0, 0, 2),
                    },
                    new TextBlock {
                        Text = userLayoutDisplay, Foreground = TextFg,
                        FontWeight = FontWeights.SemiBold, FontSize = 14,
                    },
                }},
            });

            root.Children.Add(new TextBlock {
                Text = bodyText, Foreground = TextFg,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(new TextBlock {
                Text = "Submitted as your signed-in account. Your username isn't shown on submissions.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancelBtn = new Button {
                Content = "Not now",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
                // Escape dismisses the modal - mirrors every other
                // modal in the plugin.
                IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            string affirmText;
            switch (state)
            {
                case ShareState.First:       affirmText = "Share";        break;
                case ShareState.Confirming:  affirmText = "Confirm";      break;
                default:                     affirmText = "Share anyway"; break;
            }
            var shareBtn = new Button {
                Content = affirmText,
                Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            shareBtn.Click += (s, e) => { DialogResult = true; Close(); };
            btnRow.Children.Add(shareBtn);

            root.Children.Add(btnRow);
        }
    }
}
