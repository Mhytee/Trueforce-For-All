// Restore-from-backup options dialog. Two independent choices: what to do with
// the preset library (replace vs merge) and whether to take the backup's
// settings. Replace + Apply is the old "Replace everything" behavior. Merge runs
// the conflict resolver (TrueforceDialog.ShowConfirmWithCheckbox) in Restore_Click.
// Code-built (no XAML) to match WelcomeWindow; dark theme + gold primary button.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class RestoreOptionsWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));

        private readonly RadioButton _libReplace;
        private readonly RadioButton _libMerge;
        private readonly RadioButton _setApply;
        private readonly RadioButton _setKeep;

        /// <summary>False = replace the library with the backup's; true = merge.</summary>
        public bool MergeLibrary => _libMerge.IsChecked == true;
        /// <summary>True = apply the backup's settings; false = keep the current ones.</summary>
        public bool ApplyBackupSettings => _setApply.IsChecked == true;

        public RestoreOptionsWindow()
        {
            Title         = "Restore from backup";
            Width         = 500;
            SizeToContent = SizeToContent.Height;
            Background     = WindowBg;
            Foreground     = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Choose how to bring in this backup. Either way, your current library is moved aside to a recoverable folder first, so nothing is lost.",
                Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
            });

            root.Children.Add(MakeHeader("Presets and car tunings"));
            _libReplace = MakeRadio("lib",
                "Replace mine with the backup's",
                "Your current presets and car tunings are moved aside; the backup's become your library.", true);
            _libMerge = MakeRadio("lib",
                "Merge the backup's into mine",
                "Add the backup's presets to your library. If a name clashes, you'll choose which copy to keep.", false);
            root.Children.Add(_libReplace);
            root.Children.Add(_libMerge);

            root.Children.Add(MakeHeader("Settings (gains, effect tuning, force feedback)"));
            _setApply = MakeRadio("set", "Apply the backup's settings",
                "Take the master gain, effect tuning, and force-feedback settings from the backup.", true);
            _setKeep = MakeRadio("set", "Keep my current settings",
                "Bring in only the presets above; leave your settings as they are.", false);
            root.Children.Add(_setApply);
            root.Children.Add(_setKeep);

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
            };
            var cancel = new Button {
                Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0),
                Foreground = TextFg, IsCancel = true,
                Style = MakeFilledButtonStyle(TextFg, Color.FromRgb(0x33, 0x33, 0x33), Color.FromRgb(0x40, 0x40, 0x40), Color.FromRgb(0x2B, 0x2B, 0x2B)),
            };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancel);

            var cont = new Button {
                Content = "Continue", Padding = new Thickness(16, 6, 16, 6),
                Foreground = WindowBg, FontWeight = FontWeights.SemiBold, IsDefault = true,
                Style = MakeFilledButtonStyle(WindowBg, Color.FromRgb(0xE5, 0xC0, 0x4A), Color.FromRgb(0xF2, 0xD3, 0x71), Color.FromRgb(0xCF, 0xA9, 0x3A)),
            };
            cont.Click += (s, e) => { DialogResult = true; Close(); };
            btnRow.Children.Add(cont);
            root.Children.Add(btnRow);
        }

        private TextBlock MakeHeader(string text) => new TextBlock {
            Text = text, Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 13,
            Margin = new Thickness(0, 6, 0, 6),
        };

        private RadioButton MakeRadio(string group, string title, string body, bool isChecked)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            stack.Children.Add(new TextBlock { Text = title, Foreground = TextFg, FontSize = 13 });
            stack.Children.Add(new TextBlock {
                Text = body, Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
            return new RadioButton {
                GroupName = group, IsChecked = isChecked, Foreground = TextFg,
                Margin = new Thickness(0, 0, 0, 10), Content = stack,
            };
        }

        // Filled button with an explicit hover/pressed template so WPF's default
        // chrome doesn't paint gray over the gold background on mouse-over.
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
    }
}
