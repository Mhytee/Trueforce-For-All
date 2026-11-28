// The effect Test buttons. A press plays the effect's test (a recorded clip
// through the real effect, or the synthetic shape for effects without one;
// see TrueforcePlugin.TestEffect) and the button becomes "Stop" while a fill
// runs left to right across it for the test's length. Pressing it again
// stops the test. Pressing another effect's Test stops this one first: one
// test at a time, so two presses never mix into a sound neither effect makes.
//
// The fill is a WPF animation, not a timer-driven width, so it stays smooth
// whatever the panel's status tick is doing.
//
// While no test can play (no wheel, or Mode is not Normal) the buttons grey
// out and their tooltip says why. One tooltip per button rather than a line
// of text beside each, which would repeat thirteen times (owner, 2026-11-24).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TrueforceForAll.Plugin.Effects;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        private Button _playingTestButton;
        private BindingBase _playingTestLabel;
        private DispatcherTimer _testEndTimer;
        // Last reason applied to the buttons; "\0" forces the first pass.
        private string _testBlockedShown = "\0";
        private Button[] _effectTestButtons;

        private Button[] EffectTestButtons => _effectTestButtons ?? (_effectTestButtons = new[]
        {
            EngineTestButton, BumpsTestButton, TractionTestButton, AxleSlipTestButton,
            KerbThumpTestButton, LockupJudderTestButton, ShiftTestButton, ImplementThudTestButton,
            AbsTestButton, PitLimiterTestButton, DrsTestButton, CollisionTestButton, RevLimiterTestButton,
        });

        /// <summary>A Test button's click: play, or stop when this button's
        /// test is the one playing.</summary>
        private void RunEffectTest(object sender, TelemetryEffect effect)
        {
            if (_plugin == null || effect == null) return;
            var btn = sender as Button;
            if (btn != null && ReferenceEquals(btn, _playingTestButton))
            {
                _plugin.StopEffectTest(effect);
                EndTestButtonPlayback();
                return;
            }
            EndTestButtonPlayback();
            int ms = _plugin.TestEffect(effect);
            if (ms > 0 && btn != null) BeginTestButtonPlayback(btn, ms);
        }

        private void BeginTestButtonPlayback(Button btn, int ms)
        {
            _playingTestButton = btn;

            // Hold the width the button had as "Test", so the header row does
            // not jump when the label changes (a longer "Stop" still grows it).
            btn.MinWidth = btn.ActualWidth;
            _playingTestLabel = BindingOperations.GetBindingBase(btn, ContentControl.ContentProperty);
            btn.SetBinding(ContentControl.ContentProperty,
                new Binding("[Effects_TestStop]") { Source = Loc.Instance, Mode = BindingMode.OneWay });
            btn.ToolTip = Loc.T("Effects_TestStop_Tip");

            btn.ApplyTemplate();
            if (btn.Template?.FindName("PART_Progress", btn) is FrameworkElement fill)
            {
                var scale = new ScaleTransform(0, 1);
                fill.RenderTransform = scale;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
            }

            if (_testEndTimer == null)
            {
                _testEndTimer = new DispatcherTimer(DispatcherPriority.Normal);
                _testEndTimer.Tick += (s, e) => EndTestButtonPlayback();
            }
            _testEndTimer.Stop();
            _testEndTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _testEndTimer.Start();
        }

        private void EndTestButtonPlayback()
        {
            _testEndTimer?.Stop();
            var btn = _playingTestButton;
            _playingTestButton = null;
            if (btn == null) return;

            btn.ClearValue(FrameworkElement.MinWidthProperty);
            if (_playingTestLabel != null) btn.SetBinding(ContentControl.ContentProperty, _playingTestLabel);
            else btn.Content = Loc.T("Common_Test");
            _playingTestLabel = null;
            btn.ToolTip = null;
            if (btn.Template?.FindName("PART_Progress", btn) is FrameworkElement fill
                && fill.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.ScaleX = 0;
            }
            // Re-apply the greyed tooltip if the wheel went away mid-test.
            _testBlockedShown = "\0";
        }

        /// <summary>Status tick: grey the Test buttons, with the reason as
        /// their tooltip, while no test can play. Writes only on a change.</summary>
        private void RefreshEffectTestButtons()
        {
            string reason = _plugin?.EffectTestBlockedReason;
            if (reason == _testBlockedShown) return;
            _testBlockedShown = reason;

            if (reason != null && _playingTestButton != null) EndTestButtonPlayback();
            _testBlockedShown = reason;
            foreach (var b in EffectTestButtons)
            {
                if (b == null) continue;
                b.IsEnabled = reason == null;
                if (!ReferenceEquals(b, _playingTestButton)) b.ToolTip = reason;
            }
        }
    }
}
