using System;
using System.Windows;

namespace TrueforceForAll.Plugin
{
    // Shared "Option C" share funnel, used by both the Preset Manager and the
    // active-card (Effects tab) share buttons. The buttons stay enabled even when
    // the community gates aren't met; a click routes here, which turns each gate
    // into the action that clears it instead of dead-ending:
    //   - Community off  -> a 2-button modal (Sign up / Sign in vs Not now) whose
    //                       primary flips community on, then continues.
    //   - Signed out     -> the sign-in / sign-up modal directly (community already on).
    // Returns false if the user backs out of either step, so the caller aborts quietly.
    internal static class ShareGate
    {
        public static bool EnsureReady(Window owner, TrueforcePlugin plugin, string title)
        {
            if (plugin?.Settings == null) return false;

            if (plugin.Settings.CommunityEnabled != true)
            {
                bool? go = TrueforceDialog.Show(owner, title,
                    "Sharing presets needs community features (online) turned on and an account. Sign up or sign in to get started?",
                    DialogKind.Confirm, "Sign up / Sign in", "Not now");
                if (go != true) return false;
                // Persisted + raises CommunityEnabledChanged so every surface
                // (and the Settings checkbox) refreshes consistently.
                plugin.SetCommunityEnabled(true);
            }

            if (plugin.AuthIsSignedIn != true)
            {
                var dlg = new SignInWindow(plugin) { Owner = owner };
                bool? ok = dlg.ShowDialog();
                if (ok != true || plugin.AuthIsSignedIn != true) return false;
            }

            return true;
        }
    }
}
