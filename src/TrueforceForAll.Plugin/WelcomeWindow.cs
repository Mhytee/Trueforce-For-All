// One-shot welcome modal explaining the community / networked features.
// Fires on first plugin load AND once more for existing installs when the
// community default flipped to on (CommunityDefaultOnRepitchedV1).
// Persistence latch: Settings.HasSeenNetworkedWelcome flips true on
// dismiss; never reopens unless reset.
//
// This is a PROCEED, not a consent gate: community features and car-data
// sharing are the default posture, so the callers enable them on ANY
// dismissal (buttons, Esc, or the X). The modal's job is disclosure (what
// is shared, where the off switch is) plus an optional account:
// "Sign in / create account" additionally runs the sign-in flow;
// "Continue without an account" just closes.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class WelcomeWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BulletFg = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));

        public bool SignInRequested { get; private set; }

        // Process-wide "shown this session" guard. Two independent paths can
        // trigger this modal (the Settings panel's MaybeShowNetworkedWelcome
        // and the plugin's init/telemetry MaybeShowNetworkedWelcomeFromInit),
        // and the Settings path itself dispatches from both Loaded and the
        // car-change tick. Their latches are separate and all gate only on
        // HasSeenNetworkedWelcome, which isn't committed until the flow
        // completes - so while one modal blocks, a queued dispatch would open
        // a second on top of it. "Sign in now" closes the top one, leaving the
        // other behind for the user to dismiss by hand. Each path sets this at
        // its commit point (after every show-gate passes) and returns early if
        // it's already set, so only one welcome modal exists per session.
        // Cross-session re-show stays governed by WelcomeNextShowAt; reset this
        // on identity change and on the dev WELCOME reset.
        internal static bool ShownThisSession;

        public WelcomeWindow()
        {
            Title         = "Welcome to networked Trueforce For All";
            Width         = 500;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Introducing: community presets and car data",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 17,
                Margin = new Thickness(0, 0, 0, 6),
            });
            root.Children.Add(new TextBlock {
                Text = "This update adds a community preset browser, crowd-sourced car data, and per-car corrections. "
                     + "Community features are on: car data you tune (redlines, engine types, car names) is shared "
                     + "anonymously and improves everyone's defaults. You can turn any of this off in Settings. "
                     + "An account is only needed for the preset browser (free email sign-in).",
                Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 18),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(MakePrereleaseNotice());

            root.Children.Add(MakeBullet(
                "Crowd-sourced car data",
                "Effects like the engine pulse and rev limiter feel best when they know a car's engine and redline. Games don't always report that data in telemetry, and a few don't even give the car a real name, just a code like \"car_123\". Now, drivers can fill in the gaps, building a shared pool everyone benefits from. Saved per tune, so a swapped engine keeps its own redline."));
            root.Children.Add(MakeBullet(
                "Community presets",
                "Browse and download presets other drivers have shared, for the car you're driving or the game you're playing. Vote on the ones you've tried so the best rise to the top."));
            root.Children.Add(MakeBullet(
                "It gets better as it grows",
                "The more drivers take part, the more often the right setup is already waiting when you load in. What you add helps the next person the same way."));
            root.Children.Add(MakeBullet(
                "Earn achievements",
                "Earn achievements for things like sharing presets, confirming car data, and supporting the project. Link your Discord account to get a matching role in the community server."));
            root.Children.Add(MakeBullet(
                "Privacy by default",
                "Car data you contribute is anonymous. Sign-in (only needed for presets) takes just your email and a one-time code. No password, no signup form. The privacy policy spells out what's stored and how to remove it."));

            // Policy link, indented to align with the bullet bodies.
            var policyLine = new TextBlock {
                FontSize = 11, Margin = new Thickness(18, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            var policyLink = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run("Read the privacy policy"))
            { Foreground = HeaderFg };
            policyLink.Click += (s, e) => OpenUrl(SettingsControl.PrivacyPolicyUrl);
            policyLine.Inlines.Add(policyLink);
            root.Children.Add(policyLine);

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
            };
            var continueBtn = new Button {
                Content = "Continue without an account", Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = TextFg, IsCancel = true,
                Style = MakeFilledButtonStyle(
                    TextFg,
                    Color.FromRgb(0x33, 0x33, 0x33),   // normal (matches PanelBg)
                    Color.FromRgb(0x40, 0x40, 0x40),   // hover (lighter)
                    Color.FromRgb(0x2B, 0x2B, 0x2B)),  // pressed (darker)
            };
            continueBtn.Click += (s, e) => { DialogResult = true; Close(); };
            btnRow.Children.Add(continueBtn);

            var signInBtn = new Button {
                Content = "Sign in / create account", Padding = new Thickness(16, 6, 16, 6),
                Foreground = WindowBg,
                FontWeight = FontWeights.SemiBold, IsDefault = true,
                Style = MakeFilledButtonStyle(
                    WindowBg,
                    Color.FromRgb(0xE5, 0xC0, 0x4A),   // normal (matches HeaderFg)
                    Color.FromRgb(0xF2, 0xD3, 0x71),   // hover (lighter gold)
                    Color.FromRgb(0xCF, 0xA9, 0x3A)),  // pressed (deeper gold)
            };
            signInBtn.Click += (s, e) =>
            {
                SignInRequested = true;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(signInBtn);
            root.Children.Add(btnRow);
        }

        // Filled button with an explicit hover/pressed template. Without a
        // custom template WPF's default Button chrome ignores our Background on
        // mouse-over and paints the stock light-gray hover brush, which made the
        // gold "Get started" button turn gray instead of lightening.
        private static Style MakeFilledButtonStyle(Brush fg, Color normal, Color hover, Color pressed)
        {
            var template = new ControlTemplate(typeof(Button));

            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(normal));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            template.VisualTree = border;

            var onHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            onHover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hover), "bd"));
            template.Triggers.Add(onHover);

            var onPress = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            onPress.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(pressed), "bd"));
            template.Triggers.Add(onPress);

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, fg));
            return style;
        }

        // A distinct callout (not one of the green marketing bullets) so the
        // pre-release state of the community backend is unmissable at first
        // contact. The reassurance in the last sentence holds only if the
        // eventual data cleanup stays selective: keep accounts, achievements,
        // and car facts; reset only the shared preset/pack marketplace.
        private FrameworkElement MakePrereleaseNotice()
        {
            var border = new Border {
                Background      = new SolidColorBrush(Color.FromRgb(0x39, 0x33, 0x22)),
                BorderBrush     = HeaderFg,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius    = new CornerRadius(2),
                Padding         = new Thickness(12, 10, 12, 10),
                Margin          = new Thickness(0, 0, 0, 16),
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock {
                Text = "Pre-release test backend",
                Foreground = HeaderFg, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3),
            });
            stack.Children.Add(new TextBlock {
                Text = "The community backend is still in testing. Shared presets and packs here are test data and may be reset before the public launch. Your account, achievements, and any car data you contribute are kept.",
                Foreground = TextFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
            border.Child = stack;
            return border;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        }

        private FrameworkElement MakeBullet(string title, string body)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new TextBlock {
                Text = "●", Foreground = BulletFg, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 0, 0),
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock {
                Text = title, Foreground = TextFg, FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });
            stack.Children.Add(new TextBlock {
                Text = body, Foreground = MutedFg, FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
            return grid;
        }
    }
}
