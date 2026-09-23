// Click-to-type slider readouts.
//
// A slider alone cannot land on an exact number: a redline, a filter corner in
// Hz, a period in milliseconds. The readout beside it already shows the value,
// so this makes that readout the place to type it, and the slider stays the way
// to feel for one.
//
// Pairing is by name: a field "<Base>Slider" of type Slider and a field
// "<Base>Text" of type TextBox become one control. Reflection over the generated
// x:Name fields does the matching, so a new slider is covered the moment it
// follows the convention, with nothing to register.
//
// This lived inside SettingsControl and was therefore private to it, which is
// why every other window's readouts were read-only. It is its own class now so
// a window opts in with one call.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    internal static class EditableReadout
    {
        // Readouts whose display unit is not their slider's unit. Typed input is
        // divided by this before it reaches the slider, and the slider's value is
        // multiplied by it to fill the box. Keyed by the readout field name.
        // Everything absent from here maps 1:1, which is nearly all of them.
        private static readonly Dictionary<string, double> Scale =
            new Dictionary<string, double>
            {
                // Shown as a percentage of a 0 to 1 slider.
                ["AirborneReductionText"] = 100.0,
                // The sweep slider is in milliseconds and the readout is in
                // seconds, so without this a user typing 3 would get 3 ms and
                // land on the 600 ms minimum.
                ["LedSweepText"]          = 0.001,
                // Spike reduction stores raw device counts (full scale 32767)
                // and shows a share of full force, so typing "6.3" has to land
                // on 2064 LSB rather than 6 LSB.
                ["FfbSpikeLimitText"]     = SpikePercentScale,
                ["FfbSpikeThresholdText"] = SpikePercentScale,
                ["FfbPeakLimitText"]      = SpikePercentScale,
            };

        // Percent of full-scale FFB per LSB. Kept here rather than referencing
        // TrueforceSettings so the table stays a plain literal map.
        private const double SpikePercentScale = 100.0 / 32767.0;

        /// <summary>Wire every slider-and-readout pair on a window or control.
        /// Each box writes back to its OWN slider and its own range, and that
        /// slider's existing ValueChanged still formats the display, so values
        /// stay per-slider and nothing is normalised.</summary>
        internal static void WireAll(object owner)
        {
            if (owner == null) return;
            var fields = owner.GetType().GetFields(System.Reflection.BindingFlags.Instance
                                                   | System.Reflection.BindingFlags.NonPublic
                                                   | System.Reflection.BindingFlags.Public);
            var byName = new Dictionary<string, System.Reflection.FieldInfo>();
            foreach (var f in fields) byName[f.Name] = f;

            foreach (var f in fields)
            {
                if (f.FieldType != typeof(Slider) || !f.Name.EndsWith("Slider")) continue;
                string readoutName = f.Name.Substring(0, f.Name.Length - "Slider".Length) + "Text";
                if (!byName.TryGetValue(readoutName, out var rf) || rf.FieldType != typeof(TextBox)) continue;
                if (f.GetValue(owner) is Slider slider && rf.GetValue(owner) is TextBox box)
                    Attach(box, slider, readoutName);
            }
        }

        /// <summary>Wire one pair by hand, for a readout that cannot follow the
        /// naming convention.</summary>
        internal static void Attach(TextBox box, Slider slider, string readoutName)
        {
            if (box == null || slider == null) return;
            double scale = Scale.TryGetValue(readoutName ?? "", out var sc) ? sc : 1.0;

            // Flat readout look (no border or background), but focusable and
            // typeable, so nothing moves when a plain label becomes one of these.
            box.BorderThickness = new Thickness(0);
            box.Background = Brushes.Transparent;
            box.Padding = new Thickness(0);
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            box.TextAlignment = TextAlignment.Right;
            box.HorizontalContentAlignment = HorizontalAlignment.Right;
            box.Cursor = Cursors.IBeam;
            if (box.ToolTip == null) box.ToolTip = "Click to type an exact value.";

            box.GotKeyboardFocus += (s, e) =>
            {
                // Already editing. box.Tag is the "edit in progress" sentinel,
                // and running the swap again would cache the half-typed text as
                // the value Escape restores.
                if (box.Tag is string) return;
                // Swap the formatted display ("8 (2ms)", "75%") for a clean
                // editable number in display units, and select it.
                box.Tag = box.Text;
                box.Text = (slider.Value * scale).ToString("0.####", CultureInfo.InvariantCulture);
                box.Dispatcher.BeginInvoke(new Action(box.SelectAll), DispatcherPriority.Input);
            };
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)       { Commit(box, slider, scale); Keyboard.ClearFocus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Restore(box);               Keyboard.ClearFocus(); e.Handled = true; }
            };
            box.LostFocus += (s, e) => Commit(box, slider, scale);
        }

        /// <summary>True while the user is part way through typing into this box.
        /// A refresh uses it to know not to write over them.</summary>
        internal static bool IsEditing(TextBox box) => box != null && box.Tag is string;

        // Parse what was typed and write it back to the slider, clamped to that
        // slider's range and with any display scale undone. The cached display in
        // box.Tag doubles as the "edit in progress" sentinel, so Enter followed by
        // LostFocus commits once: re-parsing a rounded display like "86%" would
        // otherwise drift the value a little further on every pass.
        private static void Commit(TextBox box, Slider slider, double scale)
        {
            if (!(box.Tag is string cached)) return; // not in an edit session
            box.Tag = null;                          // end the session
            // Either decimal mark, and units after the number are ignored so a
            // readout can carry its own ("45 dB", "2.5 s"). See ReadoutNumber.
            if (Core.ReadoutNumber.TryParse(box.Text, out double typed))
            {
                double val = Clamp(typed / scale, slider);
                // IsSnapToTickEnabled governs dragging and the arrow keys, not an
                // assignment to Value, so a typed number would otherwise leave
                // the thumb between ticks and the setting on a step the slider
                // itself cannot reach.
                if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
                    val = Clamp(slider.Minimum
                              + Math.Round((val - slider.Minimum) / slider.TickFrequency)
                                * slider.TickFrequency, slider);
                if (Math.Abs(val - slider.Value) > 1e-9)
                {
                    slider.Value = val; // fires ValueChanged -> reformats + applies
                    return;
                }
            }
            box.Text = cached; // no-op or unparseable: restore the formatted display
        }

        private static double Clamp(double v, Slider slider)
            => v < slider.Minimum ? slider.Minimum
             : v > slider.Maximum ? slider.Maximum : v;

        private static void Restore(TextBox box)
        {
            if (box.Tag is string cached) box.Text = cached;
            box.Tag = null;
        }
    }
}
