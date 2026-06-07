// Two-stage email OTP sign-in modal. Mirrors the housinggrade pattern
// (Mhytee/Housing-Report-Card app.js handleSignInSendCode +
// handleSignInVerifyCode + handleSignInResend):
//   Stage A: email field + "Email me a sign-in code"
//   Stage B: 6-digit code field + Sign-in button, with
//     * "Use a different email" to restart
//     * "Send a new code" resend with 60-second cooldown matching
//       Supabase's max_frequency (1 minute) so users don't spam-click
//       and trip the server-side rate limit
//   Error copy is specific per outcome (rate limit / expired / bad code
//   / network / generic) instead of a single "failed" fallback.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    internal sealed class SignInWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush OkFg     = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        private static readonly Brush ErrFg    = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));
        private static readonly Brush InfoFg   = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xD8));

        private const int ResendCooldownSeconds = 60;  // matches Supabase max_frequency

        private readonly TrueforcePlugin _plugin;
        private string _pendingEmail;

        public SignInWindow(TrueforcePlugin plugin)
        {
            _plugin = plugin;

            Title         = "Sign in to manage your shared presets";
            Width         = 440;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            // Start with a value-prop preview so the user understands
            // what sign-in unlocks BEFORE typing an email address.
            // "Use a different email" on the verify step calls
            // ShowEmailStep directly, so re-entry skips this preview.
            ShowPreviewStep();
        }

        // ---- Stage 0: value-prop preview ---------------------------------

        private void ShowPreviewStep()
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Sign in",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "Sign in to share your presets, vote, and manage your uploads. We email a 6-digit code (no password).",
                Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 18),
                TextWrapping = TextWrapping.Wrap,
            });

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

            var continueBtn = new Button {
                Content = "Continue", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            continueBtn.Click += (s, e) => ShowEmailStep();
            btnRow.Children.Add(continueBtn);
            root.Children.Add(btnRow);
        }

        // ---- Stage A: email entry ----------------------------------------

        private void ShowEmailStep(string prefilledEmail = null, string errorMessage = null)
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Sign in",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "We'll email a 6-digit code. No password to remember. Signing in lets you use community features.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(new TextBlock {
                Text = "Email", Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var emailInput = new TextBox {
                Text = prefilledEmail ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 13, MaxLength = 254,
                Margin = new Thickness(0, 0, 0, 12),
            };
            emailInput.Loaded += (s, e) => emailInput.Focus();
            root.Children.Add(emailInput);

            var statusText = new TextBlock {
                Foreground = string.IsNullOrEmpty(errorMessage) ? MutedFg : ErrFg,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
                Text = errorMessage ?? "",
            };
            root.Children.Add(statusText);

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

            var sendBtn = new Button {
                Content = "Email me a sign-in code", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            btnRow.Children.Add(sendBtn);
            root.Children.Add(btnRow);

            sendBtn.Click += async (s, e) =>
            {
                string email = (emailInput.Text ?? "").Trim();
                if (!IsLikelyEmail(email))
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Enter a valid email address.";
                    return;
                }
                sendBtn.IsEnabled = false;
                cancelBtn.IsEnabled = false;
                statusText.Foreground = InfoFg;
                statusText.Text = "Sending a sign-in code to your inbox...";
                AuthCallResult result;
                try { result = await _plugin.AuthSendOtpAsync(email); }
                catch (Exception ex)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Could not send the code: " + ex.Message;
                    sendBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }
                cancelBtn.IsEnabled = true;
                if (result == AuthCallResult.Ok)
                {
                    _pendingEmail = email;
                    ShowVerifyStep(email);
                    return;
                }
                statusText.Foreground = ErrFg;
                statusText.Text = SendErrorCopy(result);
                sendBtn.IsEnabled = true;
            };
        }

        // ---- Stage B: code entry + resend --------------------------------

        private void ShowVerifyStep(string email)
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Enter your code",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "We sent a 6-digit code to " + email + ". Paste it below.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(new TextBlock {
                Text = "6-digit code", Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var codeInput = new TextBox {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 16, MaxLength = 6,
                Margin = new Thickness(0, 0, 0, 4),
            };
            // Numeric-only filter so the user can't type letters and waste
            // a verify round-trip (mirrors housinggrade's pattern="[0-9]{6}"
            // input attribute).
            codeInput.PreviewTextInput += (s, e) =>
            {
                foreach (var ch in e.Text) if (!char.IsDigit(ch)) { e.Handled = true; return; }
            };
            DataObject.AddPastingHandler(codeInput, (s, e) =>
            {
                if (e.DataObject.GetDataPresent(typeof(string)))
                {
                    string pasted = (string)e.DataObject.GetData(typeof(string));
                    string digits = "";
                    foreach (var ch in pasted) if (char.IsDigit(ch)) digits += ch;
                    if (digits.Length == 0) { e.CancelCommand(); return; }
                    if (digits.Length != pasted.Length)
                    {
                        e.CancelCommand();
                        codeInput.Text = digits.Substring(0, Math.Min(6, digits.Length));
                        codeInput.CaretIndex = codeInput.Text.Length;
                    }
                }
            });
            codeInput.Loaded += (s, e) => codeInput.Focus();
            root.Children.Add(codeInput);

            var resendRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12),
            };
            resendRow.Children.Add(new TextBlock {
                Text = "Didn't get it? ", Foreground = MutedFg, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var resendBtn = new Button {
                Content = "Send a new code",
                Padding = new Thickness(0), Margin = new Thickness(0),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                Foreground = InfoFg, FontSize = 11,
                Cursor = Cursors.Hand,
            };
            resendRow.Children.Add(resendBtn);
            root.Children.Add(resendRow);

            var statusText = new TextBlock {
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(statusText);

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var restartBtn = new Button {
                Content = "Use a different email", Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
            };
            restartBtn.Click += (s, e) =>
            {
                _pendingEmail = null;
                ShowEmailStep(email);
            };
            btnRow.Children.Add(restartBtn);

            var signInBtn = new Button {
                Content = "Sign in", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            btnRow.Children.Add(signInBtn);
            root.Children.Add(btnRow);

            signInBtn.Click += async (s, e) =>
            {
                string code = (codeInput.Text ?? "").Trim();
                if (code.Length != 6 || !System.Text.RegularExpressions.Regex.IsMatch(code, "^[0-9]{6}$"))
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Enter the 6-digit code from your email.";
                    return;
                }
                signInBtn.IsEnabled = false;
                restartBtn.IsEnabled = false;
                statusText.Foreground = InfoFg;
                statusText.Text = "Verifying...";

                AuthCallResult result;
                try { result = await _plugin.AuthVerifyOtpAsync(email, code); }
                catch (Exception ex)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Verify failed: " + ex.Message;
                    signInBtn.IsEnabled = true;
                    restartBtn.IsEnabled = true;
                    return;
                }
                if (result == AuthCallResult.Ok)
                {
                    statusText.Foreground = OkFg;
                    statusText.Text = "Signed in.";
                    DialogResult = true;
                    Close();
                    return;
                }
                statusText.Foreground = ErrFg;
                statusText.Text = VerifyErrorCopy(result);
                signInBtn.IsEnabled = true;
                restartBtn.IsEnabled = true;
            };

            resendBtn.Click += async (s, e) =>
            {
                resendBtn.IsEnabled = false;
                statusText.Foreground = InfoFg;
                statusText.Text = "Sending a new code...";

                AuthCallResult result;
                try { result = await _plugin.AuthSendOtpAsync(email); }
                catch (Exception ex)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Could not resend: " + ex.Message;
                    resendBtn.IsEnabled = true;
                    return;
                }
                if (result == AuthCallResult.Ok)
                {
                    statusText.Foreground = OkFg;
                    statusText.Text = "Sent a new code to " + email + ".";
                    StartResendCooldown(resendBtn);
                }
                else
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = SendErrorCopy(result);
                    if (result == AuthCallResult.RateLimited)
                        StartResendCooldown(resendBtn);
                    else
                        resendBtn.IsEnabled = true;
                }
            };
        }

        // 60-second resend cooldown matching Supabase's max_frequency.
        // Mirrors housinggrade's setTimeout-driven button-disable.
        private void StartResendCooldown(Button resendBtn)
        {
            int remaining = ResendCooldownSeconds;
            resendBtn.IsEnabled = false;
            string originalLabel = resendBtn.Content as string ?? "Send a new code";
            resendBtn.Content = $"Sent. Try again in {remaining}s";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    timer.Stop();
                    resendBtn.Content = originalLabel;
                    resendBtn.IsEnabled = true;
                    return;
                }
                resendBtn.Content = $"Sent. Try again in {remaining}s";
            };
            timer.Start();
        }

        // ---- Copy helpers (housinggrade-style specific messages) ---------

        private static string SendErrorCopy(AuthCallResult r)
        {
            switch (r)
            {
                case AuthCallResult.RateLimited:
                    return "Slow down a moment, then try again.";
                case AuthCallResult.InvalidInput:
                    return "Enter a valid email address.";
                case AuthCallResult.NetworkFailure:
                    return "Could not reach the sign-in server. Check your internet and try again.";
                default:
                    return "Could not send the code. Try again in a moment.";
            }
        }

        private static string VerifyErrorCopy(AuthCallResult r)
        {
            switch (r)
            {
                case AuthCallResult.Expired:
                    return "That code expired. Tap 'Send a new code' to get a fresh one.";
                case AuthCallResult.BadCode:
                    return "That code didn't match. Double-check or request a new one.";
                case AuthCallResult.RateLimited:
                    return "Slow down a moment, then try again.";
                case AuthCallResult.InvalidInput:
                    return "Enter the 6-digit code from your email.";
                case AuthCallResult.NetworkFailure:
                    return "Could not reach the sign-in server. Check your internet and try again.";
                default:
                    return "Could not verify the code. Try again.";
            }
        }

        private static bool IsLikelyEmail(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            int at = s.IndexOf('@');
            return at > 0 && at < s.Length - 3 && s.IndexOf('.', at) > at;
        }
    }
}
