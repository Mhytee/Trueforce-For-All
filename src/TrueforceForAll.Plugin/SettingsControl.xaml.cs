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
        // Dirty = current tuning has drifted from the active preset's saved
        // snapshot. Set by user changes; cleared by Apply/Save/Import and by
        // game-change auto-apply. Drives the "★ unsaved" suffix and the
        // unsaved-tuning confirmation prompts.
        private bool _dirty;

        // Per-section dirty state. The amber per-section "Save…" button is
        // itself the unsaved-changes indicator: visible only when the
        // section's dirty bit is set. The grey "↶" Revert button next to it
        // shows under the same condition AND when an active preset exists
        // (nothing to revert to otherwise). Master + Ducking are global-only;
        // their popover collapses to the preset choices (no per-car option).
        // Values mirror TrueforcePlugin.SectionKind so we can pass through.
        private enum EffectKind { Audio = 2, Engine = 3, Bumps = 4, Traction = 5, Shift = 6, Abs = 7, Master = 0, Ducking = 1 }
        private readonly bool[] _effectDirty = new bool[8];
        private System.Windows.Controls.Button GetEffectSaveBtn(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:   return MasterSaveBtn;
                case EffectKind.Ducking:  return DuckingSaveBtn;
                case EffectKind.Audio:    return AudioSaveBtn;
                case EffectKind.Engine:   return EngineSaveBtn;
                case EffectKind.Bumps:    return BumpsSaveBtn;
                case EffectKind.Traction: return TractionSaveBtn;
                case EffectKind.Shift:    return ShiftSaveBtn;
                case EffectKind.Abs:      return AbsSaveBtn;
            }
            return null;
        }
        private System.Windows.Controls.Button GetEffectRevertBtn(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:   return MasterRevertBtn;
                case EffectKind.Ducking:  return DuckingRevertBtn;
                case EffectKind.Audio:    return AudioRevertBtn;
                case EffectKind.Engine:   return EngineRevertBtn;
                case EffectKind.Bumps:    return BumpsRevertBtn;
                case EffectKind.Traction: return TractionRevertBtn;
                case EffectKind.Shift:    return ShiftRevertBtn;
                case EffectKind.Abs:      return AbsRevertBtn;
            }
            return null;
        }
        private static string EffectLabel(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:   return "Master";
                case EffectKind.Ducking:  return "Sidechain ducking";
                case EffectKind.Audio:    return "Audio capture";
                case EffectKind.Engine:   return "Engine pulse";
                case EffectKind.Bumps:    return "Road bumps";
                case EffectKind.Traction: return "Traction loss";
                case EffectKind.Shift:    return "Gear shift";
                case EffectKind.Abs:      return "ABS pulse";
            }
            return "section";
        }
        // Master + Ducking are global-only. The save popover hides the
        // per-car option for these (no override concept).
        private static bool SectionHasCarScope(EffectKind w)
            => w != EffectKind.Master && w != EffectKind.Ducking;

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(TrueforcePlugin plugin) : this()
        {
            _plugin = plugin;

            // Diagnostics expander (collapsed by default) — verbose status.
            WheelText.Text  = plugin.WheelStatus;
            StreamText.Text = plugin.StreamStatus;
            FfbTapText.Text = plugin.FfbTapStatus;
            VoicesText.Text = plugin.ActiveVoiceCount.ToString();

            RefreshFromPlugin();
            UpdateStatusPill();

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
                FfbSkipPassthroughCheck.IsChecked = _plugin.Settings?.SkipFfbPassthrough ?? false;
                FfbSmoothSlider.Value  = _plugin.Settings?.FfbSmoothTimeConstantMs ?? 0.0;
                FfbSmoothText.Text     = FfbSmoothSlider.Value.ToString("F1");
                FfbSpikeLimitSlider.Value = _plugin.Settings?.FfbSpikeMaxLsbPerMs ?? 0.0;
                FfbSpikeLimitText.Text    = FfbSpikeLimitSlider.Value <= 0
                    ? "off"
                    : ((int)FfbSpikeLimitSlider.Value).ToString();
                FfbPeakLimitSlider.Value  = _plugin.Settings?.FfbPeakSoftLimitLsb ?? 0.0;
                FfbPeakLimitText.Text     = FfbPeakLimitSlider.Value <= 0
                    ? "off"
                    : ((int)FfbPeakLimitSlider.Value).ToString();

                DuckDepthSlider.Value   = _plugin.Settings?.DuckDepth ?? 0.5;
                DuckDepthText.Text      = DuckDepthSlider.Value.ToString("F2");
                DuckAttackSlider.Value  = _plugin.Settings?.DuckAttackMs ?? 5.0;
                DuckAttackText.Text     = ((int)DuckAttackSlider.Value).ToString();
                DuckReleaseSlider.Value = _plugin.Settings?.DuckReleaseMs ?? 80.0;
                DuckReleaseText.Text    = ((int)DuckReleaseSlider.Value).ToString();

                var audio = _plugin.ActiveAudio;
                AudioEnabledCheck.IsChecked      = audio?.Enabled ?? false;
                AudioGainSlider.Value            = audio?.Gain ?? 1.0;
                AudioGainText.Text               = AudioGainSlider.Value.ToString("F2");
                AudioFilterSlider.Value          = audio?.LowpassCutoffHz ?? 350.0;
                AudioFilterText.Text             = ((int)AudioFilterSlider.Value).ToString();
                AudioHighpassSlider.Value        = audio?.HighpassCutoffHz ?? 30.0;
                AudioHighpassText.Text           = ((int)AudioHighpassSlider.Value).ToString();

                RefreshPresetSection();

                CaptureExeOverrideBox.Text = _plugin.ActiveCaptureExeOverride ?? "";

                // Header strip context.
                HeaderGameText.Text = string.IsNullOrEmpty(game) ? "(none)" : game;
                HeaderCarText.Text  = string.IsNullOrEmpty(_plugin.ActiveCarId) ? "(none)" : _plugin.ActiveCarId;

                bool carDetected = !string.IsNullOrEmpty(_plugin.ActiveCarId);
                ResetCarOverridesButton.IsEnabled = carDetected;
                ExportCarPresetButton.IsEnabled   = carDetected;
                ImportCarPresetButton.IsEnabled   = true;

                // Skip-passthrough makes the FFB tuning controls (scale/smooth/invert/
                // safety limiters) irrelevant — game writes the wheel directly. Grey
                // them so users don't waste time tuning controls that don't apply.
                FfbPassthroughControls.IsEnabled = !(_plugin.Settings?.SkipFfbPassthrough ?? false);

                // Hint text below "Active car" header gets a more useful line when
                // no car is detected.
                ActiveCarHint.Text = carDetected
                    ? "Per-car tuning is saved automatically when you click 'Save…' on an effect and pick 'For this car'. Each car gets its own preset file under PluginsData/Common/TrueforceCars; sharing one is just sharing the file."
                    : "No car detected yet. The 'For this car' save option in each effect's popover will become available once telemetry identifies a car.";

                // Engine
                var es = _plugin.ActiveEngine;
                if (es != null)
                {
                    EngineEnabledCheck.IsChecked      = es.Enabled;
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
                    TractionGainSlider.Value             = ts.Gain;
                    TractionGainText.Text                = ts.Gain.ToString("F2");
                    TractionSensitivitySlider.Value      = ts.Sensitivity;
                    TractionSensitivityText.Text         = ts.Sensitivity.ToString("F2");
                    SelectWaveform(TractionWaveformCombo, ts.Waveform);
                    TractionFreqSlider.Value             = ts.Freq;
                    TractionFreqText.Text                = ((int)ts.Freq).ToString();
                    TractionNoiseLpSlider.Value          = ts.NoiseLowpassHz;
                    TractionNoiseLpText.Text             = ((int)ts.NoiseLowpassHz).ToString();
                    TractionNoiseHpSlider.Value          = ts.NoiseHighpassHz;
                    TractionNoiseHpText.Text             = ((int)ts.NoiseHighpassHz).ToString();
                }
                // Shift
                var ss = _plugin.ActiveShift;
                if (ss != null)
                {
                    ShiftEnabledCheck.IsChecked      = ss.Enabled;
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

                // Override badges in expander headers — visible only when this
                // section has its own per-car override active.
                AudioOverrideBadge.Visibility    = (_plugin.IsAudioOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                EngineOverrideBadge.Visibility   = (_plugin.IsEngineOverridden   && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                BumpsOverrideBadge.Visibility    = (_plugin.IsBumpsOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                TractionOverrideBadge.Visibility = (_plugin.IsTractionOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                ShiftOverrideBadge.Visibility    = (_plugin.IsShiftOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                AbsOverrideBadge.Visibility      = (_plugin.IsAbsOverridden      && carDetected) ? Visibility.Visible : Visibility.Collapsed;

                // Conditional dimming/hiding on dependent settings:
                //  - Traction noise LP/HP only matter for the Noise waveform.
                //  - ABS pulse rate/duty only matter in Pulse mode.
                bool tractionIsNoise = ts != null && ts.Waveform == Waveform.Noise;
                TractionNoiseLpRow.Visibility = tractionIsNoise ? Visibility.Visible : Visibility.Collapsed;
                TractionNoiseHpRow.Visibility = tractionIsNoise ? Visibility.Visible : Visibility.Collapsed;
                AbsPulseControls.IsEnabled    = abs == null || abs.Mode == AbsMode.Pulse;
            }
            finally { _suppressEvents = false; }

            // After all UI controls have been re-synced from plugin state,
            // re-derive each section's dirty bit from the (now-current)
            // values vs the active preset's snapshot. This catches scope
            // changes (override on/off), preset apply, game/car switches,
            // and toggles back to original values.
            RecomputeAllEffectDirty();
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
            if (_plugin != null)
            {
                FfbTapText.Text = _plugin.FfbTapStatus;
                StreamText.Text = _plugin.StreamStatus;
                VoicesText.Text = _plugin.ActiveVoiceCount.ToString();
            }

            // Telemetry-source line in Diagnostics: source name + live measured Hz,
            // plus an "audio only" suffix when frames are arriving but contain no
            // useful physics data (custom SimHub games without telemetry).
            var telSrc = _plugin?.TelemetrySource;
            if (telSrc != null)
            {
                string label = telSrc.IsEnhanced ? "Enhanced Effects" : "SimHub";
                double hz = telSrc.MeasuredHz;
                string baseText = hz > 0 ? $"{label} · {hz:0} Hz" : $"{label} · idle";
                bool gameRunning = !string.IsNullOrEmpty(_plugin.ActiveGame);
                bool audioOnly   = gameRunning && hz > 0 && !_plugin.HasUsefulTelemetry;
                if (audioOnly) baseText += " · audio only";
                TelemetrySourceText.Text = baseText;

                // Grey out the telemetry-effect controls (engine pulse, road
                // bumps, traction loss, gear shift, ABS) ONLY when a game is
                // running and we've established it provides no telemetry.
                // No game → keep enabled so the user can still tune presets
                // for any future session. Test buttons stay reachable from
                // the disabled panel because IsEnabled=false dims but doesn't
                // remove access — actually IsEnabled=false on a StackPanel
                // disables children, so Test buttons would be inert too.
                // That matches "no data, no point testing" intent.
                if (TelemetryEffectsPanel != null)
                    TelemetryEffectsPanel.IsEnabled = !audioOnly;
            }

            UpdateStatusPill();

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
                // Game change → plugin may have auto-applied a preset for the
                // new game. Treat the resulting state as the saved baseline so
                // the "★ unsaved" indicator doesn't fire spuriously when the
                // user hasn't changed anything yet.
                ClearDirty();
                RefreshFromPlugin();
            }
        }

        // Status-pill colors. Cached as static brushes so the 60 Hz tick
        // doesn't allocate a fresh brush on every refresh.
        private static readonly System.Windows.Media.SolidColorBrush PillGreenBg   = MakeBrush("#3D8B40");
        private static readonly System.Windows.Media.SolidColorBrush PillGreenDot  = MakeBrush("#A5D6A7");
        private static readonly System.Windows.Media.SolidColorBrush PillAmberBg   = MakeBrush("#B26A00");
        private static readonly System.Windows.Media.SolidColorBrush PillAmberDot  = MakeBrush("#FFCC80");
        private static readonly System.Windows.Media.SolidColorBrush PillGreyBg    = MakeBrush("#666666");
        private static readonly System.Windows.Media.SolidColorBrush PillGreyDot   = MakeBrush("#BDBDBD");
        private static readonly System.Windows.Media.SolidColorBrush PillMutedBg   = MakeBrush("#5D8B40");
        private static readonly System.Windows.Media.SolidColorBrush PillMutedDot  = MakeBrush("#C5E1A5");
        private static System.Windows.Media.SolidColorBrush MakeBrush(string hex)
        {
            var b = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        /// <summary>Drives the single colored status pill in the header strip.
        /// Boils all the wheel/stream/telemetry strings down to one of:
        /// Disabled / Wheel not detected / Stream stopped / Ready /
        /// Active / Audio only / Waiting for telemetry. The optional
        /// "✨ Enhanced N Hz" hype badge appears only when a per-game
        /// enhanced telemetry source is actively delivering frames.</summary>
        private void UpdateStatusPill()
        {
            if (_plugin == null) return;

            bool   enabled    = _plugin.PluginEnabled;
            string wheelStr   = _plugin.WheelStatus  ?? "";
            string streamStr  = _plugin.StreamStatus ?? "";
            bool   wheelOk    = !wheelStr.StartsWith("Not detected");
            bool   streamOk   = streamStr.StartsWith("Streaming");
            bool   gameOn     = !string.IsNullOrEmpty(_plugin.ActiveGame);

            var    telSrc     = _plugin.TelemetrySource;
            double hz         = telSrc?.MeasuredHz ?? 0;
            bool   isEnhanced = telSrc?.IsEnhanced ?? false;
            bool   useful     = _plugin.HasUsefulTelemetry;

            string text;
            System.Windows.Media.SolidColorBrush bg, dot;

            if (!enabled)                       { text = "Disabled";              bg = PillGreyBg;  dot = PillGreyDot;  }
            else if (!wheelOk)                  { text = "Wheel not detected";    bg = PillAmberBg; dot = PillAmberDot; }
            else if (!streamOk)                 { text = "Stream stopped";        bg = PillAmberBg; dot = PillAmberDot; }
            else if (!gameOn)                   { text = "Ready";                 bg = PillGreenBg; dot = PillGreenDot; }
            else if (hz > 0 && useful)          { text = "Active";                bg = PillGreenBg; dot = PillGreenDot; }
            else if (hz > 0)                    { text = "Audio only";            bg = PillMutedBg; dot = PillMutedDot; }
            else                                { text = "Waiting for telemetry"; bg = PillAmberBg; dot = PillAmberDot; }

            StatusPillText.Text   = text;
            StatusPill.Background = bg;
            StatusPillDot.Fill    = dot;

            // Enhanced hype badge: only when actually delivering frames from
            // a per-game source (not the generic SimHub pipe), and the plugin
            // is actually doing something with them.
            if (enabled && wheelOk && streamOk && isEnhanced && hz > 0)
            {
                EnhancedBadge.Visibility = Visibility.Visible;
                EnhancedBadgeText.Text   = $"✨ Enhanced · {hz:0} Hz";
            }
            else
            {
                EnhancedBadge.Visibility = Visibility.Collapsed;
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

        // Apply() is called by per-effect handlers AFTER the _suppressEvents
        // guard, so reaching it implies a real user change → push to live
        // device and recompute the affected effect's dirty state. Per-car
        // file is NOT auto-written: saves are explicit (Save… → For this car).
        private void Apply(EffectKind which)
        {
            _plugin.ApplyActiveCarOverride();
            MarkEffectDirty(which);
        }

        private void MarkDirty()
        {
            if (_dirty) return;
            _dirty = true;
            UpdateHeaderPresetDisplay();
        }

        private void ClearDirty()
        {
            // Always cascade through per-section dirty too: every place that
            // calls ClearDirty (preset apply / save / import / game change)
            // implies a fresh baseline for the entire state, not just the
            // global-drift indicator.
            ClearAllEffectDirty();
            if (!_dirty) return;
            _dirty = false;
            UpdateHeaderPresetDisplay();
        }

        /// <summary>Recompute the section's dirty state by asking the plugin
        /// whether its values still match the active preset's snapshot. This
        /// replaces sticky-bit MarkEffectDirty so that changing a value and
        /// changing it back clears the dirty indicator.</summary>
        private void MarkEffectDirty(EffectKind which)
        {
            bool dirty;
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActivePresetName))
            {
                // No active preset = no anchor → fall back to sticky-true
                // behavior. (Auto-save flow will create a preset on first
                // save, eliminating this state in normal use.)
                dirty = true;
            }
            else
            {
                dirty = _plugin.IsSectionDirty((TrueforcePlugin.SectionKind)(int)which);
            }

            if (_effectDirty[(int)which] == dirty) return;
            _effectDirty[(int)which] = dirty;

            var saveBtn   = GetEffectSaveBtn(which);
            var revertBtn = GetEffectRevertBtn(which);
            if (saveBtn != null) saveBtn.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            // Revert only makes sense with an anchor.
            if (revertBtn != null)
                revertBtn.Visibility = (dirty && !string.IsNullOrEmpty(_plugin?.ActivePresetName))
                    ? Visibility.Visible : Visibility.Collapsed;

            // Recompute global dirty from the per-section bits.
            UpdateGlobalDirtyFromEffects();
        }

        /// <summary>Recompute every section's dirty state from the plugin —
        /// called from the end of RefreshFromPlugin (so override toggles,
        /// game/car switches, preset apply, etc. all sync the per-section
        /// indicators correctly without each handler having to enumerate).</summary>
        private void RecomputeAllEffectDirty()
        {
            if (_plugin == null) return;
            bool hasPreset = !string.IsNullOrEmpty(_plugin.ActivePresetName);
            for (int i = 0; i < _effectDirty.Length; i++)
            {
                bool dirty;
                if (!hasPreset)
                {
                    // No anchor — preserve sticky bit (don't auto-clear).
                    dirty = _effectDirty[i];
                }
                else
                {
                    dirty = _plugin.IsSectionDirty((TrueforcePlugin.SectionKind)i);
                }
                if (_effectDirty[i] == dirty) continue;
                _effectDirty[i] = dirty;
                var saveBtn   = GetEffectSaveBtn((EffectKind)i);
                var revertBtn = GetEffectRevertBtn((EffectKind)i);
                if (saveBtn   != null) saveBtn.Visibility   = dirty ? Visibility.Visible : Visibility.Collapsed;
                if (revertBtn != null) revertBtn.Visibility = (dirty && hasPreset) ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateGlobalDirtyFromEffects();
        }

        private void UpdateGlobalDirtyFromEffects()
        {
            bool any = false;
            int dirtyCount = 0;
            for (int i = 0; i < _effectDirty.Length; i++) if (_effectDirty[i]) { any = true; dirtyCount++; }
            if (any != _dirty)
            {
                _dirty = any;
                UpdateHeaderPresetDisplay();
            }
            // Drive the main Save preset button's prominent-amber styling +
            // content based on dirty count. Tag="dirty" lights up the amber
            // template trigger; content escalates to "Save all" when 2+
            // sections need committing.
            if (SavePresetButton != null)
            {
                SavePresetButton.Tag = any ? "dirty" : null;
                SavePresetButton.Content = !any
                    ? "Save preset"
                    : (dirtyCount >= 2 ? "★ Save all changes" : "★ Save preset");
            }
        }

        private void ClearEffectDirty(EffectKind which)
        {
            if (!_effectDirty[(int)which]) return;
            _effectDirty[(int)which] = false;
            var saveBtn   = GetEffectSaveBtn(which);
            var revertBtn = GetEffectRevertBtn(which);
            if (saveBtn   != null) saveBtn.Visibility   = Visibility.Collapsed;
            if (revertBtn != null) revertBtn.Visibility = Visibility.Collapsed;
        }

        private void ClearAllEffectDirty()
        {
            for (int i = 0; i < _effectDirty.Length; i++)
            {
                _effectDirty[i] = false;
                var saveBtn   = GetEffectSaveBtn((EffectKind)i);
                var revertBtn = GetEffectRevertBtn((EffectKind)i);
                if (saveBtn   != null) saveBtn.Visibility   = Visibility.Collapsed;
                if (revertBtn != null) revertBtn.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Composes the preset header line based on active preset, game
        /// default, and whether tuning has drifted from the saved snapshot.
        ///
        ///   "MyGT3"                                  active matches saved snapshot
        ///   "MyGT3 · default for this game"          active is also the game default
        ///   "MyGT3 · game default: Stock"            active set, different default exists
        ///   "MyGT3 · ★ unsaved"                       active set, tuning has drifted
        ///   "MyGT3 · default for this game · ★ unsaved" both
        ///   "(unsaved tuning)"                        no active preset, user tuning live
        ///   "(unsaved tuning) · game default: Stock"  same with a known default
        ///   "(none)"                                  no preset, no default, no dirty
        /// </summary>
        private void UpdateHeaderPresetDisplay()
        {
            if (_plugin == null || HeaderPresetText == null) return;
            string activeP = _plugin.ActivePresetName;
            string defName = _plugin.DefaultPresetForActiveGame;

            if (string.IsNullOrEmpty(activeP) && string.IsNullOrEmpty(defName) && !_dirty)
            {
                HeaderPresetText.Text = "(none)";
                return;
            }
            string main   = string.IsNullOrEmpty(activeP) ? "(unsaved tuning)" : activeP;
            string suffix = "";
            if (!string.IsNullOrEmpty(activeP) && activeP == defName) suffix += " · default for this game";
            else if (!string.IsNullOrEmpty(defName))                  suffix += $" · game default: {defName}";
            // The "(unsaved tuning)" main label already conveys the dirty state
            // when there's no active preset, so don't double up the suffix.
            if (_dirty && !string.IsNullOrEmpty(activeP))             suffix += " · ★ unsaved";
            HeaderPresetText.Text = main + suffix;
        }

        // ---------- Master / Audio ----------

        private void PluginEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            // Plugin-enabled is a per-game preference (GameEnabled dict), not
            // part of preset content — so it doesn't dirty the active preset.
            _plugin.SetPluginEnabled(PluginEnabledCheck.IsChecked == true);
        }

        private void MasterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            MasterGainText.Text = v.ToString("F2");
            _plugin.MasterGain = v;
            MarkEffectDirty(EffectKind.Master);
        }
        private void FfbScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbScaleText.Text = v.ToString("F2");
            _plugin.SetFfbScale(v);
            MarkEffectDirty(EffectKind.Master);
        }
        private void FfbInvert_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetFfbInvertSign(FfbInvertCheck.IsChecked == true);
            MarkEffectDirty(EffectKind.Master);
        }
        private void FfbSkipPassthrough_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            bool skip = FfbSkipPassthroughCheck.IsChecked == true;
            _plugin.SetSkipFfbPassthrough(skip);
            // Grey/ungrey the passthrough-only controls live, without a full Refresh.
            FfbPassthroughControls.IsEnabled = !skip;
            MarkEffectDirty(EffectKind.Master);
        }

        private void CaptureExeOverride_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitCaptureExeOverride();
        }
        private void CaptureExeOverride_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Enter commits and moves focus off the textbox so the LostFocus
            // handler doesn't fire a duplicate save.
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                CommitCaptureExeOverride();
                System.Windows.Input.Keyboard.ClearFocus();
                e.Handled = true;
            }
        }
        private void CommitCaptureExeOverride()
        {
            if (_suppressEvents || _plugin == null) return;
            string game = _plugin.ActiveGame;
            if (string.IsNullOrEmpty(game))
            {
                // No active game — nothing to scope the override under.
                // Clear any stale text so it's not misleading.
                if (!string.IsNullOrEmpty(CaptureExeOverrideBox.Text))
                    CaptureExeOverrideBox.Text = "";
                return;
            }
            _plugin.SetAudioCaptureExeOverride(game, CaptureExeOverrideBox.Text);
        }
        private void FfbSmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbSmoothText.Text = v.ToString("F1");
            _plugin.SetFfbSmoothMs(v);
            MarkEffectDirty(EffectKind.Master);
        }
        private void FfbSpikeLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbSpikeLimitText.Text = v <= 0 ? "off" : ((int)v).ToString();
            _plugin.SetFfbSpikeMaxLsbPerMs(v);
            MarkEffectDirty(EffectKind.Master);
        }
        private void FfbPeakLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbPeakLimitText.Text = v <= 0 ? "off" : ((int)v).ToString();
            _plugin.SetFfbPeakSoftLimitLsb(v);
            MarkEffectDirty(EffectKind.Master);
        }
        private void DuckDepthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckDepthText.Text = v.ToString("F2");
            _plugin.Settings.DuckDepth = v;
            MarkEffectDirty(EffectKind.Ducking);
        }
        private void DuckAttackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckAttackText.Text = ((int)v).ToString();
            _plugin.Settings.DuckAttackMs = v;
            MarkEffectDirty(EffectKind.Ducking);
        }
        private void DuckReleaseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckReleaseText.Text = ((int)v).ToString();
            _plugin.Settings.DuckReleaseMs = v;
            MarkEffectDirty(EffectKind.Ducking);
        }

        private void EngineTest_Click   (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.EnginePulse);
        private void BumpsTest_Click    (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.RoadBumps);
        private void TractionTest_Click (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.TractionLoss);
        private void ShiftTest_Click    (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.GearShift);
        private void AbsTest_Click      (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.AbsClick);
        private void AudioEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            _plugin.ActiveAudio.Enabled = AudioEnabledCheck.IsChecked == true;
            Apply(EffectKind.Audio);
        }
        private void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            float v = (float)e.NewValue;
            AudioGainText.Text = v.ToString("F2");
            _plugin.ActiveAudio.Gain = v;
            Apply(EffectKind.Audio);
        }
        private void AudioFilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            double v = e.NewValue;
            AudioFilterText.Text = ((int)v).ToString();
            _plugin.ActiveAudio.LowpassCutoffHz = v;
            Apply(EffectKind.Audio);
        }
        private void AudioHighpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            double v = e.NewValue;
            AudioHighpassText.Text = ((int)v).ToString();
            _plugin.ActiveAudio.HighpassCutoffHz = v;
            Apply(EffectKind.Audio);
        }

        // ---------- Active car ----------

        private void ResetCarOverrides_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string carId = _plugin.ActiveCarId;
            if (MessageBox.Show(
                    $"Reset '{carId}' to game defaults? Deletes this car's per-car preset file; the car will use the active game preset's globals.",
                    "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _plugin.ResetCarToGameDefaults(carId);
            RefreshFromPlugin();
        }

        // ---------- Engine pulse ----------

        private void EngineEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveEngine.Enabled = EngineEnabledCheck.IsChecked == true;
            Apply(EffectKind.Engine);
        }
        private void EngineGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineGainText.Text = v.ToString("F2");
            _plugin.ActiveEngine.Gain = v;
            Apply(EffectKind.Engine);
        }
        private void EngineCylindersSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)Math.Round(e.NewValue);
            EngineCylindersText.Text = v.ToString();
            _plugin.ActiveEngine.Cylinders = v;
            Apply(EffectKind.Engine);
        }
        private void EnginePitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EnginePitchText.Text = v.ToString("F2");
            _plugin.ActiveEngine.Pitch = v;
            Apply(EffectKind.Engine);
        }
        private void EngineLowpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            EngineLowpassText.Text = ((int)v).ToString();
            _plugin.ActiveEngine.LowpassHz = v;
            Apply(EffectKind.Engine);
        }
        private void EngineWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveEngine.Waveform = WaveformOf(EngineWaveformCombo);
            Apply(EffectKind.Engine);
        }

        // ---------- Road bumps ----------

        private void SlipEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveBumps.Enabled = SlipEnabledCheck.IsChecked == true;
            Apply(EffectKind.Bumps);
        }
        private void SlipGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            SlipGainText.Text = v.ToString("F2");
            _plugin.ActiveBumps.Gain = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveBumps.Waveform = WaveformOf(BumpsWaveformCombo);
            Apply(EffectKind.Bumps);
        }
        private void BumpsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsFreqText.Text = ((int)v).ToString();
            _plugin.ActiveBumps.Freq = v;
            Apply(EffectKind.Bumps);
        }

        // ---------- Traction loss ----------

        private void TractionEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveTraction.Enabled = TractionEnabledCheck.IsChecked == true;
            Apply(EffectKind.Traction);
        }
        private void TractionGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionGainText.Text = v.ToString("F2");
            _plugin.ActiveTraction.Gain = v;
            Apply(EffectKind.Traction);
        }
        private void TractionSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionSensitivityText.Text = v.ToString("F2");
            _plugin.ActiveTraction.Sensitivity = v;
            Apply(EffectKind.Traction);
        }
        private void TractionWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var wf = WaveformOf(TractionWaveformCombo);
            _plugin.ActiveTraction.Waveform = wf;
            // Show/hide noise filter rows live — they only matter for Noise.
            var vis = wf == Waveform.Noise ? Visibility.Visible : Visibility.Collapsed;
            TractionNoiseLpRow.Visibility = vis;
            TractionNoiseHpRow.Visibility = vis;
            Apply(EffectKind.Traction);
        }
        private void TractionFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionFreqText.Text = ((int)v).ToString();
            _plugin.ActiveTraction.Freq = v;
            Apply(EffectKind.Traction);
        }
        private void TractionNoiseLpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            TractionNoiseLpText.Text = ((int)v).ToString();
            _plugin.ActiveTraction.NoiseLowpassHz = v;
            Apply(EffectKind.Traction);
        }
        private void TractionNoiseHpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            TractionNoiseHpText.Text = ((int)v).ToString();
            _plugin.ActiveTraction.NoiseHighpassHz = v;
            Apply(EffectKind.Traction);
        }

        // ---------- Gear shift ----------

        private void ShiftEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveShift.Enabled = ShiftEnabledCheck.IsChecked == true;
            Apply(EffectKind.Shift);
        }
        private void ShiftGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftGainText.Text = v.ToString("F2");
            _plugin.ActiveShift.Gain = v;
            Apply(EffectKind.Shift);
        }
        private void ShiftFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftFreqText.Text = ((int)v).ToString();
            _plugin.ActiveShift.Freq = v;
            Apply(EffectKind.Shift);
        }
        private void ShiftWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveShift.Waveform = WaveformOf(ShiftWaveformCombo);
            Apply(EffectKind.Shift);
        }

        // ---------- ABS ----------

        private void AbsEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveAbs.Enabled = AbsEnabledCheck.IsChecked == true;
            Apply(EffectKind.Abs);
        }
        private void AbsGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsGainText.Text = v.ToString("F2");
            _plugin.ActiveAbs.Gain = v;
            Apply(EffectKind.Abs);
        }
        private void AbsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsFreqText.Text = ((int)v).ToString();
            _plugin.ActiveAbs.Freq = v;
            Apply(EffectKind.Abs);
        }
        private void AbsPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsPulseFreqText.Text = v.ToString("F1");
            _plugin.ActiveAbs.PulseFreq = v;
            Apply(EffectKind.Abs);
        }
        private void AbsDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsDutyCycleText.Text = v.ToString("F2");
            _plugin.ActiveAbs.DutyCycle = v;
            Apply(EffectKind.Abs);
        }
        private void AbsMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int idx = AbsModeCombo.SelectedIndex; if (idx < 0) idx = 0;
            var mode = (AbsMode)idx;
            _plugin.ActiveAbs.Mode = mode;
            // Pulse rate / duty are unused in PerTick mode — grey them live.
            AbsPulseControls.IsEnabled = mode == AbsMode.Pulse;
            Apply(EffectKind.Abs);
        }
        private void AbsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveAbs.Waveform = WaveformOf(AbsWaveformCombo);
            Apply(EffectKind.Abs);
        }

        // ---------- Per-effect Save popover ----------

        private void EffectSave_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (!(sender is System.Windows.Controls.Button b) || !(b.Tag is string tag)) return;
            if (!Enum.TryParse<EffectKind>(tag, out var which)) return;
            ShowEffectSavePopover(which);
        }

        private void EffectRevert_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (!(sender is System.Windows.Controls.Button b) || !(b.Tag is string tag)) return;
            if (!Enum.TryParse<EffectKind>(tag, out var which)) return;
            string activeP = _plugin.ActivePresetName;
            if (string.IsNullOrEmpty(activeP)) return;  // nothing to revert to
            string label = EffectLabel(which);
            if (MessageBox.Show(
                    $"Revert {label} to the saved values in preset '{activeP}'? Your unsaved {label} changes will be discarded.",
                    "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            // EffectKind values mirror SectionKind, so pass through directly.
            if (_plugin.RevertSection((TrueforcePlugin.SectionKind)(int)which))
            {
                ClearEffectDirty(which);
                // Global ★ unsaved indicator: clear only when ALL sections
                // are now clean.
                bool anyDirty = false;
                for (int i = 0; i < _effectDirty.Length; i++) if (_effectDirty[i]) { anyDirty = true; break; }
                if (!anyDirty) ClearDirty();
                RefreshFromPlugin();
            }
        }

        /// <summary>Per-effect Save popover. Two adaptive choices:
        ///   • "For [car]" — toggles the override on (snapshotting current
        ///     section values into the per-car override) when not already
        ///     overridden; just clears the section's dirty dot if it was.
        ///   • "Update preset 'X'" — updates the active preset in place
        ///     (whole-snapshot save; clears global dirty too).
        ///   • "Save as new preset…" — appears when there's no active preset;
        ///     opens the existing name-prompt flow.
        /// Choices are individually disabled when their precondition isn't
        /// met (no car detected → no per-car save; no preset → no update).</summary>
        private void ShowEffectSavePopover(EffectKind which)
        {
            string carId       = _plugin.ActiveCarId;
            bool   carDetected = !string.IsNullOrEmpty(carId);
            string activeP     = _plugin.ActivePresetName;
            bool   hasPreset   = !string.IsNullOrEmpty(activeP);
            bool   builtin     = hasPreset && _plugin.IsBuiltinPreset(activeP);
            string label       = EffectLabel(which);
            bool   carScope    = SectionHasCarScope(which);

            var win = new Window
            {
                Title  = $"Save {label}",
                Width  = 420,
                Height = carScope ? 240 : 170,  // shorter when no per-car option
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode    = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = $"Save current {label} settings to…",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            // Choice 1: per-car override (only for sections with a per-car concept).
            Button carBtn = null;
            if (carScope)
            {
                carBtn = new Button
                {
                    Content = carDetected ? $"For this car ({carId})" : "For this car (no car detected)",
                    Height = 32, Margin = new Thickness(0, 0, 0, 6),
                    IsEnabled = carDetected,
                    ToolTip = carDetected
                        ? "Saves these settings just for this car. Won't affect global tuning or other cars."
                        : "No car detected yet — drive a car to enable this option.",
                };
                sp.Children.Add(carBtn);
                sp.Children.Add(new TextBlock
                {
                    Text = carDetected
                        ? $"Toggles 'Override for this car' on and snapshots the current {label} values into the per-car override."
                        : "Per-car save needs the active car to be identified by telemetry first.",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }

            // Choice 2: update game defaults, fork from built-in, or save as new.
            string presetLabel;
            string presetHint;
            if (!hasPreset)
            {
                presetLabel = "Save as game defaults";
                presetHint  = "Saves your current tuning as a new preset and binds it as this game's default.";
            }
            else if (builtin)
            {
                presetLabel = "Save as game defaults";
                presetHint  = $"'{activeP}' is a built-in default that can't be overwritten. Saves your current tuning as a new user preset (named after the game) and binds it as this game's default. The built-in stays available as fallback.";
            }
            else
            {
                presetLabel = "Update game defaults";
                presetHint  = $"Overwrites '{activeP}' (this game's default preset) with your current tuning. Per-car preset files are independent and not touched.";
            }
            var presetBtn = new Button
            {
                Content = presetLabel,
                Height = 32, Margin = new Thickness(0, 0, 0, 6),
            };
            sp.Children.Add(presetBtn);
            sp.Children.Add(new TextBlock
            {
                Text = presetHint,
                FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);

            win.Content = sp;

            if (carBtn != null)
            {
                carBtn.Click += (s, args) =>
                {
                    ApplyEffectSaveForCar(which);
                    win.DialogResult = true;
                };
            }
            presetBtn.Click += (s, args) =>
            {
                // If this section has a per-car override active, lift its
                // values up to the global section first — so the values the
                // user is looking at become the new game-default for this
                // section. Drops the override (the new global takes effect
                // for this car too).
                _plugin.PromoteSectionToGlobal((TrueforcePlugin.SectionKind)(int)which);

                if (!hasPreset)        SaveAsNewPresetFromUi();
                else if (builtin)      ForkAndSaveAsGamePreset();
                else                   UpdateActivePresetFromUi();
                win.DialogResult = true;
            };

            win.ShowDialog();
        }

        /// <summary>Per-car save for one effect: writes the section's
        /// current values to the per-car file. If the section had no
        /// override, snapshots from globals; if it had one, just persists
        /// the existing values. After save, the file matches in-memory →
        /// section is clean.</summary>
        private void ApplyEffectSaveForCar(EffectKind which)
        {
            _plugin.SaveSectionForCar((TrueforcePlugin.SectionKind)(int)which);
            RefreshFromPlugin();
        }

        /// <summary>Update active preset in place — whole-snapshot save.
        /// Clears all dirty indicators (global + every section).</summary>
        private void UpdateActivePresetFromUi()
        {
            string name = _plugin.ActivePresetName;
            if (string.IsNullOrEmpty(name)) return;
            _plugin.SavePresetAs(name);
            ClearDirty();
            RefreshFromPlugin();
        }

        /// <summary>Save current full state as a new named preset — same flow
        /// as the existing "Save as new" preset library button.</summary>
        private void SaveAsNewPresetFromUi()
        {
            string suggested = _plugin.ActiveGame ?? "My preset";
            string name = PromptForName("Save as new preset", "Preset name:", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            // Confirm overwrite if the name collides.
            bool exists = false;
            if (_plugin.PresetNames != null)
                foreach (var n in _plugin.PresetNames) { if (n == name) { exists = true; break; } }
            if (exists && MessageBox.Show($"A preset called '{name}' already exists. Overwrite?",
                                          "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _plugin.SavePresetAs(name);
            ClearDirty();
            RefreshFromPlugin();
        }

        // ---------- Preset library ----------

        private void RefreshPresetSection()
        {
            if (_plugin == null) return;

            string game     = _plugin.ActiveGame;
            string defName  = _plugin.DefaultPresetForActiveGame;
            string activeP  = _plugin.ActivePresetName;

            UpdateHeaderPresetDisplay();

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

            bool hasSelection   = PresetCombo.SelectedItem is string;
            bool gameDetected   = !string.IsNullOrEmpty(game);
            bool gameHasDefault = !string.IsNullOrEmpty(defName);
            bool selBuiltin     = hasSelection && _plugin.IsBuiltinPreset((string)PresetCombo.SelectedItem);

            ApplyPresetButton.IsEnabled   = hasSelection;
            // Save is always available now: if active preset is built-in or
            // missing, ForkAndSaveAsGamePreset takes over.
            SavePresetButton.IsEnabled    = true;
            SaveAsPresetButton.IsEnabled  = true;
            // Built-in presets are factory defaults — refuse delete.
            DeletePresetButton.IsEnabled  = hasSelection && !selBuiltin;
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
            string sel = SelectedPresetName;
            bool hasSelection = sel != null;
            bool selBuiltin   = hasSelection && _plugin != null && _plugin.IsBuiltinPreset(sel);
            ApplyPresetButton.IsEnabled  = hasSelection;
            DeletePresetButton.IsEnabled = hasSelection && !selBuiltin;
            SetDefaultButton.IsEnabled   = hasSelection && !string.IsNullOrEmpty(_plugin?.ActiveGame);
            ExportPresetButton.IsEnabled = hasSelection;
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            string name = SelectedPresetName;
            if (_plugin == null || string.IsNullOrEmpty(name)) return;
            if (_dirty)
            {
                var r = MessageBox.Show(
                    $"Apply preset '{name}'? Your unsaved tuning will be discarded.\n\nClick No to cancel and Save first.",
                    "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            if (!_plugin.ApplyPreset(name))
                MessageBox.Show($"Could not apply '{name}' (preset missing).", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            ClearDirty();
            RefreshFromPlugin();
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string activeP = _plugin.ActivePresetName;
            bool builtin = !string.IsNullOrEmpty(activeP) && _plugin.IsBuiltinPreset(activeP);

            // Fork case: no active preset, or active is a built-in we can't
            // overwrite. Auto-create a game-named user preset and bind it as
            // the game's default.
            if (string.IsNullOrEmpty(activeP) || builtin)
            {
                ForkAndSaveAsGamePreset();
                return;
            }
            // Regular overwrite
            if (MessageBox.Show($"Overwrite preset '{activeP}' with current settings?",
                                "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _plugin.SavePresetAs(activeP);
            ClearDirty();
            RefreshFromPlugin();
        }

        /// <summary>Fork-on-save flow: create a new user preset named after
        /// the game (or after the built-in being forked, minus the
        /// " (default)" suffix) and bind it as the game's default. If a
        /// preset with that name already exists, append " (1)", " (2)" until
        /// unique. Falls back to the Save as… name prompt when there's no
        /// game context to derive a name from.</summary>
        private void ForkAndSaveAsGamePreset()
        {
            string activeP = _plugin.ActivePresetName;
            string game    = _plugin.ActiveGame;
            string baseName;
            // Prefer "<built-in> minus (default)" so fork inherits the friendly name.
            const string defaultSuffix = " (default)";
            if (!string.IsNullOrEmpty(activeP) && activeP.EndsWith(defaultSuffix))
                baseName = activeP.Substring(0, activeP.Length - defaultSuffix.Length);
            else if (!string.IsNullOrEmpty(game))
                baseName = game;
            else
            {
                // No game and no built-in to fork from — fall back to name prompt.
                SaveAsNewPresetFromUi();
                return;
            }

            string newName = baseName;
            // De-dupe if collision.
            if (_plugin.PresetNames != null)
            {
                var existing = new System.Collections.Generic.HashSet<string>(_plugin.PresetNames);
                int i = 1;
                while (existing.Contains(newName)) newName = $"{baseName} ({i++})";
            }

            _plugin.SavePresetAs(newName);
            // Auto-bind as game's default if a game is loaded.
            if (!string.IsNullOrEmpty(game))
                _plugin.SetDefaultPresetForActiveGame(newName);
            ClearDirty();
            RefreshFromPlugin();
            MessageBox.Show(
                string.IsNullOrEmpty(game)
                    ? $"Saved as '{newName}'."
                    : $"Saved as '{newName}' and bound as the default for '{game}'. The built-in default stays available as fallback.",
                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
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
            ClearDirty();
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
                // RefreshFromPlugin → RecomputeAllEffectDirty syncs the dirty
                // dots with whatever sections the import touched.
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
                ClearDirty();
                RefreshFromPlugin();
                MessageBox.Show("Settings imported.", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- Support ----------

        private const string DonateUrl = "https://ko-fi.com/mhytee";

        private void Donate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DonateUrl)
                {
                    UseShellExecute = true,  // .NET Framework 4.8 launches the URL via the default browser
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open browser:\n{ex.Message}\n\nURL: {DonateUrl}",
                                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
