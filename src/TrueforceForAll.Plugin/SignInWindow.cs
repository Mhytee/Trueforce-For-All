// Two-stage email OTP sign-in modal. Mirrors the housinggrade pattern
// (Mhytee/Housing-Report-Card app.js handleSignInSendCode +
// handleSignInVerifyCode + handleSignInResend):
//   Stage A: email field + "Email me a sign-in code"
//   Stage B: 6-digit code field + Sign-in button, with
//     * "Use a different email" to restart
//     * "Send a new code" resend with a live 60-second countdown matching
//       Supabase's max_frequency (1 minute). The countdown starts as soon
//       as the verify step opens (a code was just sent) and again after any
//       resend or rate-limit, so the button always shows when the next
//       request is allowed instead of tripping the server-side rate limit.
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
        private bool _closed;
        private DispatcherTimer _resendTimer;

        public SignInWindow(TrueforcePlugin plugin)
        {
            _plugin = plugin;

            Title         = "Sign in to access community features";
            Width         = 440;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            // Open straight on the email step. Entering an email either signs
            // the user in or creates their account via the emailed code (there's
            // no separate sign-up), so a value-prop preview just added a click.
            ShowEmailStep();
        }

        // Once the modal has closed, any in-flight AuthSendOtpAsync /
        // AuthVerifyOtpAsync continuation must NOT touch controls or set
        // DialogResult (setting DialogResult on a closed dialog throws, and
        // from an async void handler that would surface unhandled on the
        // dispatcher). The click handlers check _closed after each await.
        // Also stop any running resend-cooldown timer so it doesn't keep
        // ticking on the now-orphaned button.
        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            StopResendTimer();
            base.OnClosed(e);
        }

        // ---- Stage A: email entry ----------------------------------------

        private void ShowEmailStep(string prefilledEmail = null, string errorMessage = null)
        {
            // Leaving the verify step (e.g. "Use a different email") must stop
            // any running resend cooldown so its timer doesn't keep ticking on
            // the button that's about to be orphaned by the Content swap.
            StopResendTimer();
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Sign in or create your account",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "New here? There's no separate sign-up. Enter your email and we'll send a 6-digit code that signs you in or creates your account. No password. You can then browse and share presets, vote, and help fill in car data.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });

            // Moment-of-collection disclosure: this is where the email is
            // first asked for, so the policy link lives here too.
            var privacyLine = new TextBlock {
                FontSize = 10.5, Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            };
            privacyLine.Inlines.Add(new System.Windows.Documents.Run(
                "Your email is used to sign you in and for occasional account notices, nothing else. ")
            { Foreground = MutedFg });
            var privacyLink = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run("Privacy policy"))
            { Foreground = HeaderFg };
            privacyLink.Click += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        SettingsControl.PrivacyPolicyUrl)
                    { UseShellExecute = true });
                }
                catch { }
            };
            privacyLine.Inlines.Add(privacyLink);
            root.Children.Add(privacyLine);

            root.Children.Add(new TextBlock {
                Text = "Email", Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            // Prefill: an explicit value (e.g. "Use a different email" restart)
            // wins; otherwise fall back to the remembered email when the user has
            // "Remember my email" on.
            string initialEmail = prefilledEmail
                ?? (_plugin.Settings.RememberSignInEmail ? LoadEmail(_plugin.Settings.LastSignInEmail) : null);
            var emailInput = new TextBox {
                Text = initialEmail ?? "",
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 13, MaxLength = 254,
                Margin = new Thickness(0, 0, 0, 8),
            };
            emailInput.Loaded += (s, e) => { emailInput.Focus(); emailInput.SelectAll(); };
            root.Children.Add(emailInput);

            var rememberCheck = new CheckBox {
                Content = "Remember my email",
                IsChecked = _plugin.Settings.RememberSignInEmail,
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(rememberCheck);

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
                IsCancel = true,
            };
            ModalButtonTheme.Secondary(cancelBtn);
            cancelBtn.Click += (s, e) => DialogResult = false;  // setting DialogResult closes the dialog
            btnRow.Children.Add(cancelBtn);

            var sendBtn = new Button {
                Content = "Email me a sign-in code", Padding = new Thickness(16, 5, 16, 5),
                FontWeight = FontWeights.SemiBold, IsDefault = true,
            };
            ModalButtonTheme.Primary(sendBtn);
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
                // Persist the "remember my email" choice at send time (like most
                // sites). "Use a different email" returns to this same step and
                // reads it back, so the checkbox reflects the last choice. If this
                // send fails, the user's corrected email simply overwrites it on
                // the next attempt. Stored DPAPI-encrypted.
                bool remember = rememberCheck.IsChecked == true;
                _plugin.Settings.RememberSignInEmail = remember;
                _plugin.Settings.LastSignInEmail = remember ? StoreEmail(email) : "";
                try { _plugin.PersistSettings(); } catch { }
                sendBtn.IsEnabled = false;
                cancelBtn.IsEnabled = false;
                statusText.Foreground = InfoFg;
                statusText.Text = "Sending a sign-in code to your inbox...";
                AuthCallResult result;
                try { result = await _plugin.AuthSendOtpAsync(email); }
                catch (Exception ex)
                {
                    if (_closed) return;
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Couldn't reach the sign-in server. Check your internet and try again.";
                    TrueforceDialog.LogError("Sign-in send", ex);
                    sendBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }
                if (_closed) return;
                cancelBtn.IsEnabled = true;
                if (result == AuthCallResult.Ok)
                {
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
                Text = "We sent a 6-digit code to " + email + ". Enter the code below.",
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
                foreach (var ch in e.Text) if (!IsAsciiDigit(ch)) { e.Handled = true; return; }
            };
            DataObject.AddPastingHandler(codeInput, (s, e) =>
            {
                if (e.DataObject.GetDataPresent(typeof(string)))
                {
                    string pasted = (string)e.DataObject.GetData(typeof(string));
                    string digits = "";
                    foreach (var ch in pasted) if (IsAsciiDigit(ch)) digits += ch;
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
                Padding = new Thickness(0), Margin = new Thickness(0),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                FocusVisualStyle = MakeLinkFocusVisual(),
            };
            // A code was just sent to reach this step, but the resend link
            // stays available (classic pattern). If the user taps too soon,
            // the server's rate-limit reply drives an accurate countdown.
            SetResendIdle(resendBtn);
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
            };
            ModalButtonTheme.Secondary(restartBtn);
            restartBtn.Click += (s, e) =>
            {
                ShowEmailStep(email);
            };
            btnRow.Children.Add(restartBtn);

            var signInBtn = new Button {
                Content = "Sign in", Padding = new Thickness(16, 5, 16, 5),
                FontWeight = FontWeights.SemiBold, IsDefault = true,
            };
            ModalButtonTheme.Primary(signInBtn);
            btnRow.Children.Add(signInBtn);
            root.Children.Add(btnRow);

            signInBtn.Click += async (s, e) =>
            {
                string code = (codeInput.Text ?? "").Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[0-9]{6}$"))  // regex already enforces exactly 6 digits
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
                    if (_closed) return;
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Couldn't reach the sign-in server. Check your internet and try again.";
                    TrueforceDialog.LogError("Sign-in verify", ex);
                    signInBtn.IsEnabled = true;
                    restartBtn.IsEnabled = true;
                    return;
                }
                if (_closed) return;
                if (result == AuthCallResult.Ok)
                {
                    statusText.Foreground = OkFg;
                    statusText.Text = "Signed in.";
                    DialogResult = true;  // setting DialogResult closes the dialog
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
                    if (_closed) return;
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Couldn't reach the sign-in server. Check your internet and try again.";
                    TrueforceDialog.LogError("Sign-in resend", ex);
                    SetResendIdle(resendBtn);
                    return;
                }
                if (_closed) return;
                if (result == AuthCallResult.Ok)
                {
                    // Classic resend: confirm and leave the link available.
                    statusText.Foreground = OkFg;
                    statusText.Text = "New code sent to " + email + ".";
                    SetResendIdle(resendBtn);
                }
                else if (result == AuthCallResult.RateLimited)
                {
                    // A code went out moments ago. Count down to the moment the
                    // server will accept the next request: its 429 reply carries
                    // the wait (fall back to max_frequency if absent). The server
                    // floors the seconds, so add 1s of slack to the parsed value
                    // to avoid re-enabling a hair early and earning one more
                    // rate-limit.
                    int? serverWait = _plugin.AuthLastSendRetryAfterSeconds;
                    int wait = serverWait.HasValue ? serverWait.Value + 1 : ResendCooldownSeconds;
                    statusText.Foreground = InfoFg;
                    statusText.Text = "You just requested a code.";
                    StartResendCooldown(resendBtn, wait, "Try again in {0}s");
                }
                else
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = SendErrorCopy(result);
                    SetResendIdle(resendBtn);
                }
            };
        }

        // Stop and release any running resend-cooldown timer. Safe to call
        // when none is active (window close, step change, or before starting
        // a fresh one) so a DispatcherTimer never keeps ticking on a button
        // orphaned by a Content swap or a closed window.
        private void StopResendTimer()
        {
            if (_resendTimer != null)
            {
                _resendTimer.Stop();
                _resendTimer = null;
            }
        }

        // Resting state for the resend control: a de-emphasized underlined
        // link, always available. (A code was just sent, but if the user
        // taps too soon the server's rate-limit reply drives an accurate
        // countdown via StartResendCooldown.)
        private void SetResendIdle(Button resendBtn)
        {
            StopResendTimer();
            resendBtn.IsEnabled = true;
            resendBtn.Content = new TextBlock {
                Text = "Resend code",
                Foreground = MutedFg, FontSize = 11,
                TextDecorations = TextDecorations.Underline,
            };
        }

        // A visible keyboard-focus ring for transparent link-style buttons, which
        // otherwise show no focus cue on the dark theme. Draws a thin dashed accent
        // rectangle just outside the link when it is tabbed to (not on mouse click).
        private static Style MakeLinkFocusVisual()
        {
            var style = new Style(typeof(Control));
            var template = new ControlTemplate(typeof(Control));
            var rect = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
            rect.SetValue(System.Windows.Shapes.Shape.StrokeProperty,
                new SolidColorBrush(Color.FromRgb(0x6F, 0xB1, 0xFF)));
            rect.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);
            rect.SetValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty,
                new DoubleCollection(new double[] { 2.0, 2.0 }));
            rect.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
            rect.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);
            template.VisualTree = rect;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        // Disable the resend link and count down live to the moment the next
        // request is allowed. seconds comes from the server's rate-limit
        // reply when available (else the max_frequency default). labelFormat
        // takes the remaining seconds, e.g. "Try again in {0}s".
        private void StartResendCooldown(Button resendBtn, int seconds, string labelFormat)
        {
            StopResendTimer();
            int remaining = Math.Max(1, seconds);
            resendBtn.IsEnabled = false;
            resendBtn.Content = string.Format(labelFormat, remaining);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _resendTimer = timer;
            timer.Tick += (s, e) =>
            {
                // If a newer cooldown superseded this timer, don't touch the
                // link the newer one now owns; just retire this stale tick.
                if (_resendTimer != timer) { timer.Stop(); return; }
                remaining--;
                if (remaining <= 0)
                {
                    SetResendIdle(resendBtn);
                    return;
                }
                resendBtn.Content = string.Format(labelFormat, remaining);
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
                // Generic (and the verify-only Expired/BadCode, which the send
                // path never returns) share the same fallback copy.
                case AuthCallResult.Generic:
                default:
                    return "Could not send the code. Try again in a moment.";
            }
        }

        private static string VerifyErrorCopy(AuthCallResult r)
        {
            switch (r)
            {
                case AuthCallResult.Expired:
                    return "That code expired. Tap 'Resend code' to get a fresh one.";
                case AuthCallResult.BadCode:
                    return "That code didn't match. Double-check or request a new one.";
                case AuthCallResult.RateLimited:
                    return "Slow down a moment, then try again.";
                case AuthCallResult.InvalidInput:
                    return "Enter the 6-digit code from your email.";
                case AuthCallResult.NetworkFailure:
                    return "Could not reach the sign-in server. Check your internet and try again.";
                case AuthCallResult.Generic:
                default:
                    return "Could not verify the code. Try again.";
            }
        }

        // The remembered email is a per-PC convenience but still PII, so it's
        // kept DPAPI-encrypted at rest (CurrentUser scope), same as the auth
        // tokens. Protect falls back to plaintext only if DPAPI is unavailable,
        // so the "remember" feature never breaks.
        private static string StoreEmail(string email)
            => DpapiCipher.Protect(email) ?? email;

        // Read a remembered email back: decrypt if it's marker-wrapped (null on
        // decrypt failure, e.g. a different Windows user, so we simply don't
        // prefill); pass legacy plaintext through unchanged (it re-encrypts on
        // the next successful send).
        private static string LoadEmail(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            if (DpapiCipher.IsProtected(stored))
            {
                string plain = DpapiCipher.Unprotect(stored);
                return string.IsNullOrEmpty(plain) ? null : plain;
            }
            return stored;
        }

        // ASCII-only digit test. char.IsDigit also accepts Unicode digits
        // (Arabic-Indic, Devanagari, fullwidth, etc.), which would pass the
        // input filters but then fail the ^[0-9]{6}$ check here and on the
        // server, so a "6-digit" code would be silently rejected. Restrict to
        // 0-9 up front to match what actually gets accepted.
        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        // Best-effort client-side pre-check so obviously malformed addresses
        // don't waste a send round-trip. The server stays authoritative.
        // Rejects: blank input, any whitespace, more than one '@', an empty
        // local or domain label, a domain with no dot, a TLD shorter than 2,
        // and leading/trailing/consecutive dots.
        private static bool IsLikelyEmail(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            foreach (var ch in s) if (char.IsWhiteSpace(ch)) return false;
            if (s.IndexOf("..", StringComparison.Ordinal) >= 0) return false;

            int at = s.IndexOf('@');
            if (at <= 0) return false;                   // need a local part
            if (at != s.LastIndexOf('@')) return false;  // exactly one '@'

            string local  = s.Substring(0, at);
            string domain = s.Substring(at + 1);
            if (local[0] == '.' || local[local.Length - 1] == '.') return false;
            if (domain.Length == 0 || domain[0] == '.') return false;

            int dot = domain.LastIndexOf('.');
            if (dot <= 0) return false;                  // domain needs a label then a dot
            if (dot >= domain.Length - 2) return false;  // TLD must be at least 2 chars
            return true;
        }
    }
}
