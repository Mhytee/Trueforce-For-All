// Tiny generic two-input edit modal. One single-line TextBox + one multi-
// line TextBox with custom labels + initial values. Used by the community
// browser's Edit preset flow (name + description) but kept generic in
// case other future edits need the same shape.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class TwoLineEditWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

        public string Line1Result { get; private set; }
        public string Line2Result { get; private set; }

        public TwoLineEditWindow(
            string title, string line1Label, string line1Init,
            string line2Label, string line2Init, int line2Lines = 4)
        {
            Title         = title ?? "Edit";
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
                Text = title ?? "Edit",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 14),
            });

            root.Children.Add(new TextBlock {
                Text = line1Label ?? "", Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var input1 = new TextBox {
                Text = line1Init ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 13, MaxLength = 96,
                Margin = new Thickness(0, 0, 0, 10),
            };
            input1.Loaded += (s, e) => { input1.Focus(); input1.SelectAll(); };
            root.Children.Add(input1);

            root.Children.Add(new TextBlock {
                Text = line2Label ?? "", Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var input2 = new TextBox {
                Text = line2Init ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12, MaxLength = 1024,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 20 * line2Lines,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(input2);

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
                Line1Result = input1.Text;
                Line2Result = input2.Text;
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);
        }
    }
}
