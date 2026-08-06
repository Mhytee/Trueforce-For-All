using System;
using System.Collections.Generic;
using System.Windows;

namespace TrueforceForAll.Plugin
{
    /// <summary>Shared "set the car's official name" flow behind the Car facts
    /// panel's Save button (the single naming entry point). Writes the name
    /// locally first (so the user always sees it), then submits it to the
    /// community exactly like every other car fact: saving IS how a name reaches
    /// the community, there is no separate Share step.
    ///
    /// Behaviour mirrors the redline / engine save paths (owner decision
    /// 2026-07-22: a name is a car fact like any other, and the per-language
    /// consensus is the quality gate, so the old per-save confirm/correct
    /// ceremony is gone):
    ///   - sharing on (the default)     -> silent submit, no modal
    ///   - consent never asked (residual upgrader) -> the one-time
    ///     CarFactsConsentGate ask, then submit if granted
    ///   - opted out / community off     -> local-only save, no submit, no modal
    /// The submit is hard-gated again at the network layer (CommunityClient
    /// FireAndForgetRpc checks AutoSubmitCarFacts), so an opted-out user never
    /// sends even if a caller reaches this far.</summary>
    internal static class CarNameShareFlow
    {
        // Per-session dedupe so repeat Save clicks on the same name don't re-hit
        // the server; a different name re-engages. Mirrors the redline save
        // path's _enginePromptedThisSession guard. Process-global is fine: a car
        // name is a process-global concept and this only needs to survive the
        // session (a SimHub restart clears it).
        private static readonly HashSet<string> _submittedThisSession =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly object _submittedLock = new object();

        public static void SetNameAndMaybeShare(
            TrueforcePlugin plugin, string game, string carId,
            string newName, Window owner)
        {
            if (plugin == null) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();

            // Local first: the header / Car facts panel re-renders to the new
            // name regardless of community state. WriteCarNameFact validates
            // length and returns false on invalid input.
            if (!plugin.WriteCarNameFact(game, carId, newName)) return;

            // Self-gating submit, same as the redline/engine save paths:
            //   sharing on      -> returns true silently, we submit
            //   consent unasked -> one-time consent ask (rare under default-on)
            //   opted out       -> returns false silently, local-only save
            if (!CarFactsConsentGate.EnsureConsent(owner, plugin)) return;

            string key = game + "/" + carId + "|name|" + newName.ToLowerInvariant();
            lock (_submittedLock)
            {
                if (!_submittedThisSession.Add(key)) return;   // same name already sent this session
            }
            plugin.SubmitCarNameToCommunity(game, carId, newName);
        }

        /// <summary>Rename from the phone dash. Saves locally, and shares only
        /// when sharing is already settled as yes.</summary>
        /// <remarks>
        /// The desktop path can put a consent modal on screen. That is right
        /// at a keyboard and wrong from a phone: the dialog would open on the
        /// PC, behind the game, with nobody there to answer it, and the dash
        /// would sit waiting on a window the driver cannot see. So the ask is
        /// simply skipped here. The name still saves, and the next rename from
        /// the desktop asks properly.
        /// </remarks>
        public static bool SetNameFromDash(
            TrueforcePlugin plugin, string game, string carId, string newName)
        {
            if (plugin == null) return false;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return false;
            if (string.IsNullOrWhiteSpace(newName)) return false;
            newName = newName.Trim();

            if (!plugin.WriteCarNameFact(game, carId, newName)) return false;

            var s = plugin.Settings;
            bool canShareSilently = s != null && s.AutoSubmitCarFacts && s.CommunityEnabled;
            if (!canShareSilently) return true;   // saved locally, nothing asked

            string key = game + "/" + carId + "|name|" + newName.ToLowerInvariant();
            lock (_submittedLock)
            {
                if (!_submittedThisSession.Add(key)) return true;
            }
            plugin.SubmitCarNameToCommunity(game, carId, newName);
            return true;
        }
    }
}
