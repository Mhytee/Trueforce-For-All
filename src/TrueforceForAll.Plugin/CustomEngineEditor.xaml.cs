// Modal editor for a single CustomEngineDef. Opens via SettingsControl
// when the user picks "Custom..." (new) from the engine dropdown, or via
// the Edit button on the Custom Engines tab of ManagePresetsDialog.
//
// On Save the dialog writes back to the CustomEngineDef passed in; the
// caller is responsible for adding new entries to TrueforceSettings.CustomEngines
// and persisting. Cancel closes without mutating the input.
//
// UI flow:
//   - Top: Name textbox, Electric checkbox.
//   - When Electric: only the Behavior dropdown is shown.
//   - When Combustion: Shape dropdown + Count slider + Pattern textbox.
//     Picking a shape or moving the count slider regenerates the pattern
//     string from FiringPatternDb.BuildPatternString; the user can also
//     hand-edit the textbox for expert tuning.

using System;
using System.Windows;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    public partial class CustomEngineEditor : Window
    {
        // The entry being edited. Set in Init; mutated on Save. Cancel leaves
        // it untouched so callers can rely on Save returning true => mutated.
        private CustomEngineDef _target;
        private bool _suppress;   // skip event handlers during programmatic UI updates
        private string _lastGeneratedPattern;   // last auto-generated pattern; lets us detect hand-edits

        public CustomEngineEditor()
        {
            InitializeComponent();
            // Default the shape dropdown to Even-fire so the count slider
            // starts in a sensible state; will be overridden in Init.
            _suppress = true;
            ShapeCombo.SelectedIndex = 0;
            ElectricBehaviorCombo.SelectedIndex = 0;
            _suppress = false;
        }

        /// <summary>Initialize the dialog with an existing entry (edit) or
        /// a new one (create). For create, callers pass a fresh
        /// <see cref="CustomEngineDef"/> with Id pre-assigned; the dialog
        /// fills in the rest on Save. The pattern textbox seeds from
        /// target.Pattern when non-empty, else defaults to even-fire 4-cyl.</summary>
        public void Init(CustomEngineDef target, string windowTitle)
        {
            _target = target ?? new CustomEngineDef { Id = Guid.NewGuid().ToString("N") };
            Title = string.IsNullOrEmpty(windowTitle) ? "Custom engine" : windowTitle;

            _suppress = true;
            try
            {
                NameTextBox.Text = _target.Name ?? "";
                ElectricCheck.IsChecked = _target.IsElectric;
                ElectricBehaviorCombo.SelectedIndex =
                    _target.ElectricMode == ElectricCarMode.Silent ? 1 : 0;

                // Seed combustion fields. Pattern defaults to even-fire 4-cyl
                // for new entries so the user has something concrete to edit
                // rather than an empty textbox.
                bool hasPattern = !string.IsNullOrWhiteSpace(_target.Pattern);
                string seeded = hasPattern
                    ? _target.Pattern
                    : FiringPatternDb.BuildPatternString(FiringPatternDb.CustomEngineShape.EvenFire, 4);
                PatternTextBox.Text = seeded;
                // Track the last auto-generated value so we can tell whether the
                // user has hand-edited the pattern. For an existing entry the saved
                // pattern is treated as user-owned (null marker) so a stray shape /
                // count change never silently overwrites it.
                _lastGeneratedPattern = hasPattern ? null : seeded;
                // Reflect the saved pattern's shape instead of always "Even-fire"
                // (so the dropdown isn't misleading and an explicit Regenerate
                // doesn't silently switch shape). Falls back to 0 for a
                // hand-edited pattern that matches no generated shape.
                ShapeCombo.SelectedIndex = hasPattern ? InferShapeIndex(seeded) : 0;
                CountSlider.Value = InferPulseCount(PatternTextBox.Text, fallback: 4);
                CountText.Text = ((int)CountSlider.Value).ToString();
                ApplyShapeConstraints();
                UpdateElectricCombustionVisibility();
            }
            finally { _suppress = false; }
        }

        /// <summary>True when the user clicked Save. Result also written to
        /// the <see cref="DialogResult"/> property so callers can use the
        /// standard <see cref="Window.ShowDialog"/> bool? convention.</summary>
        public bool Saved { get; private set; }

        private FiringPatternDb.CustomEngineShape SelectedShape()
        {
            switch (ShapeCombo.SelectedIndex)
            {
                case 0:  return FiringPatternDb.CustomEngineShape.EvenFire;
                case 1:  return FiringPatternDb.CustomEngineShape.V8CrossPlane;
                case 2:  return FiringPatternDb.CustomEngineShape.V6OddFire;
                case 3:  return FiringPatternDb.CustomEngineShape.VTwin90;
                case 4:  return FiringPatternDb.CustomEngineShape.VTwin60;
                case 5:  return FiringPatternDb.CustomEngineShape.VTwin45;
                case 6:  return FiringPatternDb.CustomEngineShape.Inline4CrossPlane;
                case 7:  return FiringPatternDb.CustomEngineShape.Rotary;
                case 8:  return FiringPatternDb.CustomEngineShape.Twin180;
                case 9:  return FiringPatternDb.CustomEngineShape.V4TwinPulse;
                case 10: return FiringPatternDb.CustomEngineShape.Boxer4Rumble;
                default: return FiringPatternDb.CustomEngineShape.EvenFire;
            }
        }

        private const int ShapeComboMaxIndex = 10;

        // Inverse of SelectedShape: the combo index that selects a given shape.
        private static FiringPatternDb.CustomEngineShape ShapeForIndex(int idx)
        {
            switch (idx)
            {
                case 1:  return FiringPatternDb.CustomEngineShape.V8CrossPlane;
                case 2:  return FiringPatternDb.CustomEngineShape.V6OddFire;
                case 3:  return FiringPatternDb.CustomEngineShape.VTwin90;
                case 4:  return FiringPatternDb.CustomEngineShape.VTwin60;
                case 5:  return FiringPatternDb.CustomEngineShape.VTwin45;
                case 6:  return FiringPatternDb.CustomEngineShape.Inline4CrossPlane;
                case 7:  return FiringPatternDb.CustomEngineShape.Rotary;
                case 8:  return FiringPatternDb.CustomEngineShape.Twin180;
                case 9:  return FiringPatternDb.CustomEngineShape.V4TwinPulse;
                case 10: return FiringPatternDb.CustomEngineShape.Boxer4Rumble;
                default: return FiringPatternDb.CustomEngineShape.EvenFire;
            }
        }

        // Best-effort: figure out which shape generated a saved pattern so the
        // dropdown reflects it instead of always showing "Even-fire". Matches the
        // saved string against each shape's generated pattern (at its fixed count,
        // or the saved count for variable shapes). Returns 0 (Even-fire) when
        // nothing matches, i.e. a hand-edited pattern.
        private static int InferShapeIndex(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return 0;
            string target = pattern.Trim();
            int count = InferPulseCount(pattern, 4);
            for (int idx = 0; idx <= ShapeComboMaxIndex; idx++)
            {
                var shape = ShapeForIndex(idx);
                int n = FiringPatternDb.FixedCountForShape(shape) ?? count;
                string gen;
                try { gen = FiringPatternDb.BuildPatternString(shape, n); }
                catch { continue; }
                if (string.Equals(gen?.Trim(), target, StringComparison.Ordinal))
                    return idx;
            }
            return 0;
        }

        // Pull a sensible starting count from the saved pattern: the number
        // of comma-separated positions. Used only to seed the slider when
        // opening an existing entry; the actual pattern stays in the textbox.
        private static int InferPulseCount(string pattern, int fallback)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return fallback;
            int commas = 0;
            foreach (var ch in pattern) if (ch == ',') commas++;
            int n = commas + 1;
            if (n < 1) return fallback;
            if (n > 16) return 16;
            return n;
        }

        private void Electric_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            UpdateElectricCombustionVisibility();
        }

        // Toggle which rows are visible based on the Electric checkbox so the
        // user never sees a pattern textbox for an EV (or a behavior dropdown
        // for a combustion entry).
        private void UpdateElectricCombustionVisibility()
        {
            bool isElectric = ElectricCheck.IsChecked == true;
            ShapeRow.Visibility            = isElectric ? Visibility.Collapsed : Visibility.Visible;
            CountRow.Visibility            = isElectric ? Visibility.Collapsed : Visibility.Visible;
            PatternRow.Visibility          = isElectric ? Visibility.Collapsed : Visibility.Visible;
            PatternHelpRow.Visibility      = isElectric ? Visibility.Collapsed : Visibility.Visible;
            ElectricBehaviorRow.Visibility = isElectric ? Visibility.Visible   : Visibility.Collapsed;
        }

        private void Shape_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            ApplyShapeConstraints();
            // After clamping, regenerate the pattern from (shape, count).
            RegeneratePattern();
        }

        // Clamp the count slider's range / value to the selected shape and
        // flip the label to "Rotors:" when Rotary is picked. Fixed-count
        // shapes lock the slider; user-configurable shapes (Even-fire, Rotary)
        // re-enable it with the appropriate range.
        private void ApplyShapeConstraints()
        {
            var shape = SelectedShape();
            var (min, max) = FiringPatternDb.CountRangeForShape(shape);
            int? fixedCount = FiringPatternDb.FixedCountForShape(shape);

            CountLabel.Text   = shape == FiringPatternDb.CustomEngineShape.Rotary ? "Rotors:" : "Cylinders:";
            CountSlider.Minimum = min;
            CountSlider.Maximum = max;
            CountSlider.IsEnabled = fixedCount == null;
            if (fixedCount is int n)
            {
                _suppress = true;
                try
                {
                    CountSlider.Value = n;
                    CountText.Text = n.ToString();
                }
                finally { _suppress = false; }
            }
            else
            {
                // Snap current slider value into the new range if it fell out.
                int v = (int)Math.Round(CountSlider.Value);
                if (v < min) v = min;
                if (v > max) v = max;
                _suppress = true;
                try
                {
                    CountSlider.Value = v;
                    CountText.Text = v.ToString();
                }
                finally { _suppress = false; }
            }
        }

        private void Count_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppress) return;
            int v = (int)Math.Round(e.NewValue);
            CountText.Text = v.ToString();
            RegeneratePattern();
        }

        // Rebuild the pattern textbox from (shape, count). By default this
        // refuses to clobber a hand-edited pattern: it only overwrites when the
        // box still holds the exact string we last generated (or is blank). The
        // user's expert edits survive a stray shape / count change and are only
        // replaced when they explicitly click "Regenerate" (force: true).
        private void RegeneratePattern(bool force = false)
        {
            if (!force && PatternIsUserEdited()) return;
            string s = FiringPatternDb.BuildPatternString(SelectedShape(), (int)Math.Round(CountSlider.Value));
            _suppress = true;
            try { PatternTextBox.Text = s; }
            finally { _suppress = false; }
            _lastGeneratedPattern = s;
        }

        // True when the pattern box differs from the last value we generated,
        // i.e. the user has typed their own pattern. A blank box counts as
        // not-edited so clearing it re-enables auto-generation.
        private bool PatternIsUserEdited()
        {
            string cur = (PatternTextBox.Text ?? "").Trim();
            if (cur.Length == 0) return false;
            return !string.Equals(cur, (_lastGeneratedPattern ?? "").Trim(), StringComparison.Ordinal);
        }

        // Expert escape hatch: rebuild the pattern from the current shape and
        // count even when the box was hand-edited.
        private void RegeneratePattern_Click(object sender, RoutedEventArgs e)
        {
            RegeneratePattern(force: true);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = (NameTextBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                TrueforceDialog.Show(this, "Custom engine",
                    "Please enter a name for this engine.", DialogKind.Info);
                NameTextBox.Focus();
                return;
            }

            bool isElectric = ElectricCheck.IsChecked == true;
            string pattern = (PatternTextBox.Text ?? "").Trim();

            // Validate combustion pattern parses. Don't gate on this for
            // electric entries (the textbox is hidden / ignored in that mode).
            if (!isElectric)
            {
                var parsed = FiringPatternDb.ParseCustom(pattern);
                if (parsed == null || parsed.Pulses < 1)
                {
                    TrueforceDialog.Show(this, "Custom engine",
                        "The firing pattern couldn't be parsed. Expected format: comma-separated numbers in [0, 1) "
                        + "with optional ':amplitude' per entry (e.g. 0, 0.25:1.0, 0.5, 0.75:0.85).",
                        DialogKind.Warning);
                    PatternTextBox.Focus();
                    return;
                }
            }

            _target.Name = name;
            _target.IsElectric = isElectric;
            _target.ElectricMode = ElectricBehaviorCombo.SelectedIndex == 1
                ? ElectricCarMode.Silent
                : ElectricCarMode.MutedHum;
            _target.Pattern = isElectric ? "" : pattern;
            if (string.IsNullOrEmpty(_target.Id)) _target.Id = Guid.NewGuid().ToString("N");

            Saved = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Saved = false;
            DialogResult = false;
            Close();
        }
    }
}
