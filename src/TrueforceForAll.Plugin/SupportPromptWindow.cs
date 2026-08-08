// The periodic "support us" modal, shown on the plugin page to people who have
// never backed the project. Paced by TrueforcePlugin.ShouldShowSupportPrompt off
// banked seat time, so it only ever reaches someone the plugin has worked for.
//
// It withholds nothing: every feature stays available whether they support or
// not. What supporting buys is that this stops, permanently, on the first
// confirmed contribution (TrueforceSettings.HasEverSupported, latched from
// entitlements.supporter_since, which survives a lapse).
//
// Built in code (not XAML) to match the existing plugin modal convention.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class SupportPromptWindow : Window
    {
        // Palette matches the other plugin modals (CarFactsShareWindow et al).
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush AccentFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        // Text colour for the gold support button (dark, for contrast on gold).
        private static readonly Brush ButtonDarkFg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        // Hairline rule under the centred header (same tone as other modal borders).
        private static readonly Brush DividerBg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

        public const string PatreonUrl = "https://www.patreon.com/Mhytee";

        // Deliberately short. The prompt reaches people who already use the plugin,
        // so this is there to total up what they are getting rather than to explain
        // it, with just enough breadth that a Trueforce-only user learns there is a
        // phone dash. The last bullet carries what the longer list used to spell out.
        private static readonly string[] Bullets =
        {
            "Trueforce haptics in games with no native support",
            "Improved force feedback in a growing list of games",
            "Rev lights and your wheel's built-in OLED screen in games that never had them (select titles for now)",
            "A dash for your phone or tablet",
            "Tunable effects, community car data, and more",
        };

        /// <summary>True when the user clicked through to Patreon. DialogResult is
        /// true in that case and false on dismiss; the caller feeds it to
        /// NoteSupportPromptShown so repeated no's stretch the next ask.</summary>
        public bool WentToPatreon { get; private set; }

        /// <param name="bankedHours">Whole hours of banked seat time, from
        /// ActiveStreamingSeconds. Omitted from the copy when under 1.</param>
        public SupportPromptWindow(int bankedHours)
        {
            Title         = "Trueforce For All";
            Width         = 520;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Trueforce For All", Foreground = HeaderFg,
                FontWeight = FontWeights.SemiBold, FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            });

            root.Children.Add(new TextBlock {
                Text = "TF4ALL adds what games leave out:",
                Foreground = TextFg, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 9),
                TextWrapping = TextWrapping.Wrap,
            });

            // Divider: closes the centred header block off from the left-aligned
            // bullets below it.
            root.Children.Add(new Border {
                Height = 1, Background = DividerBg,
                Margin = new Thickness(0, 0, 0, 11),
            });

            foreach (var b in Bullets)
            {
                var row = new StackPanel {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(2, 0, 0, 5),
                };
                row.Children.Add(new TextBlock {
                    Text = "•", Foreground = AccentFg, FontSize = 12,
                    Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Top,
                });
                row.Children.Add(new TextBlock {
                    Text = b, Foreground = TextFg, FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 452,
                });
                root.Children.Add(row);
            }

            // Closes the bullets off from the earned-value line and the ask below.
            // Unconditional: with no hours line it still separates the list from
            // the money talk.
            root.Children.Add(new Border {
                Height = 1, Background = DividerBg,
                Margin = new Thickness(0, 12, 0, 11),
            });

            // Earned-value line. Skipped under an hour so it can never read as
            // "you have driven 0 hours with it". Centred, so it reads as its own
            // beat between the list and the ask rather than as a body paragraph.
            if (bankedHours >= 1)
            {
                root.Children.Add(new TextBlock {
                    Foreground = TextFg, FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Inlines = {
                        new System.Windows.Documents.Run("You have driven "),
                        new System.Windows.Documents.Bold(
                            new System.Windows.Documents.Run(
                                bankedHours + (bankedHours == 1 ? " hour" : " hours"))) {
                            Foreground = AccentFg },
                        new System.Windows.Documents.Run(" with it."),
                    },
                });
            }

            root.Children.Add(new TextBlock {
                Text = "Supporting us on Patreon helps us keep the plugin free for everybody "
                     + "and continue our work on the project. It costs real money to run: a "
                     + "server for the community features, games bought purely to test against, "
                     + "and a lot of hours.",
                Foreground = TextFg, FontSize = 12,
                Margin = new Thickness(0, 12, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            });

            // "Forever" is literally true: HasEverSupported latches off
            // entitlements.supporter_since, which is stamped once and never
            // cleared, so a lapse never brings this back.
            root.Children.Add(new TextBlock {
                Text = "Memberships start at $1, and supporting once hides this message forever.",
                Foreground = TextFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            });

            // Escape hatch for people who already gave. Supporter status is per
            // account, so a supporter who never signed in reads as a non-supporter
            // and would otherwise be stuck being asked forever.
            root.Children.Add(new TextBlock {
                Text = "Already supporting? Sign in on the Account tab, then use Link Patreon. "
                     + "If you donated another way, message me on Discord.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap,
            });

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var laterBtn = new Button {
                Content = "Not now",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
                IsCancel = true,   // Escape dismisses, as in every other plugin modal
            };
            laterBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(laterBtn);

            // Gold, and the only coloured control in the modal, so the ask is the
            // one thing the eye lands on. Dark text: gold needs the contrast.
            var supportBtn = new Button {
                Content = "Support on Patreon",
                Padding = new Thickness(14, 5, 14, 5),
                Foreground = ButtonDarkFg, Background = AccentFg,
                BorderBrush = AccentFg, FontWeight = FontWeights.SemiBold,
                IsDefault = true,
            };
            supportBtn.Click += (s, e) =>
            {
                WentToPatreon = true;
                try { System.Diagnostics.Process.Start(PatreonUrl); } catch { }
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(supportBtn);

            root.Children.Add(btnRow);
        }
    }
}
