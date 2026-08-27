// Flat, dark-theme button appearance for the code-built modal windows
// (PresetShareWindow, PresetPreviewWindow, etc). WPF's default button template
// draws system chrome and a distracting accent highlight when IsDefault=true,
// which ignores our dark palette - that's why primary actions like "Upload" and
// "Download…" rendered as unstyled system buttons. Apply a self-contained flat
// template that respects the theme and keeps IsDefault's Enter-key behaviour.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal static class ModalButtonTheme
    {
        // Primary = the confirm/default action (matches the app's gold accent).
        private static readonly Brush PrimaryBg     = Frozen(0xE5, 0xC0, 0x4A);
        private static readonly Brush PrimaryFg     = Frozen(0x1E, 0x1E, 0x1E);
        private static readonly Brush PrimaryBorder = Frozen(0xC9, 0xA9, 0x3F);
        // Secondary = cancel/close.
        private static readonly Brush SecondaryBg     = Frozen(0x33, 0x33, 0x33);
        private static readonly Brush SecondaryFg     = Frozen(0xE0, 0xE0, 0xE0);
        private static readonly Brush SecondaryBorder = Frozen(0x4A, 0x4A, 0x4A);
        // Destructive = deletes or removes something the user has. Same red the
        // modal already used for its affirmative button, moved here so a button
        // in a panel and the button in the confirm it opens are the same colour
        // by construction rather than by two copies of the same hex.
        private static readonly Brush DestructiveBg     = Frozen(0x8B, 0x2E, 0x2E);
        private static readonly Brush DestructiveFg     = Frozen(0xFF, 0xE0, 0xE0);
        private static readonly Brush DestructiveBorder = Frozen(0xA5, 0x3B, 0x3B);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // Flat template: a rounded Border bound to the button's own
        // Background/BorderBrush/Padding, with hover/press/disabled feedback via
        // opacity (palette-agnostic). Parsed once and shared across buttons.
        private const string TemplateXaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
            " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Border x:Name='bd' CornerRadius='3' Background='{TemplateBinding Background}'" +
            " BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'" +
            " Margin='{TemplateBinding Padding}'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.88'/></Trigger>" +
            "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.74'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='bd' Property='Opacity' Value='0.42'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";

        private static bool _templateTried;
        private static ControlTemplate _template;
        private static ControlTemplate Template
        {
            get
            {
                if (_templateTried) return _template;
                _templateTried = true;
                try { _template = (ControlTemplate)XamlReader.Parse(TemplateXaml); }
                catch { _template = null; }  // fall back to default chrome, never throw in a modal
                return _template;
            }
        }

        /// <summary>Style a confirm/default modal button (Upload, Download, Save…).</summary>
        public static void Primary(Button btn) => Apply(btn, PrimaryBg, PrimaryFg, PrimaryBorder);

        /// <summary>Style a cancel/close modal button.</summary>
        public static void Secondary(Button btn) => Apply(btn, SecondaryBg, SecondaryFg, SecondaryBorder);

        /// <summary>Style a button that removes or deletes something (Remove,
        /// Delete, Uninstall). Reach for this only where the action takes
        /// something away: red everywhere makes red mean nothing.</summary>
        public static void Destructive(Button btn) => Apply(btn, DestructiveBg, DestructiveFg, DestructiveBorder);

        private static void Apply(Button btn, Brush bg, Brush fg, Brush border)
        {
            btn.Background = bg;
            btn.Foreground = fg;
            btn.BorderBrush = border;
            btn.BorderThickness = new Thickness(1);
            if (btn.Padding.Left == 0 && btn.Padding.Right == 0)
                btn.Padding = new Thickness(12, 5, 12, 5);
            var t = Template;
            if (t != null) btn.Template = t;
        }
    }
}
