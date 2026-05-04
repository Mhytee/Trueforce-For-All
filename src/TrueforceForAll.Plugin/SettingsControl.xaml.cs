using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly TrueforcePlugin _plugin;
        private readonly DispatcherTimer _meterTimer;
        private bool _suppressEvents;
        private string _lastShownCarId;
        private string _lastShownGame;

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(TrueforcePlugin plugin) : this()
        {
            _plugin = plugin;

            WheelText.Text  = plugin.WheelStatus;
            StreamText.Text = plugin.StreamStatus;
            FfbTapText.Text = plugin.FfbTapStatus;
            VoicesText.Text = plugin.ActiveVoiceCount.ToString();

            RefreshFromPlugin();

            // 60 Hz meter updates (matches WPF compositor) with exponential
            // interpolation = visibly smoother than 30 Hz + abrupt width snaps.
            _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _meterTimer.Tick += MeterTimer_Tick;
            Loaded   += (_, __) => _meterTimer.Start();
            Unloaded += (_, __) => _meterTimer.Stop();
        }

        /// <summary>Pull all visible UI values from the plugin's effective settings.</summary>
        public void RefreshFromPlugin()
        {
            if (_plugin == null) return;
            _suppressEvents = true;
            try
            {
                PluginEnabledCheck.IsChecked = _plugin.PluginEnabled;
                string game = _plugin.ActiveGame;
                PluginEnabledHint.Text = string.IsNullOrEmpty(game)
                    ? "Choice is auto-remembered per game. Disable for games with native Trueforce (e.g. iRacing) so this plugin yields the wheel."
                    : $"Auto-remembered for '{game}'. Disable for games with native Trueforce (e.g. iRacing) so this plugin yields the wheel.";

                MasterGainSlider.Value = _plugin.Settings?.MasterGain ?? 1.0;
                MasterGainText.Text    = MasterGainSlider.Value.ToString("F2");

                FfbScaleSlider.Value   = _plugin.Settings?.FfbScale ?? 1.0;
                FfbScaleText.Text      = FfbScaleSlider.Value.ToString("F2");
                FfbInvertCheck.IsChecked = _plugin.Settings?.FfbInvertSign ?? true;
                FfbSmoothSlider.Value  = _plugin.Settings?.FfbSmoothTimeConstantMs ?? 3.0;
                FfbSmoothText.Text     = FfbSmoothSlider.Value.ToString("F1");

                DuckDepthSlider.Value   = _plugin.Settings?.DuckDepth ?? 0.5;
                DuckDepthText.Text      = DuckDepthSlider.Value.ToString("F2");
                DuckAttackSlider.Value  = _plugin.Settings?.DuckAttackMs ?? 5.0;
                DuckAttackText.Text     = ((int)DuckAttackSlider.Value).ToString();
                DuckReleaseSlider.Value = _plugin.Settings?.DuckReleaseMs ?? 80.0;
                DuckReleaseText.Text    = ((int)DuckReleaseSlider.Value).ToString();

                AudioEnabledCheck.IsChecked = _plugin.AudioCapture?.Enabled ?? false;
                AudioGainSlider.Value       = _plugin.AudioCapture?.Gain ?? 1.0;
                AudioGainText.Text          = AudioGainSlider.Value.ToString("F2");
                AudioFilterSlider.Value     = _plugin.AudioCapture?.LowpassCutoffHz ?? 350.0;
                AudioFilterText.Text        = ((int)AudioFilterSlider.Value).ToString();
                AudioHighpassSlider.Value   = _plugin.AudioCapture?.HighpassCutoffHz ?? 30.0;
                AudioHighpassText.Text      = ((int)AudioHighpassSlider.Value).ToString();

                RefreshPresetSection();

                ActiveCarText.Text = string.IsNullOrEmpty(_plugin.ActiveCarId) ? "(none)" : _plugin.ActiveCarId;
                bool carDetected = !string.IsNullOrEmpty(_plugin.ActiveCarId);
                ResetCarOverridesButton.IsEnabled = carDetected;
                ExportCarPresetButton.IsEnabled   = carDetected;
                ImportCarPresetButton.IsEnabled   = true;

                // Engine
                var es = _plugin.ActiveEngine;
                if (es != null)
                {
                    EngineEnabledCheck.IsChecked      = es.Enabled;
                    EngineCarOverrideCheck.IsChecked  = _plugin.IsEngineOverridden;
                    EngineCarOverrideCheck.IsEnabled  = carDetected;
                    EngineGainSlider.Value            = es.Gain;
                    EngineGainText.Text               = es.Gain.ToString("F2");
                    EngineCylindersSlider.Value       = es.Cylinders;
                    EngineCylindersText.Text          = es.Cylinders.ToString();
                    EnginePitchSlider.Value           = es.Pitch;
                    EnginePitchText.Text              = es.Pitch.ToString("F2");
                    EngineLowpassSlider.Value         = es.LowpassHz;
                    EngineLowpassText.Text            = ((int)es.LowpassHz).ToString();
                    SelectWaveform(EngineWaveformCombo, es.Waveform);
                }
                // Bumps
                var bs = _plugin.ActiveBumps;
                if (bs != null)
                {
                    SlipEnabledCheck.IsChecked     = bs.Enabled;
                    BumpsCarOverrideCheck.IsChecked = _plugin.IsBumpsOverridden;
                    BumpsCarOverrideCheck.IsEnabled = carDetected;
                    SlipGainSlider.Value           = bs.Gain;
                    SlipGainText.Text              = bs.Gain.ToString("F2");
                    SelectWaveform(BumpsWaveformCombo, bs.Waveform);
                    BumpsFreqSlider.Value          = bs.Freq;
                    BumpsFreqText.Text             = ((int)bs.Freq).ToString();
                }
                // Traction
                var ts = _plugin.ActiveTraction;
                if (ts != null)
                {
                    TractionEnabledCheck.IsChecked       = ts.Enabled;
                    TractionCarOverrideCheck.IsChecked   = _plugin.IsTractionOverridden;
                    TractionCarOverrideCheck.IsEnabled   = carDetected;
                    TractionGainSlider.Value             = ts.Gain;
                    TractionGainText.Text                = ts.Gain.ToString("F2");
                    TractionSensitivitySlider.Value      = ts.Sensitivity;
                    TractionSensitivityText.Text         = ts.Sensitivity.ToString("F2");
                    SelectWaveform(TractionWaveformCombo, ts.Waveform);
                    TractionFreqSlider.Value             = ts.Freq;
                    TractionFreqText.Text                = ((int)ts.Freq).ToString();
                }
                // Shift
                var ss = _plugin.ActiveShift;
                if (ss != null)
                {
                    ShiftEnabledCheck.IsChecked      = ss.Enabled;
                    ShiftCarOverrideCheck.IsChecked  = _plugin.IsShiftOverridden;
                    ShiftCarOverrideCheck.IsEnabled  = carDetected;
                    ShiftGainSlider.Value            = ss.Gain;
                    ShiftGainText.Text               = ss.Gain.ToString("F2");
                    ShiftFreqSlider.Value            = ss.Freq;
                    ShiftFreqText.Text               = ((int)ss.Freq).ToString();
                    SelectWaveform(ShiftWaveformCombo, ss.Waveform);
                }
                // ABS
                var abs = _plugin.ActiveAbs;
                if (abs != null)
                {
                    AbsEnabledCheck.IsChecked     = abs.Enabled;
                    AbsCarOverrideCheck.IsChecked = _plugin.IsAbsOverridden;
                    AbsCarOverrideCheck.IsEnabled = carDetected;
                    AbsGainSlider.Value           = abs.Gain;
                    AbsGainText.Text              = abs.Gain.ToString("F2");
                    AbsFreqSlider.Value           = abs.Freq;
                    AbsFreqText.Text              = ((int)abs.Freq).ToString();
                    AbsPulseFreqSlider.Value      = abs.PulseFreq;
                    AbsPulseFreqText.Text         = abs.PulseFreq.ToString("F1");
                    AbsDutyCycleSlider.Value      = abs.DutyCycle;
                    AbsDutyCycleText.Text         = abs.DutyCycle.ToString("F2");
                    AbsModeCombo.SelectedIndex    = (int)abs.Mode;
                    SelectWaveform(AbsWaveformCombo, abs.Waveform);
                }
            }
            finally { _suppressEvents = false; }
        }

        private static void SelectWaveform(ComboBox combo, Waveform w) => combo.SelectedIndex = (int)w;
        private static Waveform WaveformOf(ComboBox combo)
        {
            int idx = combo.SelectedIndex; if (idx < 0) idx = 0;
            return (Waveform)idx;
        }

        // ---------- meter tick + active-car sync ----------

        private void MeterTimer_Tick(object sender, EventArgs e)
        {
            var src = _plugin?.AudioCapture;
            if (src != null)
            {
                float peak = src.ReadAndResetPeak();
                if (peak > 1f) peak = 1f;
                double cur = AudioLevelMeter.Value;
                AudioLevelMeter.Value = peak > cur ? peak : cur * 0.85;
                CaptureStatusText.Text = _plugin.CaptureStatus;
            }
            if (_plugin != null) FfbTapText.Text = _plugin.FfbTapStatus;

            // Live activity meters — only updated when the Expander is open.
            // Peak-hold smoothing with 25% decay per tick (~80 ms half-life)
            // so bars feel responsive but don't flicker on every sample.
            if (LiveActivityExpander?.IsExpanded == true)
            {
                UpdateMeter(EngineMeterTrack,       EngineMeterFill,       _plugin?.EnginePulse?.ActivityLevel  ?? 0);
                UpdateMeter(BumpsMeterTrack,        BumpsMeterFill,        _plugin?.RoadBumps?.ActivityLevel    ?? 0);
                UpdateMeter(TractionMeterTrack,     TractionMeterFill,     _plugin?.TractionLoss?.ActivityLevel ?? 0);
                UpdateMeter(GearMeterTrack,         GearMeterFill,         _plugin?.GearShift?.ActivityLevel    ?? 0);
                UpdateMeter(AbsMeterTrack,          AbsMeterFill,          _plugin?.AbsClick?.ActivityLevel     ?? 0);
                UpdateMeter(AudioCaptureMeterTrack, AudioCaptureMeterFill, src != null ? AudioLevelMeter.Value  : 0);
                double duck = 1.0 - (_plugin?.EnginePulse?.DuckMultiplier ?? 1.0);
                UpdateMeter(DuckMeterTrack, DuckMeterFill, Math.Max(0, duck));
            }

            string carId = _plugin?.ActiveCarId;
            string game  = _plugin?.ActiveGame;
            if (carId != _lastShownCarId || game != _lastShownGame)
            {
                _lastShownCarId = carId;
                _lastShownGame  = game;
                RefreshFromPlugin();
            }
        }

        private static void UpdateMeter(System.Windows.Controls.Border track, System.Windows.Controls.Border fill, double level)
        {
            if (track == null || fill == null) return;
            double w = track.ActualWidth;
            if (w <= 0) return;
            double cur = double.IsNaN(fill.Width) ? 0 : fill.Width;
            double target = level * w;
            // Exponential interpolation in both directions, asymmetric: faster
            // attack (snap-up to peaks), slower release (smooth fall).
            double alpha = (target > cur) ? 0.55 : 0.18;
            fill.Width = cur + (target - cur) * alpha;
        }

        private void Apply() => _plugin.ApplyActiveCarOverride();

        // ---------- Master / Audio ----------

        private void PluginEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetPluginEnabled(PluginEnabledCheck.IsChecked == true);
        }

        private void MasterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            MasterGainText.Text = v.ToString("F2");
            _plugin.MasterGain = v;
        }
        private void FfbScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbScaleText.Text = v.ToString("F2");
            _plugin.SetFfbScale(v);
        }
        private void FfbInvert_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetFfbInvertSign(FfbInvertCheck.IsChecked == true);
        }
        private void FfbSmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbSmoothText.Text = v.ToString("F1");
            _plugin.SetFfbSmoothMs(v);
        }
        private void DuckDepthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckDepthText.Text = v.ToString("F2");
            _plugin.Settings.DuckDepth = v;
        }
        private void DuckAttackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckAttackText.Text = ((int)v).ToString();
            _plugin.Settings.DuckAttackMs = v;
        }
        private void DuckReleaseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckReleaseText.Text = ((int)v).ToString();
            _plugin.Settings.DuckReleaseMs = v;
        }

        private void EngineTest_Click   (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.EnginePulse);
        private void BumpsTest_Click    (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.RoadBumps);
        private void TractionTest_Click (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.TractionLoss);
        private void ShiftTest_Click    (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.GearShift);
        private void AbsTest_Click      (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.AbsClick);
        private void AudioEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.AudioCapture == null) return;
            _plugin.AudioCapture.Enabled = AudioEnabledCheck.IsChecked == true;
            if (_plugin.Settings != null) _plugin.Settings.AudioCapture.Enabled = _plugin.AudioCapture.Enabled;
        }
        private void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.AudioCapture == null) return;
            float v = (float)e.NewValue;
            AudioGainText.Text = v.ToString("F2");
            _plugin.AudioCapture.Gain = v;
            if (_plugin.Settings != null) _plugin.Settings.AudioCapture.Gain = v;
        }
        private void AudioFilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.AudioCapture == null) return;
            double v = e.NewValue;
            AudioFilterText.Text = ((int)v).ToString();
            _plugin.AudioCapture.LowpassCutoffHz = v;
            if (_plugin.Settings != null) _plugin.Settings.AudioCapture.LowpassCutoffHz = v;
        }
        private void AudioHighpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.AudioCapture == null) return;
            double v = e.NewValue;
            AudioHighpassText.Text = ((int)v).ToString();
            _plugin.AudioCapture.HighpassCutoffHz = v;
            if (_plugin.Settings != null) _plugin.Settings.AudioCapture.HighpassCutoffHz = v;
        }

        // ---------- Active car ----------

        private void ResetCarOverrides_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            if (MessageBox.Show($"Clear all per-car overrides for '{_plugin.ActiveCarId}'? Sections will revert to global settings.",
                                "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _plugin.SetEngineOverride(false);
            _plugin.SetBumpsOverride(false);
            _plugin.SetTractionOverride(false);
            _plugin.SetShiftOverride(false);
            _plugin.SetAbsOverride(false);
            RefreshFromPlugin();
        }

        // ---------- Engine pulse ----------

        private void EngineEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveEngine.Enabled = EngineEnabledCheck.IsChecked == true;
            Apply();
        }
        private void EngineCarOverride_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetEngineOverride(EngineCarOverrideCheck.IsChecked == true);
            RefreshFromPlugin();
        }
        private void EngineGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineGainText.Text = v.ToString("F2");
            _plugin.ActiveEngine.Gain = v;
            Apply();
        }
        private void EngineCylindersSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)Math.Round(e.NewValue);
            EngineCylindersText.Text = v.ToString();
            _plugin.ActiveEngine.Cylinders = v;
            Apply();
        }
        private void EnginePitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EnginePitchText.Text = v.ToString("F2");
            _plugin.ActiveEngine.Pitch = v;
            Apply();
        }
        private void EngineLowpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            EngineLowpassText.Text = ((int)v).ToString();
            _plugin.ActiveEngine.LowpassHz = v;
            Apply();
        }
        private void EngineWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveEngine.Waveform = WaveformOf(EngineWaveformCombo);
            Apply();
        }

        // ---------- Road bumps ----------

        private void SlipEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveBumps.Enabled = SlipEnabledCheck.IsChecked == true;
            Apply();
        }
        private void BumpsCarOverride_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetBumpsOverride(BumpsCarOverrideCheck.IsChecked == true);
            RefreshFromPlugin();
        }
        private void SlipGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            SlipGainText.Text = v.ToString("F2");
            _plugin.ActiveBumps.Gain = v;
            Apply();
        }
        private void BumpsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveBumps.Waveform = WaveformOf(BumpsWaveformCombo);
            Apply();
        }
        private void BumpsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsFreqText.Text = ((int)v).ToString();
            _plugin.ActiveBumps.Freq = v;
            Apply();
        }

        // ---------- Traction loss ----------

        private void TractionEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveTraction.Enabled = TractionEnabledCheck.IsChecked == true;
            Apply();
        }
        private void TractionCarOverride_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetTractionOverride(TractionCarOverrideCheck.IsChecked == true);
            RefreshFromPlugin();
        }
        private void TractionGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionGainText.Text = v.ToString("F2");
            _plugin.ActiveTraction.Gain = v;
            Apply();
        }
        private void TractionSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionSensitivityText.Text = v.ToString("F2");
            _plugin.ActiveTraction.Sensitivity = v;
            Apply();
        }
        private void TractionWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveTraction.Waveform = WaveformOf(TractionWaveformCombo);
            Apply();
        }
        private void TractionFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionFreqText.Text = ((int)v).ToString();
            _plugin.ActiveTraction.Freq = v;
            Apply();
        }

        // ---------- Gear shift ----------

        private void ShiftEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveShift.Enabled = ShiftEnabledCheck.IsChecked == true;
            Apply();
        }
        private void ShiftCarOverride_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetShiftOverride(ShiftCarOverrideCheck.IsChecked == true);
            RefreshFromPlugin();
        }
        private void ShiftGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftGainText.Text = v.ToString("F2");
            _plugin.ActiveShift.Gain = v;
            Apply();
        }
        private void ShiftFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftFreqText.Text = ((int)v).ToString();
            _plugin.ActiveShift.Freq = v;
            Apply();
        }
        private void ShiftWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveShift.Waveform = WaveformOf(ShiftWaveformCombo);
            Apply();
        }

        // ---------- ABS ----------

        private void AbsEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveAbs.Enabled = AbsEnabledCheck.IsChecked == true;
            Apply();
        }
        private void AbsCarOverride_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetAbsOverride(AbsCarOverrideCheck.IsChecked == true);
            RefreshFromPlugin();
        }
        private void AbsGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsGainText.Text = v.ToString("F2");
            _plugin.ActiveAbs.Gain = v;
            Apply();
        }
        private void AbsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsFreqText.Text = ((int)v).ToString();
            _plugin.ActiveAbs.Freq = v;
            Apply();
        }
        private void AbsPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsPulseFreqText.Text = v.ToString("F1");
            _plugin.ActiveAbs.PulseFreq = v;
            Apply();
        }
        private void AbsDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsDutyCycleText.Text = v.ToString("F2");
            _plugin.ActiveAbs.DutyCycle = v;
            Apply();
        }
        private void AbsMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int idx = AbsModeCombo.SelectedIndex; if (idx < 0) idx = 0;
            _plugin.ActiveAbs.Mode = (AbsMode)idx;
            Apply();
        }
        private void AbsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveAbs.Waveform = WaveformOf(AbsWaveformCombo);
            Apply();
        }

        // ---------- Preset library ----------

        private void RefreshPresetSection()
        {
            if (_plugin == null) return;

            string game     = _plugin.ActiveGame;
            string defName  = _plugin.DefaultPresetForActiveGame;
            string activeP  = _plugin.ActivePresetName;

            ActiveGameText.Text     = string.IsNullOrEmpty(game)    ? "(none)" : game;
            DefaultPresetText.Text  = string.IsNullOrEmpty(defName) ? "(none)" : defName;
            ActivePresetText.Text   = string.IsNullOrEmpty(activeP) ? "(none — unsaved)" : activeP;

            // Repopulate dropdown without re-firing SelectionChanged into our
            // handler — _suppressEvents wraps the whole RefreshFromPlugin call.
            string keepSelected = PresetCombo.SelectedItem as string ?? activeP;
            PresetCombo.Items.Clear();
            if (_plugin.PresetNames != null)
                foreach (var name in _plugin.PresetNames) PresetCombo.Items.Add(name);

            // Prefer the active preset if it still exists; else the previous
            // selection if it still exists; else nothing.
            if (!string.IsNullOrEmpty(activeP) && PresetCombo.Items.Contains(activeP))
                PresetCombo.SelectedItem = activeP;
            else if (!string.IsNullOrEmpty(keepSelected) && PresetCombo.Items.Contains(keepSelected))
                PresetCombo.SelectedItem = keepSelected;

            bool hasSelection = PresetCombo.SelectedItem is string;
            bool gameDetected = !string.IsNullOrEmpty(game);
            bool gameHasDefault = !string.IsNullOrEmpty(defName);

            ApplyPresetButton.IsEnabled   = hasSelection;
            SavePresetButton.IsEnabled    = !string.IsNullOrEmpty(activeP);  // only when an active preset exists to overwrite
            SaveAsPresetButton.IsEnabled  = true;
            DeletePresetButton.IsEnabled  = hasSelection;
            SetDefaultButton.IsEnabled    = hasSelection && gameDetected;
            ClearDefaultButton.IsEnabled  = gameDetected && gameHasDefault;
            ExportPresetButton.IsEnabled  = hasSelection;
            ImportPresetButton.IsEnabled  = true;
        }

        private string SelectedPresetName => PresetCombo?.SelectedItem as string;

        private void PresetCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Selection alone doesn't apply — user must click Apply. Just
            // refresh button-enabled states so Delete/Apply enable correctly.
            if (_suppressEvents) return;
            bool hasSelection = SelectedPresetName != null;
            ApplyPresetButton.IsEnabled  = hasSelection;
            DeletePresetButton.IsEnabled = hasSelection;
            SetDefaultButton.IsEnabled   = hasSelection && !string.IsNullOrEmpty(_plugin?.ActiveGame);
            ExportPresetButton.IsEnabled = hasSelection;
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            string name = SelectedPresetName;
            if (_plugin == null || string.IsNullOrEmpty(name)) return;
            if (!_plugin.ApplyPreset(name))
                MessageBox.Show($"Could not apply '{name}' (preset missing).", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshFromPlugin();
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string name = _plugin.ActivePresetName;
            if (string.IsNullOrEmpty(name))
            {
                // No active preset — fall through to Save as.
                SaveAsPreset_Click(sender, e);
                return;
            }
            if (MessageBox.Show($"Overwrite preset '{name}' with current settings?",
                                "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _plugin.SavePresetAs(name);
            RefreshFromPlugin();
        }

        private void SaveAsPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string suggested = _plugin.ActivePresetName ?? _plugin.ActiveGame ?? "My preset";
            string name = PromptForName("Save preset as", "Preset name:", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();

            // Confirm overwrite if name collides.
            bool exists = false;
            if (_plugin.PresetNames != null)
                foreach (var n in _plugin.PresetNames) { if (n == name) { exists = true; break; } }
            if (exists && MessageBox.Show($"A preset called '{name}' already exists. Overwrite?",
                                          "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _plugin.SavePresetAs(name);
            RefreshFromPlugin();
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            string name = SelectedPresetName;
            if (_plugin == null || string.IsNullOrEmpty(name)) return;
            if (MessageBox.Show($"Delete preset '{name}'? Any games defaulting to it will lose their auto-load binding.",
                                "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _plugin.DeletePreset(name);
            RefreshFromPlugin();
        }

        private void SetDefault_Click(object sender, RoutedEventArgs e)
        {
            string name = SelectedPresetName;
            if (_plugin == null || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(_plugin.ActiveGame)) return;
            _plugin.SetDefaultPresetForActiveGame(name);
            RefreshFromPlugin();
        }

        private void ClearDefault_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveGame)) return;
            _plugin.ClearDefaultPresetForActiveGame();
            RefreshFromPlugin();
        }

        private void ExportPreset_Click(object sender, RoutedEventArgs e)
        {
            string name = SelectedPresetName;
            if (_plugin == null || string.IsNullOrEmpty(name)) return;
            string safeName = MakeFileSafe(name);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "Trueforce preset (*.tfpreset.json)|*.tfpreset.json|JSON (*.json)|*.json",
                FileName = $"Trueforce-{safeName}.tfpreset.json",
                Title    = "Export Trueforce preset",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _plugin.ExportPreset(name, dlg.FileName);
                MessageBox.Show($"Exported '{name}' to:\n{dlg.FileName}",
                                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Trueforce preset (*.tfpreset.json;*.json)|*.tfpreset.json;*.json|All files (*.*)|*.*",
                Title  = "Import Trueforce preset",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string presetName = _plugin.ImportPreset(dlg.FileName);
                MessageBox.Show($"Imported preset '{presetName}' into your library. Select it from the dropdown and click Apply, or set it as a game default.",
                                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshFromPlugin();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Tiny inline name-prompt dialog. WPF has no built-in
        /// InputBox; this draws a 360x140 modal with TextBox + OK/Cancel.</summary>
        private string PromptForName(string title, string label, string defaultValue)
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            var tb = new TextBox { Text = defaultValue ?? "" };
            sp.Children.Add(tb);
            var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
                                          HorizontalAlignment = HorizontalAlignment.Right,
                                          Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);
            win.Content = sp;

            string result = null;
            ok.Click += (s, args) => { result = tb.Text; win.DialogResult = true; };
            win.Loaded += (s, args) => { tb.Focus(); tb.SelectAll(); };
            return win.ShowDialog() == true ? result : null;
        }

        private void ExportCarPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string safeGame = MakeFileSafe(_plugin.ActiveGame ?? "any");
            string safeCar  = MakeFileSafe(_plugin.ActiveCarId);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "Trueforce car preset (*.tfcar.json)|*.tfcar.json|JSON (*.json)|*.json",
                FileName = $"Trueforce-{safeGame}-{safeCar}.tfcar.json",
                Title    = "Export Trueforce car preset",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _plugin.ExportActiveCarPreset(dlg.FileName);
                MessageBox.Show($"Exported '{_plugin.ActiveCarId}' to:\n{dlg.FileName}",
                                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportCarPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Trueforce car preset (*.tfcar.json;*.json)|*.tfcar.json;*.json|All files (*.*)|*.*",
                Title  = "Import Trueforce car preset",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string carId = _plugin.ImportCarPreset(dlg.FileName);
                bool applied = carId == _plugin.ActiveCarId;
                MessageBox.Show(applied
                    ? $"Imported car preset for '{carId}'. Applied (this is the active car)."
                    : $"Imported car preset for '{carId}'. Stored — will apply when you drive that car.",
                    "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshFromPlugin();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string MakeFileSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "preset";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(invalid, arr[i]) >= 0) arr[i] = '_';
            return new string(arr);
        }

        // ---------- Export / Import ----------

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "Trueforce settings (*.json)|*.json",
                FileName = "Trueforce-settings.json",
                Title    = "Export Trueforce settings",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _plugin.ExportSettings(dlg.FileName);
                MessageBox.Show($"Exported to:\n{dlg.FileName}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Trueforce settings (*.json)|*.json",
                Title  = "Import Trueforce settings",
            };
            if (dlg.ShowDialog() != true) return;
            if (MessageBox.Show("Importing replaces all current Trueforce settings (master, audio, every effect, all per-car overrides). Continue?",
                                "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                _plugin.ImportSettings(dlg.FileName);
                RefreshFromPlugin();
                MessageBox.Show("Settings imported.", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
