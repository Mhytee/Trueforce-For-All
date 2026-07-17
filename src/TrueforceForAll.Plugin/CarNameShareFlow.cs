using System;
using System.Windows;

namespace TrueforceForAll.Plugin
{
    /// <summary>Shared "set the car's official name" flow used by BOTH rename
    /// entry points (the Settings header button and the Preset Manager row
    /// button) so they can't drift apart. Writes the name locally first (so the
    /// user always sees it), then, once the car-data sharing consent is
    /// granted (CarFactsConsentGate; no sign-in needed, no username shown),
    /// offers a confirm/correct submission of the OFFICIAL name for the
    /// user's language.
    ///
    /// Names are NOT auto-submittable. Unlike measured facts (redline, engine
    /// layout) a name is editorial, so every contribution goes through the
    /// confirm/correct modal with no "Always share" button. That modal is the
    /// quality gate. It shows the existing community name before the user adds
    /// their own variant, which is the reconsider-before-you-pollute moment.</summary>
    internal static class CarNameShareFlow
    {
        public static void SetNameAndMaybeShare(
            TrueforcePlugin plugin, string game, string carId,
            string newName, Window owner)
        {
            if (plugin == null) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();

            // Local first: the header / preset list re-renders to the new name
            // regardless of community state. WriteCarNameFact validates length
            // and returns false on invalid input.
            if (!plugin.WriteCarNameFact(game, carId, newName)) return;

            // Community contribution gate: the one-time anonymous-sharing
            // consent (which may itself enable community features on first
            // ask). A name save is a fact-worthy moment, so an unasked user
            // gets the consent modal here; a declined user skips silently.
            // The confirm/correct modal below still runs regardless of
            // auto-submit: a name is editorial, so it always needs eyes.
            if (!CarFactsConsentGate.EnsureConsent(owner, plugin)) return;

            // Per-language consensus: what does the community already call this
            // car in the user's language? Frames the confirm/correct modal.
            CarNameConsensus consensus = null;
            try { consensus = plugin.FetchCarNameConsensus(game, carId); }
            catch { /* best-effort; treat as "first" on any failure */ }

            CarFactsShareWindow.ShareState state;
            string consensusName = null;
            int supporting = 0;
            if (consensus == null || string.IsNullOrEmpty(consensus.Name))
            {
                state = CarFactsShareWindow.ShareState.First;
            }
            else
            {
                supporting = consensus.SupportingSubmissions;
                consensusName = consensus.Name;
                state = string.Equals(consensus.Name, newName, StringComparison.OrdinalIgnoreCase)
                    ? CarFactsShareWindow.ShareState.Confirming
                    : CarFactsShareWindow.ShareState.Alternative;
            }

            // Identity line is just the carId (a clean anchor); the value chip
            // shows the name being submitted. offerAlways:false => no auto-submit.
            var dialog = new CarFactsShareWindow(
                null, carId, newName, state, consensusName, supporting,
                offerAlways: false, valueLabel: "You're naming this car")
            {
                Owner = owner,
            };
            if (dialog.ShowDialog() != true) return;

            plugin.SubmitCarNameToCommunity(game, carId, newName);
            // Optimistic local injection of the new community consensus, but
            // only when the user's name IS or BECOMES the consensus: First
            // starts the record, Confirming strengthens it. For an Alternative
            // we leave the real consensus in place. The user's own name already
            // shows in the header (the local fact), and the "community" line
            // honestly shows it differs from the community's until enough others
            // agree. Self-guards to the active car inside NotifyCarNameConsensus
            // (a no-op otherwise).
            if (state != CarFactsShareWindow.ShareState.Alternative)
            {
                plugin.NotifyCarNameConsensus(game, carId, new CarNameConsensus
                {
                    Name = newName,
                    SupportingSubmissions =
                        state == CarFactsShareWindow.ShareState.Confirming ? supporting + 1 : 1,
                });
            }
        }
    }
}
