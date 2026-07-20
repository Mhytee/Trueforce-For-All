// Modal shown after first sign-in (or when the user clicks Edit on a
// signed-in username field). Live availability check while typing:
// 300ms debounce, green hint when available, specific copy when format
// / reserved / taken. Save calls set_username and closes on success.
//
// Standard Supabase pattern: server-side uniqueness enforced via a
// SECURITY DEFINER RPC backed by a unique index on
// profiles.username_lower. Race conditions resolve cleanly because the
// unique violation surfaces as a categorized error on the loser.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    internal sealed class PickUsernameWindow : Window
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

        private readonly TrueforcePlugin _plugin;
        private readonly DispatcherTimer _debounce;
        private string _lastCheckedText = "";

        public string ChosenUsername { get; private set; }

        public PickUsernameWindow(TrueforcePlugin plugin, string suggested)
        {
            _plugin = plugin;

            Title         = "Pick your username";
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
                Text = "Pick your username",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "This is your public name across the community, shown as your username on anything you share. 3 to 32 characters, letters / numbers / underscore.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(new TextBlock {
                Text = "Username", Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var input = new TextBox {
                Text = SafeSeed(suggested),
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 14, MaxLength = 32,
                Margin = new Thickness(0, 0, 0, 4),
            };
            input.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
            root.Children.Add(input);

            var hint = new TextBlock {
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                Text = "",
            };
            root.Children.Add(hint);

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
                IsEnabled = false,
            };
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);

            // Debounce typing so we don't hammer the server on every
            // keystroke. 300ms is the standard SaaS-app feel. Outer
            // try/catch on the async-void tick keeps any uncaught
            // exception (e.g., something CheckAndRender's internal
            // try/catch doesn't cover) from crashing the dispatcher
            // and leaving the UI stuck on "Checking...".
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += async (s, e) =>
            {
                _debounce.Stop();
                try { await CheckAndRender(input.Text, hint, saveBtn); }
                catch (Exception ex)
                {
                    hint.Foreground = ErrFg;
                    hint.Text = "Couldn't check that name. Check your connection and try again.";
                    TrueforceDialog.LogError("Username check", ex);
                    saveBtn.IsEnabled = false;
                }
            };
            input.TextChanged += (s, e) =>
            {
                hint.Foreground = MutedFg;
                hint.Text = "Checking...";
                saveBtn.IsEnabled = false;
                _debounce.Stop();
                _debounce.Start();
            };

            saveBtn.Click += async (s, e) =>
            {
                string name = (input.Text ?? "").Trim();
                if (string.IsNullOrEmpty(name)) return;
                saveBtn.IsEnabled = false;
                cancelBtn.IsEnabled = false;
                hint.Foreground = InfoFg;
                hint.Text = "Saving...";
                UsernameAvailability result;
                try { result = await _plugin.AuthSetUsernameAsync(name); }
                catch (Exception ex)
                {
                    hint.Foreground = ErrFg;
                    hint.Text = "Couldn't save your username. Check your connection and try again.";
                    TrueforceDialog.LogError("Username save", ex);
                    saveBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }
                cancelBtn.IsEnabled = true;
                if (result == UsernameAvailability.Available
                    || result == UsernameAvailability.SelfOwned)
                {
                    ChosenUsername = name;
                    DialogResult = true;
                    Close();
                    return;
                }
                // Server raced us or we missed something - re-render the hint.
                hint.Foreground = ErrFg;
                hint.Text = HintCopy(result);
                saveBtn.IsEnabled = false;
            };

            // Kick off the first check so the seeded value shows
            // feedback. Outer try/catch matches the Tick handler so a
            // network glitch on the seeded value can't crash the
            // dispatcher.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await CheckAndRender(input.Text, hint, saveBtn); }
                catch (Exception ex)
                {
                    hint.Foreground = ErrFg;
                    hint.Text = "Couldn't check that name. Check your connection and try again.";
                    TrueforceDialog.LogError("Username check", ex);
                    saveBtn.IsEnabled = false;
                }
            }), DispatcherPriority.Background);
        }

        private async Task CheckAndRender(string text, TextBlock hint, Button saveBtn)
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrEmpty(text))
            {
                hint.Foreground = MutedFg;
                hint.Text = "";
                saveBtn.IsEnabled = false;
                return;
            }
            _lastCheckedText = text;
            UsernameAvailability result;
            try { result = await _plugin.AuthCheckUsernameAvailableAsync(text); }
            catch
            {
                hint.Foreground = ErrFg;
                hint.Text = "Could not reach the server.";
                saveBtn.IsEnabled = false;
                return;
            }
            // Drop stale results if the user kept typing.
            if (!string.Equals(text, _lastCheckedText, StringComparison.Ordinal)) return;
            switch (result)
            {
                case UsernameAvailability.Available:
                    hint.Foreground = OkFg;
                    hint.Text = "Available.";
                    saveBtn.IsEnabled = true;
                    break;
                case UsernameAvailability.SelfOwned:
                    hint.Foreground = MutedFg;
                    hint.Text = "That's already your username.";
                    saveBtn.IsEnabled = false;
                    break;
                default:
                    hint.Foreground = ErrFg;
                    hint.Text = HintCopy(result);
                    saveBtn.IsEnabled = false;
                    break;
            }
        }

        private static string HintCopy(UsernameAvailability r)
        {
            switch (r)
            {
                case UsernameAvailability.Format:
                    return "3-32 characters. Letters, numbers, or underscore only.";
                case UsernameAvailability.Reserved:
                    return "That name is reserved. Pick another.";
                case UsernameAvailability.Profanity:
                    return "That name contains a blocked word. Pick another.";
                case UsernameAvailability.Taken:
                    return "That name is taken. Try another.";
                case UsernameAvailability.Network:
                    return "Could not reach the server.";
                default:
                    return "Not available.";
            }
        }

        private static string SafeSeed(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s.Trim())
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            string clean = sb.ToString();
            if (clean.Length < 3) clean = "";
            if (clean.Length > 32) clean = clean.Substring(0, 32);
            return clean;
        }

        /// <summary>Pre-modal gate for share flows. Verifies the
        /// signed-in user's server profile has a username and prompts
        /// the picker when it doesn't, so the upload doesn't later
        /// surface a raw "set a username first" backend error. The
        /// local SharingAuthor alone can't be trusted here: a freeform
        /// alias typed while signed out (or a name left behind by
        /// sign-out) passes a non-empty check while the upload RPCs
        /// still reject the account. Returns false when the user ends
        /// up without a username (cancelled picker); the caller should
        /// abort with a friendly status in that case. Not-signed-in
        /// returns true so the share dialog can run its own inline
        /// sign-in (which already calls EnsureUsernameAsync on
        /// success). Server-unreachable leaves local state alone, so
        /// the share proceeds and fails on its own error, not this
        /// gate.</summary>
        public static async Task<bool> EnsureUsernameBeforeShareAsync(
            TrueforcePlugin plugin, Window owner)
        {
            if (plugin?.Settings == null) return false;
            if (!plugin.AuthIsSignedIn) return true;
            // Owner-null guard: if the calling control unloaded during
            // the await chain (or the caller never had a window), an
            // un-owned picker would float free without modal focus.
            // Fall back to the local check rather than orphan a dialog.
            if (owner == null) return !string.IsNullOrWhiteSpace(plugin.Settings.SharingAuthor);
            try { await EnsureUsernameAsync(plugin, owner); }
            catch { return false; }
            // EnsureUsernameAsync syncs SharingAuthor to the server
            // profile (including clearing a stale alias when the profile
            // has no username), so this check now reflects server truth.
            return !string.IsNullOrWhiteSpace(plugin.Settings.SharingAuthor);
        }

        /// <summary>Shared post-sign-in flow: sync SharingAuthor from
        /// profile.username, or open this picker when the server has
        /// no username yet. Used by both the Settings panel's sign-in
        /// row and inline sign-ins from share/upload modals so they
        /// can't bypass it -- without this, a freshly-signed-up user's
        /// first upload errors out as "set a username first" from the
        /// server.</summary>
        public static async Task EnsureUsernameAsync(TrueforcePlugin plugin, Window owner)
        {
            if (plugin?.Settings == null || !plugin.AuthIsSignedIn) return;
            bool signedIn = false; string username = null;
            try { (signedIn, _, username) = await plugin.AuthGetMyProfileAsync(); }
            catch { /* server unreachable: signedIn stays false, bail below with local state untouched */ }
            if (!signedIn) return;
            if (!string.IsNullOrWhiteSpace(username))
            {
                plugin.Settings.SharingAuthor = username;
                try { plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                return;
            }

            // The server is authoritative while signed in: the profile has
            // no username, so a non-empty local SharingAuthor is a leftover
            // (signed-out freeform alias, or a previous account's name -
            // plain sign-out doesn't clear it). Wipe it so callers'
            // "has a username?" checks can't pass on a value the upload
            // RPCs would reject with "set a username first".
            if (!string.IsNullOrWhiteSpace(plugin.Settings.SharingAuthor))
            {
                plugin.Settings.SharingAuthor = "";
                try { plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
            }

            // No username yet - seed the picker with the email prefix.
            string emailLocal = plugin.AuthSignedInEmail ?? "";
            int atIdx = emailLocal.IndexOf('@');
            string seed = atIdx > 0 ? emailLocal.Substring(0, atIdx) : emailLocal;

            var picker = new PickUsernameWindow(plugin, seed) { Owner = owner };
            bool? ok = picker.ShowDialog();
            if (ok == true && !string.IsNullOrEmpty(picker.ChosenUsername))
            {
                plugin.Settings.SharingAuthor = picker.ChosenUsername;
                try { plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
            }
        }
    }
}
