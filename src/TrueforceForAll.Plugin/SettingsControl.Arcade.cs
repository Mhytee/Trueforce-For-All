// The arcade cabinet panel on the FFB tab.
//
// It is deliberately small: a handful of controls, each of which has no
// equivalent anywhere else in this plugin.
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
using System.Windows.Controls;
using TrueforceForAll.Core;

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

            // The leaderboard controls used to live here; they are on the Settings tab now, and
            // are refreshed from RefreshFromPlugin so they fill in whether or not a cabinet is
            // running. Only the one-time notice stays gated on an arcade game being active, since
            // there is nothing to disclose to somebody who has never launched one.
            MaybeShowArcadeSubmitNotice();

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
                        ? "The arcade plugin's settings file is not beside this game, so the "
                          + "linger setting below has nothing to write to."
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
            // OURS now, not the reference plugin's ini value, so it works whether or not that ini
            // was found and it does not step at centre. See MinForcePercent for why.
            int minPct = _plugin.Settings?.Arcade?.MinForcePercent ?? 0;
            if (ArcadeMinForceBox != null)
            {
                ArcadeMinForceBox.IsEnabled = true;
                ArcadeMinForceBox.Text = minPct.ToString(CultureInfo.InvariantCulture);
            }
            if (ArcadeMinForceSlider != null) ArcadeMinForceSlider.Value = minPct;
            if (ArcadeHoldMsSlider != null)
            {
                ArcadeHoldMsSlider.IsEnabled = haveIni;
                int hold = _plugin.ArcadeIniGetInt("FeedbackLength", 0);
                if (hold >= 50 && hold <= 5000) ArcadeHoldMsSlider.Value = hold;
            }
            if (ArcadeHoldMsBox != null)
            {
                ArcadeHoldMsBox.IsEnabled = haveIni;
                ArcadeHoldMsBox.Text = _plugin.ArcadeIniGetInt("FeedbackLength", 120)
                                              .ToString(CultureInfo.InvariantCulture);
            }

            // Ours, and per cabinet, so it needs no ini and is always available.
            int wavePct = CurrentArcadeWaveGainPercent();
            if (ArcadeWaveGainSlider != null) ArcadeWaveGainSlider.Value = wavePct;
            if (ArcadeWaveGainBox != null)
                ArcadeWaveGainBox.Text = wavePct.ToString(CultureInfo.InvariantCulture);

            // Ours and per cabinet, so they need no ini and are always available.
            // 0 on either one means "leave the game's own length alone", which is
            // what the panel showed before these existed.
            var tune = _plugin.ArcadeTuningPeek(_plugin.ActiveGame);
            int steerHold = Clamp(tune.SteeringHoldMs, 0, SteerHoldMaxMs);
            int vibHold   = Clamp(tune.VibrationHoldMs, 0, 500);
            if (ArcadeSteerHoldSlider != null) ArcadeSteerHoldSlider.Value = steerHold;
            if (ArcadeSteerHoldBox != null)
                ArcadeSteerHoldBox.Text = steerHold.ToString(CultureInfo.InvariantCulture);
            if (ArcadeVibHoldSlider != null) ArcadeVibHoldSlider.Value = vibHold;
            if (ArcadeVibHoldBox != null)
                ArcadeVibHoldBox.Text = vibHold.ToString(CultureInfo.InvariantCulture);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The top of the steering hold slider.
        ///
        /// Raised from 5000 on 2026-09-07 because the measurement outgrew it: this
        /// game leaves gaps of 5017 ms and 6067 ms with no steering command, so the
        /// old stop could not reach the case it was meant to cover, and "raise it
        /// until the wheel stops cutting out" was advice the control could not
        /// take. 10000 clears the worst gap seen with room to spare, and it is also
        /// the experiment that separates the two readings of this protocol: if the
        /// cabinet latches its force until replaced, a hold this long should feel
        /// right and never stuck; if the game means the wheel to go light while
        /// sliding, it should feel wrong in exactly those corners.</summary>
        private const int SteerHoldMaxMs = 10000;

        private void WriteArcadeIni(string key, string value)
        {
            if (_plugin == null) return;
            string err = _plugin.ArcadeIniSet(key, value);
            if (err != null && ArcadeTuningStatus != null)
                ArcadeTuningStatus.Text = "Could not save that: " + err + ".";
        }

        /// <summary>The slider is the primary control and the box beside it is the exact value, the
        /// same pairing every other tuning row uses. The slider commits as it moves so the change
        /// can be felt while dragging, which is the whole reason it is a slider.</summary>
        private void ArcadeMinForceSlider_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            var arc = _plugin.Settings?.Arcade;
            if (arc == null) return;
            int v = (int)System.Math.Round(e.NewValue);
            if (v < 0) v = 0;
            if (v > 95) v = 95;
            if (arc.MinForcePercent == v) return;
            arc.MinForcePercent = v;
            if (ArcadeMinForceBox != null)
            {
                _suppressEvents = true;
                try { ArcadeMinForceBox.Text = v.ToString(CultureInfo.InvariantCulture); }
                finally { _suppressEvents = false; }
            }
            try { _plugin.PersistSettings(); } catch { }
            _plugin.ApplyArcadeMinForce();
        }

        private void ArcadeHoldMsSlider_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || ArcadeHoldMsBox == null) return;
            // Rounded to 50 ms: the underlying value is the cabinet's own and nobody is choosing
            // between 317 and 320 milliseconds of linger.
            int v = (int)(System.Math.Round(e.NewValue / 50.0) * 50);
            if (v < 50) v = 50;
            if (v > 5000) v = 5000;
            _suppressEvents = true;
            try { ArcadeHoldMsBox.Text = v.ToString(CultureInfo.InvariantCulture); }
            finally { _suppressEvents = false; }
            WriteArcadeIni("FeedbackLength", v.ToString(CultureInfo.InvariantCulture));
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
            var arc = _plugin.Settings?.Arcade;
            if (arc == null) return;
            int v;
            if (!int.TryParse((ArcadeMinForceBox.Text ?? "").Trim(),
                              NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                ArcadeMinForceBox.Text = arc.MinForcePercent.ToString(CultureInfo.InvariantCulture);
                return;
            }
            // Past about 95 there is no range left above the floor and the whole curve collapses
            // onto one value, so the wheel would push the same amount whatever the game asked for.
            if (v < 0) v = 0;
            if (v > 95) v = 95;
            ArcadeMinForceBox.Text = v.ToString(CultureInfo.InvariantCulture);
            if (ArcadeMinForceSlider != null && (int)ArcadeMinForceSlider.Value != v)
            {
                _suppressEvents = true;
                try { ArcadeMinForceSlider.Value = v; } finally { _suppressEvents = false; }
            }
            arc.MinForcePercent = v;
            try { _plugin.PersistSettings(); } catch { }
            _plugin.ApplyArcadeMinForce();
        }

        /// <summary>How loud this cabinet's waveform effects play: the sines,
        /// triangles and sawtooths it commands as buzz and rumble.
        ///
        /// It earns a control of its own because the buzz and the steering force
        /// are two separate commands that want two separate amounts. Turning the
        /// wheel up until the steering feels right turns the buzz up with it, and
        /// on a strong wheel that lands somewhere between distracting and harsh.
        /// Nothing else on this tab can lower one without lowering the other.
        ///
        /// Per cabinet, because how loud a game's waveforms arrive is a property
        /// of that game's protocol rather than of this wheel.</summary>
        private void ArcadeWaveGainSlider_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            // Stepped to 5 percent: nobody is choosing between 82 and 83.
            ApplyArcadeWaveGain((int)(System.Math.Round(e.NewValue / 5.0) * 5), false);
        }

        private void ArcadeWaveGain_LostFocus(object sender, RoutedEventArgs e) => CommitArcadeWaveGain();

        private void ArcadeWaveGain_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitArcadeWaveGain();
            e.Handled = true;
        }

        private void CommitArcadeWaveGain()
        {
            if (_suppressEvents || _plugin == null || ArcadeWaveGainBox == null) return;
            int v;
            if (!int.TryParse((ArcadeWaveGainBox.Text ?? "").Trim(),
                              NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                // Put back the stored value rather than leaving nonsense on screen.
                ArcadeWaveGainBox.Text = CurrentArcadeWaveGainPercent()
                                             .ToString(CultureInfo.InvariantCulture);
                return;
            }
            ApplyArcadeWaveGain(v, true);
        }

        /// <summary>The running cabinet's stored waveform scale as a percentage.
        /// Reads without creating an entry, so merely opening this tab does not
        /// seed a saved tuning for every game the user has ever launched.</summary>
        private int CurrentArcadeWaveGainPercent()
        {
            if (_plugin == null) return 100;
            double g = _plugin.ArcadeTuningPeek(_plugin.ActiveGame).WaveformGain;
            if (double.IsNaN(g)) return 100;
            int v = (int)System.Math.Round(g * 100.0);
            return v < 0 ? 0 : v > 200 ? 200 : v;
        }

        /// <summary>Stores the scale against the cabinet that is running and tells
        /// the pump to re-read it, so the change is felt while the slider is still
        /// being dragged rather than at the next launch.</summary>
        private void ApplyArcadeWaveGain(int percent, bool echoToSlider)
        {
            if (percent < 0) percent = 0;
            if (percent > 200) percent = 200;
            var t = _plugin.ArcadeTuningFor(_plugin.ActiveGame);
            if (t == null) return;

            _suppressEvents = true;
            try
            {
                if (ArcadeWaveGainBox != null)
                    ArcadeWaveGainBox.Text = percent.ToString(CultureInfo.InvariantCulture);
                if (echoToSlider && ArcadeWaveGainSlider != null
                    && (int)ArcadeWaveGainSlider.Value != percent)
                    ArcadeWaveGainSlider.Value = percent;
            }
            finally { _suppressEvents = false; }

            double g = percent / 100.0;
            if (System.Math.Abs(t.WaveformGain - g) < 1e-9) return;
            t.WaveformGain = g;
            try { _plugin.PersistSettings(); } catch { }
            _plugin.InvalidateArcadeWaveformGain();
        }

        // The per-effect holds.
        //
        // They exist because the cabinet's one duration setting reaches the wrong
        // set of effects. Measured on the rig 2026-09-06: the steering force
        // arrives stamped with FeedbackLength, the spring does too, and a sine
        // arrives with its own period instead, 6 ms and 49 ms. So raising that one
        // number to stop the wheel going slack during a burst of buzz also
        // lengthens the spring, and never reaches the buzz at all. Splitting it
        // gives each of the two things its own control and leaves the spring on
        // the game's own setting.
        //
        // 0 on either means "use the length the game published", so a user who
        // never touches these gets exactly the behaviour that shipped.

        private void ArcadeSteerHoldSlider_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            ApplyArcadeHold(true, (int)(System.Math.Round(e.NewValue / 50.0) * 50), false);
        }

        private void ArcadeVibHoldSlider_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            ApplyArcadeHold(false, (int)(System.Math.Round(e.NewValue / 10.0) * 10), false);
        }

        private void ArcadeSteerHold_LostFocus(object sender, RoutedEventArgs e) => CommitArcadeHold(true);

        private void ArcadeSteerHold_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitArcadeHold(true);
            e.Handled = true;
        }

        private void ArcadeVibHold_LostFocus(object sender, RoutedEventArgs e) => CommitArcadeHold(false);

        private void ArcadeVibHold_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitArcadeHold(false);
            e.Handled = true;
        }

        private void CommitArcadeHold(bool steering)
        {
            if (_suppressEvents || _plugin == null) return;
            var box = steering ? ArcadeSteerHoldBox : ArcadeVibHoldBox;
            if (box == null) return;
            var tune = _plugin.ArcadeTuningPeek(_plugin.ActiveGame);
            int v;
            if (!int.TryParse((box.Text ?? "").Trim(),
                              NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                // Put back the stored value rather than leaving nonsense on screen.
                box.Text = (steering ? tune.SteeringHoldMs : tune.VibrationHoldMs)
                               .ToString(CultureInfo.InvariantCulture);
                return;
            }
            ApplyArcadeHold(steering, v, true);
        }

        /// <summary>Stores one hold against the cabinet that is running and pushes
        /// it at the source, so the change is felt while the slider is still
        /// moving rather than at the next launch.</summary>
        private void ApplyArcadeHold(bool steering, int ms, bool echoToSlider)
        {
            int max = steering ? SteerHoldMaxMs : 500;
            ms = Clamp(ms, 0, max);
            var t = _plugin.ArcadeTuningFor(_plugin.ActiveGame);
            if (t == null) return;

            var box = steering ? ArcadeSteerHoldBox : ArcadeVibHoldBox;
            var slider = steering ? ArcadeSteerHoldSlider : ArcadeVibHoldSlider;
            _suppressEvents = true;
            try
            {
                if (box != null) box.Text = ms.ToString(CultureInfo.InvariantCulture);
                if (echoToSlider && slider != null && (int)slider.Value != ms) slider.Value = ms;
            }
            finally { _suppressEvents = false; }

            if ((steering ? t.SteeringHoldMs : t.VibrationHoldMs) == ms) return;
            if (steering) t.SteeringHoldMs = ms; else t.VibrationHoldMs = ms;
            try { _plugin.PersistSettings(); } catch { }
            _plugin.ApplyArcadeHolds();
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

        // ---- Initial D 8 leaderboards ----
        //
        // Only ID8 has boards we know how to fill, so these controls appear with the arcade panel
        // and simply do nothing for other cabinets. They are cheap to leave visible: the service
        // itself no-ops unless the running game is the one the memory map was built for.

        /// <summary>Show the one-time "your times go on the leaderboard" notice.
        ///
        /// Dispatched rather than shown inline: this is reached from RefreshFromPlugin with
        /// _suppressEvents set, and a modal there would block the refresh and re-enter the panel
        /// while it is half filled in. Posting it means the panel finishes drawing first and the
        /// dialog opens over a settled UI.
        ///
        /// The settings panel is the right place for it rather than the moment the game starts:
        /// ID8 runs full screen through TeknoParrot, and a modal that appears behind a full-screen
        /// arcade game is a notice nobody reads.</summary>
        /// <summary>Latched when the notice is POSTED, not when it is answered. That distinction
        /// is the whole fix: see MaybeShowArcadeSubmitNotice.</summary>
        private static bool _arcadeNoticePosted;

        private void MaybeShowArcadeSubmitNotice()
        {
            var a = _plugin?.Settings?.Arcade;
            if (a == null || a.Id8SubmitNoticeShown || !a.Id8LeaderboardsEnabled) return;

            // Id8SubmitNoticeShown is only written once the dialog has been ANSWERED, and this is
            // reached from every panel refresh. Without a latch taken synchronously right here,
            // each refresh between the post and the answer queues another dialog and the notice
            // appears several times over. Static, so a second control instance cannot reopen it.
            if (_arcadeNoticePosted) return;
            _arcadeNoticePosted = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ArcadeSubmitNotice.EnsureShown(Window.GetWindow(this), _plugin);
                    bool prior = _suppressEvents;
                    _suppressEvents = true;
                    try { RefreshArcadeLeaderboardControls(); }
                    finally { _suppressEvents = prior; }
                }
                catch { /* a notice failing to show must not break the settings panel */ }
            }));
        }

        private void RefreshArcadeLeaderboardControls()
        {
            var a = _plugin?.Settings?.Arcade;
            if (a == null) return;

            if (ArcadeLeaderboardsCheck != null) ArcadeLeaderboardsCheck.IsChecked = a.Id8LeaderboardsEnabled;
            if (ArcadeSubmitTimesCheck != null) ArcadeSubmitTimesCheck.IsChecked = a.Id8SubmitTimesEnabled;
            // Always available now. It used to grey out unless a board was set to All or
            // TeknoParrot, because a ladder needs a field with rungs in it. Climb mode no longer
            // takes the source from the dropdown at all: it ranks against the merged field either
            // way, so there is nothing left for the source to make it incompatible with.
            if (ArcadeLadderClimbCheck != null) ArcadeLadderClimbCheck.IsChecked = a.Id8LadderClimbEnabled;
            if (ArcadeLeaderboardOptions != null)
                ArcadeLeaderboardOptions.IsEnabled = a.Id8LeaderboardsEnabled;

            SelectByTag(ArcadeOnlineSourceCombo, a.Id8OnlineBoardSource.ToString());

            // The shop source is not a choice while the ladder is on. Climb mode ranks against the
            // merged field whatever the dropdown says, so leaving it readable at Trueforce For All
            // was the panel stating something the board was not doing.
            //
            // Greyed, and reading LADDER rather than All. All would be true and would still leave
            // somebody wondering why they cannot change it; Ladder answers that in the one place
            // they are looking. The label greys with the box rather than the box alone.
            bool ladder = a.Id8LadderClimbEnabled;
            if (ArcadeShopSourceLadderItem != null)
                ArcadeShopSourceLadderItem.Visibility = ladder ? Visibility.Visible : Visibility.Collapsed;

            if (ladder && ArcadeShopSourceLadderItem != null && ArcadeShopSourceCombo != null)
                ArcadeShopSourceCombo.SelectedItem = ArcadeShopSourceLadderItem;
            else
                SelectByTag(ArcadeShopSourceCombo, a.Id8ShopBoardSource.ToString());

            if (ArcadeShopSourceRow != null)
            {
                ArcadeShopSourceRow.IsEnabled = !ladder;
                ArcadeShopSourceRow.ToolTip = ladder
                    ? "Ladder climb ranks you against everyone, so it takes the shop board over."
                    : null;
            }
        }

        /// <summary>What the shop board was set to before the ladder took it over, so unticking
        /// gives it back rather than leaving them on All. Not persisted: across a restart the
        /// setting simply reads All, which is at least a visible state they can change.</summary>
        private Id8BoardSource? _shopSourceBeforeLadder;

        private static void SelectByTag(System.Windows.Controls.ComboBox combo, string tag)
        {
            if (combo == null) return;
            foreach (object o in combo.Items)
            {
                var item = o as System.Windows.Controls.ComboBoxItem;
                if (item != null && string.Equals((string)item.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void ArcadeLeaderboards_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Arcade == null) return;
            bool on = ArcadeLeaderboardsCheck?.IsChecked == true;
            _plugin.Settings.Arcade.Id8LeaderboardsEnabled = on;
            if (ArcadeLeaderboardOptions != null) ArcadeLeaderboardOptions.IsEnabled = on;

            // Turning it off puts the game's own board back rather than leaving our rows sitting
            // there until a restart. The pre-write snapshot is what makes that possible.
            if (!on) _plugin.RestoreArcadeBoards();

            _plugin.PersistSettings();
        }

        private void ArcadeLadderClimb_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Arcade == null) return;
            var a = _plugin.Settings.Arcade;
            bool on = ArcadeLadderClimbCheck?.IsChecked == true;
            a.Id8LadderClimbEnabled = on;

            if (on)
            {
                // The ladder TAKES the shop board. It ranks against the merged field regardless of
                // this setting, so the setting has to say so instead of quietly meaning nothing.
                if (a.Id8ShopBoardSource != Id8BoardSource.Merged)
                {
                    _shopSourceBeforeLadder = a.Id8ShopBoardSource;
                    a.Id8ShopBoardSource = Id8BoardSource.Merged;
                }

                // And with the shop board showing everyone, an online board showing everyone too
                // would be two views of the same field. Trueforce For All is the more useful thing
                // to put beside a ladder: the people you can actually race.
                //
                // Only from the shipped default, never over a choice. If they have already picked
                // TeknoParrot or Local there, they meant it, and a toggle that quietly rewrites a
                // setting somebody set is worse than one that leaves a redundant view.
                if (a.Id8OnlineBoardSource == Id8BoardSource.Merged)
                    a.Id8OnlineBoardSource = Id8BoardSource.Community;
            }
            else if (_shopSourceBeforeLadder.HasValue)
            {
                a.Id8ShopBoardSource = _shopSourceBeforeLadder.Value;
                _shopSourceBeforeLadder = null;
            }

            bool priorSuppress = _suppressEvents;
            _suppressEvents = true;
            try { RefreshArcadeLeaderboardControls(); } finally { _suppressEvents = priorSuppress; }

            _plugin.PersistSettings();

            // Same reason the source combos refill: the boards are written once per attach, so a
            // change made mid-session would otherwise not show until the player left the course.
            _plugin.RefillArcadeBoards();
        }

        private void ArcadeSubmitTimes_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Arcade == null) return;
            bool on = ArcadeSubmitTimesCheck?.IsChecked == true;

            // Ticking it on is the same disclosure the first-run notice makes, so treat it as
            // having been made: the alternative is a notice that appears after the user has
            // already said yes, which reads as a bug.
            if (on) _plugin.Settings.Arcade.Id8SubmitNoticeShown = true;

            _plugin.Settings.Arcade.Id8SubmitTimesEnabled = on;
            _plugin.PersistSettings();
        }

        private void ArcadeBoardSource_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Arcade == null) return;

            var a = _plugin.Settings.Arcade;
            a.Id8OnlineBoardSource = TagToSource(ArcadeOnlineSourceCombo, a.Id8OnlineBoardSource);
            a.Id8ShopBoardSource = TagToSource(ArcadeShopSourceCombo, a.Id8ShopBoardSource);
            _plugin.PersistSettings();

            // Whether ladder climb can do anything depends on the source just chosen.
            bool prior = _suppressEvents;
            _suppressEvents = true;
            try { RefreshArcadeLeaderboardControls(); } finally { _suppressEvents = prior; }

            // The boards are only rewritten when the course changes, so a source picked mid-session
            // would otherwise not show until the player left the course and came back.
            _plugin.RefillArcadeBoards();
        }

        private static Id8BoardSource TagToSource(System.Windows.Controls.ComboBox combo, Id8BoardSource fallback)
        {
            var item = combo?.SelectedItem as System.Windows.Controls.ComboBoxItem;
            string tag = item?.Tag as string;
            Id8BoardSource parsed;
            return Enum.TryParse(tag, true, out parsed) ? parsed : fallback;
        }
    }
}
