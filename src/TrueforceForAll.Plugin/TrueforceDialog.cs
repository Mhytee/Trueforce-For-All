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

    /// <summary>Result of the 3-button <see cref="TrueforceDialog.ShowChoice"/>:
    /// the user picked the primary action, the secondary action, or cancelled
    /// (incl. [X] / Esc).</summary>
    internal enum DialogChoice { Cancel, Primary, Secondary }

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
            string okLabel = null, string cancelLabel = null,
            // Opt-in: paint the affirmative button with the app's gold accent
            // instead of the flat gray. Off by default so other callers are
            // unaffected; ignored for Destructive (that stays red).
            bool goldOk = false)
        {
            var dlg = new TrueforceDialog(title, body, kind, okLabel, cancelLabel, goldOk);
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

        private DialogChoice _choice = DialogChoice.Cancel;

        /// <summary>Show a modal with three buttons: a primary affirmative, a
        /// secondary affirmative, and Cancel. For "Submit / Always submit /
        /// Not now" style prompts. Returns which the user picked; [X]/Esc =
        /// Cancel. Layout left-to-right: Cancel, Secondary, Primary (default).</summary>
        public static DialogChoice ShowChoice(Window owner, string title, string body,
            string primaryLabel, string secondaryLabel, string cancelLabel)
        {
            var dlg = new TrueforceDialog(title, body, primaryLabel, secondaryLabel, cancelLabel);
            if (owner != null)
            {
                dlg.Owner = owner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            dlg.ShowDialog();
            return dlg._choice;
        }

        // Window chrome + header + body shared by both constructors. Returns
        // the (empty) button row already added to the root so each ctor just
        // populates its own buttons.
        private StackPanel BuildChrome(string title, string body, DialogKind kind)
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
            root.Children.Add(btnRow);
            return btnRow;
        }

        // 3-button variant: Cancel (left) | Secondary | Primary (right,
        // default). Records which affirmative was picked in _choice so the
        // caller can distinguish "always" from "just this one".
        private TrueforceDialog(string title, string body,
            string primaryLabel, string secondaryLabel, string cancelLabel)
        {
            var btnRow = BuildChrome(title, body, DialogKind.Confirm);

            var cancel = new Button
            {
                Content = string.IsNullOrEmpty(cancelLabel) ? "Not now" : cancelLabel,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancel.Click += (s, e) => { _choice = DialogChoice.Cancel; DialogResult = false; Close(); };
            btnRow.Children.Add(cancel);

            var secondary = new Button
            {
                Content = secondaryLabel,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
            };
            secondary.Click += (s, e) => { _choice = DialogChoice.Secondary; DialogResult = true; Close(); };
            btnRow.Children.Add(secondary);

            var primary = new Button
            {
                Content = primaryLabel,
                Padding = new Thickness(14, 5, 14, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            primary.Click += (s, e) => { _choice = DialogChoice.Primary; DialogResult = true; Close(); };
            btnRow.Children.Add(primary);
        }

        private TrueforceDialog(string title, string body, DialogKind kind,
            string okLabel, string cancelLabel, bool goldOk = false)
        {
            var btnRow = BuildChrome(title, body, kind);

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
                    // Destructive: make Cancel the default so Enter takes the safe
                    // path and the red affirmative needs a deliberate click.
                    IsDefault = isDestructive,
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
                IsDefault  = !isDestructive,   // destructive: Cancel is the default instead
                IsCancel   = !isConfirm,       // info dialogs: OK doubles as Esc/close
            };
            // Gold accent when the caller opts in (non-destructive only). Uses
            // the shared modal theme so hover/press feedback matches the rest.
            if (goldOk && !isDestructive)
                ModalButtonTheme.Primary(ok);
            ok.Click += (s, e) => { DialogResult = true; Close(); };
            btnRow.Children.Add(ok);
        }
    }
}
