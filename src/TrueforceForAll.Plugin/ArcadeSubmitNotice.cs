using System;
using System.Windows;

namespace TrueforceForAll.Plugin
{
    // The one-time notice that finished Time Attack runs get sent to the community leaderboard.
    //
    // A NOTICE, not a consent request, because submission ships on: the boards are dead filler
    // otherwise, and a leaderboard nobody submits to stays empty forever. But it ships on under a
    // name, which is the part worth telling somebody about before it happens rather than after, so
    // the opt-out is a real button on the same dialog rather than something to go hunting for in a
    // settings tab.
    //
    // Unlike CarFactsConsentGate this needs no sign-in step of its own: the server rejects an
    // anonymous submission outright and reads the display name from the account, so a signed-out
    // player is already submitting nothing and there is nothing to disclose to them yet.
    internal static class ArcadeSubmitNotice
    {
        /// <summary>Show the notice once. Returns true when times may be submitted.
        ///
        /// Safe to call from any UI surface: it returns immediately once the notice has been
        /// answered, so it can sit on a panel-load path without being a per-open dialog.</summary>
        public static bool EnsureShown(Window owner, TrueforcePlugin plugin)
        {
            var s = plugin?.Settings;
            if (s?.Arcade == null) return false;
            if (s.Arcade.Id8SubmitNoticeShown) return s.Arcade.Id8SubmitTimesEnabled;

            bool? ok = TrueforceDialog.Show(owner,
                "Your lap times go on the leaderboard",
                "TF4ALL fills Initial D 8's in-game leaderboards, which SEGA's servers stopped " +
                "serving years ago. When you finish a Time Attack run, your time is sent to the " +
                "community board under your TF4ALL username, so other players see it in the game." +
                Environment.NewLine + Environment.NewLine +
                "Only runs you actually finish are sent, and only while you are signed in. You can " +
                "change this any time under Arcade leaderboards on the Settings tab.",
                DialogKind.Confirm,
                "OK, submit my times",
                "Don't submit my times",
                // Gold: this is the path the notice recommends, and two identical grey
                // buttons make an opt-out notice read as a question with no answer.
                goldOk: true,
                quietCancel: true);

            s.Arcade.Id8SubmitNoticeShown = true;
            // A dismissed dialog (the X, or Escape) is not agreement. Only the explicit OK leaves
            // submission on; anything else opts out, which is the safe reading of "no answer".
            s.Arcade.Id8SubmitTimesEnabled = ok == true;

            try { plugin.PersistSettings(); }
            catch { /* at worst the notice shows once more next session */ }

            return s.Arcade.Id8SubmitTimesEnabled;
        }
    }
}
