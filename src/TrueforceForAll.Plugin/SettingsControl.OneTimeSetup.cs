// Opening One time setup (OneTimeSetupWindow) from the panel, and applying
// what it collects. A fresh install is owed it (Settings.OneTimeSetupPending)
// and meets it through MaybeShowNetworkedWelcome, which every first-open path
// already calls; anyone can run it again from the Settings tab button.
//
// The welcome rides along as setup's community page, near the end, while it is
// unseen. If a game started before the panel was ever opened, the plugin's init
// path has shown the standalone welcome by then, and setup leaves that page out.
// A skip before the community page leaves the welcome owed, so the standalone
// one still delivers the disclosure later.

using System;
using System.Windows;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        /// <param name="preview">The ONBOARDING access code: every page, whatever
        /// this PC's state (welcome already seen, signed in, no wheel found), and
        /// nothing committed on close. The answers given on the pages still apply,
        /// as they would for a real user.</param>
        private async void MaybeShowOneTimeSetup(bool force, bool preview = false)
        {
            var s = _plugin?.Settings;
            if (s == null) return;
            if (preview) force = true;
            if (!force && !s.OneTimeSetupPending) return;
            // Same reason as the welcome: SimHub caches this control, so a
            // dispatched call can land after Unloaded with no window to own the
            // dialog. Leave the pending flag set and let the next open retry.
            var owner = Window.GetWindow(this);
            if (owner == null) return;
            if (!force && OneTimeSetupWindow.ShownThisSession) return;
            // The standalone welcome is on screen right now (the init path opened
            // it and has not committed yet). Never stack on top of it; the next
            // panel open picks setup up without the welcome page.
            if (WelcomeWindow.ShownThisSession && !s.HasSeenNetworkedWelcome) return;

            bool backend = !string.IsNullOrEmpty(s.CommunityBackendUrl)
                && !string.IsNullOrEmpty(s.CommunityBackendAnonKey);
            bool includeWelcome = preview || (!s.HasSeenNetworkedWelcome && backend);
            // Claimed before the await below, so a second dispatch arriving while
            // the Discord status loads cannot open a second window.
            OneTimeSetupWindow.ShownThisSession = true;
            _welcomeTriggeredThisSession = true;

            // The account page: signed out, it offers sign-in then Discord; signed
            // in but not linked, it is just the Discord link; signed in and linked,
            // there is nothing to offer and it is left out. The status is cached by
            // the plugin, and the wait is capped so a slow connection never holds
            // setup up: an unknown status shows the page, the safe side.
            bool includeAccount = preview || backend;
            if (includeAccount && !preview && _plugin.AuthIsSignedIn)
            {
                try
                {
                    var status = _plugin.GetDiscordStatusAsync(System.Threading.CancellationToken.None);
                    if (await System.Threading.Tasks.Task.WhenAny(status, System.Threading.Tasks.Task.Delay(3000)) == status
                        && status.Result.linked)
                        includeAccount = false;
                }
                catch { /* unknown: show the page */ }
                owner = Window.GetWindow(this);
                if (owner == null) { OneTimeSetupWindow.ShownThisSession = false; return; }
            }
            // Holds the init path off while setup carries the welcome.
            if (includeWelcome && !preview) WelcomeWindow.ShownThisSession = true;

            OneTimeSetupWindow setup = null;
            setup = new OneTimeSetupWindow(
                includeWelcome,
                includeAccount,
                includeLights: () => preview || _plugin.WheelDetected,
                isElevated: () => _plugin.IsRunningElevated,
                isGHubRunning: () => _plugin.IsLogitechGHubRunning,
                wheelFound: () => _plugin.WheelDetected,
                wheelName: () => _plugin.WheelModelLabel,
                simpleNow: s.SimpleMode,
                setSimpleMode: SetSimpleModeFromSetup,
                lovelyNow: s.LovelyCarDataEnabled,
                setLovely: SetLovelyFromSetup,
                lightsProgrammable: () => preview || _plugin.WheelHasSelectableLightPattern,
                idleNow: s.IdleLedMode,
                setIdle: SetIdleLightsFromSetup,
                isSignedIn: () => _plugin.AuthIsSignedIn,
                // The same order as the standalone welcome: the proceed commit
                // first, so the account is created with community features on
                // and a cancelled sign-in leaves nothing half done.
                signIn: w =>
                {
                    if (setup != null && setup.WelcomeShown && !preview) CommitNetworkedWelcome();
                    RunWelcomeSignIn(w);
                },
                discordLinked: DiscordLinkedNowAsync,
                // The Account tab's own flow, so its row and status line stay in
                // step with what setup shows.
                linkDiscord: async () =>
                {
                    await LinkDiscordAsync();
                    return await DiscordLinkedNowAsync();
                })
            { Owner = owner };
            bool finished = setup.ShowDialog() == true;
            // Finish lands on the Effects tab, on the view just chosen. A skip
            // leaves the panel where it was.
            if (finished && MainTabs != null && EffectsTab != null) MainTabs.SelectedItem = EffectsTab;
            if (preview) return;

            // Finished or skipped, it is no longer owed.
            s.OneTimeSetupPending = false;
            if (setup.WelcomeShown) CommitNetworkedWelcome();
            else
            {
                // Skipped before the community page: the disclosure was not given,
                // so the standalone welcome stays owed and may show this session,
                // at the next game start or the next open of this panel.
                if (includeWelcome)
                {
                    WelcomeWindow.ShownThisSession = false;
                    _welcomeTriggeredThisSession = false;
                }
                try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Error("[TF4ALL] Persist settings failed: " + ex.Message); }
            }
        }

        private async System.Threading.Tasks.Task<bool> DiscordLinkedNowAsync()
        {
            if (_plugin == null || !_plugin.AuthIsSignedIn) return false;
            try
            {
                var (linked, _) = await _plugin.GetDiscordStatusAsync(
                    System.Threading.CancellationToken.None, forceRefresh: true);
                return linked;
            }
            catch { return false; }
        }

        // What AmbientLedMode_Changed does for the idle picker, with the picker
        // itself brought along.
        private void SetIdleLightsFromSetup(AmbientLedMode mode)
        {
            if (_plugin?.Settings == null) return;
            _plugin.Settings.IdleLedMode = mode;
            _plugin.PersistSettings();
            _plugin.SyncAudioOutputMeter();
            if (IdleLedModeCombo != null)
            {
                var prev = _suppressEvents;
                _suppressEvents = true;
                try { IdleLedModeCombo.SelectedIndex = (int)mode; }
                finally { _suppressEvents = prev; }
            }
            RefreshAmbientLedRows();
        }

        private void SetSimpleModeFromSetup(bool simple)
        {
            if (_plugin?.Settings == null) return;
            _plugin.Settings.SimpleMode = simple;
            ApplySimpleMode();
            SchedulePersistDebounced();
        }

        // What LovelyCarData_Changed does, with the LIGHTSYNC tab's checkbox
        // brought along so the two never disagree.
        private void SetLovelyFromSetup(bool on)
        {
            if (_plugin?.Settings == null) return;
            _plugin.Settings.LovelyCarDataEnabled = on;
            _plugin.PersistSettings();
            _plugin.OnLovelyEnabledChanged();
            if (LovelyCarDataCheck != null)
            {
                var prev = _suppressEvents;
                _suppressEvents = true;
                try { LovelyCarDataCheck.IsChecked = on; }
                finally { _suppressEvents = prev; }
            }
        }

        private void RunOneTimeSetup_Click(object sender, RoutedEventArgs e) => MaybeShowOneTimeSetup(force: true);
    }
}
