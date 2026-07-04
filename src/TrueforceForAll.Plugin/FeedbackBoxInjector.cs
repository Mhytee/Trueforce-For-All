// Splices a Trueforce quick-gain tile into SimHub's home-screen "Feedback"
// section, next to the built-in Motors / Wind / Bass-shakers boxes.
//
// Why a runtime visual-tree splice and not an API: SimHub's home page is owned
// by SimHubWPF.exe. The Feedback section (SimHubWPF.Controls.FeedbackWidget)
// hardcodes exactly three plugin references and exposes no extension point, and
// the SDK's IWidgetProvider/WidgetManager framework is not wired into the home
// page at all. The only way in is to find the live panel that hosts those tiles
// (a System.Windows.Controls.Primitives.UniformGrid full of SHSubTitledBox
// children, inside an SHSection) and add our own SHSubTitledBox to it.
//
// This pokes at host internals, so everything here is defensive:
//   - all work runs on the WPF dispatcher and is wrapped in try/catch
//   - if the layout isn't found (home tab not realized, or a future SimHub
//     reshuffles it) we simply don't appear, and never throw into SimHub
//   - a low-frequency timer re-attaches the tile if SimHub rebuilds the section
//     (FeedbackWidget recreates itself on reload), and keeps the slider values
//     in sync with the settings panel and preset loads
//
// Enabled by default (TrueforceSettings.ShowFeedbackBox = true); the HOMEBOX
// access code is now just a live dev toggle, NOT the gate. Failure is cosmetic:
// the tile simply doesn't appear and never throws into SimHub. Note FindFeedbackGrid
// uses a "most SHSubTitledBox children" heuristic that could mis-target if a future
// SimHub home layout nests more such tiles elsewhere (cosmetic risk only).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;
using SimHub.Plugins.Styles;

namespace TrueforceForAll.Plugin
{
    internal sealed class FeedbackBoxInjector
    {
        // Tag stamped on our tile so we never count it as a "Feedback" tile when
        // picking the host grid, and so we can recognise our own on rescans.
        private const string BoxTag = "TrueforceFeedbackBox";

        // Master-gain slider range mirrors the settings panel (0..2.0); audio
        // mirrors the audio Gain slider (0..3.0). Kept in one place so the home
        // tile and the settings panel can't drift.
        private const double MasterMax = 2.0;
        private const double AudioMax  = 3.0;

        private readonly TrueforcePlugin _plugin;
        private DispatcherTimer _timer;
        private bool _enabled;
        private bool _loggedInjected;
        private bool _loggedError;

        // Built once, then re-parented if SimHub rebuilds the Feedback section.
        private SHSubTitledBox _box;
        private Slider _masterSlider, _audioSlider;
        private ToggleButton _masterToggle, _audioToggle;

        // Set when a slider drag changed a gain; the timer persists it (debounced)
        // so a drag doesn't write the settings file on every value tick.
        private bool _pendingPersist;

        // Guards the refresh -> ValueChanged -> apply feedback loop.
        private bool _syncing;

        public FeedbackBoxInjector(TrueforcePlugin plugin)
        {
            _plugin = plugin;
        }

        // Called from plugin Init. Honours the persisted opt-in.
        public void Start()
        {
            SetEnabled(_plugin?.Settings?.ShowFeedbackBox == true);
        }

        // Toggle the feature on/off live (HOMEBOX access code / diagnostics).
        public void SetEnabled(bool on)
        {
            _enabled = on;
            OnUi(() =>
            {
                if (_enabled)
                {
                    EnsureTimer();
                    TryInject();
                }
                else
                {
                    _timer?.Stop();
                    RemoveBox();
                }
            });
        }

        // Called from plugin End. Best-effort; the box dies with the app anyway.
        public void Stop()
        {
            OnUi(() =>
            {
                _timer?.Stop();
                RemoveBox();
            });
        }

        // ---------- internals ----------

        private static Dispatcher UiDispatcher()
            => Application.Current?.Dispatcher;

        // Marshal an action onto the UI thread, swallowing everything: SimHub
        // must never see an exception from here.
        private void OnUi(Action act)
        {
            var d = UiDispatcher();
            if (d == null) return;
            d.BeginInvoke(new Action(() =>
            {
                try { act(); }
                catch (Exception ex) { LogErrorOnce(ex); }
            }), DispatcherPriority.Background);
        }

        private void EnsureTimer()
        {
            if (_timer != null)
            {
                if (!_timer.IsEnabled) _timer.Start();
                return;
            }
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1000),
            };
            _timer.Tick += (s, e) =>
            {
                try
                {
                    if (!_enabled) { _timer.Stop(); return; }
                    TryInject();
                    RefreshValues();
                    FlushPersistIfIdle();
                }
                catch (Exception ex) { LogErrorOnce(ex); }
            };
            _timer.Start();
        }

        // Attach (or re-attach) our tile to the live Feedback grid. Cheap in the
        // steady state: when our box is already alive in a UniformGrid we skip the
        // full visual-tree scan entirely and only rescan after a rebuild.
        private void TryInject()
        {
            if (!_enabled) return;

            if (_box != null
                && _box.Parent is UniformGrid
                && PresentationSource.FromVisual(_box) != null)
            {
                return; // still attached and live
            }

            var panel = FindFeedbackGrid();
            if (panel == null)
                return; // home tab not realized yet, or layout not found

            if (_box != null && ReferenceEquals(_box.Parent, panel))
                return; // already a child of the live panel

            DetachBoxFromParent();
            if (_box == null) _box = BuildBox();
            panel.Children.Add(_box);

            // Copy the visual look (track brush, tick marks, scale transform,
            // style) from a real built-in slider in this section so ours match
            // exactly, without hardcoding theme values that a SimHub update could
            // change. Best-effort: if no reference slider is found, ours keep the
            // default look.
            MatchBuiltInSliders(panel);

            if (!_loggedInjected)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Home Feedback gain tile injected.");
                _loggedInjected = true;
            }
        }

        // Find the UniformGrid that hosts the Feedback tiles: the one whose
        // children include the most SHSubTitledBox tiles that aren't ours. On the
        // home page that is the Feedback section's grid (Motors/Wind/Bass shakers).
        private UniformGrid FindFeedbackGrid()
        {
            var app = Application.Current;
            if (app == null) return null;

            UniformGrid best = null;
            int bestCount = 0;
            foreach (Window w in app.Windows)
            {
                if (w == null) continue;
                foreach (var v in Descendants(w))
                {
                    if (!(v is UniformGrid grid)) continue;
                    int count = 0;
                    foreach (var child in grid.Children)
                        if (child is SHSubTitledBox b && !IsOurs(b)) count++;
                    if (count > bestCount) { bestCount = count; best = grid; }
                }
            }
            return bestCount > 0 ? best : null;
        }

        private static bool IsOurs(FrameworkElement fe)
            => fe != null && (fe.Tag as string) == BoxTag;

        // Find a built-in slider in this section and clone its look onto ours so
        // the tile matches Motors/Wind. We copy only visual properties (not range
        // / value), so our own gain bindings stay intact.
        private void MatchBuiltInSliders(UniformGrid panel)
        {
            Slider reference = null;
            foreach (var d in Descendants(panel))
            {
                if (d is Slider s
                    && !ReferenceEquals(s, _masterSlider)
                    && !ReferenceEquals(s, _audioSlider))
                {
                    reference = s;
                    break;
                }
            }
            if (reference != null)
            {
                CopySliderLook(reference, _masterSlider);
                CopySliderLook(reference, _audioSlider);
            }
            // Toggles use SHToggleCheckbox, which is self-styled, so no clone needed.
        }

        private static void CopySliderLook(Slider from, Slider to)
        {
            if (from == null || to == null) return;

            // Style drives the control template (track/thumb). Brushes + ticks +
            // the scale transform are set locally on the built-in sliders.
            if (from.Style != null) to.Style = from.Style;
            if (from.Background != null) to.Background = from.Background;
            if (from.Foreground != null) to.Foreground = from.Foreground;
            if (from.BorderBrush != null) to.BorderBrush = from.BorderBrush;
            to.BorderThickness     = from.BorderThickness;
            to.TickPlacement       = from.TickPlacement;
            // Don't copy TickFrequency raw: the built-in slider's frequency is
            // tuned to ITS range (so on our 0..2 / 0..3 range it would land only
            // at the ends). Match the built-in's tick COUNT instead, so ticks are
            // evenly spaced along the whole slider like theirs.
            double fromRange = from.Maximum - from.Minimum;
            double toRange   = to.Maximum - to.Minimum;
            if (from.TickFrequency > 0 && fromRange > 0 && toRange > 0)
            {
                double ticks = fromRange / from.TickFrequency;
                if (ticks >= 1) to.TickFrequency = toRange / ticks;
            }
            to.IsSnapToTickEnabled = from.IsSnapToTickEnabled;
            to.Orientation         = from.Orientation;
            to.RenderTransformOrigin = from.RenderTransformOrigin;
            to.Padding             = from.Padding;
            if (from.MinHeight > 0) to.MinHeight = from.MinHeight;

            // LayoutTransform is a Freezable; clone so the two sliders don't share
            // one mutable instance.
            var lt = from.LayoutTransform;
            if (lt != null && lt != Transform.Identity)
                to.LayoutTransform = lt.CloneCurrentValue();
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null) yield break;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        private SHSubTitledBox BuildBox()
        {
            // Match the built-in tiles (Motors/Wind): a centered icon up top, the
            // controls below. The built-ins use a MahApps PackIconMaterial, so we
            // use the same control; Steering is the steering-wheel glyph, fitting
            // for a wheel-haptics mod.
            var content = new StackPanel { Margin = new Thickness(6, 2, 6, 4) };

            content.Children.Add(new PackIconMaterial
            {
                Kind = PackIconMaterialKind.Steering,
                Width = 30,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 6),
            });

            // Master row: a switch toggle (whole-plugin enable) with its name as
            // the toggle's own label, then the gain slider, matching the Wind box.
            _masterSlider = MakeSlider(MasterMax, 0.05, 0.1);
            _masterSlider.ValueChanged += (s, e) =>
            {
                if (_syncing) return;
                _plugin.MasterGain = (float)e.NewValue;   // live: mixer + settings
                _pendingPersist = true;
            };
            _masterToggle = MakeToggle("Master", "Turn all TF4ALL haptics on/off");
            _masterToggle.Checked   += (s, e) => { if (!_syncing) _plugin.SetPluginEnabled(true); };
            _masterToggle.Unchecked += (s, e) => { if (!_syncing) _plugin.SetPluginEnabled(false); };
            content.Children.Add(MakeRow(_masterToggle, _masterSlider));

            // Audio row: the toggle enables/disables the audio-haptics layer.
            _audioSlider = MakeSlider(AudioMax, 0.05, 0.25);
            _audioSlider.ValueChanged += (s, e) =>
            {
                if (_syncing) return;
                _plugin.SetActiveAudioGainLive((float)e.NewValue); // live: _audio + settings
                _pendingPersist = true;
            };
            _audioToggle = MakeToggle("Audio", "Turn audio haptics on/off");
            _audioToggle.Checked   += (s, e) => { if (!_syncing) { _plugin.SetActiveAudioEnabledLive(true);  _pendingPersist = true; } };
            _audioToggle.Unchecked += (s, e) => { if (!_syncing) { _plugin.SetActiveAudioEnabledLive(false); _pendingPersist = true; } };
            content.Children.Add(MakeRow(_audioToggle, _audioSlider));

            var box = new SHSubTitledBox
            {
                Title = "Trueforce",
                ShowCog = true,   // hover cog (top-left), like the built-in tiles
                Tag = BoxTag,
                Margin = new Thickness(2),
                Content = content,
            };
            // Cog -> jump to our plugin's page, matching the built-ins.
            box.CogClicked += (s, e) => { NavigateToPluginPage(); e.Handled = true; };
            return box;
        }

        private static Slider MakeSlider(double max, double small, double large)
            => new Slider
            {
                Minimum = 0,
                Maximum = max,
                SmallChange = small,
                LargeChange = large,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 90,
            };

        // A switch-style toggle that carries its line name as its own label, like
        // the Wind box's Idle/Race toggles. SHToggleCheckbox is self-styled, so no
        // chrome cloning is needed.
        private static ToggleButton MakeToggle(string label, string tip)
            => new SHToggleCheckbox
            {
                Content = label,
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = tip,
            };

        // One row: [toggle + its label] [slider], all on a single line. No numeric
        // value readout, the slider gets that space.
        private static FrameworkElement MakeRow(ToggleButton toggle, Slider slider)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(toggle, 0);
            Grid.SetColumn(slider, 1);
            grid.Children.Add(toggle);
            grid.Children.Add(slider);
            return grid;
        }

        // Pull current gains back into the sliders so the tile tracks the settings
        // panel and preset loads. Skips a slider the user is actively dragging.
        private void RefreshValues()
        {
            if (_box == null || _plugin == null) return;
            _syncing = true;
            try
            {
                if (_masterSlider != null && !IsBusy(_masterSlider))
                    _masterSlider.Value = Clamp(_plugin.MasterGain, 0, MasterMax);
                if (_audioSlider != null && !IsBusy(_audioSlider))
                    _audioSlider.Value = Clamp(_plugin.ActiveAudioGain, 0, AudioMax);

                // Keep the on/off toggles in step with the plugin / audio enable
                // state (settings panel, per-game auto-enable, presets).
                if (_masterToggle != null)
                {
                    bool on = _plugin.PluginEnabled;
                    if (_masterToggle.IsChecked != on) _masterToggle.IsChecked = on;
                }
                if (_audioToggle != null)
                {
                    bool on = _plugin.ActiveAudioEnabled;
                    if (_audioToggle.IsChecked != on) _audioToggle.IsChecked = on;
                }
            }
            finally { _syncing = false; }
        }

        private static bool IsBusy(Slider s)
            => s.IsMouseCaptureWithin || s.IsKeyboardFocusWithin;

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);

        // Debounced settings write: persist at most once per timer tick, and only
        // when no slider is mid-drag, so a drag doesn't thrash the settings file.
        private void FlushPersistIfIdle()
        {
            if (!_pendingPersist) return;
            if ((_masterSlider != null && IsBusy(_masterSlider))
                || (_audioSlider != null && IsBusy(_audioSlider)))
                return;
            _pendingPersist = false;
            try { _plugin.PersistSettings(); }
            catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
        }

        private void RemoveBox()
        {
            DetachBoxFromParent();
            _box = null;
            _masterSlider = _audioSlider = null;
            _masterToggle = _audioToggle = null;
            _pendingPersist = false;
        }

        // Cog -> navigate to our plugin's left-menu page. SimHub's nav is a MahApps
        // HamburgerMenu; its OnItemClick simply sets the menu's SelectedItem and
        // Content to the chosen item. We replicate that via reflection (no MahApps
        // reference): find the menu, find the item whose Label matches our
        // LeftMenuTitle, and select it. Defensive; does nothing if not found.
        private void NavigateToPluginPage()
        {
            try
            {
                var window = (_box != null ? Window.GetWindow(_box) : null)
                             ?? Application.Current?.MainWindow;
                if (window == null) return;

                object menu = null;
                foreach (var d in Descendants(window))
                {
                    if (d.GetType().FullName == "MahApps.Metro.Controls.HamburgerMenu")
                    {
                        menu = d;
                        break;
                    }
                }
                if (menu == null) return;

                var menuType = menu.GetType();
                var items = menuType.GetProperty("Items")?.GetValue(menu) as System.Collections.IEnumerable;
                if (items == null) return;

                string wanted = _plugin?.LeftMenuTitle;
                object target = null;
                foreach (var item in items)
                {
                    if (item == null) continue;
                    var label = item.GetType().GetProperty("Label")?.GetValue(item) as string;
                    if (string.Equals(label, wanted, StringComparison.Ordinal))
                    {
                        target = item;
                        break;
                    }
                }
                if (target == null) return;

                // Mirror MainWindow.HamburgerMenuControl_OnItemClick:
                //   menu.SelectedItem = item;  menu.Content = item;
                menuType.GetProperty("SelectedItem")?.SetValue(menu, target);
                if (menu is ContentControl cc) cc.Content = target;
            }
            catch (Exception ex) { LogErrorOnce(ex); }
        }

        private void DetachBoxFromParent()
        {
            if (_box?.Parent is Panel p)
                p.Children.Remove(_box);
        }

        private void LogErrorOnce(Exception ex)
        {
            if (_loggedError) return;
            _loggedError = true;
            try
            {
                SimHub.Logging.Current.Warn(
                    "[TF4ALL] Home Feedback tile injection failed; the tile won't appear. " +
                    "This is cosmetic and safe to ignore. " + ex.Message);
            }
            catch { }
        }
    }
}
