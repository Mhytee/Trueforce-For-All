// One-shot welcome modal explaining the community / networked features
// that landed in this build. Fires on first plugin load AND on first
// load after upgrading from a build that pre-dated the community DB.
// Persistence latch: Settings.HasSeenNetworkedWelcome flips true on
// dismiss; never reopens unless reset.
//
// "Sign in now" routes through the standard sign-in flow.
// "Maybe later" just dismisses; the user can still browse + upload
// anonymously, just not edit / delete their own uploads.

using System.Windows;
using System.Windows.Controls;
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

        public WelcomeWindow()
        {
            Title         = "Welcome to networked Trueforce";
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
                Text = "Trueforce now talks to the community",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 17,
                Margin = new Thickness(0, 0, 0, 6),
            });
            root.Children.Add(new TextBlock {
                Text = "This update adds shared tunes, crowd-sourced car data, and per-car corrections. Sign in to get the most from it - or stay anonymous and use it for free.",
                Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 18),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(MakeBullet(
                "Crowd-sourced car data",
                "Engine layouts, redlines, and car names other drivers have confirmed for the cars you load. Right values appear automatically; no setup."));
            root.Children.Add(MakeBullet(
                "Community presets",
                "Browse and download tunes other drivers have shared for the car you're driving. Take just the sections you want."));
            root.Children.Add(MakeBullet(
                "Your contributions count",
                "When you correct a redline or share a preset, your contribution flows to everyone else loading that car. Sign in to put your username on what you share and keep your uploads yours."));
            root.Children.Add(MakeBullet(
                "Privacy by default",
                "Anonymous if you stay signed out. Sign in with email - no password to remember, no signup form, just a one-time code."));

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
            };
            var laterBtn = new Button {
                Content = "Maybe later", Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            laterBtn.Click += (s, e) => { DialogResult = true; Close(); };
            btnRow.Children.Add(laterBtn);

            var signInBtn = new Button {
                Content = "Sign in now", Padding = new Thickness(14, 6, 14, 6),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
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
