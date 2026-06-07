// Small modal shown when a user re-shares a preset they already uploaded
// to the community and the body has diverged from that last upload. The
// caller decides between PATCH (Update) and POST (Share as new) based on
// which button the user clicks. Cancel skips the upload entirely.
//
// Styling mirrors CarNameInputWindow (dark bg #2A2A2A, gold header,
// muted subtext). Buttons are panel-grey to match every other modal in
// the plugin; the gold "Share" CTA styling stays on the entry points, not
// inside this picker.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class UpdateVsNewChooserWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));

        public bool IsUpdate { get; private set; }

        public UpdateVsNewChooserWindow(string title, string message,
                                        string updateBtnLabel, string newBtnLabel)
        {
            Title         = title ?? "Update or share as new";
            Width         = 460;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = title ?? "Update or share as new",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 8),
            });

            root.Children.Add(new TextBlock {
                Text = message ?? "",
                Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap,
            });

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            // Cancel sits leftmost so the destructive-looking choices stay
            // clustered on the right where the eye lands after reading the
            // message. IsCancel binds Escape; IsDefault on Update binds Enter.
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var newBtn = new Button {
                Content = string.IsNullOrEmpty(newBtnLabel) ? "Share as new" : newBtnLabel,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
            };
            newBtn.Click += (s, e) =>
            {
                IsUpdate = false;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(newBtn);

            var updateBtn = new Button {
                Content = string.IsNullOrEmpty(updateBtnLabel) ? "Update" : updateBtnLabel,
                Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            updateBtn.Click += (s, e) =>
            {
                IsUpdate = true;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(updateBtn);

            root.Children.Add(btnRow);
        }
    }
}
