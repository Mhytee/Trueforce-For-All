// The arcade cabinet panel on the FFB tab.
//
// It is deliberately small: three controls, each of which has no equivalent
// anywhere else in this plugin.
//
// Strength, damping, smoothing and invert already live on this tab, they already
// apply to the arcade force, and they are already stored in the preset, which for
// an arcade cabinet means per cabinet: detection gives each one its own game
// identity, so each one binds its own preset. Repeating any of that here would be
// a second set of knobs doing the same job and a second place to look when the
// wheel feels wrong.
//
// The arcade plugin's damper has no control here because it needs none: whatever
// damper reaches us is rendered, through the damper gain calibrated on the effects
// bench. That plugin emits one only when its own EnableDamper is set, so a damper
// arriving at all means somebody asked for it, and the place they asked is where
// they can change it.
//
// Worth keeping straight: ours is a passive damper, one steady amount, while a
// commanded damper varies as the game decides. Our slider is not a substitute for
// one, it is a different thing that happens to sit next to it.
//
// Minimum force earns its place by being the RIGHT answer to a real complaint.
// "I cannot feel the light forces" has two candidate fixes in that plugin, and
// they are not equally good: Power mode bends the whole curve as a square root
// and lifts the smallest commands about tenfold, which on a strong wheel is
// enough to start it oscillating around centre (owner, rig, 2026-09-04).
// Minimum force lifts every non-zero command by a fixed amount and leaves the
// shape above it alone.
//
// So Power mode is NOT offered here. Owner's call, on that evidence: a control
// whose realistic outcome is a wheel that shakes on its own is not worth the
// row it sits in, when the setting beside it does the same job safely. The key
// still exists and the arcade plugin's own GUI still writes it, so the status
// line calls it out when a cabinet arrives with it already on, rather than
// leaving it switched on with nothing on screen to say so.
//
// What is left is the settings with no equivalent anywhere in this plugin, and
// all of them belong to the arcade plugin rather than to us. So the panel edits that
// game's FFBPlugin.ini directly instead of keeping copies: that plugin reads the
// file, our build of it re-reads the file when it changes, and its own GUI edits
// the same keys the same way. One file, one truth, and nothing to keep in sync.
//
// Its own partial file because SettingsControl.xaml.cs is past 18,000 lines.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        /// <summary>Show or hide the panel and fill it in. Called from
        /// RefreshFromPlugin, which has already set _suppressEvents, so writing
        /// into these controls cannot fire their handlers back at us.</summary>
        private void RefreshArcadePanel(bool arcadeGame)
        {
            if (ArcadeTuningPanel == null) return;
            ArcadeTuningPanel.Visibility = arcadeGame ? Visibility.Visible : Visibility.Collapsed;
            if (!arcadeGame || _plugin == null) return;

            var target = _plugin.ActiveArcadeTarget();

            if (ArcadeTuningHeader != null)
                ArcadeTuningHeader.Text = target != null
                    ? "Arcade cabinet: " + target.DisplayName
                    : "Arcade cabinet";

            if (ArcadeTuningStatus != null)
            {
                string route = _plugin.ArcadeRouteLabel();
                ArcadeTuningStatus.Text = target == null
                    ? "Running, but this cabinet is not one TeknoParrot has a profile for."
                    : !target.IniFound
                        ? "The arcade plugin's settings file is not beside this game, so the two "
                          + "settings below have nothing to write to."
                        : route == null
                            ? "Waiting for force from this cabinet."
                            : "Force is arriving from " + route + ".";

                // Power mode has no control here any more, so a cabinet that
                // already has it switched on would carry it with nothing on
                // screen to say so and no way to reach it. Say so, and say where.
                if (target != null && target.IniFound
                    && _plugin.ArcadeIniGetInt("PowerMode", 0) != 0)
                {
                    ArcadeTuningStatus.Text +=
                        " Note: Power mode is on in this game's FFBPlugin.ini. It lifts the smallest "
                        + "forces about tenfold and can set a strong wheel oscillating around centre. "
                        + "Minimum force below does the same job without that. Set PowerMode=0 in that "
                        + "file, or in the arcade plugin's own settings screen.";
                }
            }

            bool haveIni = target != null && target.IniFound;
            if (ArcadeMinForceBox != null)
            {
                ArcadeMinForceBox.IsEnabled = haveIni;
                ArcadeMinForceBox.Text = _plugin.ArcadeIniGetInt("MinForce", 0)
                                                .ToString(CultureInfo.InvariantCulture);
            }
            if (ArcadeHoldMsBox != null)
            {
                ArcadeHoldMsBox.IsEnabled = haveIni;
                ArcadeHoldMsBox.Text = _plugin.ArcadeIniGetInt("FeedbackLength", 120)
                                              .ToString(CultureInfo.InvariantCulture);
            }
        }

        private void WriteArcadeIni(string key, string value)
        {
            if (_plugin == null) return;
            string err = _plugin.ArcadeIniSet(key, value);
            if (err != null && ArcadeTuningStatus != null)
                ArcadeTuningStatus.Text = "Could not save that: " + err + ".";
        }

        private void ArcadeMinForce_LostFocus(object sender, RoutedEventArgs e) => CommitArcadeMinForce();

        private void ArcadeMinForce_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitArcadeMinForce();
            e.Handled = true;
        }

        /// <summary>The floor under the cabinet's steering force, as a percentage
        /// of full scale.
        ///
        /// The arcade plugin computes level = strength * (MaxForce - MinForce) +
        /// MinForce, and only when the command asked for something (its own
        /// strength > 0.001 gate), so a command for nothing stays nothing and this
        /// cannot make the wheel hum at rest. That gate is why this is the right
        /// control for "I cannot feel the light forces" and Power mode is not:
        /// Power mode bends the whole curve as a square root and lifts the
        /// smallest commands roughly tenfold, which is enough to put a strong
        /// wheel into oscillation around centre.</summary>
        private void CommitArcadeMinForce()
        {
            if (_suppressEvents || _plugin == null || ArcadeMinForceBox == null) return;
            int v;
            if (!int.TryParse((ArcadeMinForceBox.Text ?? "").Trim(),
                              NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                ArcadeMinForceBox.Text = _plugin.ArcadeIniGetInt("MinForce", 0)
                                                .ToString(CultureInfo.InvariantCulture);
                return;
            }
            // Above the max there is no range left to scale into, and the whole
            // curve collapses to one value.
            int max = _plugin.ArcadeIniGetInt("MaxForce", 100);
            if (v < 0) v = 0;
            if (v > max) v = max;
            ArcadeMinForceBox.Text = v.ToString(CultureInfo.InvariantCulture);
            WriteArcadeIni("MinForce", v.ToString(CultureInfo.InvariantCulture));
        }

        private void ArcadeHoldMs_LostFocus(object sender, RoutedEventArgs e) => CommitArcadeHoldMs();

        private void ArcadeHoldMs_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitArcadeHoldMs();
            e.Handled = true;
        }

        private void CommitArcadeHoldMs()
        {
            if (_suppressEvents || _plugin == null || ArcadeHoldMsBox == null) return;
            int v;
            if (!int.TryParse((ArcadeHoldMsBox.Text ?? "").Trim(),
                              NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                // Put back what the file says rather than leaving nonsense on screen.
                ArcadeHoldMsBox.Text = _plugin.ArcadeIniGetInt("FeedbackLength", 120)
                                              .ToString(CultureInfo.InvariantCulture);
                return;
            }
            // Below one loop iteration a command cannot hold at all, so anything
            // under 16 ms is the same as 16 and reads as a broken control.
            if (v < 16) v = 16;
            if (v > 20000) v = 20000;
            ArcadeHoldMsBox.Text = v.ToString(CultureInfo.InvariantCulture);
            WriteArcadeIni("FeedbackLength", v.ToString(CultureInfo.InvariantCulture));
        }
    }
}
