// Simple mode on the Effects tab: one switch at the top of the tab that hides
// the fine tuning (waveforms, pulse rates, envelopes, the ducking
// coordinators) and leaves each effect's on/off, gain and frequency. It hides
// fields and nothing else: a hidden setting keeps its value and keeps
// applying, so switching modes never changes how the wheel feels.
//
// What hides is marked in the XAML, not listed here: any element on the tab
// with Tag="Advanced" collapses in Simple mode. Nothing else may set a tagged
// element's Visibility. A row that other code also shows and hides (the
// per-game panels, the Noise-only filter rows) sits INSIDE a tagged wrapper,
// so the two gates stay independent and neither undoes the other.
//
// Global (Settings.SimpleMode), Portable in backup. Fresh installs start in
// Simple (TrueforcePlugin.Init); an existing install stays in Advanced until
// the user switches.

using System.Collections.Generic;
using System.Windows;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        private const string AdvancedTag = "Advanced";

        // Collected once: the tagged set is fixed at XAML load.
        private List<FrameworkElement> _advancedElements;
        // The mode last applied, so the RefreshFromPlugin timer only touches
        // the tree when the setting moved (a backup import, say).
        private bool? _simpleModeShown;

        private void ApplySimpleMode()
        {
            bool simple = _plugin?.Settings?.SimpleMode == true;
            if (_advancedElements == null)
            {
                _advancedElements = new List<FrameworkElement>();
                if (EffectsTab != null) CollectAdvanced(EffectsTab, _advancedElements);
            }
            var vis = simple ? Visibility.Collapsed : Visibility.Visible;
            foreach (var fe in _advancedElements) fe.Visibility = vis;

            var prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                if (EffectsModeSimpleButton != null) EffectsModeSimpleButton.IsChecked = simple;
                if (EffectsModeAdvancedButton != null) EffectsModeAdvancedButton.IsChecked = !simple;
            }
            finally { _suppressEvents = prev; }
            _simpleModeShown = simple;
        }

        /// <summary>Re-applies only when the stored mode differs from what is
        /// on screen. Cheap enough for the refresh timer.</summary>
        private void SyncSimpleMode()
        {
            bool simple = _plugin?.Settings?.SimpleMode == true;
            if (_simpleModeShown != simple) ApplySimpleMode();
        }

        // Logical tree, not visual: a collapsed Expander's body has no visual
        // children until it is first opened, but its logical children exist
        // from load.
        private static void CollectAdvanced(DependencyObject node, List<FrameworkElement> into)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (!(child is DependencyObject d)) continue;
                if (d is FrameworkElement fe && (fe.Tag as string) == AdvancedTag)
                {
                    into.Add(fe);
                    continue;   // its descendants go with it
                }
                CollectAdvanced(d, into);
            }
        }

        private void EffectsMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            bool simple = sender == EffectsModeSimpleButton;
            if (_plugin.Settings.SimpleMode == simple) return;
            _plugin.Settings.SimpleMode = simple;
            ApplySimpleMode();
            SchedulePersistDebounced();
        }
    }
}
