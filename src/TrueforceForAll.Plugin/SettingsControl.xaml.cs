using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        // CarIds we've already prompted to submit engine data for in this
        // SimHub session. The save-time prompt only fires for cars with no
        // resolver-cached engine info AND only once per car so a user who
        // declines isn't badgered every save. Cleared on plugin reload.
        private readonly HashSet<string> _enginePromptedThisSession
            = new HashSet<string>(StringComparer.Ordinal);
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
        // Numeric values mirror TrueforcePlugin.SectionKind so we can pass
        // through with a cast.
        private enum EffectKind { Master = 0, Ducking = 1, Audio = 2, Engine = 3, Bumps = 4, Traction = 5, Shift = 6, Abs = 7, SpikeReduction = 8, PitLimiter = 9, Drs = 10, Collision = 11 }
        private readonly bool[] _effectDirty = new bool[12];
        private System.Windows.Controls.Button GetEffectSaveBtn(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:         return MasterSaveBtn;
                case EffectKind.Ducking:        return DuckingSaveBtn;
                case EffectKind.Audio:          return AudioSaveBtn;
                case EffectKind.Engine:         return EngineSaveBtn;
                case EffectKind.Bumps:          return BumpsSaveBtn;
                case EffectKind.Traction:       return TractionSaveBtn;
                case EffectKind.Shift:          return ShiftSaveBtn;
                case EffectKind.Abs:            return AbsSaveBtn;
                case EffectKind.SpikeReduction: return SpikeReductionSaveBtn;
                case EffectKind.PitLimiter:     return PitLimiterSaveBtn;
                case EffectKind.Drs:            return DrsSaveBtn;
                case EffectKind.Collision:      return CollisionSaveBtn;
            }
            return null;
        }
        private System.Windows.Controls.Button GetEffectRevertBtn(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:         return MasterRevertBtn;
                case EffectKind.Ducking:        return DuckingRevertBtn;
                case EffectKind.Audio:          return AudioRevertBtn;
                case EffectKind.Engine:         return EngineRevertBtn;
                case EffectKind.Bumps:          return BumpsRevertBtn;
                case EffectKind.Traction:       return TractionRevertBtn;
                case EffectKind.Shift:          return ShiftRevertBtn;
                case EffectKind.Abs:            return AbsRevertBtn;
                case EffectKind.SpikeReduction: return SpikeReductionRevertBtn;
                case EffectKind.PitLimiter:     return PitLimiterRevertBtn;
                case EffectKind.Drs:            return DrsRevertBtn;
                case EffectKind.Collision:      return CollisionRevertBtn;
            }
            return null;
        }
        private static string EffectLabel(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:         return "Master";
                case EffectKind.Ducking:        return "Sidechain ducking";
                case EffectKind.Audio:          return "Audio capture";
                case EffectKind.Engine:         return "Engine pulse";
                case EffectKind.Bumps:          return "Road bumps";
                case EffectKind.Traction:       return "Traction loss";
                case EffectKind.Shift:          return "Gear shift";
                case EffectKind.Abs:            return "ABS pulse";
                case EffectKind.SpikeReduction: return "FFB spike reduction";
                case EffectKind.PitLimiter:     return "Pit limiter";
                case EffectKind.Drs:            return "DRS";
                case EffectKind.Collision:      return "Collision";
            }
            return "section";
        }
        // Master + Ducking + SpikeReduction are global-only. The save popover
        // hides the per-car option for these (no override concept).
        private static bool SectionHasCarScope(EffectKind w)
            => w != EffectKind.Master && w != EffectKind.Ducking && w != EffectKind.SpikeReduction;

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(TrueforcePlugin plugin) : this()
        {
            _plugin = plugin;

            // Header version readout. Read once at construction; doesn't change
            // at runtime within a session. ToString(3) drops the build/revision
            // components so users see "0.1.0" not "0.1.0.0".
            var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            HeaderVersionText.Text = asmVersion != null ? "v" + asmVersion.ToString(3) : "";

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
            Loaded   += (_, __) =>
            {
                _meterTimer.Start();
                if (_plugin != null) _plugin.AutoRatchetBumped += OnAutoRatchetBumped;
            };
            Unloaded += (_, __) =>
            {
                _meterTimer.Stop();
                if (_plugin != null) _plugin.AutoRatchetBumped -= OnAutoRatchetBumped;
            };
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
                SpikeTamingEnabledCheck.IsChecked = _plugin.Settings?.FfbSpikeTamingEnabled ?? false;
                FfbSpikeLimitSlider.Value = _plugin.Settings?.FfbSpikeMaxLsbPerMs ?? 0.0;
                FfbSpikeLimitText.Text    = FfbSpikeLimitSlider.Value <= 0
                    ? "off"
                    : ((int)FfbSpikeLimitSlider.Value).ToString();
                FfbPeakLimitSlider.Value  = _plugin.Settings?.FfbPeakSoftLimitLsb ?? 0.0;
                FfbPeakLimitText.Text     = FfbPeakLimitSlider.Value <= 0
                    ? "off"
                    : ((int)FfbPeakLimitSlider.Value).ToString();

                // Performance section
                var perf = _plugin.Settings?.Performance;
                if (perf != null)
                {
                    PerfAutoRadio.IsChecked   = perf.Mode == PerformanceMode.Auto;
                    PerfManualRadio.IsChecked = perf.Mode == PerformanceMode.Manual;
                    bool manual = perf.Mode == PerformanceMode.Manual;
                    PerfTfRingSlider.IsEnabled    = manual;
                    PerfAudioRingSlider.IsEnabled = manual;
                    PerfTfRingSlider.Value    = perf.TfRingSize;
                    PerfAudioRingSlider.Value = perf.AudioRingSize;
                    PerfTfRingText.Text    = FormatRing(perf.TfRingSize);
                    PerfAudioRingText.Text = FormatRing(perf.AudioRingSize);
                }

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

                // Forza section
                var fz = _plugin.Settings?.Forza;
                if (fz != null)
                {
                    ForzaEnabledCheck.IsChecked        = fz.Enabled;
                    ForzaPortBox.Text                  = fz.Port.ToString();
                    ForzaBindBox.Text                  = fz.BindAddress ?? "0.0.0.0";
                    ForzaAlwaysListenCheck.IsChecked   = fz.AlwaysListen;
                    ForzaForwardEnabledCheck.IsChecked = fz.ForwardEnabled;
                    ForzaForwardHostBox.Text           = fz.ForwardHost ?? "127.0.0.1";
                    ForzaForwardPortBox.Text           = fz.ForwardPort > 0 ? fz.ForwardPort.ToString() : "";
                }

                // F1 section
                var f1 = _plugin.Settings?.F1;
                if (f1 != null)
                {
                    F1EnabledCheck.IsChecked        = f1.Enabled;
                    F1PortBox.Text                  = f1.Port.ToString();
                    F1BindBox.Text                  = f1.BindAddress ?? "0.0.0.0";
                    F1AlwaysListenCheck.IsChecked   = f1.AlwaysListen;
                    F1ForwardEnabledCheck.IsChecked = f1.ForwardEnabled;
                    F1ForwardHostBox.Text           = f1.ForwardHost ?? "127.0.0.1";
                    F1ForwardPortBox.Text           = f1.ForwardPort > 0 ? f1.ForwardPort.ToString() : "";
                }

                // Header strip context.
                HeaderGameText.Text = string.IsNullOrEmpty(game) ? "(none)" : game;
                HeaderCarText.Text  = string.IsNullOrEmpty(_plugin.ActiveCarId) ? "(none)" : _plugin.ActiveCarId;

                bool carDetected = !string.IsNullOrEmpty(_plugin.ActiveCarId);
                ExportCarPresetButton.IsEnabled   = carDetected;
                ImportCarPresetButton.IsEnabled   = true;
                RefreshCarPresetCombo();

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
                    // Cylinders=0 sentinel = "auto" (use AutoCylinders or fallback).
                    // Slider position 0 displays as "auto"; 1-12 as the numeric value.
                    EngineCylindersSlider.Value       = es.Cylinders;
                    EngineCylindersText.Text          = es.Cylinders == 0 ? "auto" : es.Cylinders.ToString();
                    EnginePitchSlider.Value           = es.Pitch;
                    EnginePitchText.Text              = es.Pitch.ToString("F2");
                    EngineLowpassSlider.Value         = es.LowpassHz;
                    EngineLowpassText.Text            = ((int)es.LowpassHz).ToString();
                    SelectWaveform(EngineWaveformCombo, es.Waveform);
                    if (EngineElectricModeCombo != null)
                        EngineElectricModeCombo.SelectedIndex =
                            es.ElectricMode == ElectricCarMode.Silent ? 1 : 0;

                    // Firing-order pattern controls.
                    if (EngineFiringOrderCheck != null)
                        EngineFiringOrderCheck.IsChecked = es.FiringOrderEnabled;
                    if (EngineConfigCombo != null)
                        EngineConfigCombo.SelectedIndex = EngineConfigToIndex(es.EngineConfig);
                    UpdateFiringPatternReadout(es);
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
                    BumpsSurfaceEnabledCheck.IsChecked      = bs.SurfaceEnabled;
                    BumpsSurfaceGainSlider.Value            = bs.SurfaceGain;
                    BumpsSurfaceGainText.Text               = bs.SurfaceGain.ToString("F2");
                    BumpsSurfaceFreqSlider.Value            = bs.SurfaceFreq;
                    BumpsSurfaceFreqText.Text               = ((int)bs.SurfaceFreq).ToString();
                    SelectWaveform(BumpsSurfaceWaveformCombo, bs.SurfaceWaveform);
                    BumpsSurfaceRumbleScaleSlider.Value     = bs.SurfaceRumbleScale;
                    BumpsSurfaceRumbleScaleText.Text        = bs.SurfaceRumbleScale.ToString("F2");
                    BumpsRumbleStripPulseSlider.Value       = bs.RumbleStripPulseAmp;
                    BumpsRumbleStripPulseText.Text          = bs.RumbleStripPulseAmp.ToString("F2");
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
                // Pit limiter
                var pl = _plugin.ActivePitLimiter;
                if (pl != null && PitLimiterEnabledCheck != null)
                {
                    PitLimiterEnabledCheck.IsChecked    = pl.Enabled;
                    PitLimiterGainSlider.Value          = pl.Gain;
                    PitLimiterGainText.Text             = pl.Gain.ToString("F2");
                    SelectWaveform(PitLimiterWaveformCombo, pl.Waveform);
                    PitLimiterFreqSlider.Value          = pl.Freq;
                    PitLimiterFreqText.Text             = ((int)pl.Freq).ToString();
                    PitLimiterPulseFreqSlider.Value     = pl.PulseFreq;
                    PitLimiterPulseFreqText.Text        = pl.PulseFreq.ToString("F1");
                    PitLimiterDutyCycleSlider.Value     = pl.DutyCycle;
                    PitLimiterDutyCycleText.Text        = pl.DutyCycle.ToString("F2");
                    PitLimiterActiveAmpSlider.Value     = pl.ActiveAmp;
                    PitLimiterActiveAmpText.Text        = pl.ActiveAmp.ToString("F2");
                }
                // DRS
                var drs = _plugin.ActiveDrs;
                if (drs != null && DrsEnabledCheck != null)
                {
                    DrsEnabledCheck.IsChecked       = drs.Enabled;
                    DrsGainSlider.Value             = drs.Gain;
                    DrsGainText.Text                = drs.Gain.ToString("F2");
                    SelectWaveform(DrsWaveformCombo, drs.Waveform);
                    DrsActivationFreqSlider.Value   = drs.ActivationFreq;
                    DrsActivationFreqText.Text      = ((int)drs.ActivationFreq).ToString();
                    DrsActivationMsSlider.Value     = drs.ActivationMs;
                    DrsActivationMsText.Text        = drs.ActivationMs.ToString();
                    DrsActivationAmpSlider.Value    = drs.ActivationAmp;
                    DrsActivationAmpText.Text       = drs.ActivationAmp.ToString("F2");
                    DrsSustainedFreqSlider.Value    = drs.SustainedFreq;
                    DrsSustainedFreqText.Text       = ((int)drs.SustainedFreq).ToString();
                    DrsSustainedAmpSlider.Value     = drs.SustainedAmp;
                    DrsSustainedAmpText.Text        = drs.SustainedAmp.ToString("F2");
                }
                // Collision (per-car overridable like the other effects)
                var coll = _plugin.ActiveCollision;
                if (coll != null && CollisionEnabledCheck != null)
                {
                    CollisionEnabledCheck.IsChecked    = coll.Enabled;
                    CollisionGainSlider.Value          = coll.Gain;
                    CollisionGainText.Text             = coll.Gain.ToString("F2");
                    CollisionMinThresholdSlider.Value  = coll.MinThreshold;
                    CollisionMinThresholdText.Text     = coll.MinThreshold.ToString("F2");
                    CollisionMaxAmpSlider.Value        = coll.MaxAmp;
                    CollisionMaxAmpText.Text           = coll.MaxAmp.ToString("F2");
                    CollisionFreqSlider.Value          = coll.Freq;
                    CollisionFreqText.Text             = ((int)coll.Freq).ToString();
                    CollisionEnvelopeMsSlider.Value    = coll.EnvelopeMs;
                    CollisionEnvelopeMsText.Text       = coll.EnvelopeMs.ToString();
                }

                // Override badges in expander headers — visible only when this
                // section has its own per-car override active.
                AudioOverrideBadge.Visibility    = (_plugin.IsAudioOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                EngineOverrideBadge.Visibility   = (_plugin.IsEngineOverridden   && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                BumpsOverrideBadge.Visibility    = (_plugin.IsBumpsOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                TractionOverrideBadge.Visibility = (_plugin.IsTractionOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                ShiftOverrideBadge.Visibility    = (_plugin.IsShiftOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                AbsOverrideBadge.Visibility      = (_plugin.IsAbsOverridden      && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (PitLimiterOverrideBadge != null)
                    PitLimiterOverrideBadge.Visibility = (_plugin.IsPitLimiterOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (DrsOverrideBadge != null)
                    DrsOverrideBadge.Visibility        = (_plugin.IsDrsOverridden        && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (CollisionOverrideBadge != null)
                    CollisionOverrideBadge.Visibility  = (_plugin.IsCollisionOverridden  && carDetected) ? Visibility.Visible : Visibility.Collapsed;

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

                // Forza UDP section visibility — only relevant when running a
                // Forza title or when AlwaysListen is on (lets users toggle
                // it off without launching Forza first).
                if (ForzaSection != null)
                {
                    var want = _plugin.ShouldShowForzaSection
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (ForzaSection.Visibility != want) ForzaSection.Visibility = want;
                }
                if (F1Section != null)
                {
                    var want = _plugin.ShouldShowF1Section
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (F1Section.Visibility != want) F1Section.Visibility = want;
                }

                // Update banner. Hidden until UpdateChecker confirms a newer
                // GitHub release, then surfaces the version in its label.
                var upd = _plugin.UpdateChecker;
                if (UpdateBanner != null)
                {
                    bool show = upd != null && upd.IsUpdateAvailable;
                    var want = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    if (UpdateBanner.Visibility != want) UpdateBanner.Visibility = want;
                    if (show && UpdateBannerText != null)
                    {
                        string desired = $"Update available: v{upd.LatestVersionDisplay}";
                        if (UpdateBannerText.Text != desired) UpdateBannerText.Text = desired;
                    }
                }

                // "What's new" banner + per-effect NEW badges. Both driven by
                // the plugin's SeenEffects / LastSeenVersion state.
                RefreshChangelogBanner();
                RefreshNewBadges();

                // Forza listener status: the source object exposes packet
                // count + last IsRaceOn. When the source isn't active (game
                // isn't Forza, or AlwaysListen is off + no Forza game), we
                // show "(idle)". This is the user's primary "is my Data Out
                // wiring working" feedback so make it specific.
                // Cylinder auto-detect indicator: shows the live AutoCylinders
                // when telemetry has supplied one and the user has no per-car
                // engine override. Empty otherwise so the slider's own number
                // is the authoritative readout.
                if (EngineCylindersAutoText != null)
                {
                    var ep = _plugin.EnginePulse;
                    string activeCar = _plugin.ActiveCarId;
                    if (ep != null && ep.UseAutoCylinders && ep.AutoCylinders is int auto
                        && auto >= 1 && auto <= 12)
                    {
                        // Build a friendly reason string from the source token.
                        string detectSrc = ep.AutoCylinderSource;
                        string clarification;
                        if (ep.AutoCylinderIsRotary)
                        {
                            int rotors = auto / 2;
                            clarification = $"Auto-detected: {auto} effective cyl ({rotors}-rotor rotary)";
                        }
                        else if (string.Equals(detectSrc, "telemetry", StringComparison.OrdinalIgnoreCase))
                            clarification = $"Auto-detected from telemetry: {auto}";
                        else if (string.Equals(detectSrc, "baked", StringComparison.OrdinalIgnoreCase))
                            clarification = $"Auto-detected: {auto} (from built-in car list)";
                        else if (string.Equals(detectSrc, "cache", StringComparison.OrdinalIgnoreCase))
                            clarification = $"Auto-detected: {auto} (cached from earlier session)";
                        else if (!string.IsNullOrEmpty(detectSrc))
                            clarification = $"Auto-detected: {auto} (heuristic: {detectSrc})";
                        else
                            clarification = $"Auto-detected: {auto}";

                        // Note when slider value is being overridden by auto so users
                        // know their slider isn't in effect.
                        if (auto != ep.Cylinders)
                            clarification += ". Slider value is overridden until you save a per-car engine override.";
                        EngineCylindersAutoText.Text = clarification;
                    }
                    else if (ep != null && ep.UseAutoCylinders
                             && string.IsNullOrEmpty(ep.AutoCylinderSource)
                             && !string.IsNullOrEmpty(activeCar))
                    {
                        // Active car loaded but no auto-detection available —
                        // prompt the user to dial it in via test playback.
                        EngineCylindersAutoText.Text =
                            $"Could not auto-detect cylinder count for '{activeCar}'. "
                            + "Move the slider and use Test to find the value that feels closest, "
                            + "or save a per-car engine override.";
                    }
                    else if (ep != null && !ep.UseAutoCylinders
                             && ep.AutoCylinders is int autoVal
                             && autoVal >= 1 && autoVal <= 12
                             && autoVal != ep.Cylinders)
                    {
                        // Manual override active and the resolver disagrees —
                        // surface the auto value so the user knows what they're
                        // overriding (and can revert by dragging slider to 0).
                        string srcSuffix = string.IsNullOrEmpty(ep.AutoCylinderSource)
                            ? ""
                            : $" ({ep.AutoCylinderSource})";
                        EngineCylindersAutoText.Text =
                            $"Manual override: {ep.Cylinders}. Auto would be {autoVal}{srcSuffix}. "
                            + "Drag slider to 0 to use auto.";
                    }
                    else
                    {
                        EngineCylindersAutoText.Text = "";
                    }

                    // Engine-config resolver coverage. Updates each tick with
                    // a single text line summarizing baked + heuristic hits
                    // for both cyl and config so users can see how complete
                    // the firing-order coverage is for cars they drive.
                    if (EngineCoverageText != null)
                        EngineCoverageText.Text = _plugin.EngineConfigCoverageText;

                    // Report/submit engine-data button. The save-time popup
                    // is the primary submission path; this button is a
                    // persistent fallback for: (a) users who declined the
                    // popup and changed their mind later in the session,
                    // (b) users who clicked Yes but never actually hit
                    // Submit on the Google Form (we can't detect form
                    // submission, so the button stays available as a resume
                    // path), and (c) cars loaded with prior-session
                    // overrides where no save event has fired this session.
                    //
                    // Visibility + label are driven by the shared classifier:
                    //   * Hidden if no car is loaded, the engine section is
                    //     mid-tweak (dirty), or there's nothing worth
                    //     submitting (CONFIRM / no data).
                    //   * "Submit engine data for this car..." in CONTRIB
                    //     mode (no detection, user has tuned).
                    //   * "Report wrong engine data for this car..." in
                    //     CORRECTION mode (detection present, user's
                    //     committed values disagree).
                    // Gating on !engineDirty ensures submissions reflect
                    // committed values, not mid-tweak slider positions.
                    // No popup-shown gate: the popup is modal so it visually
                    // takes priority at the save moment, and dropping the
                    // gate means prior-session overrides have an immediate
                    // entry point without needing to re-save.
                    if (ReportEngineDataButton != null)
                    {
                        var submitState = GetEngineSubmitState();
                        bool engineDirty = _effectDirty[(int)EffectKind.Engine];
                        bool show = !string.IsNullOrEmpty(activeCar)
                                 && !engineDirty
                                 && submitState != EngineSubmitState.None;
                        ReportEngineDataButton.Visibility = show
                            ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
                        if (show)
                        {
                            ReportEngineDataButton.Content =
                                submitState == EngineSubmitState.Contribute
                                    ? "Submit engine data for this car..."
                                    : "Report wrong engine data for this car...";
                        }
                    }
                }

                if (ForzaStatusText != null)
                {
                    var fzSrc = _plugin.TelemetrySource as TrueforceForAll.Core.ForzaUdpTelemetrySource;
                    if (fzSrc == null)
                    {
                        ForzaStatusText.Text = (_plugin.Settings?.Forza?.Enabled ?? true)
                            ? "(idle, not active for current game)"
                            : "(disabled)";
                    }
                    else if (fzSrc.PacketsReceived == 0)
                    {
                        ForzaStatusText.Text =
                            $"Listening on {(_plugin.Settings?.Forza?.BindAddress ?? "0.0.0.0")}:{(_plugin.Settings?.Forza?.Port ?? 0)} — no packets yet (check Forza Data Out config + the troubleshooter below)";
                    }
                    else
                    {
                        ForzaStatusText.Text = fzSrc.LastIsRaceOn
                            ? $"Receiving — {fzSrc.PacketsReceived:N0} packets, driving"
                            : $"Receiving — {fzSrc.PacketsReceived:N0} packets, paused / in menu";
                    }

                    if (ForzaForwardStatusText != null)
                    {
                        var fwd = _plugin.Settings?.Forza;
                        if (fwd == null || !fwd.ForwardEnabled)
                        {
                            ForzaForwardStatusText.Text = "(disabled)";
                        }
                        else if (fzSrc == null)
                        {
                            ForzaForwardStatusText.Text = "(armed, will relay once a Forza title is detected)";
                        }
                        else
                        {
                            ForzaForwardStatusText.Text =
                                $"{fzSrc.PacketsForwarded:N0} packets relayed to {fwd.ForwardHost}:{fwd.ForwardPort}";
                        }
                    }

                    // Discovered-port banner: shown only when the active
                    // source is Forza UDP and the plugin found an alternate.
                    if (ForzaDiscoveryBanner != null)
                    {
                        int alt = _plugin.DiscoveredAlternatePort;
                        bool show = fzSrc != null && alt > 0;
                        ForzaDiscoveryBanner.Visibility = show
                            ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
                        if (show && ForzaDiscoveryText != null)
                        {
                            ForzaDiscoveryText.Text =
                                $"Forza packets detected on port {alt}. Switch to it?";
                        }
                    }
                }

                // F1 listener status. Mirrors the Forza shape but adds a
                // separate yellow rate-warning when packets are arriving
                // below the recommended 60 Hz threshold (so the user knows
                // to bump UDP Send Rate in F1's settings).
                if (F1StatusText != null)
                {
                    var f1Src = _plugin.TelemetrySource as TrueforceForAll.Core.F1UdpTelemetrySource;
                    if (f1Src == null)
                    {
                        F1StatusText.Text = (_plugin.Settings?.F1?.Enabled ?? true)
                            ? "(idle, not active for current game)"
                            : "(disabled)";
                        if (F1RateWarning != null) F1RateWarning.Visibility = System.Windows.Visibility.Collapsed;
                    }
                    else if (f1Src.PacketsReceived == 0)
                    {
                        F1StatusText.Text =
                            $"Listening on {(_plugin.Settings?.F1?.BindAddress ?? "0.0.0.0")}:{(_plugin.Settings?.F1?.Port ?? 0)}. No packets yet (check F1's UDP Telemetry settings).";
                        if (F1RateWarning != null) F1RateWarning.Visibility = System.Windows.Visibility.Collapsed;
                    }
                    else
                    {
                        double hz = f1Src.MeasuredHz;
                        F1StatusText.Text = $"Receiving {f1Src.PacketsReceived:N0} packets at ~{hz:0} Hz";
                        if (F1RateWarning != null)
                        {
                            // Only show the warning once we've seen enough
                            // packets that MeasuredHz isn't a startup
                            // transient. ~50 Hz is the trigger so 60 Hz with
                            // a little jitter doesn't false-fire.
                            bool warn = hz > 0 && hz < TrueforceForAll.Core.F1UdpTelemetrySource.LowRateThresholdHz
                                        && f1Src.PacketsReceived > 30;
                            F1RateWarning.Visibility = warn
                                ? System.Windows.Visibility.Visible
                                : System.Windows.Visibility.Collapsed;
                            if (warn && F1RateWarningText != null)
                            {
                                F1RateWarningText.Text =
                                    $"UDP Send Rate looks low (~{hz:0} Hz). Set it to 60Hz in F1's Telemetry Settings for the most responsive haptics.";
                            }
                        }
                    }

                    // F1 discovered-port banner.
                    if (F1DiscoveryBanner != null)
                    {
                        int alt = _plugin.DiscoveredAlternatePort;
                        bool show = f1Src != null && alt > 0;
                        F1DiscoveryBanner.Visibility = show
                            ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
                        if (show && F1DiscoveryText != null)
                        {
                            F1DiscoveryText.Text =
                                $"F1 packets detected on port {alt}. Switch to it?";
                        }
                    }

                    // F1 forwarder status: mirrors the Forza shape.
                    if (F1ForwardStatusText != null)
                    {
                        var fwd = _plugin.Settings?.F1;
                        if (fwd == null || !fwd.ForwardEnabled)
                        {
                            F1ForwardStatusText.Text = "(disabled)";
                        }
                        else if (f1Src == null)
                        {
                            F1ForwardStatusText.Text = "(armed, will relay once an F1 title is detected)";
                        }
                        else
                        {
                            F1ForwardStatusText.Text =
                                $"{f1Src.PacketsForwarded:N0} packets relayed to {fwd.ForwardHost}:{fwd.ForwardPort}";
                        }
                    }
                }
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

            // Why-is-my-wheel-quiet diagnostic. Always evaluated (cheap).
            // Sits below the status pill, so users see the actual root cause
            // without having to mentally cross-reference five separate
            // status fields.
            string diag = _plugin?.WheelQuietDiagnostic;
            if (WheelQuietDiagnosticBox != null)
            {
                if (string.IsNullOrEmpty(diag))
                {
                    WheelQuietDiagnosticBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    WheelQuietDiagnosticBox.Visibility = Visibility.Visible;
                    if (WheelQuietDiagnosticText.Text != diag)
                        WheelQuietDiagnosticText.Text = diag;
                }
            }

            if (GHubWarningBox != null)
            {
                GHubWarningBox.Visibility = (_plugin?.IsLogitechGHubRunning ?? false)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // Performance counters update every meter tick (cheap — array sum
            // of 60 longs). Doesn't depend on any expander being open.
            UpdatePerfCounters();

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

        // Apply() is called by per-effect handlers AFTER the _suppressEvents
        // guard, so reaching it implies a real user change → push to live
        // device and recompute the affected effect's dirty state. Per-car
        // file is NOT auto-written: saves are explicit (Save… → For this car).
        // First touch on an effect also clears its "NEW" badge.
        private void Apply(EffectKind which)
        {
            _plugin.ApplyActiveCarOverride();
            MarkEffectDirty(which);
            string id = EffectIdFor(which);
            if (id != null && _plugin.IsEffectUnseen(id))
            {
                _plugin.MarkEffectSeen(id);
                RefreshNewBadges();
            }
        }

        // Effect-ID strings shared with EffectChangelog.KnownEffectIds and
        // the per-effect SeenEffects entries. Null for global-only sections
        // (Master, Ducking, SpikeReduction) that don't get a NEW badge.
        private static string EffectIdFor(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Audio:      return "Audio";
                case EffectKind.Engine:     return "Engine";
                case EffectKind.Bumps:      return "Bumps";
                case EffectKind.Traction:   return "Traction";
                case EffectKind.Shift:      return "Shift";
                case EffectKind.Abs:        return "Abs";
                case EffectKind.PitLimiter: return "PitLimiter";
                case EffectKind.Drs:        return "Drs";
                case EffectKind.Collision:  return "Collision";
                default:                    return null;
            }
        }

        // Maps an effect-ID string to its NEW-badge Border in the header.
        // Returns null for unknown IDs.
        private System.Windows.Controls.Border GetNewBadge(string effectId)
        {
            switch (effectId)
            {
                case "Audio":      return AudioNewBadge;
                case "Engine":     return EngineNewBadge;
                case "Bumps":      return BumpsNewBadge;
                case "Traction":   return TractionNewBadge;
                case "Shift":      return ShiftNewBadge;
                case "Abs":        return AbsNewBadge;
                case "PitLimiter": return PitLimiterNewBadge;
                case "Drs":        return DrsNewBadge;
                case "Collision":  return CollisionNewBadge;
                default:           return null;
            }
        }

        /// <summary>Recompute every NEW badge's visibility from the
        /// plugin's SeenEffects state. Called on construction, after
        /// MarkEffectSeen, and from RefreshFromPlugin so external state
        /// changes (e.g. import bringing in a SeenEffects snapshot) refresh
        /// the visible chrome.</summary>
        private void RefreshNewBadges()
        {
            if (_plugin == null) return;
            foreach (var id in EffectChangelog.KnownEffectIds)
            {
                var badge = GetNewBadge(id);
                if (badge == null) continue;
                badge.Visibility = _plugin.IsEffectUnseen(id)
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
        }

        /// <summary>Fires once when the user opens an effect's expander —
        /// counts as an "I've seen this" interaction and clears the NEW
        /// badge for that section. Routes via the Expander.Name suffix
        /// ("EngineExpander" → "Engine") to keep the XAML hookups one-shot.</summary>
        private void EffectExpander_Expanded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var exp = sender as System.Windows.Controls.Expander;
            if (exp == null) return;
            string name = exp.Name ?? "";
            const string suffix = "Expander";
            if (!name.EndsWith(suffix)) return;
            string id = name.Substring(0, name.Length - suffix.Length);
            if (!_plugin.IsEffectUnseen(id)) return;
            _plugin.MarkEffectSeen(id);
            RefreshNewBadges();
        }

        /// <summary>Show / hide the "What's new" banner based on whether
        /// the running build has any changelog entries newer than the
        /// user's stamped LastSeenVersion. Idempotent. Called from
        /// RefreshFromPlugin.</summary>
        private void RefreshChangelogBanner()
        {
            if (_plugin == null || WhatsNewBanner == null) return;
            var pending = _plugin.GetPendingChangelog();
            bool show = pending != null && pending.Count > 0;
            var want = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (WhatsNewBanner.Visibility != want) WhatsNewBanner.Visibility = want;
            if (show && WhatsNewBannerText != null)
            {
                var latest = pending[pending.Count - 1].Version;
                string desired = "What's new in v" + latest.ToString(3);
                if (WhatsNewBannerText.Text != desired) WhatsNewBannerText.Text = desired;
            }
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
            var kind = (TrueforcePlugin.SectionKind)(int)which;
            if (_plugin == null) dirty = true;
            else if (!_plugin.SectionHasAnchor(kind))
            {
                // No game-preset snapshot AND no per-car override anchor —
                // fall back to sticky-true so a global edit without a saved
                // baseline still surfaces as unsaved.
                dirty = true;
            }
            else
            {
                dirty = _plugin.IsSectionDirty(kind);
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
                var kind = (TrueforcePlugin.SectionKind)i;
                bool dirty;
                if (!_plugin.SectionHasAnchor(kind))
                {
                    // No anchor — preserve sticky bit so a no-preset edit
                    // doesn't get auto-cleared by a refresh.
                    dirty = _effectDirty[i];
                }
                else
                {
                    dirty = _plugin.IsSectionDirty(kind);
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
            // Roll car-preset drift into the global indicator independently
            // of per-section bits: per-section dirty bits derive from
            // game-preset comparisons (and become sticky-true when no game
            // preset is active), so a car-preset-only edit might not light
            // up any per-section bit. IsActiveCarPresetDirty checks live vs
            // saved car override directly and stays accurate either way.
            bool carDirty = _plugin?.IsActiveCarPresetDirty() ?? false;
            if (carDirty)
            {
                any = true;
                if (dirtyCount == 0) dirtyCount = 1;
            }
            if (any != _dirty)
            {
                _dirty = any;
                UpdateHeaderPresetDisplay();
            }
            // Drive the main Save preset button's prominent-amber styling +
            // content based on dirty count. Tag="dirty" lights up the amber
            // template trigger; content escalates to "Save all" when 2+
            // sources need committing.
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
        private void SpikeTamingEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetFfbSpikeTamingEnabled(SpikeTamingEnabledCheck.IsChecked == true);
            MarkEffectDirty(EffectKind.SpikeReduction);
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
            MarkEffectDirty(EffectKind.SpikeReduction);
        }
        private void FfbPeakLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbPeakLimitText.Text = v <= 0 ? "off" : ((int)v).ToString();
            _plugin.SetFfbPeakSoftLimitLsb(v);
            MarkEffectDirty(EffectKind.SpikeReduction);
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
        private void PitLimiterTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.PitLimiter);
        private void DrsTest_Click       (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.Drs);
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

        // ---------- Active car preset dropdown / save / delete ----------

        /// <summary>Repopulate the active-car preset dropdown from the
        /// store and select the currently-active preset. Built-in (default)
        /// presets get sorted to the top with a "(default)" suffix already
        /// in their name. Save / Delete enabled state derives from the
        /// active preset's IsBuiltin and whether a car is detected.</summary>
        private void RefreshCarPresetCombo()
        {
            if (_plugin == null) { CarPresetCombo.IsEnabled = false; SaveCarPresetButton.IsEnabled = false; DeleteCarPresetButton.IsEnabled = false; return; }
            string carId = _plugin.ActiveCarId;
            bool   carDetected = !string.IsNullOrEmpty(carId);

            CarPresetCombo.IsEnabled = carDetected;
            SaveCarPresetButton.IsEnabled   = carDetected;
            DeleteCarPresetButton.IsEnabled = false;

            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                CarPresetCombo.Items.Clear();
                if (!carDetected) return;

                var presets = _plugin.GetCarPresets(carId);
                string activeName = _plugin.GetActiveCarPresetName(carId);

                // Built-ins first (alpha), then user presets (alpha).
                var ordered = new List<CarPresetEntry>();
                foreach (var kv in presets) if (kv.Value.IsBuiltin) ordered.Add(kv.Value);
                foreach (var kv in presets) if (!kv.Value.IsBuiltin) ordered.Add(kv.Value);
                ordered.Sort((a, b) =>
                {
                    if (a.IsBuiltin != b.IsBuiltin) return a.IsBuiltin ? -1 : 1;
                    return string.Compare(a.PresetName, b.PresetName, StringComparison.OrdinalIgnoreCase);
                });

                foreach (var entry in ordered)
                {
                    var item = new System.Windows.Controls.ComboBoxItem
                    {
                        Content = entry.PresetName,
                        Tag     = entry.PresetName,
                    };
                    CarPresetCombo.Items.Add(item);
                    if (string.Equals(entry.PresetName, activeName, StringComparison.Ordinal))
                        CarPresetCombo.SelectedItem = item;
                }

                // No matching active in dropdown (e.g. CarDefaults points at
                // a deleted preset) → leave the dropdown blank rather than
                // silently picking the first item.
                if (CarPresetCombo.SelectedItem == null && CarPresetCombo.Items.Count > 0
                    && string.IsNullOrEmpty(activeName))
                {
                    // Active is unset and presets exist; let the user pick.
                }

                // Delete is enabled only when a non-builtin preset is active.
                if (!string.IsNullOrEmpty(activeName) && !_plugin.IsCarPresetBuiltin(carId, activeName))
                    DeleteCarPresetButton.IsEnabled = true;
            }
            finally { _suppressEvents = prevSuppress; }
        }

        private void CarPresetCombo_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            string carId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(carId)) return;
            if (!(CarPresetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)) return;
            if (!(item.Tag is string presetName) || string.IsNullOrEmpty(presetName)) return;
            if (string.Equals(presetName, _plugin.GetActiveCarPresetName(carId), StringComparison.Ordinal))
                return;
            _plugin.SwitchActiveCarPreset(carId, presetName);
            RefreshFromPlugin();
        }

        private void SaveCarPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string carId       = _plugin.ActiveCarId;
            string activeName  = _plugin.GetActiveCarPresetName(carId);
            bool   onBuiltin   = !string.IsNullOrEmpty(activeName)
                                 && _plugin.IsCarPresetBuiltin(carId, activeName);

            if (string.IsNullOrEmpty(activeName) || onBuiltin)
            {
                // No active preset, or active is a built-in: prompt for a
                // new user-preset name and fork.
                string suggestion = onBuiltin
                    ? StripDefaultSuffix(activeName)
                    : carId;
                string newName = PromptForCarPresetName(
                    title: "Save as new car preset",
                    body: onBuiltin
                        ? $"'{activeName}' is a built-in default. Save the current tuning as a new user preset for '{carId}':"
                        : $"Save the current tuning as a new user preset for '{carId}':",
                    initial: suggestion,
                    existing: _plugin.GetCarPresets(carId));
                if (string.IsNullOrEmpty(newName)) return;
                _plugin.SaveActiveCarPresetAs(newName);
            }
            else
            {
                // On a user preset: in-place save.
                if (!_plugin.PersistActiveCarOverride())
                {
                    MessageBox.Show("Save failed (see SimHub log for details).", "Trueforce");
                    return;
                }
            }
            RefreshFromPlugin();
            MaybePromptToSubmitEngineData(carId);
        }

        private void DeleteCarPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string carId      = _plugin.ActiveCarId;
            string activeName = _plugin.GetActiveCarPresetName(carId);
            if (string.IsNullOrEmpty(activeName)) return;
            if (_plugin.IsCarPresetBuiltin(carId, activeName))
            {
                MessageBox.Show(
                    $"'{activeName}' is a built-in default and can't be deleted.",
                    "Trueforce");
                return;
            }
            if (MessageBox.Show(
                    $"Delete user preset '{activeName}' for '{carId}'? The car will fall back to its built-in default if one exists, otherwise to the active game preset's globals.",
                    "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _plugin.DeleteCarPreset(carId, activeName);
            RefreshFromPlugin();
        }

        /// <summary>Strip a trailing " (default)" suffix from a preset name
        /// so the suggested fork name doesn't end up as
        /// "X (default) (something)". Returns the original string when no
        /// suffix is present.</summary>
        private static string StripDefaultSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string suffix = " (default)";
            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }

        /// <summary>Modal name-prompt for car preset save. Disallows empty
        /// names, the suffix "(default)" (built-in territory), and names
        /// already taken by another preset for this car. Returns the
        /// chosen name, or null on cancel / invalid.</summary>
        private string PromptForCarPresetName(string title, string body, string initial,
                                              IReadOnlyDictionary<string, CarPresetEntry> existing)
        {
            var win = new Window
            {
                Title  = title,
                Width  = 460,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode    = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            var box = new System.Windows.Controls.TextBox { Text = initial ?? "", Height = 26 };
            sp.Children.Add(box);
            var error = new TextBlock { FontSize = 11, Foreground = System.Windows.Media.Brushes.Red, Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(error);
            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok     = new Button { Content = "Save", Width = 80, Height = 26, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };
            btnRow.Children.Add(ok); btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);
            win.Content = sp;

            string chosen = null;
            ok.Click += (_, __) =>
            {
                string name = (box.Text ?? "").Trim();
                if (string.IsNullOrEmpty(name)) { error.Text = "Name can't be empty."; return; }
                if (name.EndsWith("(default)", StringComparison.OrdinalIgnoreCase))
                {
                    error.Text = "Names ending with '(default)' are reserved for built-ins.";
                    return;
                }
                if (existing != null && existing.ContainsKey(name))
                {
                    error.Text = $"A preset named '{name}' already exists for this car.";
                    return;
                }
                chosen = name;
                win.DialogResult = true;
                win.Close();
            };
            box.Focus();
            box.SelectAll();
            win.ShowDialog();
            return chosen;
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
            // 0 = auto sentinel (saves Cylinders=0 → resolver / telemetry value
            // wins via EffectiveCylinders). 1-12 = explicit manual override.
            int v = (int)Math.Round(e.NewValue);
            EngineCylindersText.Text = v == 0 ? "auto" : v.ToString();
            _plugin.ActiveEngine.Cylinders = v;
            Apply(EffectKind.Engine);
        }

        // GitHub issue template URL. Pre-fills title + body with the active
        // car's state so users can submit corrections in one click — no need
        // to remember the carId, the source attribution, or the format.
        private const string ReportIssuesBase = "https://github.com/Mhytee/Trueforce-For-All/issues/new";
        private const string RepoUrl          = "https://github.com/Mhytee/Trueforce-For-All";

        private void ReportIssue_Click(object sender, RoutedEventArgs e)
        {
            // Generic "report a bug / feature request" path. Pre-fills version +
            // active game so common context is captured without typing.
            string game = _plugin?.ActiveGame ?? "(none)";
            string carId = _plugin?.ActiveCarId ?? "(none)";
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            string body =
                  "**What happened?**\n<describe the issue>\n\n"
                + "**Steps to reproduce**\n1. \n2. \n\n"
                + "**Expected behavior**\n<what should have happened>\n\n"
                + "---\n"
                + "**Environment**\n"
                + $"- Plugin version: {version}\n"
                + $"- Active game: {game}\n"
                + $"- Active car: {carId}\n"
                + "- SimHub version: <fill in>\n"
                + "- Wheel: <e.g. G PRO, RS50>\n";
            string url = ReportIssuesBase
                       + "?title=" + Uri.EscapeDataString("[bug] ")
                       + "&body="  + Uri.EscapeDataString(body);
            OpenUrl(url);
        }

        private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open browser:\n{ex.Message}\n\nURL: {url}",
                    "Trueforce", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Engine-data submission state. Drives both the Engine-section
        // button (visibility + label) and the save-time prompt:
        //   None       = nothing worth submitting (no detection AND no user
        //                data, OR detection present and user agrees -- the
        //                "CONFIRM" case that adds noise without info).
        //   Contribute = no resolver/telemetry detection, user has tuned
        //                non-default engine values. Form is a contribution.
        //   Correct    = resolver/telemetry produced a value, user has
        //                tuned something that disagrees (cylinder count,
        //                layout, custom pattern, or "we said EV, user
        //                implies combustion"). Form is a correction.
        private enum EngineSubmitState { None, Contribute, Correct }

        // Classifier shared by the button visibility logic, the save-time
        // prompt, and the form body's CONFIRM/CONTRIB/CORRECTION marker.
        // Reads live UI/preset values via _plugin.EnginePulse + ActiveEngine.
        private EngineSubmitState GetEngineSubmitState()
        {
            if (_plugin == null) return EngineSubmitState.None;
            var ep = _plugin.EnginePulse;
            var es = _plugin.ActiveEngine;
            if (ep == null || es == null) return EngineSubmitState.None;

            int? autoCyl = ep.AutoCylinders;
            string cylSrc = ep.AutoCylinderSource;
            int sliderCyl = es.Cylinders;
            var userCfg = es.EngineConfig;
            string customRaw = es.CustomFiringPattern;
            bool autoEv = ep.IsElectric;
            bool detected = !string.IsNullOrEmpty(cylSrc);
            string detectedLayout = detected ? ep.EngineConfig.ToString() : null;

            bool cylDiff = (sliderCyl != 0 && autoCyl.HasValue && sliderCyl != autoCyl.Value)
                        || (sliderCyl != 0 && !autoCyl.HasValue);
            bool layoutDiff = userCfg != Effects.EngineConfig.Auto
                           && (!detected || userCfg.ToString() != detectedLayout);
            bool customDiff = userCfg == Effects.EngineConfig.Custom
                           && !string.IsNullOrEmpty(customRaw);
            // EV mismatch: resolver flagged this car as electric but the
            // user has set a non-zero cylinder count or picked a layout.
            // That's a vote that the EV flag is wrong, worth surfacing.
            bool evDiff = autoEv && (sliderCyl >= 1 || userCfg != Effects.EngineConfig.Auto);

            bool anyDiff = cylDiff || layoutDiff || customDiff || evDiff;
            bool userHasData = sliderCyl > 0
                            || userCfg != Effects.EngineConfig.Auto
                            || !string.IsNullOrEmpty(customRaw);

            if (!detected)
                return userHasData ? EngineSubmitState.Contribute : EngineSubmitState.None;
            return anyDiff ? EngineSubmitState.Correct : EngineSubmitState.None;
        }

        // Engine-data submission target: a Google Form with a single
        // long-answer field. We URL-encode the structured markdown body
        // and prefill the field via &entry.<id>=<body>. No GitHub account
        // required; submissions land in a Google Sheet for batch triage.
        // Form: TF4ALL Engine Data (https://forms.gle/yeQ8CNNyp7QRBxnj9).
        private const string EngineDataFormUrl =
            "https://docs.google.com/forms/d/e/1FAIpQLSfgNM3AfFV9uIGYhajQtAxpE_e1Lo34-mFtsGrbP1u-nH60ng/viewform";
        private const string EngineDataFormEntry = "entry.551133954";

        // Submit engine data for the active car. Captures both what the bake/
        // resolver auto-detected AND what the user has selected via the
        // dropdowns / slider. No "FILL IN" placeholders — the user's UI
        // values ARE the proposed values; submission is one click on the
        // form. Maintainers read the response sheet to find diffs.
        private void ReportEngineDataButton_Click(object sender, RoutedEventArgs e)
        {
            OpenEngineDataForm();
        }

        // Save-time prompt: nudges the user to submit their committed engine
        // tuning. Fires after a car-preset save (or an Engine-section save)
        // because the values just written to disk are, by definition, the
        // user's settled answer for this car. That's a much higher-signal
        // moment than catching a click on a discoverable button while values
        // are still being tweaked.
        //
        // Fires for both submission states the form actually wants:
        //   Contribute: no detection, user added data. "Submit engine data".
        //   Correct: detection present, user's saved values disagree.
        //            "Report wrong engine data".
        // CONFIRM cases (user agrees with detection) and "no data" cases
        // skip the prompt: those add noise without info.
        //
        // Dedupes per car per session so a user who declines isn't badgered
        // on every subsequent save of the same car.
        private void MaybePromptToSubmitEngineData(string carId)
        {
            if (_plugin == null || string.IsNullOrEmpty(carId)) return;
            var state = GetEngineSubmitState();
            if (state == EngineSubmitState.None) return;
            if (!_enginePromptedThisSession.Add(carId)) return;

            string ask;
            if (state == EngineSubmitState.Contribute)
            {
                ask = $"We don't have engine data for '{carId}' yet.\n\n"
                    + "Submit your settings to help other users? Opens a Google form "
                    + "pre-filled with your tuning. Just hit Submit on the form, no account needed.";
            }
            else
            {
                ask = $"You've corrected the auto-detected engine data for '{carId}'.\n\n"
                    + "Submit your correction to help other users? Opens a Google form "
                    + "pre-filled with your tuning. Just hit Submit on the form, no account needed.";
            }

            var r = MessageBox.Show(
                ask,
                "Trueforce: share engine data?",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) OpenEngineDataForm();
        }

        private void OpenEngineDataForm()
        {
            if (_plugin == null) return;
            string carId  = _plugin.ActiveCarId ?? "(no car loaded)";
            string game   = _plugin.ActiveGame  ?? "(unknown)";
            var ep        = _plugin.EnginePulse;
            var es        = _plugin.ActiveEngine;
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

            // What the bake / resolver said.
            int? autoCyl       = ep?.AutoCylinders;
            string cylSrc      = ep?.AutoCylinderSource ?? "";
            bool autoEv        = ep != null && ep.IsElectric;
            var autoLayout     = ep?.EngineConfig ?? Effects.EngineConfig.Auto;
            bool layoutDetected = !string.IsNullOrEmpty(cylSrc) && !autoEv && autoLayout != Effects.EngineConfig.Auto;

            // What the user has on the panel (their committed preset values).
            // ElectricMode (silent / muted hum) is intentionally not collected:
            // it's a per-user response-curve preference, not a fact about the
            // car, so it doesn't help the bake even on EV submissions.
            int sliderCyl = es?.Cylinders ?? 0;
            var userCfg   = es?.EngineConfig ?? Effects.EngineConfig.Auto;
            string customRaw  = es?.CustomFiringPattern ?? "";
            string customName = (es?.CustomFiringPatternName ?? "").Trim();
            bool userImpliesCombustion = sliderCyl >= 1 || userCfg != Effects.EngineConfig.Auto;

            // Build the proposed-changes block as "before -> after" lines.
            // Plain text only -- Google Forms long-answer fields don't render
            // markdown, so any **bold** or `code` ticks would just show as
            // literal characters in the response sheet.
            string CylDisplay(int? n)        => n.HasValue ? n.Value.ToString() : "not detected";
            string LayoutDetectedDisplay()   =>
                  autoEv             ? "not detected (flagged electric)"
                : !layoutDetected    ? "not detected"
                                     : EngineConfigFriendlyName(autoLayout);

            var diff = new System.Collections.Generic.List<string>();
            if (sliderCyl != 0 && (!autoCyl.HasValue || sliderCyl != autoCyl.Value))
                diff.Add($"Cylinders: {CylDisplay(autoCyl)} -> {sliderCyl}");
            if (userCfg != Effects.EngineConfig.Auto && (!layoutDetected || userCfg != autoLayout))
                diff.Add($"Engine layout: {LayoutDetectedDisplay()} -> {EngineConfigFriendlyName(userCfg)}");
            if (userCfg == Effects.EngineConfig.Custom && !string.IsNullOrEmpty(customName))
                diff.Add($"Custom pattern name: {customName}");
            if (userCfg == Effects.EngineConfig.Custom && !string.IsNullOrEmpty(customRaw))
                diff.Add($"Custom firing pattern: {customRaw}");
            if (autoEv && userImpliesCombustion)
                diff.Add("Electric flag: yes -> no (user set cylinders/layout)");

            // CONFIRM is unreachable under the new UX (button hidden + popup
            // skipped when state is None), so the two real categories are:
            //   CORRECTION = resolver had detection, user is changing it
            //   CONTRIB    = no detection, user filled in from scratch
            string category = !string.IsNullOrEmpty(cylSrc) ? "CORRECTION" : "CONTRIB";

            // Header line with the sortable bits, then the diff lines as a
            // plain list, then a one-line source attribution (only for
            // CORRECTION -- CONTRIB has nothing to reference), then Notes.
            // No "Reference" dump: every value we'd have shown is either
            // already on the arrow's left side or duplicated from the
            // user's panel settings.
            //
            // Two version stamps: plugin assembly version covers the bake
            // list + resolver code; CarCylinderResolver.CurrentCacheVersion
            // is bumped whenever heuristics change and forces the
            // persistent cache to rebuild. Together they let the maintainer
            // tell exactly which detection generation produced the values
            // the user is correcting (e.g. "plugin v1.5 (data v3)" came
            // from a build that shipped v3 heuristics).
            string body = $"[{category}] {carId}  |  {game}  |  plugin v{version} (data v{CarCylinderResolver.CurrentCacheVersion})\n\n";

            if (diff.Count > 0)
            {
                body += string.Join("\n", diff) + "\n\n";
            }

            // Source attribution: tells the maintainer how confident the
            // pre-existing detection was (baked = curated list, heuristic =
            // pattern-matched, telemetry = sim-supplied, ev = electric tag).
            // Only meaningful when something WAS detected, so it's skipped
            // for CONTRIB.
            if (!string.IsNullOrEmpty(cylSrc))
            {
                body += $"Detection source: {cylSrc}\n\n";
            }

            body += "Notes (engine codename, mod page link, anything else):\n\n";

            string url = EngineDataFormUrl
                       + "?usp=pp_url&" + EngineDataFormEntry + "="
                       + Uri.EscapeDataString(body);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                // Browser launch failed (rare). Try to copy the prefilled
                // URL to the clipboard so the user can paste it instead of
                // having to retype the whole submission. Fall back to the
                // bare form URL if clipboard access is also unavailable.
                string clipboardNote;
                try
                {
                    Clipboard.SetText(url);
                    clipboardNote = "The full prefilled URL has been copied to your clipboard. Paste it into your browser.";
                }
                catch
                {
                    clipboardNote = "Open this URL manually:\n" + EngineDataFormUrl;
                }
                MessageBox.Show(
                    $"Couldn't open browser:\n{ex.Message}\n\n{clipboardNote}",
                    "Trueforce", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
        private void EngineElectricMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveEngine.ElectricMode = EngineElectricModeCombo.SelectedIndex == 1
                ? ElectricCarMode.Silent
                : ElectricCarMode.MutedHum;
            Apply(EffectKind.Engine);
        }

        // ---------- Firing-order pattern (Batch 1) ----------

        private void EngineFiringOrder_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveEngine.FiringOrderEnabled = EngineFiringOrderCheck.IsChecked == true;
            Apply(EffectKind.Engine);
            UpdateFiringPatternReadout(_plugin.ActiveEngine);
        }

        private void EngineConfig_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var es = _plugin.ActiveEngine;
            es.EngineConfig = IndexToEngineConfig(EngineConfigCombo.SelectedIndex);
            Apply(EffectKind.Engine);
            UpdateFiringPatternReadout(es);
        }

        // User-edited the textbox under Custom layout; parse and apply on focus loss.
        private void EngineFiringPattern_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var es = _plugin.ActiveEngine;
            if (es.EngineConfig != Effects.EngineConfig.Custom) return;   // editable only in Custom
            es.CustomFiringPattern = EngineFiringPatternText.Text ?? "";
            Apply(EffectKind.Engine);
            UpdateFiringPatternReadout(es);
        }

        // Optional name for the custom firing pattern. Only meaningful when
        // Custom is the active layout; the input row is hidden otherwise.
        private void EngineFiringPatternName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var es = _plugin.ActiveEngine;
            if (es.EngineConfig != Effects.EngineConfig.Custom) return;
            es.CustomFiringPatternName = (EngineFiringPatternNameText.Text ?? "").Trim();
            Apply(EffectKind.Engine);
        }

        // Sync the read-only-ness, content, and tooltip of the pattern textbox
        // to the current engine config + active resolved pattern. Also
        // toggles the optional pattern-name row, which is only meaningful
        // (and only visible) when Custom is the active layout.
        private void UpdateFiringPatternReadout(EnginePulseSettings es)
        {
            if (EngineFiringPatternText == null || es == null) return;
            bool isCustom = es.EngineConfig == Effects.EngineConfig.Custom;
            EngineFiringPatternText.IsReadOnly = !isCustom;
            // When Custom, show the user's saved string (lets them edit / paste).
            // Otherwise, show what the resolver picked for the active cyl + config
            // so they can copy / submit it back to us if it sounds wrong.
            string display;
            if (isCustom)
            {
                display = es.CustomFiringPattern ?? "";
            }
            else
            {
                int cyl = _plugin.EnginePulse?.EffectiveCylinders ?? 0;
                if (cyl < 1) cyl = 4;
                var pat = Effects.FiringPatternDb.Resolve(cyl, es.EngineConfig);
                display = pat == null
                    ? ""
                    : $"{pat.Name}: {Effects.FiringPatternDb.Format(pat)}";
            }
            // Suppress LostFocus echo when we programmatically update the text.
            bool oldSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                EngineFiringPatternText.Text = display;
                if (EngineFiringPatternNameRow != null)
                {
                    EngineFiringPatternNameRow.Visibility = isCustom
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                }
                if (EngineFiringPatternNameText != null)
                {
                    EngineFiringPatternNameText.Text = isCustom
                        ? (es.CustomFiringPatternName ?? "")
                        : "";
                }
            }
            finally { _suppressEvents = oldSuppress; }
        }

        // EngineConfig <-> dropdown index. Order MUST match the XAML
        // ComboBoxItem list under EngineConfigCombo.
        private static Effects.EngineConfig IndexToEngineConfig(int i)
        {
            switch (i)
            {
                case 0:  return Effects.EngineConfig.Auto;
                case 1:  return Effects.EngineConfig.Inline;
                case 2:  return Effects.EngineConfig.Boxer;
                case 3:  return Effects.EngineConfig.V60;
                case 4:  return Effects.EngineConfig.V90Even;
                case 5:  return Effects.EngineConfig.V8CrossPlane;
                case 6:  return Effects.EngineConfig.V8FlatPlane;
                case 7:  return Effects.EngineConfig.V6OddFire;
                case 8:  return Effects.EngineConfig.VTwin90;
                case 9:  return Effects.EngineConfig.VTwin45;
                case 10: return Effects.EngineConfig.Rotary;
                case 11: return Effects.EngineConfig.Custom;
                default: return Effects.EngineConfig.Auto;
            }
        }
        private static int EngineConfigToIndex(Effects.EngineConfig c)
        {
            switch (c)
            {
                case Effects.EngineConfig.Auto:         return 0;
                case Effects.EngineConfig.Inline:       return 1;
                case Effects.EngineConfig.Boxer:        return 2;
                case Effects.EngineConfig.V60:          return 3;
                case Effects.EngineConfig.V90Even:      return 4;
                case Effects.EngineConfig.V8CrossPlane: return 5;
                case Effects.EngineConfig.V8FlatPlane:  return 6;
                case Effects.EngineConfig.V6OddFire:    return 7;
                case Effects.EngineConfig.VTwin90:      return 8;
                case Effects.EngineConfig.VTwin45:      return 9;
                case Effects.EngineConfig.Rotary:       return 10;
                case Effects.EngineConfig.Custom:       return 11;
                default:                                return 0;
            }
        }

        // Human-readable layout label used in the engine-data submission
        // body. Mirrors the dropdown text users see, minus the parenthetical
        // descriptions (kept compact for the form's plain-text rendering).
        private static string EngineConfigFriendlyName(Effects.EngineConfig c)
        {
            switch (c)
            {
                case Effects.EngineConfig.Auto:         return "Auto";
                case Effects.EngineConfig.Inline:       return "Inline / even-fire";
                case Effects.EngineConfig.Boxer:        return "Boxer";
                case Effects.EngineConfig.V60:          return "V60 even-fire";
                case Effects.EngineConfig.V90Even:      return "V90 even-fire";
                case Effects.EngineConfig.V8CrossPlane: return "V8 cross-plane";
                case Effects.EngineConfig.V8FlatPlane:  return "V8 flat-plane";
                case Effects.EngineConfig.V6OddFire:    return "V6 odd-fire";
                case Effects.EngineConfig.VTwin90:      return "V-twin 90";
                case Effects.EngineConfig.VTwin45:      return "V-twin 45";
                case Effects.EngineConfig.Rotary:       return "Rotary";
                case Effects.EngineConfig.Custom:       return "Custom";
                default:                                return c.ToString();
            }
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
        private void BumpsSurfaceEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveBumps.SurfaceEnabled = BumpsSurfaceEnabledCheck.IsChecked == true;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceGainText.Text = v.ToString("F2");
            _plugin.ActiveBumps.SurfaceGain = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceFreqText.Text = ((int)v).ToString();
            _plugin.ActiveBumps.SurfaceFreq = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveBumps.SurfaceWaveform = WaveformOf(BumpsSurfaceWaveformCombo);
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceRumbleScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceRumbleScaleText.Text = v.ToString("F2");
            _plugin.ActiveBumps.SurfaceRumbleScale = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsRumbleStripPulseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsRumbleStripPulseText.Text = v.ToString("F2");
            _plugin.ActiveBumps.RumbleStripPulseAmp = v;
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

        // ---------- Pit limiter ----------

        private void PitLimiterEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActivePitLimiter.Enabled = PitLimiterEnabledCheck.IsChecked == true;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterGainText.Text = v.ToString("F2");
            _plugin.ActivePitLimiter.Gain = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActivePitLimiter.Waveform = WaveformOf(PitLimiterWaveformCombo);
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterFreqText.Text = ((int)v).ToString();
            _plugin.ActivePitLimiter.Freq = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterPulseFreqText.Text = v.ToString("F1");
            _plugin.ActivePitLimiter.PulseFreq = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterDutyCycleText.Text = v.ToString("F2");
            _plugin.ActivePitLimiter.DutyCycle = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterActiveAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterActiveAmpText.Text = v.ToString("F2");
            _plugin.ActivePitLimiter.ActiveAmp = v;
            Apply(EffectKind.PitLimiter);
        }

        // ---------- DRS ----------

        private void DrsEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveDrs.Enabled = DrsEnabledCheck.IsChecked == true;
            Apply(EffectKind.Drs);
        }
        private void DrsGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsGainText.Text = v.ToString("F2");
            _plugin.ActiveDrs.Gain = v;
            Apply(EffectKind.Drs);
        }
        private void DrsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveDrs.Waveform = WaveformOf(DrsWaveformCombo);
            Apply(EffectKind.Drs);
        }
        private void DrsActivationFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsActivationFreqText.Text = ((int)v).ToString();
            _plugin.ActiveDrs.ActivationFreq = v;
            Apply(EffectKind.Drs);
        }
        private void DrsActivationMsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)e.NewValue;
            DrsActivationMsText.Text = v.ToString();
            _plugin.ActiveDrs.ActivationMs = v;
            Apply(EffectKind.Drs);
        }
        private void DrsActivationAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsActivationAmpText.Text = v.ToString("F2");
            _plugin.ActiveDrs.ActivationAmp = v;
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsSustainedFreqText.Text = ((int)v).ToString();
            _plugin.ActiveDrs.SustainedFreq = v;
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsSustainedAmpText.Text = v.ToString("F2");
            _plugin.ActiveDrs.SustainedAmp = v;
            Apply(EffectKind.Drs);
        }

        // ---------- Collision ----------

        private void CollisionEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.ActiveCollision.Enabled = CollisionEnabledCheck.IsChecked == true;
            Apply(EffectKind.Collision);
        }
        private void CollisionGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionGainText.Text = v.ToString("F2");
            _plugin.ActiveCollision.Gain = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionMinThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionMinThresholdText.Text = v.ToString("F2");
            _plugin.ActiveCollision.MinThreshold = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionMaxAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionMaxAmpText.Text = v.ToString("F2");
            _plugin.ActiveCollision.MaxAmp = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionFreqText.Text = ((int)v).ToString();
            _plugin.ActiveCollision.Freq = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionEnvelopeMsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)e.NewValue;
            CollisionEnvelopeMsText.Text = v.ToString();
            _plugin.ActiveCollision.EnvelopeMs = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.Collision);

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

        // ---------- Forza UDP listener ----------

        private void ForzaEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            _plugin.Settings.Forza.Enabled = ForzaEnabledCheck.IsChecked == true;
            _plugin.ApplyForzaSettings();
        }

        private void ForzaAlwaysListen_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            _plugin.Settings.Forza.AlwaysListen = ForzaAlwaysListenCheck.IsChecked == true;
            _plugin.ApplyForzaSettings();
        }

        private void ForzaPort_LostFocus(object sender, RoutedEventArgs e) => CommitForzaPort();
        private void ForzaPort_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaPort();
        }
        private void CommitForzaPort()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaPortBox.Text?.Trim();
            if (int.TryParse(raw, out int port) && port >= 1 && port <= 65535)
            {
                if (_plugin.Settings.Forza.Port != port)
                {
                    _plugin.Settings.Forza.Port = port;
                    _plugin.ApplyForzaSettings();
                }
            }
            else
            {
                // Reject invalid input by snapping back to the saved value.
                ForzaPortBox.Text = _plugin.Settings.Forza.Port.ToString();
            }
        }

        private void ForzaBind_LostFocus(object sender, RoutedEventArgs e) => CommitForzaBind();
        private void ForzaBind_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaBind();
        }
        private void CommitForzaBind()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaBindBox.Text?.Trim() ?? "";
            // Empty / blank → 0.0.0.0 (any). Garbage stays accepted but the
            // plugin's parser falls back to Any too, so it's consistent.
            if (string.IsNullOrWhiteSpace(raw)) raw = "0.0.0.0";
            if (_plugin.Settings.Forza.BindAddress != raw)
            {
                _plugin.Settings.Forza.BindAddress = raw;
                _plugin.ApplyForzaSettings();
            }
        }

        // ---- Port discovery banner handlers ----
        // Shared between Forza and F1: the plugin exposes a single
        // DiscoveredAlternatePort and AdoptDiscoveredAlternatePort handles
        // both kinds based on the active source type.
        private void ForzaDiscoveryAdopt_Click(object sender, RoutedEventArgs e)
            => _plugin?.AdoptDiscoveredAlternatePort();
        private void ForzaDiscoveryDismiss_Click(object sender, RoutedEventArgs e)
            => _plugin?.DismissDiscoveredAlternatePort();
        private void F1DiscoveryAdopt_Click(object sender, RoutedEventArgs e)
            => _plugin?.AdoptDiscoveredAlternatePort();
        private void F1DiscoveryDismiss_Click(object sender, RoutedEventArgs e)
            => _plugin?.DismissDiscoveredAlternatePort();

        // ---- F1 UDP handlers ----
        // Mirror the Forza ones; no forwarder field (F1 doesn't share a
        // single-destination limitation the way Forza does).

        private void F1Enabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.F1 == null) return;
            _plugin.Settings.F1.Enabled = F1EnabledCheck.IsChecked == true;
            _plugin.ApplyF1Settings();
        }

        private void F1AlwaysListen_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.F1 == null) return;
            _plugin.Settings.F1.AlwaysListen = F1AlwaysListenCheck.IsChecked == true;
            _plugin.ApplyF1Settings();
        }

        private void F1Port_LostFocus(object sender, RoutedEventArgs e) => CommitF1Port();
        private void F1Port_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitF1Port();
        }
        private void CommitF1Port()
        {
            if (_suppressEvents || _plugin?.Settings?.F1 == null) return;
            string raw = F1PortBox.Text?.Trim();
            if (int.TryParse(raw, out int port) && port >= 1 && port <= 65535)
            {
                if (_plugin.Settings.F1.Port != port)
                {
                    _plugin.Settings.F1.Port = port;
                    _plugin.ApplyF1Settings();
                }
            }
            else
            {
                F1PortBox.Text = _plugin.Settings.F1.Port.ToString();
            }
        }

        private void F1Bind_LostFocus(object sender, RoutedEventArgs e) => CommitF1Bind();
        private void F1Bind_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitF1Bind();
        }
        private void CommitF1Bind()
        {
            if (_suppressEvents || _plugin?.Settings?.F1 == null) return;
            string raw = F1BindBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) raw = "0.0.0.0";
            if (_plugin.Settings.F1.BindAddress != raw)
            {
                _plugin.Settings.F1.BindAddress = raw;
                _plugin.ApplyF1Settings();
            }
        }

        private void F1ForwardEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.F1 == null) return;
            _plugin.Settings.F1.ForwardEnabled = F1ForwardEnabledCheck.IsChecked == true;
            _plugin.ApplyF1Settings();
        }

        private void F1ForwardHost_LostFocus(object sender, RoutedEventArgs e) => CommitF1ForwardHost();
        private void F1ForwardHost_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitF1ForwardHost();
        }
        private void CommitF1ForwardHost()
        {
            if (_suppressEvents || _plugin?.Settings?.F1 == null) return;
            string raw = F1ForwardHostBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) raw = "127.0.0.1";
            if (_plugin.Settings.F1.ForwardHost != raw)
            {
                _plugin.Settings.F1.ForwardHost = raw;
                _plugin.ApplyF1Settings();
            }
        }

        private void F1ForwardPort_LostFocus(object sender, RoutedEventArgs e) => CommitF1ForwardPort();
        private void F1ForwardPort_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitF1ForwardPort();
        }
        private void CommitF1ForwardPort()
        {
            if (_suppressEvents || _plugin?.Settings?.F1 == null) return;
            string raw = F1ForwardPortBox.Text?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                if (_plugin.Settings.F1.ForwardPort != 0)
                {
                    _plugin.Settings.F1.ForwardPort = 0;
                    _plugin.ApplyF1Settings();
                }
                return;
            }
            if (int.TryParse(raw, out int port) && port >= 1 && port <= 65535)
            {
                if (_plugin.Settings.F1.ForwardPort != port)
                {
                    _plugin.Settings.F1.ForwardPort = port;
                    _plugin.ApplyF1Settings();
                }
            }
            else
            {
                F1ForwardPortBox.Text = _plugin.Settings.F1.ForwardPort > 0 ? _plugin.Settings.F1.ForwardPort.ToString() : "";
            }
        }

        private void ForzaForwardEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            _plugin.Settings.Forza.ForwardEnabled = ForzaForwardEnabledCheck.IsChecked == true;
            _plugin.ApplyForzaSettings();
        }

        private void ForzaForwardHost_LostFocus(object sender, RoutedEventArgs e) => CommitForzaForwardHost();
        private void ForzaForwardHost_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaForwardHost();
        }
        private void CommitForzaForwardHost()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaForwardHostBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) raw = "127.0.0.1";
            if (_plugin.Settings.Forza.ForwardHost != raw)
            {
                _plugin.Settings.Forza.ForwardHost = raw;
                _plugin.ApplyForzaSettings();
            }
        }

        private void ForzaForwardPort_LostFocus(object sender, RoutedEventArgs e) => CommitForzaForwardPort();
        private void ForzaForwardPort_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaForwardPort();
        }
        private void CommitForzaForwardPort()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaForwardPortBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                // Blank = clear / disable target. Listener stays open but
                // forwarder no-ops (BuildForzaForwardEndpoint returns null).
                if (_plugin.Settings.Forza.ForwardPort != 0)
                {
                    _plugin.Settings.Forza.ForwardPort = 0;
                    _plugin.ApplyForzaSettings();
                }
                return;
            }
            if (int.TryParse(raw, out int port) && port >= 1 && port <= 65535)
            {
                if (_plugin.Settings.Forza.ForwardPort != port)
                {
                    _plugin.Settings.Forza.ForwardPort = port;
                    _plugin.ApplyForzaSettings();
                }
            }
            else
            {
                // Snap back to saved value on invalid input so the textbox
                // doesn't keep showing user's typo.
                ForzaForwardPortBox.Text = _plugin.Settings.Forza.ForwardPort > 0
                    ? _plugin.Settings.Forza.ForwardPort.ToString()
                    : "";
            }
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

        // Result of the save-scope modal: either Cancel, "save just the
        // section the user clicked," or "save every dirty section." The
        // modal only appears when there's actually a choice to make
        // (i.e. more than one section is dirty AND the save target is an
        // overwrite path -- forks always capture whole state).
        private enum SaveScope { Cancel, JustThis, SaveAll }

        // Detect dirty sections other than the targeted one and, if any,
        // ask the user whether they want to save just the targeted section
        // or all dirty sections. Returns SaveScope.JustThis without
        // prompting when only the targeted section is dirty -- the modal
        // would have no choices to offer.
        private SaveScope PromptSaveScope(EffectKind targetSection, string saveTargetLabel)
        {
            var others = new List<EffectKind>();
            for (int i = 0; i < _effectDirty.Length; i++)
            {
                if (i == (int)targetSection) continue;
                if (_effectDirty[i]) others.Add((EffectKind)i);
            }
            if (others.Count == 0) return SaveScope.JustThis;

            string othersLabel = string.Join(", ", others.ConvertAll(EffectLabel));
            string targetLabel = EffectLabel(targetSection);
            int totalDirty = others.Count + 1;

            var win = new Window
            {
                Title = "Save scope",
                Width = 480,
                Height = 230,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode    = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = $"You also have unsaved changes in: {othersLabel}.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"What should we save to {saveTargetLabel}?",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var result = SaveScope.Cancel;
            var justBtn = new Button
            {
                Content = $"Save just {targetLabel}",
                Height = 32, Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "Writes only the section you clicked Save next to. Other sections stay dirty until you save them individually.",
            };
            var allBtn = new Button
            {
                Content = $"Save all {totalDirty} dirty sections",
                Height = 32, Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "Writes every section that currently has unsaved changes.",
            };
            sp.Children.Add(justBtn);
            sp.Children.Add(allBtn);

            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);

            win.Content = sp;
            justBtn.Click += (s, a) => { result = SaveScope.JustThis; win.DialogResult = true; };
            allBtn.Click  += (s, a) => { result = SaveScope.SaveAll;  win.DialogResult = true; };
            win.ShowDialog();
            return result;
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
                // Fork paths (no preset / built-in) always capture whole
                // state -- a freshly-forked preset IS the snapshot. The
                // scope modal only applies when overwriting an existing
                // user preset, where "just this section" is a real choice.
                if (!hasPreset)
                {
                    _plugin.PromoteSectionToGlobal((TrueforcePlugin.SectionKind)(int)which);
                    SaveAsNewPresetFromUi();
                    win.DialogResult = true;
                    return;
                }
                if (builtin)
                {
                    _plugin.PromoteSectionToGlobal((TrueforcePlugin.SectionKind)(int)which);
                    ForkAndSaveAsGamePreset();
                    win.DialogResult = true;
                    return;
                }

                // Overwrite path: ask whether to save just this section or
                // all dirty sections. PromptSaveScope returns JustThis
                // immediately when only the targeted section is dirty.
                var scope = PromptSaveScope((EffectKind)(int)which, $"preset '{activeP}'");
                if (scope == SaveScope.Cancel) { win.DialogResult = true; return; }

                _plugin.PromoteSectionToGlobal((TrueforcePlugin.SectionKind)(int)which);
                if (scope == SaveScope.JustThis)
                {
                    // Patch only the targeted section into the in-memory
                    // snapshot + write GeneralSettings. Other sections in
                    // the preset stay at their previously-saved values, so
                    // their dirty bits remain set after refresh.
                    _plugin.SaveSectionToActivePreset((TrueforcePlugin.SectionKind)(int)which);
                    ClearEffectDirty(which);
                    RefreshFromPlugin();
                }
                else
                {
                    // Save-all: whole-state snapshot. Lift every dirty
                    // override up to global first so user's active values
                    // (override or global) all land in the new preset.
                    for (int i = 0; i < _effectDirty.Length; i++)
                        if (_effectDirty[i] && i != (int)which)
                            _plugin.PromoteSectionToGlobal((TrueforcePlugin.SectionKind)i);
                    UpdateActivePresetFromUi();
                }
                win.DialogResult = true;
            };

            win.ShowDialog();
        }

        /// <summary>Per-car save for one effect: writes the section's
        /// current values to the active car preset's file. Forks to a new
        /// user preset (whole-state) when on a built-in / no preset yet.
        /// On a user car preset, asks the user whether to save just this
        /// section or every dirty section.</summary>
        private void ApplyEffectSaveForCar(EffectKind which)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string carId      = _plugin.ActiveCarId;
            string activeName = _plugin.GetActiveCarPresetName(carId);
            bool   onBuiltin  = !string.IsNullOrEmpty(activeName)
                                && _plugin.IsCarPresetBuiltin(carId, activeName);
            bool   isFork     = string.IsNullOrEmpty(activeName) || onBuiltin;

            if (isFork)
            {
                // Fork to a new user car preset: whole-state. The new file
                // captures every section the user has tuned, since the
                // preset IS the snapshot of the user's intent at fork time.
                _plugin.SnapshotSectionToCarOverride((TrueforcePlugin.SectionKind)(int)which);
                string suggestion = onBuiltin ? StripDefaultSuffix(activeName) : carId;
                string newName = PromptForCarPresetName(
                    title: "Save as new car preset",
                    body: onBuiltin
                        ? $"'{activeName}' is a built-in default. Save the current tuning as a new user preset for '{carId}':"
                        : $"Save the current tuning as a new user preset for '{carId}':",
                    initial: suggestion,
                    existing: _plugin.GetCarPresets(carId));
                if (string.IsNullOrEmpty(newName)) return;
                _plugin.SaveActiveCarPresetAs(newName);
            }
            else
            {
                // Overwrite existing user car preset: ask whether to write
                // just this section or every dirty section.
                var scope = PromptSaveScope(which, $"car preset '{activeName}'");
                if (scope == SaveScope.Cancel) return;

                _plugin.SnapshotSectionToCarOverride((TrueforcePlugin.SectionKind)(int)which);
                bool ok;
                if (scope == SaveScope.JustThis)
                {
                    // Patched save: read the on-disk override (cached as
                    // _lastPersistedCarOverrides), patch in only the
                    // targeted section, write back. Other sections in the
                    // file keep their previously-saved values; their dirty
                    // bits persist after RecomputeAllEffectDirty.
                    ok = _plugin.SaveSectionToActiveCarOverride(
                        (TrueforcePlugin.SectionKind)(int)which);
                }
                else
                {
                    // Save-all: snapshot every dirty section into the
                    // override (so sections still living in global also
                    // land in the car preset file), then persist the whole
                    // override.
                    for (int i = 0; i < _effectDirty.Length; i++)
                        if (_effectDirty[i] && i != (int)which)
                            _plugin.SnapshotSectionToCarOverride((TrueforcePlugin.SectionKind)i);
                    ok = _plugin.PersistActiveCarOverride();
                }
                if (!ok)
                {
                    MessageBox.Show("Save failed (see SimHub log for details).", "Trueforce");
                    return;
                }
            }
            RefreshFromPlugin();
            // Prompt only on Engine-section saves — that's where the user
            // committed cylinder/layout values worth submitting. Saves on
            // other sections (Bumps, Traction, etc.) shouldn't trigger a
            // form-submission ask; their data isn't what we're collecting.
            if (which == EffectKind.Engine) MaybePromptToSubmitEngineData(carId);
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

            // Save in two passes when both are dirty: car preset first
            // (because the fork-on-built-in dialog is interactive and the
            // user might cancel), then game preset. After the car save we
            // recompute per-section dirty so we know whether anything is
            // still dirty on the game side before invoking the game save.
            bool carDirty = _plugin.IsActiveCarPresetDirty();
            if (carDirty)
            {
                if (!SaveActiveCarPresetWithFork()) return; // user cancelled
                RecomputeAllEffectDirty();
            }

            // Game-side dirty: any per-section bit still set after the car
            // save means tuning has drifted from the active preset snapshot
            // outside the car-override scope.
            bool gameDirty = false;
            for (int i = 0; i < _effectDirty.Length; i++) if (_effectDirty[i]) { gameDirty = true; break; }
            if (!gameDirty)
            {
                // Nothing left to save — refresh and exit cleanly so the
                // header / button styling reflects the cleaned state.
                RefreshFromPlugin();
                return;
            }

            string activeP = _plugin.ActivePresetName;
            bool   builtin = !string.IsNullOrEmpty(activeP) && _plugin.IsBuiltinPreset(activeP);

            // Fork case: no active preset, or active is a built-in we can't
            // overwrite. Auto-create a game-named user preset and bind it
            // as the game's default.
            if (string.IsNullOrEmpty(activeP) || builtin)
            {
                ForkAndSaveAsGamePreset();
                return;
            }
            // Regular overwrite.
            if (MessageBox.Show($"Overwrite preset '{activeP}' with current settings?",
                                "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _plugin.SavePresetAs(activeP);
            ClearDirty();
            RefreshFromPlugin();
        }

        /// <summary>Save the live car override to its active preset file.
        /// On a built-in, prompts for a new user-preset name and forks; on
        /// a user preset, in-place save. Returns false if the user
        /// cancelled the fork prompt (caller should abort the save chain).
        /// Returns true if save succeeded or there was nothing to save.</summary>
        private bool SaveActiveCarPresetWithFork()
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return true;
            string carId      = _plugin.ActiveCarId;
            string activeName = _plugin.GetActiveCarPresetName(carId);
            bool   onBuiltin  = !string.IsNullOrEmpty(activeName)
                                && _plugin.IsCarPresetBuiltin(carId, activeName);

            bool ok;
            if (string.IsNullOrEmpty(activeName) || onBuiltin)
            {
                string suggestion = onBuiltin ? StripDefaultSuffix(activeName) : carId;
                string newName = PromptForCarPresetName(
                    title: "Save unsaved car-preset changes",
                    body: onBuiltin
                        ? $"'{activeName}' is a built-in default. Save the current tuning as a new user preset for '{carId}':"
                        : $"Save the current tuning as a new user preset for '{carId}':",
                    initial: suggestion,
                    existing: _plugin.GetCarPresets(carId));
                if (string.IsNullOrEmpty(newName)) return false; // cancelled
                _plugin.SaveActiveCarPresetAs(newName);
                ok = true;
            }
            else
            {
                ok = _plugin.PersistActiveCarOverride();
            }
            if (ok) MaybePromptToSubmitEngineData(carId);
            return ok;
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
            if (!PromptForExportMetadata($"Export preset '{name}'", "preset",
                out string author, out string desc, out string ver)) return;

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
                _plugin.ExportPreset(name, dlg.FileName, author, desc, ver);
                MessageBox.Show($"Exported '{name}' to:\n{dlg.FileName}",
                                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Pop the Author/Description/Version dialog. Author pre-fills from
        // SharingAuthor; on OK, persists the (possibly-edited) author back so
        // the next export pre-fills with what the user just typed. Returns
        // false on Cancel; true with the (possibly-blank) values on OK.
        private bool PromptForExportMetadata(string title, string subjectKind,
            out string author, out string description, out string authorVersion)
        {
            author = description = authorVersion = null;
            if (_plugin?.Settings == null) return false;

            var dlg = new PresetMetadataDialog(title, subjectKind,
                _plugin.Settings.SharingAuthor, "", "")
            {
                Owner = Window.GetWindow(this),
            };
            if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (dlg.ShowDialog() != true) return false;

            author        = dlg.Author;
            description   = dlg.Description;
            authorVersion = dlg.AuthorVersion;

            // Persist the (possibly edited) author so the next export pre-fills
            // with the user's most recent value. Only writes when changed.
            string newAuthor = author?.Trim() ?? "";
            if (newAuthor != (_plugin.Settings.SharingAuthor ?? ""))
            {
                _plugin.Settings.SharingAuthor = newAuthor;
                try { _plugin.PersistSettings(); } catch { }
            }
            return true;
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
                var r = _plugin.ImportPreset(dlg.FileName);
                string body = $"Imported preset '{r.PresetName}' into your library. Select it from the dropdown and click Apply, or set it as a game default.";
                string meta = FormatMetadataLines(r.Author, r.AuthorVersion, r.Description);
                if (!string.IsNullOrEmpty(meta)) body = meta + "\n\n" + body;
                MessageBox.Show(body, "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshFromPlugin();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Compose the optional "by AUTHOR / version VERSION / description …"
        // header for import dialogs. Returns "" when none of the metadata
        // fields are populated so the caller can skip prepending it.
        private static string FormatMetadataLines(string author, string version, string description)
        {
            var parts = new System.Text.StringBuilder();
            string a = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
            string v = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            string d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            if (a != null || v != null)
            {
                if (a != null) parts.Append($"By {a}");
                if (v != null) parts.Append((a != null ? "  " : "") + $"(v{v})");
            }
            if (d != null)
            {
                if (parts.Length > 0) parts.Append('\n');
                parts.Append(d);
            }
            return parts.ToString();
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
            if (!PromptForExportMetadata($"Export car preset '{_plugin.ActiveCarId}'", "car preset",
                out string author, out string desc, out string ver)) return;

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
                _plugin.ExportActiveCarPreset(dlg.FileName, author, desc, ver);
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
                var r = _plugin.ImportCarPreset(dlg.FileName);
                bool applied = r.CarId == _plugin.ActiveCarId;
                string body = applied
                    ? $"Imported car preset '{r.PresetName}' for '{r.CarId}'. Applied (this is the active car)."
                    : $"Imported car preset '{r.PresetName}' for '{r.CarId}'. Stored (will apply when you drive that car).";
                string meta = FormatMetadataLines(r.Author, r.AuthorVersion, r.Description);
                if (!string.IsNullOrEmpty(meta)) body = meta + "\n\n" + body;
                MessageBox.Show(body, "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
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

        // ---------- Export / Import pack (multi-preset zip) ----------

        private void ExportPack_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var presets = _plugin.GetExportablePresetNames();
            var cars    = _plugin.GetExportableCarPresets();
            if (presets.Count == 0 && cars.Count == 0)
            {
                MessageBox.Show("No game presets or car presets to export yet. Save some first.",
                                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new PackPickerWindow(presets, cars, exportMode: true)
            {
                Owner = Window.GetWindow(this),
            };
            if (picker.Owner == null) picker.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (picker.ShowDialog() != true) return;

            var pickedPresets = picker.SelectedPresetNames;
            var pickedCars    = picker.SelectedCarPresets;
            if (pickedPresets.Count == 0 && pickedCars.Count == 0) return;

            if (!PromptForExportMetadata("Export pack", "pack",
                out string author, out string desc, out string ver)) return;

            string defaultName = $"Trueforce-pack-{DateTime.Now:yyyy-MM-dd}.tfpack";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "Trueforce pack (*.tfpack)|*.tfpack|Zip (*.zip)|*.zip",
                FileName = defaultName,
                Title    = "Export Trueforce pack",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var (p, c) = _plugin.ExportPack(
                    dlg.FileName,
                    pickedPresets,
                    pickedCars.ConvertAll(e2 => (e2.CarId, e2.PresetName)),
                    author, desc, ver);
                MessageBox.Show($"Exported {p} game preset(s) and {c} car preset(s) to:\n{dlg.FileName}",
                                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Trueforce pack (*.tfpack;*.zip)|*.tfpack;*.zip|All files (*.*)|*.*",
                Title  = "Import Trueforce pack",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var r = _plugin.ImportPack(dlg.FileName);
                string body = $"Imported {r.PresetsImported} game preset(s) and {r.CarsImported} car preset(s) from:\n{dlg.FileName}";
                string meta = FormatMetadataLines(r.Author, r.AuthorVersion, r.Description);
                if (!string.IsNullOrEmpty(meta)) body = meta + "\n\n" + body;
                MessageBox.Show(body, "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshFromPlugin();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        // ---------- Performance tab ----------

        private static string FormatRing(int samples)
        {
            // Each sample at 4 kHz = 0.25 ms.
            double ms = samples * 0.25;
            return $"{samples} ({ms:0.#}ms)";
        }

        private void PerfMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var mode = PerfManualRadio.IsChecked == true ? PerformanceMode.Manual : PerformanceMode.Auto;
            _plugin.SetPerformanceMode(mode);
            bool manual = mode == PerformanceMode.Manual;
            PerfTfRingSlider.IsEnabled    = manual;
            PerfAudioRingSlider.IsEnabled = manual;
        }

        private void PerfTfRingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Bail before touching any UI / plugin state when invoked
            // during XAML load: the parameterless constructor runs
            // InitializeComponent() before the chained ctor sets _plugin,
            // and the slider's initial Value-set fires this handler with
            // _plugin == null and other named UI elements possibly not yet
            // wired (NRE'd PerfTfRingText.Text in 0.1.0-localtest4).
            if (_suppressEvents || _plugin == null) return;
            // The slider snaps to TickFrequency=8 so e.NewValue is already
            // {8,16,24,32,40,48,56,64} — round to nearest pow2 to avoid the
            // 24/40/48/56 in-betweens (Apply() also sanitizes defensively).
            int v = NearestPow2((int)Math.Round(e.NewValue), 8, 64);
            if (PerfTfRingText != null) PerfTfRingText.Text = FormatRing(v);
            // Only push down to the device in Manual mode — in Auto, the
            // ratchet owns ring sizes and slider edits would conflict.
            if (_plugin.Settings?.Performance?.Mode == PerformanceMode.Manual)
                _plugin.ApplyTfRingSize(v);
        }

        private void PerfAudioRingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = NearestPow2((int)Math.Round(e.NewValue), 8, 128);
            if (PerfAudioRingText != null) PerfAudioRingText.Text = FormatRing(v);
            if (_plugin.Settings?.Performance?.Mode == PerformanceMode.Manual)
                _plugin.ApplyAudioRingSize(v);
        }

        private void PerfReset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.ResetPerformanceToLowest();
            RefreshFromPlugin();
        }

        private static int NearestPow2(int v, int min, int max)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            int p = 1;
            while ((p << 1) <= v) p <<= 1;
            if (p < min) p = min;
            return p;
        }

        // Rolling 60-second underrun / glitch counters. We sample every meter
        // tick; a 60-bucket second-aligned ring tracks events-per-second so
        // we can show "events in last 60 s" without long-running reset.
        private long _perfLastTfCount, _perfLastAudioCount;
        private long _perfLastBucketSec;
        private readonly long[] _perfTfBucket    = new long[60];
        private readonly long[] _perfAudioBucket = new long[60];

        private void UpdatePerfCounters()
        {
            if (_plugin == null) return;
            long tfNow = _plugin.TfRingUnderruns;
            long auNow = _plugin.AudioRingGlitches;
            long tfDelta = tfNow - _perfLastTfCount;
            long auDelta = auNow - _perfLastAudioCount;
            _perfLastTfCount = tfNow;
            _perfLastAudioCount = auNow;

            long sec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_perfLastBucketSec == 0) _perfLastBucketSec = sec;
            // Advance buckets — clear any seconds we skipped (idle UI).
            int gap = (int)Math.Min(60, sec - _perfLastBucketSec);
            for (int i = 0; i < gap; i++)
            {
                int idx = (int)((_perfLastBucketSec + 1 + i) % 60);
                _perfTfBucket[idx] = 0;
                _perfAudioBucket[idx] = 0;
            }
            _perfLastBucketSec = sec;
            int curIdx = (int)(sec % 60);
            _perfTfBucket[curIdx]    += tfDelta;
            _perfAudioBucket[curIdx] += auDelta;

            long tfWindow = 0, auWindow = 0;
            for (int i = 0; i < 60; i++) { tfWindow += _perfTfBucket[i]; auWindow += _perfAudioBucket[i]; }
            string tfLabel = $" (cap {_plugin.CurrentTfRingSize})";
            string auLabel = $" (cap {_plugin.CurrentAudioRingSize})";
            PerfCountersText.Text =
                $"Trueforce ring{tfLabel}: {tfWindow} underruns/min · " +
                $"Audio ring{auLabel}: {auWindow} glitches/min";
        }

        private void OnAutoRatchetBumped(bool isTf, int oldCap, int newCap)
        {
            // Fired on the producer thread — marshal to UI for the modal and
            // refresh. Don't block the producer; BeginInvoke is fire-and-forget.
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshFromPlugin();
                    ShowAutoRatchetModal(isTf, oldCap, newCap);
                }));
            }
            catch { }
        }

        /// <summary>Dismissable modal explaining the auto-bump and offering
        /// Revert (drop back to the previous size — user takes the dropouts
        /// back in exchange for lower latency) or OK (accept the new size).
        /// Both options are non-destructive; either way the choice persists.</summary>
        private void ShowAutoRatchetModal(bool isTf, int oldCap, int newCap)
        {
            string ringName = isTf ? "Trueforce stream" : "Audio loopback";
            double oldMs = oldCap * 0.25;
            double newMs = newCap * 0.25;

            // Programmatically-created Window doesn't inherit SimHub's dark
            // theme styles, so explicit colors are required: dark grey
            // background and light foreground on every TextBlock. Without
            // these, default WPF colors render as black-on-near-white which
            // is unreadable against SimHub's dark chrome.
            var bg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
            var fg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE));

            var win = new Window
            {
                Title = "Trueforce — auto-tuned ring buffer",
                Width = 460,
                Height = 230,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
                Background = bg,
                Foreground = fg,
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock
            {
                Text = $"{ringName} ring buffer auto-tuned",
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = fg,
                Margin = new Thickness(0, 0, 0, 8),
            });
            sp.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = fg,
                Text =
                    $"The {ringName.ToLower()} ring was at {oldCap} samples ({oldMs:0.#} ms) " +
                    $"but has had repeated dropouts. Bumped to {newCap} samples ({newMs:0.#} ms) " +
                    $"so the wheel keeps a clean stream.\n\n" +
                    "This setting is remembered across sessions. Click OK to keep it, or Revert " +
                    "if you'd rather take the dropouts back in exchange for tighter latency.",
                Margin = new Thickness(0, 0, 0, 12),
            });
            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var revert = new Button { Content = "Revert", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            var ok     = new Button { Content = "OK",     Width = 80, IsDefault = true, IsCancel = true };
            btnRow.Children.Add(revert);
            btnRow.Children.Add(ok);
            sp.Children.Add(btnRow);
            win.Content = sp;

            revert.Click += (_, __) =>
            {
                if (isTf) _plugin.ApplyTfRingSize(oldCap);
                else      _plugin.ApplyAudioRingSize(oldCap);
                RefreshFromPlugin();
                win.Close();
            };
            ok.Click += (_, __) => win.Close();

            try { win.ShowDialog(); } catch { }
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

        // ---------- Update banner / modal ----------

        private void UpdateBanner_Click(object sender, MouseButtonEventArgs e)
        {
            ShowUpdateModal();
        }

        /// <summary>Modal showing the latest release notes plus an "Update now"
        /// button that downloads the installer to %TEMP% with a progress bar
        /// and ShellExecutes it. The installer's IsSimHubRunning loop handles
        /// the "close SimHub first" case once the user clicks Run.</summary>
        private void ShowUpdateModal()
        {
            var upd = _plugin?.UpdateChecker;
            if (upd == null || !upd.IsUpdateAvailable) return;

            var win = new Window
            {
                Title = "Trueforce For All — Update available",
                Width = 600,
                Height = 520,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { Margin = new Thickness(16) };

            // Header: version transition
            var header = new TextBlock
            {
                Text = $"v{upd.CurrentVersion.ToString(3)}  →  v{upd.LatestVersionDisplay}",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Footer area (buttons + progress) docked to bottom.
            var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);

            var status = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 4),
                Text = "",
            };
            footer.Children.Add(status);

            var progress = new ProgressBar
            {
                Height = 6,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8),
            };
            footer.Children.Add(progress);

            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var dismissBtn = new System.Windows.Controls.Button
            {
                Content = "Dismiss",
                Width = 90,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
            };
            var updateBtn = new System.Windows.Controls.Button
            {
                Content = "Update now",
                Width = 120,
                Height = 28,
                IsDefault = true,
            };
            btnRow.Children.Add(dismissBtn);
            btnRow.Children.Add(updateBtn);
            footer.Children.Add(btnRow);

            root.Children.Add(footer);

            // Center: scrollable release notes. body is markdown; render as
            // plain text for now since proper markdown rendering in WPF needs
            // a dependency we don't otherwise have.
            var notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = System.Windows.Media.Brushes.Gray,
                Padding = new Thickness(10),
            };
            var notesText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(upd.ReleaseNotes)
                    ? "(No release notes published.)"
                    : upd.ReleaseNotes,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            };
            notesScroll.Content = notesText;
            root.Children.Add(notesScroll);

            win.Content = root;

            dismissBtn.Click += (_, __) => win.Close();
            updateBtn.Click += async (_, __) =>
            {
                updateBtn.IsEnabled = false;
                dismissBtn.IsEnabled = false;
                progress.Visibility = Visibility.Visible;
                progress.IsIndeterminate = true;
                status.Text = "Downloading installer...";

                try
                {
                    string path = await upd.DownloadInstallerAsync((received, total) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (total > 0)
                            {
                                progress.IsIndeterminate = false;
                                progress.Maximum = total;
                                progress.Value = received;
                                status.Text = $"Downloading installer... {(received / 1024.0 / 1024.0):F1} / {(total / 1024.0 / 1024.0):F1} MB";
                            }
                            else
                            {
                                status.Text = $"Downloading installer... {(received / 1024.0 / 1024.0):F1} MB";
                            }
                        });
                    }, _plugin.UpdateCheckerToken);
                    status.Text = "Launching installer...";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                    {
                        UseShellExecute = true,
                    });
                    win.Close();
                }
                catch (Exception ex)
                {
                    status.Text = $"Update failed: {ex.Message}";
                    progress.Visibility = Visibility.Collapsed;
                    updateBtn.IsEnabled = true;
                    dismissBtn.IsEnabled = true;
                }
            };

            win.ShowDialog();
        }

        // ---------- "What's new" banner / modal ----------

        private void WhatsNewBanner_Click(object sender, MouseButtonEventArgs e)
        {
            ShowWhatsNewModal();
        }

        /// <summary>Modal listing every changelog version newer than the
        /// user's stamped LastSeenVersion. Closing the modal (either via
        /// the button or by dismissing the window) stamps LastSeenVersion
        /// to the running build, which hides the banner for this version.
        /// Modeled on ShowUpdateModal so it inherits the same Window
        /// chrome / centering behavior.</summary>
        private void ShowWhatsNewModal()
        {
            if (_plugin == null) return;
            var pending = _plugin.GetPendingChangelog();
            if (pending == null || pending.Count == 0) return;

            var win = new Window
            {
                Title = "Trueforce For All — What's new",
                Width = 600,
                Height = 480,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new TextBlock
            {
                Text = "What's new",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            var gotItBtn = new System.Windows.Controls.Button
            {
                Content = "Got it",
                Width = 100,
                Height = 28,
                IsDefault = true,
                IsCancel = true,
            };
            footer.Children.Add(gotItBtn);
            root.Children.Add(footer);

            // Body: a scrolling stack of (version title) + (per-entry headline + description).
            // Renders newest first so the most recent additions are at the top.
            var bodyStack = new StackPanel();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var ver = pending[i];
                bodyStack.Children.Add(new TextBlock
                {
                    Text = "v" + ver.Version.ToString(3) + (string.IsNullOrEmpty(ver.Title) ? "" : "  —  " + ver.Title),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 14,
                    Margin = new Thickness(0, i == pending.Count - 1 ? 0 : 14, 0, 6),
                });
                if (ver.Entries != null)
                {
                    foreach (var entry in ver.Entries)
                    {
                        if (entry == null) continue;
                        if (!string.IsNullOrEmpty(entry.Headline))
                        {
                            bodyStack.Children.Add(new TextBlock
                            {
                                Text = "• " + entry.Headline,
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 12,
                                Margin = new Thickness(0, 4, 0, 0),
                            });
                        }
                        if (!string.IsNullOrEmpty(entry.Description))
                        {
                            bodyStack.Children.Add(new TextBlock
                            {
                                Text = entry.Description,
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 11,
                                Opacity = 0.7,
                                Margin = new Thickness(14, 2, 0, 0),
                            });
                        }
                    }
                }
            }
            var notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = System.Windows.Media.Brushes.Gray,
                Padding = new Thickness(12),
                Content = bodyStack,
            };
            root.Children.Add(notesScroll);

            win.Content = root;
            gotItBtn.Click += (_, __) => win.Close();
            win.Closed += (_, __) =>
            {
                _plugin.DismissChangelog();
                RefreshChangelogBanner();
            };

            win.ShowDialog();
        }
    }
}
