// Replacement for System.Windows.MessageBox across the plugin so confirms /
// info / warning prompts match the dark themed UI instead of rendering as
// stock Windows alerts. Mirrors UpdateVsNewChooserWindow's styling: gold
// header, muted body, panel-grey buttons (red accent for destructive). Kept
// intentionally narrow — just the four buttons code actually needs (OK,
// Yes/No, plus a destructive flavor that paints the affirmative button red).
//
// Usage:
//   bool? r = TrueforceDialog.Show(owner, "Delete preset?", "Are you sure?", DialogKind.Destructive);
//   if (r != true) return;
//
// Returns: true=affirmative (OK/Yes), false=negative (No/Cancel button),
//          null=closed via [X] or Esc (treat as Cancel).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal enum DialogKind
    {
        Info,           // single OK; informational
        Warning,        // single OK; yellow accent header
        Error,          // single OK; red accent header
        Confirm,        // Yes / No; neutral grey buttons
        Destructive,    // Yes / No; affirmative button painted red
    }

    internal sealed class TrueforceDialog : Window
    {
        // Brushes match UpdateVsNewChooserWindow + PresetMetadataDialog so
        // every styled modal in the plugin reads as part of the same family.
        private static readonly Brush WindowBg     = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg       = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg      = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFgGold = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush HeaderFgWarn = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D));
        private static readonly Brush HeaderFgErr  = new SolidColorBrush(Color.FromRgb(0xE0, 0x77, 0x77));
        private static readonly Brush DestructiveBg= new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E));
        private static readonly Brush DestructiveFg= new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xE0));

        /// <summary>Show a modal dialog and block until the user dismisses
        /// it. Returns true=affirmative, false=negative, null=closed.</summary>
        public static bool? Show(Window owner, string title, string body,
            DialogKind kind = DialogKind.Info,
            string okLabel = null, string cancelLabel = null)
        {
            var dlg = new TrueforceDialog(title, body, kind, okLabel, cancelLabel);
            if (owner != null)
            {
                dlg.Owner = owner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            return dlg.ShowDialog();
        }

        private TrueforceDialog(string title, string body, DialogKind kind,
            string okLabel, string cancelLabel)
        {
            Title         = title ?? "Trueforce For All";
            Width         = 460;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            Brush headerBrush;
            switch (kind)
            {
                case DialogKind.Warning: headerBrush = HeaderFgWarn; break;
                case DialogKind.Error:   headerBrush = HeaderFgErr;  break;
                default:                 headerBrush = HeaderFgGold; break;
            }

            root.Children.Add(new TextBlock
            {
                Text = title ?? "Trueforce For All",
                Foreground = headerBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 8),
            });

            root.Children.Add(new TextBlock
            {
                Text = body ?? "",
                Foreground = MutedFg,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap,
            });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            bool isConfirm = kind == DialogKind.Confirm || kind == DialogKind.Destructive;
            bool isDestructive = kind == DialogKind.Destructive;

            // Confirm/Destructive: Cancel on the left, affirmative on the
            // right (where the eye finishes reading). Info/Warning/Error:
            // single OK on the right.
            if (isConfirm)
            {
                var cancel = new Button
                {
                    Content = string.IsNullOrEmpty(cancelLabel) ? "No" : cancelLabel,
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(0, 0, 8, 0),
                    Foreground = TextFg, Background = PanelBg, IsCancel = true,
                };
                cancel.Click += (s, e) => { DialogResult = false; Close(); };
                btnRow.Children.Add(cancel);
            }

            var ok = new Button
            {
                Content = string.IsNullOrEmpty(okLabel)
                    ? (isConfirm ? "Yes" : "OK")
                    : okLabel,
                Padding = new Thickness(14, 5, 14, 5),
                Foreground = isDestructive ? DestructiveFg : TextFg,
                Background = isDestructive ? DestructiveBg : PanelBg,
                IsDefault  = true,
                IsCancel   = !isConfirm,    // info dialogs: OK doubles as Esc/close
            };
            ok.Click += (s, e) => { DialogResult = true; Close(); };
            btnRow.Children.Add(ok);

            root.Children.Add(btnRow);
        }
    }
}
