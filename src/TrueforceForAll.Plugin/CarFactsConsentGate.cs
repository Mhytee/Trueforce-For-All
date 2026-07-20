using System.Windows;

namespace TrueforceForAll.Plugin
{
    // "Share car data with the community?" gate, shared by every car-fact
    // submission surface: the redline/engine/custom-engine save prompts,
    // the explicit Share/Confirm redline buttons, and the car-name flow.
    // With sharing ON by default (welcome modal = the disclosure) this is a
    // RESIDUAL path: the one-time ask only fires for upgraders whose stored
    // settings have community on but sharing off, and the explicitAction
    // re-offer handles users who opted out and later click Share. Mirrors
    // the ShareGate pattern: when community features are off, the confirm
    // button doubles as the enable-community funnel instead of dead-ending.
    // Car facts never show a username and need no account, so unlike
    // ShareGate there is no sign-in step; consenting mints the signed-out
    // fallback id (signed-in submissions are keyed to the account
    // server-side and feed the car-fact achievements).
    internal static class CarFactsConsentGate
    {
        // Cheap pre-check for the passive save-paths: true when a submission
        // could happen right now OR the one-time ask is still pending (i.e.
        // EnsureConsent might succeed). Lets callers bail before burning
        // per-session dedupe slots when consent is settled as "no".
        public static bool CanSubmitOrAsk(TrueforcePlugin plugin)
        {
            var s = plugin?.Settings;
            if (s == null) return false;
            if (s.AutoSubmitCarFacts) return s.CommunityEnabled;
            return !s.CarFactsConsentAsked;
        }

        // Returns true when car-fact submission may proceed right now.
        //   - Consent granted (AutoSubmitCarFacts) and community on -> true.
        //   - Never asked -> the one-time modal; confirming grants consent,
        //     enables community features when they're off, and mints the
        //     anon id. Declining latches silent-forever for passive paths.
        //   - Previously declined -> silent false, UNLESS explicitAction
        //     (a deliberate Share/Confirm click), which re-offers the modal:
        //     an explicit share attempt shouldn't stay bricked by a
        //     long-ago "No thanks".
        //   - Consented but community later turned off -> silent false on
        //     passive paths; explicitAction offers to turn community back on.
        public static bool EnsureConsent(Window owner, TrueforcePlugin plugin,
            bool explicitAction = false)
        {
            var s = plugin?.Settings;
            if (s == null) return false;

            if (s.AutoSubmitCarFacts)
            {
                if (s.CommunityEnabled) return true;
                if (!explicitAction) return false;
                bool? go = TrueforceDialog.Show(owner, "Share car data",
                    "Sharing car data needs community features (online) turned on. Turn them on and share?",
                    DialogKind.Confirm, "Enable community features", "Not now");
                if (go != true) return false;
                plugin.SetCommunityEnabled(true);
                return true;
            }

            if (s.CarFactsConsentAsked && !explicitAction) return false;

            bool communityOn = s.CommunityEnabled;
            bool? ok = TrueforceDialog.Show(owner,
                "Share car data with the community?",
                "When you tune a redline or engine, it can be submitted anonymously to improve everyone's defaults.",
                DialogKind.Confirm,
                communityOn ? "Share anonymously" : "Enable community features",
                "No thanks");
            s.CarFactsConsentAsked = true;
            if (ok == true)
            {
                s.AutoSubmitCarFacts = true;
                plugin.EnsureCarFactsAnonId();
                // Persists + raises CommunityEnabledChanged so every surface
                // (including the Settings checkbox) refreshes consistently.
                if (!communityOn) plugin.SetCommunityEnabled(true);
            }
            try { plugin.PersistSettings(); }
            catch { /* answered state re-latches next session at worst */ }
            return ok == true;
        }
    }
}
