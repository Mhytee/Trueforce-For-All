using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

        // Forza "Not receiving packets?" auto-open. _forzaZeroSinceTicks marks
        // when the source first read zero packets (0 = not currently at zero);
        // once that stretch lasts ForzaStallExpandTicks we open the
        // troubleshooter a single time (latched by _forzaTroubleshootAutoExpanded
        // so a manual collapse sticks). _forceForzaStall is the STALL test-code
        // override that simulates the stalled state with no live Forza session.
        private long _forzaZeroSinceTicks;
        private bool _forzaTroubleshootAutoExpanded;
        private bool _forceForzaStall;
        private static readonly long ForzaStallExpandTicks =
            System.Diagnostics.Stopwatch.Frequency * 12;   // 12 s sustained zero packets
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
        private enum EffectKind { Master = 0, Ducking = 1, Audio = 2, Engine = 3, Bumps = 4, Traction = 5, Shift = 6, Abs = 7, SpikeReduction = 8, PitLimiter = 9, Drs = 10, Collision = 11, RevLimiter = 12, Airborne = 13 }
        private readonly bool[] _effectDirty = new bool[14];
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
                case EffectKind.RevLimiter:     return RevLimiterSaveBtn;
                case EffectKind.Airborne:       return AirborneSaveBtn;
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
                case EffectKind.RevLimiter:     return RevLimiterRevertBtn;
                case EffectKind.Airborne:       return AirborneRevertBtn;
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
                case EffectKind.RevLimiter:     return "Rev limiter";
                case EffectKind.Airborne:       return "Airborne ducking";
            }
            return "section";
        }
        // Master + Ducking + SpikeReduction are global-only. The save popover
        // hides the per-car option for these (no override concept).
        private static bool SectionHasCarScope(EffectKind w)
            => w != EffectKind.Master && w != EffectKind.Ducking && w != EffectKind.SpikeReduction
               && w != EffectKind.Airborne;

        // Sections whose EDIT handlers route through the per-car DRAFT model
        // (edits land in the car's in-memory override; explicit Save with
        // This-car / Game-default / Both / Reset). The plugin-side machinery is
        // generic, so rolling another section in is just adding it here AND
        // calling _plugin.EnsureSectionDraft(kind) in its edit handlers.
        private static bool SectionUsesDraftModel(EffectKind w)
            => w == EffectKind.RevLimiter;

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

            // Diagnostics expander (collapsed by default). Verbose status.
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
                StationarySpringCheck.IsChecked = _plugin.Settings?.StationarySpringEnabled ?? false;
                StationarySpringStrengthSlider.Value = _plugin.Settings?.StationarySpringStrength ?? 1.00;
                StationarySpringStrengthText.Text    = StationarySpringStrengthSlider.Value.ToString("F2");
                StationarySpringCutoffSlider.Value   = _plugin.Settings?.StationarySpringCutoffKmh ?? 12.0;
                StationarySpringCutoffText.Text      = ((int)StationarySpringCutoffSlider.Value).ToString();
                // Strength / fade-out sliders only matter when the spring is on.
                if (StationarySpringSliders != null)
                    StationarySpringSliders.Visibility =
                        (StationarySpringCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
                bool skipFfb = _plugin.Settings?.SkipFfbPassthrough ?? false;
                FfbSkipPassthroughCheck.IsChecked         = skipFfb;
                FfbSkipPassthroughPromotedCheck.IsChecked = skipFfb;
                FfbSkipPassthroughPromotedPanel.Visibility = _plugin.ActiveGameIsNativeTrueforce
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (LogUsbBytesCheck != null)
                    LogUsbBytesCheck.IsChecked = _plugin.Settings?.LogUsbBytesEnabled ?? false;
                bool expFfb = _plugin.Settings?.ExperimentalFfbCapture ?? false;
                if (ExperimentalFfbCheck != null)     ExperimentalFfbCheck.IsChecked     = expFfb;
                if (ExperimentalFfbMainCheck != null) ExperimentalFfbMainCheck.IsChecked = expFfb;
                FfbSmoothSlider.Value  = _plugin.Settings?.FfbSmoothTimeConstantMs ?? 0.0;
                FfbSmoothText.Text     = FfbSmoothSlider.Value.ToString("F1");
                if (FfbInvertCheck != null)
                    FfbInvertCheck.IsChecked = _plugin.Settings?.FfbInvertSign ?? true;
                SpikeTamingEnabledCheck.IsChecked  = _plugin.Settings?.FfbSpikeTamingEnabled  ?? false;
                bool spikeSlewMode = _plugin.Settings?.FfbSpikeUseSlewLimiter ?? true;
                SpikeModeSlewRadio.IsChecked      = spikeSlewMode;
                SpikeModeTransientRadio.IsChecked = !spikeSlewMode;
                UpdateSpikeModeUi();
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

                // Rim rev/shift LEDs (iRacing)
                if (AccessCodeStatus != null && _plugin.Settings?.RpmLedUnlocked == true)
                    AccessCodeStatus.Text = "Test features unlocked.";
                if (RpmLedEnabledCheck != null)
                    RpmLedEnabledCheck.IsChecked = _plugin.Settings?.RpmLedsEnabled == true;
                if (MairaPassthroughCheck != null)
                    MairaPassthroughCheck.IsChecked = _plugin.Settings?.MairaFfbPassthrough == true;
                if (RpmLedStatusText != null)
                    RpmLedStatusText.Text = _plugin.RpmLedStatus;

                // Forza section
                var fz = _plugin.Settings?.Forza;
                if (fz != null)
                {
                    ForzaPortBox.Text                  = fz.Port.ToString();
                    ForzaBindBox.Text                  = fz.BindAddress ?? "0.0.0.0";
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

                // Header strip context. Prefer the resolver's DisplayName when
                // available so opaque ordinals (Forza "Car_424") render as the
                // actual car name ("1997 Mazda RX-7"). Falls back to carId for
                // games whose carIds are already descriptive (AC) or for cars
                // not in the catalog.
                HeaderGameText.Text = string.IsNullOrEmpty(game) ? "(none)" : game;
                string headerCar =
                    !string.IsNullOrEmpty(_plugin.ActiveCarDisplayName) ? _plugin.ActiveCarDisplayName
                    : !string.IsNullOrEmpty(_plugin.ActiveCarId)        ? _plugin.ActiveCarId
                    : "(none)";
                HeaderCarText.Text  = headerCar;

                bool carDetected = !string.IsNullOrEmpty(_plugin.ActiveCarId);

                // Skip-passthrough makes the FFB tuning controls (scale/smooth/invert/
                // safety limiters) irrelevant (game writes the wheel directly). Grey
                // them so users don't waste time tuning controls that don't apply.
                FfbPassthroughControls.IsEnabled = !(_plugin.Settings?.SkipFfbPassthrough ?? false);

                // Engine
                var es = _plugin.ActiveEngine;
                if (es != null)
                {
                    EngineEnabledCheck.IsChecked      = es.Enabled;
                    EngineGainSlider.Value            = es.Gain;
                    EngineGainText.Text               = es.Gain.ToString("F2");
                    EnginePitchSlider.Value           = es.Pitch;
                    EnginePitchText.Text              = es.Pitch.ToString("F2");
                    EngineLowpassSlider.Value         = es.LowpassHz;
                    EngineLowpassText.Text            = ((int)es.LowpassHz).ToString();
                    SelectWaveform(EngineWaveformCombo, es.Waveform);
                    if (EngineElectricModeCombo != null)
                        EngineElectricModeCombo.SelectedIndex =
                            es.ElectricMode == ElectricCarMode.Silent ? 1 : 0;

                    // High-RPM helpers (Load layer + boost)
                    if (EngineLoadLayerCheck != null)
                    {
                        EngineLoadLayerCheck.IsChecked       = es.LoadLayerEnabled;
                        EngineLoadLayerGainSlider.Value      = es.LoadLayerGain;
                        EngineLoadLayerGainText.Text         = es.LoadLayerGain.ToString("F2");
                        EngineHighRpmBoostCheck.IsChecked    = es.HighRpmBoostEnabled;
                        EngineHighRpmBoostSlider.Value       = es.HighRpmBoostAmount;
                        EngineHighRpmBoostText.Text          = es.HighRpmBoostAmount.ToString("F2");
                    }

                    // Engine layout dropdown is populated dynamically (built-ins
                    // + user-saved customs + action entries). ApplyEngineSettings
                    // migrates any pre-flat-enum (Cylinders, EngineConfig) state
                    // into Layout before we read it here.
                    RebuildEngineLayoutDropdown();
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
                    SelectWaveform(DrsSustainedWaveformCombo, drs.SustainedWaveform);
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
                    SelectWaveform(CollisionWaveformCombo, coll.Waveform);
                }
                // Rev limiter (per-car overridable like the other effects)
                var rl = _plugin.ActiveRevLimiter;
                if (rl != null && RevLimiterEnabledCheck != null)
                {
                    RevLimiterEnabledCheck.IsChecked    = rl.Enabled;
                    RevLimiterGainSlider.Value          = rl.Gain;
                    RevLimiterGainText.Text             = rl.Gain.ToString("F2");
                    RevLimiterThresholdSlider.Value     = rl.Threshold;
                    RevLimiterThresholdText.Text        = ((int)Math.Round(rl.Threshold * 100)).ToString() + "%";
                    SelectWaveform(RevLimiterWaveformCombo, rl.Waveform);
                    RevLimiterFreqSlider.Value          = rl.Freq;
                    RevLimiterFreqText.Text             = ((int)rl.Freq).ToString();
                    RevLimiterPulseFreqSlider.Value     = rl.PulseFreq;
                    RevLimiterPulseFreqText.Text        = rl.PulseFreq.ToString("F1");
                    RevLimiterDutyCycleSlider.Value     = rl.DutyCycle;
                    RevLimiterDutyCycleText.Text        = rl.DutyCycle.ToString("F2");
                    RevLimiterActiveAmpSlider.Value     = rl.ActiveAmp;
                    RevLimiterActiveAmpText.Text        = rl.ActiveAmp.ToString("F2");

                    // Engage mode dropdown (Auto / Percentage / Redline). The
                    // engage-% row and redline note visibility follow the
                    // effective mode, see UpdateRevLimiterEngageVisibility.
                    RevLimiterEngageModeCombo.SelectedIndex =
                        rl.EngageMode == RevLimiterEngageMode.Percentage ? 1
                      : rl.EngageMode == RevLimiterEngageMode.Redline    ? 2
                      : 0;
                    RevLimiterOffsetSlider.Value = rl.RedlineOffsetRpm;
                    RevLimiterOffsetText.Text    = FormatRedlineOffset(rl.RedlineOffsetRpm);
                    UpdateRevLimiterEngageVisibility();
                }

                // Airborne ducking (global; no per-car override). Reads from
                // Settings directly since there's no per-car draft for it.
                var air = _plugin.Settings?.Airborne;
                if (air != null && AirborneEnabledCheck != null)
                {
                    AirborneEnabledCheck.IsChecked       = air.Enabled;
                    AirborneReductionSlider.Value        = air.Reduction;
                    AirborneReductionText.Text           = ((int)Math.Round(air.Reduction * 100)).ToString() + "%";
                    AirborneDuckEngineCheck.IsChecked    = air.DuckEngine;
                    AirborneDuckAudioCheck.IsChecked     = air.DuckAudio;
                    AirborneDuckRoadBumpsCheck.IsChecked = air.DuckRoadBumps;
                    AirborneDuckTractionCheck.IsChecked  = air.DuckTractionLoss;
                    AirborneDuckRevLimiterCheck.IsChecked= air.DuckRevLimiter;
                    AirborneDuckGearShiftCheck.IsChecked = air.DuckGearShift;
                    AirborneDuckAbsCheck.IsChecked       = air.DuckAbs;
                    AirborneDuckPitLimiterCheck.IsChecked= air.DuckPitLimiter;
                    AirborneDuckDrsCheck.IsChecked       = air.DuckDrs;
                    AirborneDuckCollisionCheck.IsChecked = air.DuckCollision;
                }

                // Override badges in expander headers. Visible only when this
                // section has its own per-car override active.
                AudioOverrideBadge.Visibility    = (_plugin.IsAudioOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                EngineOverrideBadge.Visibility   = (_plugin.IsEngineOverridden   && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                BumpsOverrideBadge.Visibility    = (_plugin.IsBumpsOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                TractionOverrideBadge.Visibility = (_plugin.IsTractionOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                ShiftOverrideBadge.Visibility    = (_plugin.IsShiftOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                AbsOverrideBadge.Visibility      = (_plugin.IsAbsOverridden      && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (AbsUnsupportedBadge != null)
                    AbsUnsupportedBadge.Visibility = _plugin.ActiveGameSupportsAbs ? Visibility.Collapsed : Visibility.Visible;
                if (PitLimiterOverrideBadge != null)
                    PitLimiterOverrideBadge.Visibility = (_plugin.IsPitLimiterOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (DrsOverrideBadge != null)
                    DrsOverrideBadge.Visibility        = (_plugin.IsDrsOverridden        && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (CollisionOverrideBadge != null)
                    CollisionOverrideBadge.Visibility  = (_plugin.IsCollisionOverridden  && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (RevLimiterOverrideBadge != null)
                    RevLimiterOverrideBadge.Visibility = (_plugin.IsRevLimiterOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;

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
            UpdateOfflineEditBanner();
        }

        // Toggle the offline-edit banner's visibility and title text based
        // on whether the plugin is currently in offline-edit mode. Called
        // from RefreshFromPlugin and whenever the mode transitions.
        private void UpdateOfflineEditBanner()
        {
            if (OfflineEditBanner == null) return;
            string editing = _plugin?.OfflineEditingPresetName;
            if (string.IsNullOrEmpty(editing))
            {
                OfflineEditBanner.Visibility = Visibility.Collapsed;
                return;
            }
            OfflineEditBanner.Visibility = Visibility.Visible;
            bool builtin = _plugin.IsBuiltinPreset(editing);
            OfflineEditTitle.Text = $"Editing preset '{editing}'";
            // Save button on a built-in becomes "Save as new…" since the
            // in-place save isn't allowed; collapse the explicit Save-as
            // button to avoid two paths that do the same thing. The relabel
            // is the user's cue that built-in edits fork instead of
            // overwriting; no need for a second explainer in the banner.
            OfflineEditSaveBtn.Content      = builtin ? "Save as new…" : "Save";
            OfflineEditSaveAsBtn.Visibility = builtin ? Visibility.Collapsed : Visibility.Visible;
        }

        // Public entry point used by ManagePresetsDialog when the user picks
        // Edit on a row. Load the preset and flip the banner on.
        public void EnterOfflineEditMode(string presetName)
        {
            if (_plugin == null || string.IsNullOrEmpty(presetName)) return;
            if (!_plugin.EnterOfflineEdit(presetName)) return;
            ClearDirty();
            RefreshFromPlugin();
        }

        private void OfflineEditSave_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || !_plugin.IsOfflineEditing) return;
            string name = _plugin.OfflineEditingPresetName;
            if (_plugin.IsBuiltinPreset(name))
            {
                // Built-in fast path. Same as Save as new with a suggested
                // name derived from the built-in's name.
                PromptAndSaveAsNew(name);
                return;
            }
            if (!_plugin.ExitOfflineEditSave())
            {
                MessageBox.Show("Save failed.", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ClearDirty();
            RefreshFromPlugin();
        }

        private void OfflineEditSaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || !_plugin.IsOfflineEditing) return;
            PromptAndSaveAsNew(_plugin.OfflineEditingPresetName);
        }

        private void PromptAndSaveAsNew(string suggestedBaseName)
        {
            string suggested = string.IsNullOrEmpty(suggestedBaseName)
                ? "My preset"
                : suggestedBaseName + " (edited)";
            string newName = PromptForName("Save as new preset", "New preset name:", suggested);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (_plugin.Settings?.Presets?.ContainsKey(newName) == true)
            {
                if (MessageBox.Show($"A preset called '{newName}' already exists. Overwrite?",
                    "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _plugin.DeletePreset(newName);
            }
            if (!_plugin.ExitOfflineEditSaveAs(newName))
            {
                MessageBox.Show("Save failed.", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ClearDirty();
            RefreshFromPlugin();
        }

        private void OfflineEditDiscard_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || !_plugin.IsOfflineEditing) return;
            if (MessageBox.Show("Discard edits and restore the state you had before entering edit mode?",
                "Discard edits", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _plugin.ExitOfflineEditDiscard();
            ClearDirty();
            RefreshFromPlugin();
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

                // Surface USBPcap recovery actions only when USBPcap is
                // missing. Keeps the diagnostics row uncluttered in the
                // common case where everything's installed correctly.
                if (UsbPcapBrowseButton != null && UsbPcapReinstallButton != null)
                {
                    var want = _plugin.IsUsbPcapAvailable
                        ? System.Windows.Visibility.Collapsed
                        : System.Windows.Visibility.Visible;
                    if (UsbPcapBrowseButton.Visibility != want)    UsbPcapBrowseButton.Visibility    = want;
                    if (UsbPcapReinstallButton.Visibility != want) UsbPcapReinstallButton.Visibility = want;
                }

                // Forza UDP section visibility. Shown only while a Forza
                // title is the active game; hidden in every other game.
                if (ForzaSection != null)
                {
                    // _forceForzaStall (STALL test code) keeps the section
                    // visible even outside a Forza session so the simulated
                    // stall + auto-expand can be seen on demand.
                    var want = (_plugin.ShouldShowForzaSection || _forceForzaStall)
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
                if (RpmLedSection != null)
                {
                    // Two gates, both required: (1) the tester unlocked it
                    // with the access code (bottom of page) - the MAIRA
                    // passthrough side is still in PR / unvalidated on
                    // RS50/G923, so it stays out of the public UI until then;
                    // and (2) the active SimHub game is iRacing
                    // (ShouldShowRpmLedSection), since iRacing is the only game
                    // these toggles apply to. Gate (2) is profile-level, not
                    // run-state: _activeGame is data.GameName (the
                    // SimHub-selected game), so it shows in the iRacing profile
                    // whether or not iRacing is currently running, and stays
                    // hidden in every other profile.
                    var want = (_plugin.Settings?.RpmLedUnlocked == true
                                && _plugin.ShouldShowRpmLedSection)
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (RpmLedSection.Visibility != want) RpmLedSection.Visibility = want;
                }

                // Header update controls. When an update is available, the
                // "Check for updates" link + transient status hide and a
                // prominent "Update to vX.Y.Z" button takes their place inline
                // with the version readout. Otherwise the link stays visible
                // so users can re-poll on demand.
                var upd = _plugin.UpdateChecker;
                bool hasUpdate = upd != null && upd.IsUpdateAvailable;
                if (UpdateAvailableButton != null)
                {
                    var want = hasUpdate ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    if (UpdateAvailableButton.Visibility != want) UpdateAvailableButton.Visibility = want;
                    if (hasUpdate && UpdateAvailableButtonText != null)
                    {
                        string desired = $"Update to v{upd.LatestVersionDisplay}  →";
                        if (UpdateAvailableButtonText.Text != desired) UpdateAvailableButtonText.Text = desired;
                    }
                }
                if (CheckForUpdatesButton != null)
                {
                    var want = hasUpdate ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    if (CheckForUpdatesButton.Visibility != want) CheckForUpdatesButton.Visibility = want;
                }
                if (CheckForUpdatesStatus != null && hasUpdate && CheckForUpdatesStatus.Visibility != System.Windows.Visibility.Collapsed)
                {
                    // Don't keep a stale "Up to date" / "Checking..." line
                    // visible next to the prominent update CTA.
                    CheckForUpdatesStatus.Visibility = System.Windows.Visibility.Collapsed;
                }
                else if (CheckForUpdatesStatus != null && !hasUpdate && CheckForUpdatesStatus.Visibility != System.Windows.Visibility.Visible)
                {
                    CheckForUpdatesStatus.Visibility = System.Windows.Visibility.Visible;
                }

                // "What's new" banner + per-effect NEW badges. Both driven by
                // the plugin's SeenEffects / LastSeenVersion state.
                RefreshChangelogBanner();
                RefreshShareCtaBanner();
                RefreshExperimentalSuccessBanner();
                RefreshNewBadges();

                // Forza listener status: the source object exposes packet
                // count + last IsRaceOn. When the source isn't active (the
                // active game isn't a Forza title), we show "(idle)".
                // This is the user's primary "is my Data Out
                // wiring working" feedback so make it specific.
                // Engine auto-detect indicator: shows the layout the resolver
                // chose for the active car when Layout=Auto, or surfaces the
                // resolver's pick when the user has manually overridden it.
                if (EngineLayoutAutoText != null)
                {
                    var ep = _plugin.EnginePulse;
                    var esLive = _plugin.ActiveEngine;
                    string activeCar = _plugin.ActiveCarId;
                    bool userIsAuto = esLive != null && esLive.Layout == Effects.EngineLayout.Auto;

                    if (ep != null && userIsAuto && ep.AutoLayout is Effects.EngineLayout autoL)
                    {
                        string detectSrc = ep.AutoLayoutSource;
                        string srcSuffix;
                        if (string.Equals(detectSrc, "telemetry", StringComparison.OrdinalIgnoreCase))
                            srcSuffix = " (from telemetry)";
                        else if (string.Equals(detectSrc, "baked", StringComparison.OrdinalIgnoreCase))
                            srcSuffix = " (from built-in car list)";
                        else if (string.Equals(detectSrc, "cache", StringComparison.OrdinalIgnoreCase))
                            srcSuffix = " (cached from earlier session)";
                        else if (!string.IsNullOrEmpty(detectSrc))
                            srcSuffix = $" (heuristic: {detectSrc})";
                        else
                            srcSuffix = "";
                        EngineLayoutAutoText.Text =
                            $"Auto-detected: {Effects.FiringPatternDb.LayoutDisplayName(autoL)}{srcSuffix}";
                    }
                    else if (ep != null && userIsAuto
                             && string.IsNullOrEmpty(ep.AutoLayoutSource)
                             && !string.IsNullOrEmpty(activeCar))
                    {
                        EngineLayoutAutoText.Text =
                            $"Could not auto-detect engine type for '{activeCar}'. "
                            + "Pick the closest match from the list, or use Test to A/B.";
                    }
                    else if (ep != null && esLive != null
                             && esLive.Layout != Effects.EngineLayout.Auto
                             && ep.AutoLayout is Effects.EngineLayout autoOverridden
                             && autoOverridden != esLive.Layout)
                    {
                        // Manual override that disagrees with the resolver.
                        string srcSuffix = string.IsNullOrEmpty(ep.AutoLayoutSource)
                            ? ""
                            : $" ({ep.AutoLayoutSource})";
                        EngineLayoutAutoText.Text =
                            $"Manual override: {Effects.FiringPatternDb.LayoutDisplayName(esLive.Layout)}. "
                            + $"Auto would be {Effects.FiringPatternDb.LayoutDisplayName(autoOverridden)}{srcSuffix}. "
                            + "Pick Auto to use detection.";
                    }
                    else
                    {
                        EngineLayoutAutoText.Text = "";
                    }

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

                    // True once we're confident nothing is arriving and the
                    // user should see the troubleshooter: either the STALL test
                    // code is active, or the source has sat at zero packets past
                    // the sustain threshold. Only the "never received anything"
                    // case auto-opens; a count that stopped climbing mid-session
                    // is usually a normal quit-to-menu, not a config problem.
                    bool zeroPacketsSustained = false;

                    if (_forceForzaStall)
                    {
                        ForzaStatusText.Text =
                            "No packets arriving now (0 Hz). 0 received this session. If you're in-game, check Forza Data Out config + the troubleshooter below. [STALL test active]";
                        zeroPacketsSustained = true;
                    }
                    else if (fzSrc == null)
                    {
                        ForzaStatusText.Text = "(idle, not active for current game)";
                        _forzaZeroSinceTicks = 0;
                        _forzaTroubleshootAutoExpanded = false;
                    }
                    else if (fzSrc.PacketsReceived == 0)
                    {
                        ForzaStatusText.Text =
                            $"Listening on {(_plugin.Settings?.Forza?.BindAddress ?? "0.0.0.0")}:{(_plugin.Settings?.Forza?.Port ?? 0)}, no packets yet (check Forza Data Out config + the troubleshooter below)";
                        // Stamp when the zero-packet stretch began so we can
                        // auto-open the troubleshooter once it's sustained.
                        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (_forzaZeroSinceTicks == 0) _forzaZeroSinceTicks = nowTicks;
                        if (nowTicks - _forzaZeroSinceTicks >= ForzaStallExpandTicks)
                            zeroPacketsSustained = true;
                    }
                    else
                    {
                        // Packets have arrived: clear the zero-stretch tracking
                        // and re-arm the one-shot so a later genuine stall can
                        // open the troubleshooter again.
                        _forzaZeroSinceTicks = 0;
                        _forzaTroubleshootAutoExpanded = false;
                        // PacketsReceived is a lifetime session total and never
                        // decreases, so on its own it can't tell the user
                        // whether data is arriving right now. MeasuredHz zeroes
                        // out after 1s of silence, so it's the live-flow truth.
                        // When Hz is 0 we have a stale count: packets arrived at
                        // some point this session but nothing is coming in now.
                        // The driving/paused label is only meaningful while live
                        // (LastIsRaceOn is itself frozen at the last packet), so
                        // we only show it when Hz > 0.
                        double hz = fzSrc.MeasuredHz;
                        if (hz > 0)
                        {
                            string state = fzSrc.LastIsRaceOn ? "driving" : "paused / in menu";
                            ForzaStatusText.Text =
                                $"Receiving at ~{hz:0} Hz, {state}. {fzSrc.PacketsReceived:N0} packets this session.";
                        }
                        else
                        {
                            ForzaStatusText.Text =
                                $"No packets arriving now (0 Hz). {fzSrc.PacketsReceived:N0} received earlier this session. If you're in-game, check Forza Data Out config + the troubleshooter below.";
                        }
                    }

                    // Auto-open the "Not receiving packets?" troubleshooter once
                    // we're confident nothing is arriving, so the loopback /
                    // port / firewall guidance reaches the user without them
                    // having to find and expand it. One-shot: a user who closes
                    // it isn't fought on every refresh tick (the latch re-arms
                    // only when packets resume, above).
                    if (zeroPacketsSustained
                        && !_forzaTroubleshootAutoExpanded
                        && ForzaTroubleshootExpander != null)
                    {
                        ForzaTroubleshootExpander.IsExpanded = true;
                        _forzaTroubleshootAutoExpanded = true;
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
                // remove access. Actually IsEnabled=false on a StackPanel
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

            // Admin/elevation warning: SimHub must run as administrator for
            // reliable FFB. Shown whenever it isn't elevated.
            if (AdminWarningBox != null)
            {
                AdminWarningBox.Visibility = (_plugin != null && !_plugin.IsRunningElevated)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // FFB-tap-picker banner: HID found a wheel but USBPcap discovery
            // didn't, and the user hasn't set a manual override yet. Surfaces
            // the picker prominently so users don't have to dig into
            // Diagnostics to fix a silently-broken FFB pass-through.
            if (FfbTapPickerBanner != null)
            {
                FfbTapPickerBanner.Visibility = (_plugin?.ShouldShowFfbTapPickerBanner ?? false)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // Unverified-wheel info banner (Xbox G923 etc.): show when the
            // plugin reports a non-null notice for the detected wheel.
            if (UnverifiedWheelBanner != null)
            {
                string notice = _plugin?.UnverifiedWheelNotice;
                if (string.IsNullOrEmpty(notice))
                {
                    UnverifiedWheelBanner.Visibility = Visibility.Collapsed;
                }
                else
                {
                    UnverifiedWheelBanner.Visibility = Visibility.Visible;
                    if (UnverifiedWheelText.Text != notice)
                        UnverifiedWheelText.Text = notice;
                }
            }

            // Performance counters update every meter tick (cheap; array sum
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
                case EffectKind.RevLimiter: return "RevLimiter";
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
                case "RevLimiter": return RevLimiterNewBadge;
                case "Airborne":   return AirborneNewBadge;
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

        /// <summary>Fires once when the user opens an effect's expander.
        /// Counts as an "I've seen this" interaction and clears the NEW
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
        /// the running build is newer than the user's stamped LastSeenVersion.
        /// Header reads "What's new in v{CurrentVersion}". Idempotent. Called
        /// from RefreshFromPlugin.</summary>
        private void RefreshChangelogBanner()
        {
            if (_plugin == null || WhatsNewBanner == null) return;
            bool show = _plugin.HasUnseenChangelog;
            var want = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (WhatsNewBanner.Visibility != want) WhatsNewBanner.Visibility = want;
            if (show && WhatsNewBannerText != null)
            {
                var curr = _plugin.UpdateChecker?.CurrentVersion;
                if (curr != null)
                {
                    string desired = "What's new in v" + curr.ToString(3);
                    if (WhatsNewBannerText.Text != desired) WhatsNewBannerText.Text = desired;
                }
            }
        }

        // ---- Word-of-mouth banner ----------------------------------------

        private const string ShareProjectUrl =
            "https://github.com/Mhytee/Trueforce-For-All";

        // Nexus Mods is by far the highest-traffic distribution channel
        // (~3500 views vs handfuls on the other mirrors), so it's the
        // primary public-facing landing page when someone wants to send a
        // link rather than make a post.
        private const string ShareNexusUrl =
            "https://www.nexusmods.com/forzahorizon6/mods/34";

        /// <summary>Shows the one-and-done word-of-mouth banner once the user
        /// has banked enough working seat time. Idempotent; safe to call
        /// every RefreshFromPlugin.</summary>
        private void RefreshShareCtaBanner()
        {
            if (_plugin == null || ShareCtaBanner == null) return;
            bool show = _plugin.ShouldShowShareCta;
            var want = show ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
            if (ShareCtaBanner.Visibility != want) ShareCtaBanner.Visibility = want;
        }

        private void ShareCtaBanner_Click(object sender, MouseButtonEventArgs e)
        {
            // One-and-done: opening the dialog from the banner latches the
            // banner off whether or not the user shares. The persistent
            // "Spread the word" row at the bottom stays available forever.
            ShowShareDialog();
            _plugin?.DismissShareCta();
            RefreshShareCtaBanner();
        }

        private void ShareCtaDismiss_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.DismissShareCta();
            RefreshShareCtaBanner();
        }

        // "FFB compatibility reports" Discussions category. Slug is GitHub's
        // lowercase-hyphenated form of the category name. If the slug ever
        // 404s (category renamed/removed), GitHub lands the user on the
        // discussions/new chooser, which still works, just unfiltered.
        private const string FfbReportDiscussionsBase =
            "https://github.com/Mhytee/Trueforce-For-All/discussions/new?category=ffb-compatibility-reports";

        private void RefreshExperimentalSuccessBanner()
        {
            if (_plugin == null || ExperimentalSuccessBanner == null) return;
            var want = _plugin.ShouldShowExperimentalSuccessReport
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            if (ExperimentalSuccessBanner.Visibility != want) ExperimentalSuccessBanner.Visibility = want;
        }

        // "Yes, it's working": the human confirms FFB actually works, so this
        // is a real, attributable success. Open the prefilled report and latch.
        private void ExperimentalSuccessYes_Click(object sender, RoutedEventArgs e)
        {
            OpenFfbCompatibilityReport();
            _plugin?.DismissExperimentalSuccessReport();   // one-and-done, like the share CTA
            RefreshExperimentalSuccessBanner();
        }

        // "No, still no FFB": our capture signal was a false positive (or FFB is
        // there but wrong). Don't generate a success report; send them to the
        // diagnostics/troubleshooter instead, and latch so we stop asking.
        private void ExperimentalSuccessNo_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.DismissExperimentalSuccessReport();
            RefreshExperimentalSuccessBanner();
            // Open the Advanced dialog with Diagnostics expanded: self-test,
            // the experimental toggle, manual device picker and USBPcap
            // reinstall all live there.
            if (DiagnosticsExpander != null) DiagnosticsExpander.IsExpanded = true;
            OpenAdvancedSettings_Click(this, null);
        }

        private void ExperimentalSuccessDismiss_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.DismissExperimentalSuccessReport();
            RefreshExperimentalSuccessBanner();
        }

        // Open a prefilled compatibility-report discussion. VID/PID, game,
        // version and the capture fingerprint are filled in automatically so
        // the report carries exactly what we need to graduate a wheel out of
        // experimental; the user just hits submit. Framed as a success report,
        // not a bug, so it isn't mistaken for an issue.
        private void OpenFfbCompatibilityReport()
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            string game    = _plugin?.ActiveGame ?? "(none)";
            string wheel   = _plugin?.WheelStatus ?? "(unknown)";
            string vidpid  = (_plugin != null && (_plugin.HidWheelVid != 0 || _plugin.HidWheelPid != 0))
                ? $"{_plugin.HidWheelVid:X4}:{_plugin.HidWheelPid:X4}" : "unknown";
            string fp      = _plugin?.CaptureFingerprint ?? "(not confirmed)";

            string title = $"[FFB report] {vidpid} on {game}";
            string body  =
                  "Experimental FFB detection fixed my wheel. (Success report, not a bug.)\n\n"
                + $"- Wheel: {wheel}  ({vidpid})\n"
                + $"- Game: {game}\n"
                + $"- Plugin: {version}\n"
                + $"- Capture: {fp}\n\n"
                + "Anything else worth noting (other games tested, what wasn't working before): \n\n"
                + "(Optional but very helpful: use Advanced > Diagnostics > Export logs and drag the zip in here.)\n";

            string url = FfbReportDiscussionsBase
                       + "&title=" + Uri.EscapeDataString(title)
                       + "&body="  + Uri.EscapeDataString(body);
            OpenUrl(url);
        }

        // Persistent bottom-of-panel row. Same actions as the dialog, always
        // available, never touches the one-and-done latch.
        private void SharePanelButton_Click(object sender, RoutedEventArgs e) => ShowShareDialog();

        // ---- Share targets -----------------------------------------------
        //
        // All web-intent targets pass only the URL, never prefilled body
        // text. Auto-filled marketing copy reads as spam to the user's
        // followers and turns people off. (A "Copy a ready-to-paste post"
        // button was considered as an opt-in for the longer blurb, but
        // intentionally dropped: the bare link-outs are the whole surface.)
        //
        // Reddit: the maintainer's own Reddit account is banned (see the
        // README note), but third parties posting on the project's behalf
        // is exactly the gap this share dialog fills. We use `selftext=true`
        // to force the text-post composer (link-type submissions read as
        // low-effort drive-by spam on most subreddits) and seed the body
        // with just the URL so a preview/embed renders. Title and any
        // additional context stay empty so the poster writes their own.

        private static string XIntentUrl =>
            "https://twitter.com/intent/tweet?url="
            + Uri.EscapeDataString(ShareProjectUrl);

        private static string RedditSubmitUrl =>
            "https://www.reddit.com/submit?selftext=true&text="
            + Uri.EscapeDataString(ShareProjectUrl);

        private static string FacebookShareUrl =>
            "https://www.facebook.com/sharer/sharer.php?u="
            + Uri.EscapeDataString(ShareProjectUrl);

        /// <summary>Themed share dialog. Built in code (like the What's new
        /// modal) so it paints in SimHub's dark chrome instead of falling
        /// back to the white system default.</summary>
        private void ShowShareDialog()
        {
            var win = new Window
            {
                Title = "Spread the word",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = "Help another sim racer find TF4ALL",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            });
            root.Children.Add(new TextBlock
            {
                Text = "Almost nobody knows this plugin exists. A YouTube video, "
                     + "a Reddit post, or a link to a friend is the best way to "
                     + "help get the word out.",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
            });

            System.Windows.Controls.Button MakeBtn(string text, RoutedEventHandler onClick)
            {
                var b = new System.Windows.Controls.Button
                {
                    Content = text,
                    Height = 32,
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 0, 0, 0),
                };
                b.Click += onClick;
                return b;
            }

            root.Children.Add(MakeBtn("Post on X",
                (_, __) => OpenUrl(XIntentUrl)));
            root.Children.Add(MakeBtn("Post on Reddit",
                (_, __) => OpenUrl(RedditSubmitUrl)));
            root.Children.Add(MakeBtn("Share on Facebook",
                (_, __) => OpenUrl(FacebookShareUrl)));
            root.Children.Add(MakeBtn("Open the Nexus Mods page",
                (_, __) => OpenUrl(ShareNexusUrl)));
            root.Children.Add(MakeBtn("Open the project page on GitHub",
                (_, __) => OpenUrl(ShareProjectUrl)));

            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "Close",
                Width = 90,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
                IsCancel = true,
            };
            closeBtn.Click += (_, __) => win.Close();
            root.Children.Add(closeBtn);

            win.Content = root;
            win.ShowDialog();
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
                // No game-preset snapshot AND no per-car override anchor:
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

        /// <summary>Recompute every section's dirty state from the plugin.
        /// Called from the end of RefreshFromPlugin (so override toggles,
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
                    // No anchor: preserve sticky bit so a no-preset edit
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
            // Non-car-specific drift: any per-section bit differs from the
            // active game preset. _effectDirty is indexed 1:1 with SectionKind,
            // so this also covers the inherently non-per-car sections (Master,
            // Ducking, Spike reduction) whose changes only ever belong at the
            // game/global level, not a car. (Sticky-true when no game preset is
            // active, so unsaved tuning still surfaces.)
            bool gameDirty = false;
            for (int i = 0; i < _effectDirty.Length; i++) if (_effectDirty[i]) { gameDirty = true; break; }

            // Car-level drift, checked independently: a car-preset-only edit
            // might not light any per-section bit (those compare against the
            // game preset). IsActiveCarPresetDirty compares live vs saved car
            // override directly and stays accurate either way.
            bool carDetected = !string.IsNullOrEmpty(_plugin?.ActiveCarId);
            bool carDirty = carDetected && (_plugin?.IsActiveCarPresetDirty() ?? false);

            bool any = gameDirty || carDirty;
            if (any != _dirty)
            {
                _dirty = any;
                UpdateHeaderPresetDisplay();
            }

            // Contextual "Save all" buttons in the header (these replaced the
            // old bottom global Save). They save every changed section at once
            // and are in addition to the per-section Save buttons.
            //   * Car-side button shows on car-specific drift; saves to the
            //     active car's override.
            //   * Game-side button shows on ANY unsaved tuning, because the
            //     game default is a valid target for two things: non-car-
            //     specific changes (Master/Ducking/Spike + game-preset drift)
            //     AND promoting the CURRENT car's settings up to be the game
            //     default. So whenever the car side is dirty, the game side is
            //     offered too (the popover then lets you pick car / game /
            //     both).
            if (HeaderGameSaveAllBtn != null)
                HeaderGameSaveAllBtn.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (HeaderCarSaveAllBtn != null)
                HeaderCarSaveAllBtn.Visibility = carDirty ? Visibility.Visible : Visibility.Collapsed;

            // "Save as new…" mirrors its Save-all neighbour: it only makes sense
            // when there's unsaved tuning to capture under a new name.
            if (HeaderSaveAsNewBtn != null)
                HeaderSaveAsNewBtn.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (HeaderCarSaveAsNewBtn != null)
                HeaderCarSaveAsNewBtn.Visibility = carDirty ? Visibility.Visible : Visibility.Collapsed;
        }

        // Both header "Save all" buttons open the same target chooser (save to
        // this car / game default / both). The button's PLACEMENT (next to the
        // game vs car picker) and visibility just signal where the unsaved
        // tuning is; the popover stays so "both" remains reachable and the user
        // confirms the target. The car-side button passes a hint so the popover
        // can lead with the car option.
        private void HeaderGameSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            ShowGlobalSavePopover(preferCar: false);
        }

        private void HeaderCarSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            ShowGlobalSavePopover(preferCar: true);
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
            if (_plugin == null || HeaderPresetCombo == null) return;

            // Unsaved state is now signalled by the header "★ Save all" buttons
            // (driven from UpdateGlobalDirtyFromEffects), so there's no separate
            // badge to toggle here.
            // The pickers carry the active preset names. Rebuild both (game and
            // car) so they reflect current state.
            RefreshGamePresetPicker();
            RefreshCarPresetPicker();
        }

        // Descriptor stashed on each selectable HeaderPresetCombo row so the
        // changed-handler can route a click to the right plugin call. Section
        // headers carry Tag=null and are non-selectable.
        private sealed class PresetPick
        {
            public bool   IsCar;    // false = game-library preset; true = a car preset
            public string CarId;    // the car a car-preset belongs to
            public string Name;
            public bool   ClearCar; // true = the "None" row: clear this car's selection
        }

        // Rebuild the GAME-preset picker in the header: just the game-library
        // presets, built-ins first then user, alpha within each. Selection
        // tracks the active game preset. (Car presets live in the separate
        // HeaderCarPresetCombo picker.)
        private void RefreshGamePresetPicker()
        {
            if (_plugin == null || HeaderPresetCombo == null) return;

            string activeP = _plugin.ActivePresetName;
            string defName = _plugin.DefaultPresetForActiveGame;

            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                HeaderPresetCombo.Items.Clear();

                // Game-library presets, built-ins first then user, alpha within
                // each (the dictionary key order is otherwise unsorted). The
                // dropdown scrolls (MaxDropDownHeight) and supports type-to-jump,
                // so a long list stays navigable.
                if (_plugin.PresetNames != null)
                {
                    var gameNames = _plugin.PresetNames.ToList();
                    gameNames.Sort((a, b) =>
                    {
                        bool ba = _plugin.IsBuiltinPreset(a), bb = _plugin.IsBuiltinPreset(b);
                        if (ba != bb) return ba ? -1 : 1;
                        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                    });
                    foreach (var name in gameNames)
                    {
                        string suffix = string.Equals(name, defName, StringComparison.Ordinal)
                            ? "  ★ default" : "";
                        HeaderPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                        {
                            Content = ToBuiltinDisplay(name) + suffix,
                            Tag     = new PresetPick { IsCar = false, CarId = null, Name = name },
                        });
                    }
                }

                // Select the row matching the active game preset.
                HeaderPresetCombo.SelectedItem = null;
                foreach (var obj in HeaderPresetCombo.Items)
                {
                    if (!(obj is System.Windows.Controls.ComboBoxItem ci)) continue;
                    if (!(ci.Tag is PresetPick pick)) continue;
                    if (!pick.IsCar && string.Equals(pick.Name, activeP, StringComparison.Ordinal))
                    {
                        HeaderPresetCombo.SelectedItem = ci;
                        break;
                    }
                }
            }
            finally { _suppressEvents = prevSuppress; }
        }

        // Rebuild the CAR-preset picker in the header. When a car is loaded it
        // lists that car's presets (built-ins first, alpha) plus every OTHER
        // car's presets (each selectable copies its tuning onto the current car,
        // ApplyCopy). A display-only "None — using game default" row shows when
        // the active car has no preset; a "No car loaded" placeholder shows and
        // disables the combo when no car is detected.
        private void RefreshCarPresetPicker()
        {
            if (_plugin == null || HeaderCarPresetCombo == null) return;

            string carId      = _plugin.ActiveCarId;
            bool   carDetected = !string.IsNullOrEmpty(carId);
            string activeCar   = carDetected ? _plugin.GetActiveCarPresetName(carId) : null;

            // Local helper: build the built-in-first-then-alpha ordering used
            // for a car's preset list.
            List<CarPresetEntry> Ordered(IReadOnlyDictionary<string, CarPresetEntry> p)
            {
                var o = new List<CarPresetEntry>();
                if (p != null)
                {
                    foreach (var kv in p) if (kv.Value.IsBuiltin) o.Add(kv.Value);
                    foreach (var kv in p) if (!kv.Value.IsBuiltin) o.Add(kv.Value);
                    o.Sort((a, b) =>
                    {
                        if (a.IsBuiltin != b.IsBuiltin) return a.IsBuiltin ? -1 : 1;
                        return string.Compare(a.PresetName, b.PresetName, StringComparison.OrdinalIgnoreCase);
                    });
                }
                return o;
            }

            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                HeaderCarPresetCombo.Items.Clear();
                HeaderCarPresetCombo.IsEnabled = true;   // always available, even with no car / paused

                System.Windows.Controls.ComboBoxItem toSelect = null;

                if (!carDetected)
                {
                    // No car identified yet (menu / paused before a car loaded).
                    // Still usable: a prompt + every car's presets. Picking one
                    // pins that car for editing so edits + Save target it.
                    var prompt = new System.Windows.Controls.ComboBoxItem
                    {
                        Content = "Select a car preset to edit...",
                        Tag = null, IsEnabled = false, IsHitTestVisible = false,
                        Opacity = 0.6, FontStyle = FontStyles.Italic,
                    };
                    HeaderCarPresetCombo.Items.Add(prompt);
                    toSelect = prompt;
                }
                else
                {
                    // Selectable "None" row: the car uses no per-car preset and
                    // inherits the game preset. Selected when the car has no
                    // active preset; selectable at any time so the user can
                    // clear a preset back to none (drops the per-car override).
                    string gameDefault = _plugin.DefaultPresetForActiveGame;
                    string inherit = string.IsNullOrEmpty(gameDefault)
                        ? "use game preset"
                        : $"use game preset: {ToBuiltinDisplay(gameDefault)}";
                    var noneRow = new System.Windows.Controls.ComboBoxItem
                    {
                        Content = $"None ({inherit})",
                        Tag = new PresetPick { IsCar = true, CarId = carId, ClearCar = true },
                    };
                    HeaderCarPresetCombo.Items.Add(noneRow);
                    toSelect = noneRow;

                    HeaderCarPresetCombo.Items.Add(MakeSectionHeader($"── This car: {carId} ──"));
                    foreach (var entry in Ordered(_plugin.GetCarPresets(carId)))
                    {
                        var ci = new System.Windows.Controls.ComboBoxItem
                        {
                            Content = ToBuiltinDisplay(entry.PresetName),
                            Tag     = new PresetPick { IsCar = true, CarId = carId, Name = entry.PresetName },
                        };
                        HeaderCarPresetCombo.Items.Add(ci);
                        if (string.Equals(entry.PresetName, activeCar, StringComparison.Ordinal)) toSelect = ci;
                    }
                }

                // Every other car's presets, GROUPED BY GAME (so you can tell
                // which cars are Forza Horizon 6 vs Assetto Corsa, etc.). The
                // active game's cars come first, then other games alphabetically.
                // Each row is "<carId> · <preset>" so cars stay distinguishable
                // within a game. Picking one pins that car for editing.
                var allCars = _plugin.GetAllCarPresets();
                if (allCars != null)
                {
                    string activeGame = _plugin.ActiveGame ?? "";

                    // Bucket the other cars by their game (a car's presets share
                    // a game; take the first preset's GameName).
                    var byGame = new Dictionary<string, List<KeyValuePair<string, IReadOnlyDictionary<string, CarPresetEntry>>>>();
                    foreach (var carKv in allCars)
                    {
                        if (carDetected && string.Equals(carKv.Key, carId, StringComparison.Ordinal)) continue;
                        if (carKv.Value == null || carKv.Value.Count == 0) continue;
                        string g = "";
                        foreach (var p in carKv.Value) { g = p.Value.GameName ?? ""; break; }
                        if (!byGame.TryGetValue(g, out var list))
                        {
                            list = new List<KeyValuePair<string, IReadOnlyDictionary<string, CarPresetEntry>>>();
                            byGame[g] = list;
                        }
                        list.Add(carKv);
                    }

                    // Active game first, then the rest by friendly name.
                    foreach (var g in byGame.Keys
                                 .OrderBy(x => string.Equals(x, activeGame, StringComparison.Ordinal) ? 0 : 1)
                                 .ThenBy(GameDisplayName, StringComparer.OrdinalIgnoreCase))
                    {
                        HeaderCarPresetCombo.Items.Add(MakeSectionHeader($"── {GameDisplayName(g)} ──"));
                        var cars = byGame[g];
                        cars.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                        foreach (var carKv in cars)
                        {
                            foreach (var entry in Ordered(carKv.Value))
                            {
                                HeaderCarPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                                {
                                    Content = $"{carKv.Key} · {ToBuiltinDisplay(entry.PresetName)}",
                                    Tag     = new PresetPick { IsCar = true, CarId = carKv.Key, Name = entry.PresetName },
                                });
                            }
                        }
                    }
                }

                HeaderCarPresetCombo.SelectedItem = toSelect;
            }
            finally { _suppressEvents = prevSuppress; }
        }

        // Friendly display name for a game code (the car-preset GameName), for
        // the car picker's per-game section headers. Falls back to the raw code.
        private static string GameDisplayName(string game)
        {
            switch (game)
            {
                case "FH6":  return "Forza Horizon 6";
                case "FH5":  return "Forza Horizon 5";
                case "FH4":  return "Forza Horizon 4";
                case "AssettoCorsa": return "Assetto Corsa";
                case "AssettoCorsaCompetizione": return "Assetto Corsa Competizione";
                case "IRacing": return "iRacing";
                case "Wreckfest2": return "Wreckfest 2";
                case null:
                case "": return "Other";
                default: return game;
            }
        }

        // Build a non-selectable, dimmed/bold section-header row for the
        // grouped active-preset picker.
        private static System.Windows.Controls.ComboBoxItem MakeSectionHeader(string text)
        {
            return new System.Windows.Controls.ComboBoxItem
            {
                Content          = text,
                Tag              = null,
                IsEnabled        = false,
                IsHitTestVisible = false,
                FontWeight       = FontWeights.Bold,
                Opacity          = 0.6,
            };
        }

        // GAME-preset picker. Applies a game-library preset.
        private void HeaderPresetCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (!(HeaderPresetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)) return;
            if (!(item.Tag is PresetPick pick) || pick.IsCar || string.IsNullOrEmpty(pick.Name)) return;

            // No-op when the picked row already matches the active game preset.
            if (string.Equals(pick.Name, _plugin.ActivePresetName, StringComparison.Ordinal))
                return;

            // Unsaved-changes confirm (mirrors PresetCombo_Changed).
            if (_dirty)
            {
                var r = MessageBox.Show(
                    $"Apply preset '{pick.Name}'? Your unsaved tuning will be discarded.\n\nClick No to cancel and Save first.",
                    "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                {
                    // Cancelled: revert the picker selection without re-entering
                    // this handler.
                    RefreshGamePresetPicker();
                    return;
                }
            }

            if (!_plugin.ApplyPreset(pick.Name))
            {
                MessageBox.Show($"Could not apply '{pick.Name}' (preset missing).", "Trueforce", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            ClearDirty();
            RefreshFromPlugin();
        }

        // CAR-preset picker. Switches the active car's own preset, or copies
        // another car's tuning onto the current car (ApplyCopy).
        private void HeaderCarPresetCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (!(HeaderCarPresetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)) return;
            // Ignore the display-only rows (e.g. "Select a car..." prompt, Tag=null).
            if (!(item.Tag is PresetPick pick)) return;

            // "None" row: clear this car's preset back to the game preset.
            if (pick.ClearCar)
            {
                // No-op if the car already has no active preset.
                if (string.IsNullOrEmpty(_plugin.GetActiveCarPresetName(pick.CarId))
                    && string.Equals(pick.CarId, _plugin.ActiveCarId, StringComparison.Ordinal))
                    return;
                if (_dirty)
                {
                    var rc = MessageBox.Show(
                        "Clear this car's preset and use the game preset? Your unsaved tuning will be discarded.\n\nClick No to cancel and Save first.",
                        "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (rc != MessageBoxResult.Yes) { RefreshCarPresetPicker(); return; }
                }
                _plugin.ClearActiveCarPreset(pick.CarId);
                ClearDirty();
                RefreshFromPlugin();
                return;
            }

            if (string.IsNullOrEmpty(pick.Name)) return;

            // No-op only when re-picking the ACTIVE car's current preset.
            // Picking a different car (or a different preset) always pins +
            // loads it, even if it happens to be that other car's own default.
            if (string.Equals(pick.CarId, _plugin.ActiveCarId, StringComparison.Ordinal)
                && string.Equals(pick.Name, _plugin.GetActiveCarPresetName(pick.CarId), StringComparison.Ordinal))
                return;

            // Unsaved-changes confirm (mirrors PresetCombo_Changed).
            if (_dirty)
            {
                var r = MessageBox.Show(
                    $"Apply preset '{pick.Name}'? Your unsaved tuning will be discarded.\n\nClick No to cancel and Save first.",
                    "Trueforce", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                {
                    // Cancelled: revert the picker selection without re-entering
                    // this handler.
                    RefreshCarPresetPicker();
                    return;
                }
            }

            // Pin the picked car as the active car for editing and load its
            // preset (works for the car you're in, another car, or no car at
            // all). Edits + Save then target this car/preset; live telemetry
            // re-asserts the real car when you're actually driving.
            _plugin.SelectCarForEditing(pick.CarId, pick.Name);
            ClearDirty();
            RefreshFromPlugin();
        }

        // ---------- Master / Audio ----------

        private void PluginEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            // Plugin-enabled is a per-game preference (GameEnabled dict), not
            // part of preset content, so it doesn't dirty the active preset.
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

        // Stationary-spring handlers. Global setting (not per-game), so they
        // persist directly via PersistSettings() rather than the per-game
        // snapshot path the other FFB controls use.
        private void StationarySpring_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            bool on = StationarySpringCheck.IsChecked == true;
            _plugin.SetStationarySpringEnabled(on);
            _plugin.PersistSettings();
            if (StationarySpringSliders != null)
                StationarySpringSliders.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        private void StationarySpringStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            StationarySpringStrengthText.Text = e.NewValue.ToString("F2");
            _plugin.SetStationarySpringStrength(e.NewValue);
            _plugin.PersistSettings();
        }
        private void StationarySpringCutoffSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            StationarySpringCutoffText.Text = ((int)e.NewValue).ToString();
            _plugin.SetStationarySpringCutoffKmh(e.NewValue);
            _plugin.PersistSettings();
        }
        private void FfbSkipPassthrough_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            bool skip = FfbSkipPassthroughCheck.IsChecked == true;
            ApplySkipFfbPassthrough(skip, syncSource: FfbSkipPassthroughCheck);
        }
        private void FfbSkipPassthroughPromoted_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            bool skip = FfbSkipPassthroughPromotedCheck.IsChecked == true;
            ApplySkipFfbPassthrough(skip, syncSource: FfbSkipPassthroughPromotedCheck);
        }
        private void ApplySkipFfbPassthrough(bool skip, CheckBox syncSource)
        {
            _plugin.SetSkipFfbPassthrough(skip);
            // Keep the promoted (main view) and advanced-modal checkboxes in
            // sync without firing each other's Changed handler.
            var prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                if (syncSource != FfbSkipPassthroughCheck)
                    FfbSkipPassthroughCheck.IsChecked = skip;
                if (syncSource != FfbSkipPassthroughPromotedCheck)
                    FfbSkipPassthroughPromotedCheck.IsChecked = skip;
            }
            finally { _suppressEvents = prev; }
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
        private void SpikeMode_Changed(object sender, RoutedEventArgs e)
        {
            // The pure-UI swap runs even during a suppressed refresh so the
            // label / help / description always match the selected method.
            UpdateSpikeModeUi();
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetFfbSpikeUseSlewLimiter(SpikeModeSlewRadio?.IsChecked == true);
            MarkEffectDirty(EffectKind.SpikeReduction);
        }

        // Swaps the shared limit slider's label + help and the method
        // description for the selected method, and shows the transient-only
        // "Cap softness" row only in transient mode. Pure UI, no settings
        // writes, so it is safe to call during a suppressed refresh.
        private void UpdateSpikeModeUi()
        {
            if (SpikeModeSlewRadio == null) return; // designer / pre-init
            bool slew = SpikeModeSlewRadio.IsChecked == true;
            if (SpikeModeDescription != null)
                SpikeModeDescription.Text = slew
                    ? "Slew-rate limiter (iRacing-style): caps how fast the force is allowed to change. No amplitude reduction, sustained forces always reach full strength; a sharp spike just gets spread across a few extra milliseconds."
                    : "Transient detector: soft-caps only the part of a sudden jump that exceeds your threshold. Sustained heavy cornering passes through at full strength; crashes and big curb hits get rounded off.";
            if (FfbSpikeLimitLabel != null)
                FfbSpikeLimitLabel.Text = slew ? "Slew rate (LSB/ms):" : "Spike threshold (LSB):";
            if (FfbSpikeLimitHelp != null)
                FfbSpikeLimitHelp.Text = slew
                    ? "Maximum change in force per millisecond. Lower spreads harsh spikes over more time (softer); higher lets force move faster (sharper)."
                    : "Minimum force magnitude before the cap can engage. Below this, forces pass through untouched. Lower catches more spikes; higher only the biggest hits.";
            if (SpikeCapSoftnessRow != null)
                SpikeCapSoftnessRow.Visibility = slew
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible;
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
                // No active game. Nothing to scope the override under.
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

        // One-click health report. Aggregates every status signal that's
        // otherwise spread across the Diagnostics rows + the quiet-wheel hint
        // into a single pass/fail checklist, then fires a short thud so the
        // user gets a physical confirmation the wheel is actually driven.
        // Reads live state only; never reopens a healthy device.
        private void SelfTest_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var sb = new System.Text.StringBuilder();

            bool usbpcap   = _plugin.IsUsbPcapAvailable;
            bool ghub      = _plugin.IsLogitechGHubRunning;
            string wheel   = _plugin.WheelStatus ?? "";
            bool wheelOk   = !wheel.StartsWith("Not detected", StringComparison.OrdinalIgnoreCase)
                          && !wheel.StartsWith("Open failed", StringComparison.OrdinalIgnoreCase)
                          && wheel.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0;
            string stream  = _plugin.StreamStatus ?? "";
            bool streamOk  = stream.StartsWith("Streaming", StringComparison.OrdinalIgnoreCase);
            string tap     = _plugin.FfbTapStatus ?? "";
            bool tapStarted = tap.StartsWith("Tapping", StringComparison.OrdinalIgnoreCase);
            bool tapLive    = _plugin.FfbTapTargetFresh(750);
            var src        = _plugin.TelemetrySource;
            double hz      = src?.MeasuredHz ?? 0;
            bool gameRun   = !string.IsNullOrEmpty(_plugin.ActiveGame);
            bool elevated  = _plugin.IsRunningElevated;

            sb.AppendLine((usbpcap ? "[OK]   " : "[FAIL] ") + "USBPcap installed"
                + (usbpcap ? "" : " (FFB pass-through off; use Reinstall below)"));
            sb.AppendLine(elevated
                ? "[OK]   SimHub running as administrator"
                : "[FAIL] SimHub is NOT running as administrator. Required for reliable force feedback. "
                    + "Easiest fix: enable 'Run as administrator' in SimHub's Settings, then restart SimHub.");
            sb.AppendLine((ghub ? "[FAIL] " : "[OK]   ")
                + (ghub ? "Logitech G HUB is running. Close it; it blocks the wheel."
                        : "Logitech G HUB not running"));
            sb.AppendLine((wheelOk ? "[OK]   " : "[FAIL] ") + "Wheel: "
                + (string.IsNullOrEmpty(wheel) ? "(unknown)" : wheel));
            if (!wheelOk)
            {
                // Wheel not opened: distinguish three causes so the hint is
                // actionable. (1) supported wheel present but its haptic
                // endpoint is missing (G HUB / driver / partial enumeration);
                // (2) a wheel-like Logitech device on an unsupported PID
                // (console mode); (3) nothing wheel-like (generic hint).
                System.Collections.Generic.List<WheelMatch> noEndpoint = null;
                System.Collections.Generic.List<WheelDiscovery.UnsupportedWheel> consoleish = null;
                try { noEndpoint = WheelDiscovery.FindSupportedWithoutTrueforceInterface(); }
                catch { }
                try { consoleish = WheelDiscovery.FindUnsupportedWheelLike(); }
                catch { }

                if (noEndpoint != null && noEndpoint.Count > 0)
                {
                    var w = noEndpoint[0];
                    sb.AppendLine($"       Wheel recognized ({w.Model}) but its Trueforce/haptic USB "
                        + "interface isn't available (the 'wheel present but no haptic endpoint' case).");
                    sb.AppendLine("       Fix: open G HUB once and let it finish detecting the wheel (this loads "
                        + "the wheel's full interface set), then close G HUB and restart SimHub. If it "
                        + "persists, reboot.");
                }
                else if (consoleish != null && consoleish.Count > 0)
                {
                    var w0 = consoleish[0];
                    sb.AppendLine($"       Found a Logitech wheel in an unsupported mode: {w0.Name} "
                        + $"(PID 0x{w0.Pid:X4}). Open G HUB once and let it detect the wheel, then close "
                        + "G HUB and restart SimHub.");
                }
                else
                {
                    sb.AppendLine("       If your wheel is plugged in, open G HUB once and let it detect the "
                        + "wheel, then close G HUB and restart SimHub (G HUB must stay closed while this plugin runs).");
                }
            }
            sb.AppendLine((streamOk ? "[OK]   " : "[FAIL] ") + "Stream: "
                + (string.IsNullOrEmpty(stream) ? "(unknown)" : stream));
            // Tap "started" only proves the USBPcap process is up; tapLive
            // proves a game FFB target was actually decoded off the bus in
            // the last 750 ms, the real liveness signal.
            // The tap can only confirm LIVE pass-through while a game is
            // actually producing forces; "started" just means the USBPcap
            // capture is up and the wheel's interface was found. So no game =
            // not verifiable here, not a failure.
            // Sentinel line replaced live by the FFB watch (see below) once the
            // user has had a chance to actually produce force.
            const string FfbLiveWatchSentinel = "@@FFB_LIVE_WATCH@@";
            bool ffbLiveWatch = false;
            if (tapLive)
                sb.AppendLine("[OK]   FFB pass-through: live (game forces seen on the bus)");
            else if (tapStarted && !gameRun)
                sb.AppendLine("[skip] FFB pass-through: tap running. Start a session, then re-run this "
                    + "test while turning the wheel / driving so it can catch the game's forces.");
            else if (tapStarted)
            {
                // A game is running but no force in the last 750 ms (menu, or
                // the user simply hasn't moved yet). Don't conclude with a
                // stale "skip" the user can't act on: hold the test open and
                // watch live while they make FFB happen (resolved below).
                sb.AppendLine(FfbLiveWatchSentinel);
                ffbLiveWatch = true;
            }
            else if (wheelOk && usbpcap
                     && !tap.StartsWith("Discovering", StringComparison.OrdinalIgnoreCase))
            {
                // HID/Windows opened the wheel but USBPcap never found it on
                // the bus: the "non-standard USB" failure. The wheel is on a
                // USB controller/port USBPcap doesn't cover, or USBPcap's
                // driver hasn't attached since install. FFB pass-through stays
                // dead until the bus is visible to the capture driver.
                sb.AppendLine("[FAIL] FFB pass-through: Windows sees your wheel but USBPcap can't capture it on the USB bus.");
                sb.AppendLine("       Likely the wheel is on a USB port/controller USBPcap doesn't cover, or USBPcap needs a reboot since it was installed.");
                sb.AppendLine("       Try, in order: reboot (if USBPcap was just installed); move the wheel to a different USB port (rear motherboard ports work best, avoid front-panel ports and hubs); run SimHub as administrator; or use 'Pick device manually' below.");
            }
            else
                sb.AppendLine("[skip] FFB pass-through: " + (string.IsNullOrEmpty(tap) ? "(not started)" : tap));
            // FFB not confirmed live and experimental detection is off: a wheel
            // sending force in a shape the default path doesn't recognize is
            // exactly what experimental mode widens, so suggest it.
            if (!tapLive && !ffbLiveWatch && !(_plugin.Settings?.ExperimentalFfbCapture ?? false))
                sb.AppendLine("       If your wheel should have force feedback but you feel none, turn on "
                    + "'Enable experimental FFB detection' (main page or Advanced > FFB pass-through), then drive a few seconds.");
            if (!gameRun)
                sb.AppendLine("[skip] Telemetry: no game running (start a game, load a session)");
            else
                sb.AppendLine((hz > 0 ? "[OK]   " : "[skip] ") + "Telemetry: "
                    + (src?.Name ?? "?") + (hz > 0 ? $" ({hz:0} Hz)" : " (idle; in a menu or paused?)"));

            string diag = _plugin.WheelQuietDiagnostic;
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(diag)
                ? "Overall: healthy."
                : "Most-blocking issue: " + diag);

            sb.AppendLine();
            string checklist = sb.ToString();

            if (streamOk)
            {
                // Sustained ~2.5 s traction-loss rumble: unmistakable even on
                // a quiet gear-driven G923, unlike the brief gear thud.
                _plugin.TestEffect(_plugin.TractionLoss);

                if (ffbLiveWatch)
                {
                    // Hold the result open and poll for game FFB for a few
                    // seconds so the user can actually trigger it (turn the
                    // wheel / drive) and SEE the verdict update, instead of a
                    // snapshot that's stale before they can act.
                    SelfTestResultText.Text = checklist.Replace(FfbLiveWatchSentinel,
                        "[..]   FFB pass-through: in a session (not a menu), TURN THE WHEEL or drive NOW. Watching for ~6 s...")
                        + "(Also sent a 2.5 s test rumble to confirm the wheel responds to our output.)";
                    SelfTestResultText.Visibility = Visibility.Visible;
                    SelfTestButton.IsEnabled = false;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        bool seen = false;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (sw.Elapsed.TotalSeconds < 6.0)
                        {
                            if (_plugin.FfbTapTargetFresh(750)) { seen = true; break; }
                            System.Threading.Thread.Sleep(150);
                        }
                        Dispatcher.Invoke(() =>
                        {
                            string verdict = seen
                                ? "[OK]   FFB pass-through: LIVE - game forces captured when you moved. Pass-through is working."
                                : "[skip] FFB pass-through: no game forces seen in 6 s. Be in an ACTIVE session (not a menu/paused) and turn the wheel / drive while the watch runs."
                                  + ((_plugin.Settings?.ExperimentalFfbCapture ?? false)
                                      ? ""
                                      : " If you still feel no force feedback, turn on 'Enable experimental FFB detection' and try again.");
                            SelfTestResultText.Text = checklist.Replace(FfbLiveWatchSentinel, verdict);
                            SelfTestButton.IsEnabled = true;
                        });
                    });
                    return;
                }

                SelfTestResultText.Text =
                    checklist + "Sent a 2.5 s test rumble now: you should feel a "
                    + "clear sustained buzz build and fade.";
                SelfTestResultText.Visibility = Visibility.Visible;
                return;
            }

            // Stream is down: the live FFB watch doesn't apply (fix the stream
            // first). Resolve any sentinel to a static line so it never leaks.
            if (ffbLiveWatch)
                checklist = checklist.Replace(FfbLiveWatchSentinel,
                    "[skip] FFB pass-through: not verified (stream is down; fix that first, then re-test while driving).");

            // Stream is down: run the active staged probe (discovery, then
            // open + init + tap + stream). The init sequence blocks ~150 ms,
            // so it runs off the UI thread to not freeze SimHub; the button
            // is disabled until the staged result is appended.
            SelfTestResultText.Text       = checklist + "Stream not active: running active device probe...";
            SelfTestResultText.Visibility = Visibility.Visible;
            SelfTestButton.IsEnabled      = false;
            System.Threading.Tasks.Task.Run(() =>
            {
                string probe;
                try { probe = _plugin.RunActiveDeviceProbe(); }
                catch (Exception ex) { probe = "[FAIL] Probe crashed: " + ex.Message; }
                Dispatcher.Invoke(() =>
                {
                    SelfTestResultText.Text  = checklist + "Active device probe:\n" + probe;
                    SelfTestButton.IsEnabled = true;
                });
            });
        }
        private void AudioEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.Enabled = AudioEnabledCheck.IsChecked == true;
            Apply(EffectKind.Audio);
        }
        private void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            float v = (float)e.NewValue;
            AudioGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.Gain = v;
            Apply(EffectKind.Audio);
        }
        private void AudioFilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            double v = e.NewValue;
            AudioFilterText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.LowpassCutoffHz = v;
            Apply(EffectKind.Audio);
        }
        private void AudioHighpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            double v = e.NewValue;
            AudioHighpassText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.HighpassCutoffHz = v;
            Apply(EffectKind.Audio);
        }

        // ---------- Active car ----------

        // ---------- Active car preset dropdown / save / delete ----------

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

        // Built-in presets are stored with a trailing " (default)" suffix.
        // That's the structural marker the rest of the plugin keys off
        // (IsBuiltinPreset, refresh-on-load, export stripping, name
        // validator). For UI display we relabel to " (built-in)" so the
        // word "default" only ever means the per-game auto-load binding
        // (Set as default / "default for this game").
        private static string ToBuiltinDisplay(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string oldSuffix = " (default)";
            const string newSuffix = " (built-in)";
            return name.EndsWith(oldSuffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - oldSuffix.Length) + newSuffix
                : name;
        }

        // Find an entry in `combo.Items` whose Tag matches `tag` and
        // select it. No-op if combo is null, tag is empty, or no match.
        private static void SelectComboItemByTag(System.Windows.Controls.ComboBox combo, string tag)
        {
            if (combo == null || string.IsNullOrEmpty(tag)) return;
            foreach (var obj in combo.Items)
            {
                if (obj is System.Windows.Controls.ComboBoxItem item
                    && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
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
            ApplyDarkTheme(win);

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
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Enabled = EngineEnabledCheck.IsChecked == true;
            Apply(EffectKind.Engine);
        }
        private void EngineGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Gain = v;
            Apply(EffectKind.Engine);
        }
        // GitHub issue template URL. Pre-fills title + body with the active
        // car's state so users can submit corrections in one click. No need
        // to remember the carId, the source attribution, or the format.
        private const string ReportIssuesBase = "https://github.com/Mhytee/Trueforce-For-All/issues/new";
        private const string RepoUrl          = "https://github.com/Mhytee/Trueforce-For-All";

        private void ReportIssue_Click(object sender, RoutedEventArgs e)
        {
            // Offer to bundle logs before opening the issue form. GitHub's URL
            // length limits mean we can't paste logs into the body anyway; the
            // user attaches the zip after the form opens.
            var choice = MessageBox.Show(
                "Include your SimHub logs with this bug report?\n\n" +
                "Click Yes to first export your logs to a zip on your Desktop. " +
                "After the GitHub form opens, drag the zip into the issue body to attach it. " +
                "Including logs makes bugs MUCH easier to diagnose, especially anything wheel- or USBPcap-related.\n\n" +
                "Click No to open the form without exporting logs.",
                "Trueforce: Include logs?",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                // Re-use the export-logs path. If it fails, surface the error
                // but still open the issue form so the user can file something.
                TryExportLogs(silentOnSuccess: false);
            }

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
                + "- Wheel: <e.g. G PRO, RS50, G923>\n"
                + "\n**Logs:** attach the .zip from your Desktop (if exported)\n";
            string url = ReportIssuesBase
                       + "?title=" + Uri.EscapeDataString("[bug] ")
                       + "&body="  + Uri.EscapeDataString(body);
            OpenUrl(url);
        }

        // Standalone "Export logs" button. Same exporter the Report Issue
        // dialog uses; opens Explorer to the resulting zip so users can drag
        // it into the bug report or share it directly with support.
        private void ExportLogs_Click(object sender, RoutedEventArgs e)
        {
            TryExportLogs(silentOnSuccess: false);
        }

        // Zips SimHub's log directory + the Trueforce settings file to the
        // user's Desktop and opens Explorer with the new zip selected. Logged
        // errors instead of silent so partial failures (e.g. one log file
        // locked by SimHub) don't kill the export. Returns the zip path on
        // success or null on failure.
        private string TryExportLogs(bool silentOnSuccess)
        {
            try
            {
                // SimHub install dir. We're loaded into SimHubWPF.exe; using
                // the host process's MainModule path is more reliable than
                // walking up from our own assembly (we live under PluginsData).
                string simHubRoot = System.IO.Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                string logsDir   = System.IO.Path.Combine(simHubRoot, "Logs");
                string debugLog  = System.IO.Path.Combine(simHubRoot, "debug.log");
                string settings  = System.IO.Path.Combine(simHubRoot, "Trueforce-settings.json");

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string zipPath = System.IO.Path.Combine(desktop, $"Trueforce-logs-{ts}.zip");

                using (var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.Create))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    if (System.IO.Directory.Exists(logsDir))
                    {
                        foreach (var f in System.IO.Directory.GetFiles(logsDir))
                        {
                            try { AddFileToZip(zip, f, "Logs/" + System.IO.Path.GetFileName(f)); }
                            catch (Exception ex) { TryAddNoteToZip(zip, "Logs/_" + System.IO.Path.GetFileName(f) + ".error.txt", ex.Message); }
                        }
                    }
                    if (System.IO.File.Exists(debugLog))
                    {
                        try { AddFileToZip(zip, debugLog, "debug.log"); }
                        catch (Exception ex) { TryAddNoteToZip(zip, "debug.log.error.txt", ex.Message); }
                    }
                    if (System.IO.File.Exists(settings))
                    {
                        try { AddFileToZip(zip, settings, "Trueforce-settings.json"); } catch { }
                    }
                    // Opt-in raw USB packet trace. Only present when the user
                    // explicitly enabled the Diagnostics toggle. Real pcap
                    // file with USBPcap link type; bundled into the zip so
                    // support can open it with Wireshark + USBPcap dissector.
                    string usbTrace = TrueforcePlugin.GetUsbTraceLogPath();
                    if (System.IO.File.Exists(usbTrace))
                    {
                        try { AddFileToZip(zip, usbTrace, "usb-trace.pcap"); }
                        catch (Exception ex) { TryAddNoteToZip(zip, "usb-trace.pcap.error.txt", ex.Message); }
                    }
                    // Mini context manifest so support knows what version
                    // generated the zip without unpacking everything.
                    string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
                    string manifest =
                        $"Generated: {DateTime.Now:o}\n" +
                        $"Plugin version: {version}\n" +
                        $"Active game: {_plugin?.ActiveGame ?? "(none)"}\n" +
                        $"Active car: {_plugin?.ActiveCarId ?? "(none)"}\n" +
                        $"Wheel status: {_plugin?.WheelStatus}\n" +
                        $"Stream status: {_plugin?.StreamStatus}\n" +
                        $"FFB tap status: {_plugin?.FfbTapStatus}\n" +
                        $"Capture: {_plugin?.CaptureFingerprint ?? "(not confirmed this session)"}\n" +
                        $"Experimental FFB capture: {(_plugin?.Settings?.ExperimentalFfbCapture ?? false ? "ON" : "OFF")}\n" +
                        $"Manual USBPcap override: {(_plugin?.HasManualUsbPcapDevice ?? false ? $"{_plugin.Settings.ManualUsbPcapInterface} dev {_plugin.Settings.ManualUsbPcapDeviceAddress}" : "(none)")}\n" +
                        $"USB byte logging: {(_plugin?.Settings?.LogUsbBytesEnabled ?? false ? "enabled" : "disabled")}\n" +
                        $"SimHub root: {simHubRoot}\n";
                    TryAddNoteToZip(zip, "manifest.txt", manifest);
                }

                // Reveal in Explorer so users don't have to hunt for it.
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"")
                    {
                        UseShellExecute = true,
                    });
                }
                catch { }

                if (!silentOnSuccess)
                {
                    MessageBox.Show(
                        $"Exported logs to:\n{zipPath}\n\nAttach this zip to your bug report so support can see what's happening.",
                        "Trueforce: Logs exported",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return zipPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Couldn't export logs:\n{ex.Message}",
                    "Trueforce", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        // Copy a file into a zip entry while tolerating the file being held by
        // another process (SimHub is actively writing to its current log).
        // Opens with shared read so we don't error on an in-use rolling log.
        private static void AddFileToZip(System.IO.Compression.ZipArchive zip, string sourcePath, string entryName)
        {
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (var inStream = new System.IO.FileStream(sourcePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var outStream = entry.Open())
            {
                inStream.CopyTo(outStream);
            }
        }

        private static void TryAddNoteToZip(System.IO.Compression.ZipArchive zip, string entryName, string text)
        {
            try
            {
                var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                using (var outStream = entry.Open())
                using (var w = new System.IO.StreamWriter(outStream))
                    w.Write(text);
            }
            catch { }
        }

        // Toggle raw USB packet logging on/off. Persists the choice and
        // applies it to the live FFB tap so the next packet observed starts
        // (or stops) writing to usb-trace.bin alongside SimHub's logs.
        private void LogUsbBytes_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || LogUsbBytesCheck == null) return;
            _plugin.SetUsbBytesLoggingEnabled(LogUsbBytesCheck.IsChecked == true);
        }

        private void ExperimentalFfbDetection_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || ExperimentalFfbCheck == null) return;
            ApplyExperimentalFfbCapture(ExperimentalFfbCheck.IsChecked == true, syncSource: ExperimentalFfbCheck);
        }
        private void ExperimentalFfbDetectionMain_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || ExperimentalFfbMainCheck == null) return;
            ApplyExperimentalFfbCapture(ExperimentalFfbMainCheck.IsChecked == true, syncSource: ExperimentalFfbMainCheck);
        }
        private void ApplyExperimentalFfbCapture(bool on, CheckBox syncSource)
        {
            _plugin.SetExperimentalFfbCapture(on);
            // Keep the main-page and advanced-modal checkboxes in sync without
            // firing each other's Changed handler.
            var prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                if (syncSource != ExperimentalFfbCheck     && ExperimentalFfbCheck != null)     ExperimentalFfbCheck.IsChecked     = on;
                if (syncSource != ExperimentalFfbMainCheck && ExperimentalFfbMainCheck != null) ExperimentalFfbMainCheck.IsChecked = on;
            }
            finally { _suppressEvents = prev; }
        }

        // Open the manual USB-device picker dialog. Always available, not
        // gated on auto-discovery failing. Users can override our detection
        // at any time. Selection persists across restarts.
        private void UsbPcapPickDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new UsbDevicePickerWindow(_plugin);
            try { dlg.Owner = Window.GetWindow(this); } catch { }
            dlg.ShowDialog();
        }

        private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

        // Reciprocal funnel: TF4ALL points iRacing users at MAIRA, whose
        // "Pass FFB through TF4ALL" toggle is the supported full-feature path.
        private const string MairaRefactoredUrl = "https://github.com/mherbold/MarvinsAIRARefactored/releases/latest";
        private void GetMaira_Click(object sender, RoutedEventArgs e) => OpenUrl(MairaRefactoredUrl);

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

            string src = ep.AutoLayoutSource;
            bool detected = !string.IsNullOrEmpty(src) && ep.AutoLayout.HasValue;
            var userLayout = es.Layout;
            string customRaw = es.CustomFiringPattern;

            bool layoutDiff = userLayout != Effects.EngineLayout.Auto
                           && (!detected || userLayout != ep.AutoLayout.Value);
            bool customDiff = userLayout == Effects.EngineLayout.Custom
                           && !string.IsNullOrEmpty(customRaw);

            bool anyDiff   = layoutDiff || customDiff;
            bool userHasData = userLayout != Effects.EngineLayout.Auto
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
        // dropdowns / slider. No "FILL IN" placeholders; the user's UI
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
            string layoutSrc   = ep?.AutoLayoutSource ?? "";
            var autoLayout     = ep?.AutoLayout;
            bool layoutDetected = autoLayout.HasValue && !string.IsNullOrEmpty(layoutSrc);

            // What the user has on the panel (their committed preset values).
            // ElectricMode (silent / muted hum) is intentionally not collected:
            // it's a per-user response-curve preference, not a fact about the
            // car, so it doesn't help the bake even on EV submissions.
            var userLayout    = es?.Layout ?? Effects.EngineLayout.Auto;
            string customRaw  = es?.CustomFiringPattern ?? "";
            string customName = (es?.CustomFiringPatternName ?? "").Trim();

            // Build the proposed-changes block as "before -> after" lines.
            // Plain text only -- Google Forms long-answer fields don't render
            // markdown, so any **bold** or `code` ticks would just show as
            // literal characters in the response sheet.
            string LayoutDetectedDisplay()   =>
                !layoutDetected ? "not detected"
                                : Effects.FiringPatternDb.LayoutDisplayName(autoLayout.Value);

            var diff = new System.Collections.Generic.List<string>();
            if (userLayout != Effects.EngineLayout.Auto
                && (!layoutDetected || userLayout != autoLayout.Value))
            {
                diff.Add($"Engine type: {LayoutDetectedDisplay()} -> "
                       + $"{Effects.FiringPatternDb.LayoutDisplayName(userLayout)}");
            }
            if (userLayout == Effects.EngineLayout.Custom && !string.IsNullOrEmpty(customName))
                diff.Add($"Custom pattern name: {customName}");
            if (userLayout == Effects.EngineLayout.Custom && !string.IsNullOrEmpty(customRaw))
                diff.Add($"Custom firing pattern: {customRaw}");

            // CONFIRM is unreachable under the new UX (button hidden + popup
            // skipped when state is None), so the two real categories are:
            //   CORRECTION = resolver had detection, user is changing it
            //   CONTRIB    = no detection, user filled in from scratch
            string category = !string.IsNullOrEmpty(layoutSrc) ? "CORRECTION" : "CONTRIB";

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
            if (!string.IsNullOrEmpty(layoutSrc))
            {
                body += $"Detection source: {layoutSrc}\n\n";
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
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Pitch = v;
            Apply(EffectKind.Engine);
        }
        private void EngineLowpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            EngineLowpassText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.LowpassHz = v;
            Apply(EffectKind.Engine);
        }
        private void EngineWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Waveform = WaveformOf(EngineWaveformCombo);
            Apply(EffectKind.Engine);
        }
        private void EngineElectricMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.ElectricMode = EngineElectricModeCombo.SelectedIndex == 1
                ? ElectricCarMode.Silent
                : ElectricCarMode.MutedHum;
            Apply(EffectKind.Engine);
        }
        private void EngineLoadLayer_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.LoadLayerEnabled = EngineLoadLayerCheck.IsChecked == true;
            Apply(EffectKind.Engine);
        }
        private void EngineLoadLayerGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineLoadLayerGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.LoadLayerGain = v;
            Apply(EffectKind.Engine);
        }
        private void EngineHighRpmBoost_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.HighRpmBoostEnabled = EngineHighRpmBoostCheck.IsChecked == true;
            Apply(EffectKind.Engine);
        }
        private void EngineHighRpmBoostSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineHighRpmBoostText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.HighRpmBoostAmount = v;
            Apply(EffectKind.Engine);
        }

        // ---------- Engine layout dropdown (dynamic: built-ins + customs + actions) ----------

        // Each combobox entry is a DropdownItem so the SelectionChanged handler
        // can branch on kind. Built-ins map to an EngineLayout enum value;
        // Custom entries carry the CustomEngineDef they reference; Action
        // entries open dialogs without committing a layout change.
        private enum EngineDropdownKind { BuiltIn, Custom, ActionNew, ActionManage }
        private sealed class EngineDropdownItem
        {
            public EngineDropdownKind   Kind;
            public Effects.EngineLayout BuiltIn;   // when Kind == BuiltIn
            public CustomEngineDef      Custom;    // when Kind == Custom
            public string               Display;
            public override string ToString() => Display;
        }
        private readonly List<EngineDropdownItem> _engineItems = new List<EngineDropdownItem>();

        /// <summary>(Re)populate the engine-layout dropdown from the built-in
        /// EngineLayout enum + the user's saved customs in
        /// TrueforceSettings.CustomEngines, plus the "Custom..." and
        /// "Manage customs..." action sentinels. Preserves the current
        /// selection across rebuilds (so adding a new custom doesn't bounce
        /// the user back to Auto).</summary>
        private void RebuildEngineLayoutDropdown()
        {
            if (EngineLayoutCombo == null) return;
            var es = _plugin?.ActiveEngine;
            var targetLayout   = es?.Layout ?? Effects.EngineLayout.Auto;
            var targetCustomId = es?.CustomEngineId ?? "";

            _engineItems.Clear();
            foreach (Effects.EngineLayout l in Enum.GetValues(typeof(Effects.EngineLayout)))
            {
                if (l == Effects.EngineLayout.Custom) continue;   // Custom is reached via the library / action
                _engineItems.Add(new EngineDropdownItem
                {
                    Kind    = EngineDropdownKind.BuiltIn,
                    BuiltIn = l,
                    Display = Effects.FiringPatternDb.LayoutDisplayName(l),
                });
            }
            var customs = _plugin?.Settings?.CustomEngines;
            if (customs != null)
            {
                foreach (var c in customs)
                {
                    if (c == null) continue;
                    string name = string.IsNullOrWhiteSpace(c.Name) ? "(unnamed)" : c.Name;
                    _engineItems.Add(new EngineDropdownItem
                    {
                        Kind    = EngineDropdownKind.Custom,
                        Custom  = c,
                        Display = c.IsElectric ? $"{name}  (electric custom)" : $"{name}  (custom)",
                    });
                }
            }
            _engineItems.Add(new EngineDropdownItem { Kind = EngineDropdownKind.ActionNew,    Display = "Custom… (create new)" });
            if (customs != null && customs.Count > 0)
                _engineItems.Add(new EngineDropdownItem { Kind = EngineDropdownKind.ActionManage, Display = "Manage customs…" });

            int idx = FindEngineDropdownIndex(targetLayout, targetCustomId);
            bool old = _suppressEvents;
            _suppressEvents = true;
            try
            {
                EngineLayoutCombo.ItemsSource = null;
                EngineLayoutCombo.ItemsSource = _engineItems;
                EngineLayoutCombo.SelectedIndex = idx;
            }
            finally { _suppressEvents = old; }
        }

        private int FindEngineDropdownIndex(Effects.EngineLayout layout, string customId)
        {
            if (layout == Effects.EngineLayout.Custom)
            {
                for (int i = 0; i < _engineItems.Count; i++)
                {
                    var it = _engineItems[i];
                    if (it.Kind == EngineDropdownKind.Custom
                        && string.Equals(it.Custom?.Id, customId, StringComparison.Ordinal))
                        return i;
                }
                return 0;   // referenced custom missing, fall back to Auto
            }
            for (int i = 0; i < _engineItems.Count; i++)
            {
                var it = _engineItems[i];
                if (it.Kind == EngineDropdownKind.BuiltIn && it.BuiltIn == layout) return i;
            }
            return 0;
        }

        private void EngineLayout_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var item = EngineLayoutCombo.SelectedItem as EngineDropdownItem;
            if (item == null) return;
            var es = _plugin.ActiveEngine;

            switch (item.Kind)
            {
                case EngineDropdownKind.BuiltIn:
                    es.Layout         = item.BuiltIn;
                    es.CustomEngineId = "";
                    Apply(EffectKind.Engine);
                    UpdateFiringPatternReadout(es);
                    break;

                case EngineDropdownKind.Custom:
                    es.Layout         = Effects.EngineLayout.Custom;
                    es.CustomEngineId = item.Custom?.Id ?? "";
                    Apply(EffectKind.Engine);
                    UpdateFiringPatternReadout(es);
                    break;

                case EngineDropdownKind.ActionNew:
                    OpenCustomEngineEditorForNew();
                    break;

                case EngineDropdownKind.ActionManage:
                    OpenManageCustomEnginesDialog();
                    break;
            }
        }

        // Open the editor with a fresh entry. On Save, append to the library,
        // activate it on the current preset, and rebuild the dropdown. On
        // Cancel, just rebuild so the dropdown snaps back to the previous
        // selection (the user clicked an action item, not a real layout).
        private void OpenCustomEngineEditorForNew()
        {
            if (_plugin?.Settings == null) return;
            var def = new CustomEngineDef { Id = Guid.NewGuid().ToString("N") };
            var dlg = new CustomEngineEditor { Owner = Window.GetWindow(this) };
            dlg.Init(def, "Create custom engine");
            bool saved = dlg.ShowDialog() == true && dlg.Saved;
            if (saved)
            {
                if (_plugin.Settings.CustomEngines == null)
                    _plugin.Settings.CustomEngines = new List<CustomEngineDef>();
                _plugin.Settings.CustomEngines.Add(def);

                var es = _plugin.ActiveEngine;
                es.Layout         = Effects.EngineLayout.Custom;
                es.CustomEngineId = def.Id;
                Apply(EffectKind.Engine);
            }
            RebuildEngineLayoutDropdown();
            UpdateFiringPatternReadout(_plugin.ActiveEngine);
        }

        private void OpenManageCustomEnginesDialog()
        {
            OpenManagePresetsDialog(ManagePresetsDialog.InitialTab.CustomEngines);
        }

        /// <summary>Open the unified Manage Presets dialog. Refreshes the
        /// preset combos, engine dropdown, and live engine application when
        /// the dialog closes so any rename / delete / set-active changes
        /// land immediately. If the user picked Edit on a game-preset row,
        /// transitions the main panel into offline-edit mode for that
        /// preset before refreshing.</summary>
        private void OpenManagePresetsDialog(ManagePresetsDialog.InitialTab initialTab = ManagePresetsDialog.InitialTab.GamePresets)
        {
            if (_plugin?.Settings == null) return;
            if (_plugin.Settings.CustomEngines == null)
                _plugin.Settings.CustomEngines = new List<CustomEngineDef>();
            var dlg = new ManagePresetsDialog { Owner = Window.GetWindow(this) };
            dlg.Init(_plugin, initialTab);
            dlg.ShowDialog();
            // If Edit was clicked, hand off to the offline-edit entry point.
            // EnterOfflineEditMode itself triggers a RefreshFromPlugin so we
            // don't double-call the refresh; engine dropdown + live engine
            // apply still need to happen so renames in the Custom Engines
            // tab propagate.
            string editTarget = dlg.RequestedEditPresetName;
            if (!string.IsNullOrEmpty(editTarget))
                EnterOfflineEditMode(editTarget);
            else
                RefreshFromPlugin();
            RebuildEngineLayoutDropdown();
            Apply(EffectKind.Engine);
            UpdateFiringPatternReadout(_plugin.ActiveEngine);
        }

        private void ManagePresetsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenManagePresetsDialog(ManagePresetsDialog.InitialTab.GamePresets);
        }

        // Sync the pattern readout textbox to the currently selected layout.
        // Read-only. Authoring custom engines happens in the modal dialog,
        // not inline. Shows a friendly message for Electric / dangling custom
        // references; shows the FiringPattern's Name + serialized positions
        // for built-ins and combustion customs.
        private void UpdateFiringPatternReadout(EnginePulseSettings es)
        {
            if (EngineFiringPatternText == null || es == null) return;

            string display;
            if (es.Layout == Effects.EngineLayout.Electric)
            {
                display = "Electric: no firing pattern (behavior above)";
            }
            else if (es.Layout == Effects.EngineLayout.Custom)
            {
                var custom = FindCustomById(es.CustomEngineId);
                if (custom == null)
                {
                    display = "Custom: referenced engine not found in library";
                }
                else if (custom.IsElectric)
                {
                    display = $"Electric custom: {custom.Name} ({(custom.ElectricMode == ElectricCarMode.Silent ? "silent" : "muted hum")})";
                }
                else
                {
                    var parsed = Effects.FiringPatternDb.ParseCustom(custom.Pattern);
                    display = parsed == null
                        ? $"Custom: {custom.Name} (invalid pattern)"
                        : $"Custom: {custom.Name}, {Effects.FiringPatternDb.Format(parsed)}";
                }
            }
            else
            {
                // Show the layout's built-in pattern. Effective layout cascades
                // Auto through the resolver's pick when one is available.
                var ep = _plugin.EnginePulse;
                var layout = ep?.EffectiveLayout ?? es.Layout;
                var pat = Effects.FiringPatternDb.ResolveLayout(layout);
                display = pat == null
                    ? ""
                    : $"{pat.Name}: {Effects.FiringPatternDb.Format(pat)}";
            }
            bool old = _suppressEvents;
            _suppressEvents = true;
            try { EngineFiringPatternText.Text = display; }
            finally { _suppressEvents = old; }
        }

        private CustomEngineDef FindCustomById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var customs = _plugin?.Settings?.CustomEngines;
            if (customs == null) return null;
            foreach (var c in customs)
            {
                if (c != null && string.Equals(c.Id, id, StringComparison.Ordinal))
                    return c;
            }
            return null;
        }

        // ---------- Road bumps ----------

        private void SlipEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Enabled = SlipEnabledCheck.IsChecked == true;
            Apply(EffectKind.Bumps);
        }
        private void SlipGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            SlipGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Gain = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Waveform = WaveformOf(BumpsWaveformCombo);
            Apply(EffectKind.Bumps);
        }
        private void BumpsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Freq = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceEnabled = BumpsSurfaceEnabledCheck.IsChecked == true;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceGain = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceFreq = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceWaveform = WaveformOf(BumpsSurfaceWaveformCombo);
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceRumbleScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceRumbleScaleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceRumbleScale = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsRumbleStripPulseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsRumbleStripPulseText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.RumbleStripPulseAmp = v;
            Apply(EffectKind.Bumps);
        }

        // ---------- Traction loss ----------

        private void TractionEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Enabled = TractionEnabledCheck.IsChecked == true;
            Apply(EffectKind.Traction);
        }
        private void TractionGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Gain = v;
            Apply(EffectKind.Traction);
        }
        private void TractionSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionSensitivityText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Sensitivity = v;
            Apply(EffectKind.Traction);
        }
        private void TractionWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var wf = WaveformOf(TractionWaveformCombo);
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Waveform = wf;
            // Show/hide noise filter rows live (they only matter for Noise).
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
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Freq = v;
            Apply(EffectKind.Traction);
        }
        private void TractionNoiseLpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            TractionNoiseLpText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.NoiseLowpassHz = v;
            Apply(EffectKind.Traction);
        }
        private void TractionNoiseHpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            TractionNoiseHpText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.NoiseHighpassHz = v;
            Apply(EffectKind.Traction);
        }

        // ---------- Gear shift ----------

        private void ShiftEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Enabled = ShiftEnabledCheck.IsChecked == true;
            Apply(EffectKind.Shift);
        }
        private void ShiftGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Gain = v;
            Apply(EffectKind.Shift);
        }
        private void ShiftFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Freq = v;
            Apply(EffectKind.Shift);
        }
        private void ShiftWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Waveform = WaveformOf(ShiftWaveformCombo);
            Apply(EffectKind.Shift);
        }

        // ---------- Pit limiter ----------

        private void PitLimiterEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Enabled = PitLimiterEnabledCheck.IsChecked == true;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Gain = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Waveform = WaveformOf(PitLimiterWaveformCombo);
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Freq = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterPulseFreqText.Text = v.ToString("F1");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.PulseFreq = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterDutyCycleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.DutyCycle = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterActiveAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterActiveAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.ActiveAmp = v;
            Apply(EffectKind.PitLimiter);
        }

        private void RevLimiterEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Enabled = RevLimiterEnabledCheck.IsChecked == true;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Gain = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterThresholdText.Text = ((int)Math.Round(v * 100)).ToString() + "%";
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Threshold = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Waveform = WaveformOf(RevLimiterWaveformCombo);
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterOffsetText.Text = FormatRedlineOffset(v);
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.RedlineOffsetRpm = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterEngageMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.EngageMode =
                RevLimiterEngageModeCombo.SelectedIndex == 1 ? RevLimiterEngageMode.Percentage
              : RevLimiterEngageModeCombo.SelectedIndex == 2 ? RevLimiterEngageMode.Redline
              : RevLimiterEngageMode.Auto;
            Apply(EffectKind.RevLimiter);
            UpdateRevLimiterEngageVisibility();
        }

        // Show the engage-% row vs the "fires at redline" note based on the
        // EFFECTIVE engage behaviour: forced Percentage always shows the %;
        // forced Redline always shows the note; Auto defers to whether the
        // active game reports a usable redline (ActiveSourceUsesRedline).
        private void UpdateRevLimiterEngageVisibility()
        {
            if (_plugin == null || RevLimiterThresholdRow == null) return;
            var mode = _plugin.ActiveRevLimiter?.EngageMode ?? RevLimiterEngageMode.Auto;
            bool firesAtRedline =
                mode == RevLimiterEngageMode.Redline ||
                (mode == RevLimiterEngageMode.Auto && _plugin.ActiveSourceUsesRedline);
            RevLimiterThresholdRow.Visibility = firesAtRedline ? Visibility.Collapsed : Visibility.Visible;
            if (RevLimiterRedlineNote != null)
                RevLimiterRedlineNote.Visibility = firesAtRedline ? Visibility.Visible : Visibility.Collapsed;
            // Redline offset only applies on the real-redline path.
            if (RevLimiterOffsetRow != null)
                RevLimiterOffsetRow.Visibility = firesAtRedline ? Visibility.Visible : Visibility.Collapsed;
        }

        // "0", "-500", "+250" — signed RPM, no decimals.
        private static string FormatRedlineOffset(double rpm)
        {
            int r = (int)Math.Round(rpm);
            return r > 0 ? "+" + r.ToString() : r.ToString();
        }
        private void RevLimiterFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Freq = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterPulseFreqText.Text = v.ToString("F1");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.PulseFreq = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterDutyCycleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.DutyCycle = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterActiveAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterActiveAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.ActiveAmp = v;
            Apply(EffectKind.RevLimiter);
        }

        // ---------- Airborne ducking (global section; uses the dirty/save flow
        // like the other effects, so changes surface a Save button + feed the
        // ★ Save all pill instead of persisting silently) ----------

        private void AirborneEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.Airborne.Enabled = AirborneEnabledCheck.IsChecked == true;
            _plugin.ApplyAirborneSettings();
            MarkEffectDirty(EffectKind.Airborne);
        }
        private void AirborneReductionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            AirborneReductionText.Text = ((int)Math.Round(v * 100)).ToString() + "%";
            _plugin.Settings.Airborne.Reduction = v;
            _plugin.ApplyAirborneSettings();
            MarkEffectDirty(EffectKind.Airborne);
        }
        // One handler for every per-effect toggle: read all the checkboxes back
        // into Settings, re-apply, persist. Cheaper to write than ten near-
        // identical handlers and the work is trivial.
        private void AirborneToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var a = _plugin.Settings.Airborne;
            a.DuckEngine       = AirborneDuckEngineCheck.IsChecked    == true;
            a.DuckAudio        = AirborneDuckAudioCheck.IsChecked     == true;
            a.DuckRoadBumps    = AirborneDuckRoadBumpsCheck.IsChecked == true;
            a.DuckTractionLoss = AirborneDuckTractionCheck.IsChecked  == true;
            a.DuckRevLimiter   = AirborneDuckRevLimiterCheck.IsChecked== true;
            a.DuckGearShift    = AirborneDuckGearShiftCheck.IsChecked == true;
            a.DuckAbs          = AirborneDuckAbsCheck.IsChecked       == true;
            a.DuckPitLimiter   = AirborneDuckPitLimiterCheck.IsChecked== true;
            a.DuckDrs          = AirborneDuckDrsCheck.IsChecked       == true;
            a.DuckCollision    = AirborneDuckCollisionCheck.IsChecked == true;
            _plugin.ApplyAirborneSettings();
            MarkEffectDirty(EffectKind.Airborne);
        }

        // ---------- DRS ----------

        private void DrsEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.Enabled = DrsEnabledCheck.IsChecked == true;
            Apply(EffectKind.Drs);
        }
        private void DrsGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.Gain = v;
            Apply(EffectKind.Drs);
        }
        private void DrsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.Waveform = WaveformOf(DrsWaveformCombo);
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.SustainedWaveform = WaveformOf(DrsSustainedWaveformCombo);
            Apply(EffectKind.Drs);
        }
        private void CollisionWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Waveform = WaveformOf(CollisionWaveformCombo);
            Apply(EffectKind.Collision);
        }
        private void DrsActivationFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsActivationFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.ActivationFreq = v;
            Apply(EffectKind.Drs);
        }
        private void DrsActivationMsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)e.NewValue;
            DrsActivationMsText.Text = v.ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.ActivationMs = v;
            Apply(EffectKind.Drs);
        }
        private void DrsActivationAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsActivationAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.ActivationAmp = v;
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsSustainedFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.SustainedFreq = v;
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsSustainedAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.SustainedAmp = v;
            Apply(EffectKind.Drs);
        }

        // ---------- Collision ----------

        private void CollisionEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Enabled = CollisionEnabledCheck.IsChecked == true;
            Apply(EffectKind.Collision);
        }
        private void CollisionGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Gain = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionMinThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionMinThresholdText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.MinThreshold = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionMaxAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionMaxAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.MaxAmp = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Freq = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionEnvelopeMsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)e.NewValue;
            CollisionEnvelopeMsText.Text = v.ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.EnvelopeMs = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.Collision);
        private void RevLimiterTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.RevLimiter);

        // ---------- ABS ----------

        private void AbsEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Enabled = AbsEnabledCheck.IsChecked == true;
            Apply(EffectKind.Abs);
        }
        private void AbsGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Gain = v;
            Apply(EffectKind.Abs);
        }
        private void AbsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Freq = v;
            Apply(EffectKind.Abs);
        }
        private void AbsPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsPulseFreqText.Text = v.ToString("F1");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.PulseFreq = v;
            Apply(EffectKind.Abs);
        }
        private void AbsDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsDutyCycleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.DutyCycle = v;
            Apply(EffectKind.Abs);
        }
        private void AbsMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int idx = AbsModeCombo.SelectedIndex; if (idx < 0) idx = 0;
            var mode = (AbsMode)idx;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Mode = mode;
            // Pulse rate / duty are unused in PerTick mode (grey them live).
            AbsPulseControls.IsEnabled = mode == AbsMode.Pulse;
            Apply(EffectKind.Abs);
        }
        private void AbsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Waveform = WaveformOf(AbsWaveformCombo);
            Apply(EffectKind.Abs);
        }

        // ---------- Forza UDP listener ----------
        // No enable/disable toggle: the Forza UDP reader is the only source of
        // Forza's per-tire data, so it is always on for Forza (see
        // SwapTelemetrySource). Only the port / bind / forwarding are tunable.

        // ---------- Tester access code (unlocks the rim-LED / MAIRA section) ----------

        private void AccessCode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitAccessCode();
        }

        private void AccessCode_Changed(object sender, RoutedEventArgs e) => CommitAccessCode();

        // Single source of truth for the HELP / CODES listing. When you add a
        // new access code in CommitAccessCode below, add a line here too so
        // HELP stays accurate as the set of codes grows.
        private const string TestCodeCatalog =
            "Trueforce For All test codes (type one in the access box):\n\n" +
            "HELP / CODES   Show this list.\n" +
            "SHARE          Force the 'spread the word' banner on now.\n" +
            "RATCHET        Play the auto-tuned ring-buffer banner sequence.\n" +
            "STALL          Simulate a Forza 'no packets' stall + open the troubleshooter (toggle).\n" +
            "SPRING         Desk test of the stationary spring (motor pushes one way, then the other).\n" +
            "REV            Rev limiter buzz from a synthetic redline (tests the RPM trigger + hold).\n" +
            "WHATSNEW       Re-show the 'What's new' banner and all NEW effect badges.\n" +
            "UPDATE         Simulate an available update (banner + update dialog).\n" +
            "FAULT          Force a stream fault to test auto-reconnect.\n" +
            "NOFFB          Simulate the FFB tap capturing no game force feedback while driving (tests the whole-bus retry + 'try another USB port' notice). Toggle.\n" +
            "FFBX           Opt in to the experimental FFB-capture path (HID++ report 0x12 + faster index resolve; issue #8 RS50/FH6). Persists. Toggle.\n" +
            "FFBOK          Force the 'is your FFB working?' success banner on now, to test the Yes (report) and No (troubleshooter) paths.\n" +
            "MAIRA / TEST   Unlock the rim rev/shift-LED + MAIRA section (iRacing profile).";

        private void CommitAccessCode()
        {
            if (_suppressEvents || _plugin?.Settings == null || AccessCodeBox == null) return;
            string code = (AccessCodeBox.Text ?? string.Empty).Trim();
            // Self-documenting: list every test code and what it does.
            if (code.Equals("HELP", StringComparison.OrdinalIgnoreCase)
                || code.Equals("CODES", StringComparison.OrdinalIgnoreCase)
                || code == "?")
            {
                AccessCodeBox.Text = string.Empty;
                MessageBox.Show(Window.GetWindow(this), TestCodeCatalog,
                    "Trueforce For All: test codes", MessageBoxButton.OK, MessageBoxImage.Information);
                if (AccessCodeStatus != null) AccessCodeStatus.Text = "Showed the test-code list.";
                return;
            }
            // Dev-only: force the one-and-done word-of-mouth banner to show
            // now (it normally needs ~2h of banked seat time). Lets us see
            // the real banner + share dialog before any of it ships. Resets
            // the dismissed latch too, so it's repeatable.
            if (code.Equals("SHARE", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.ActiveStreamingSeconds = double.MaxValue;
                _plugin.Settings.ShareCtaDismissed = false;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Word-of-mouth banner forced on (test).";
                RefreshShareCtaBanner();
                return;
            }

            // Dev-only: fire a synthetic auto-ratchet sequence so the
            // inline "Auto-tuned ring buffer" banner can be exercised
            // without waiting for real underruns. The full UP-then-DOWN
            // arc demonstrates:
            //   t=0.0s   TF up  8 → 16    → banner appears
            //   t=0.7s   TF up  16 → 32   → same banner, "8 → 32"
            //   t=1.4s   Audio up 16 → 32 → same banner, both rings
            //   t=3.5s   TF down 32 → 16  → TF segment shrinks to "8 → 16"
            //   t=4.2s   TF down 16 → 8   → TF back to start, segment drops
            //   t=4.9s   Audio down 32 → 16 → audio back to start, banner auto-dismisses
            if (code.Equals("RATCHET", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin != null)
                {
                    _plugin.DebugFireRatchet(true,  8,  16);
                    ScheduleDispatcherDelay(700,  () => _plugin.DebugFireRatchet(true,  16, 32));
                    ScheduleDispatcherDelay(1400, () => _plugin.DebugFireRatchet(false, 16, 32));
                    ScheduleDispatcherDelay(3500, () => _plugin.DebugFireRatchet(true,  32, 16));
                    ScheduleDispatcherDelay(4200, () => _plugin.DebugFireRatchet(true,  16, 8));
                    ScheduleDispatcherDelay(4900, () => _plugin.DebugFireRatchet(false, 32, 16));
                }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Synthetic ratchet sequence fired (UP then DOWN; ~5 s total). Banner should appear, evolve, then auto-dismiss as both rings return to start.";
                return;
            }

            // Dev-only: simulate a Forza "no packets" stall so the status line
            // reads stalled and the "Not receiving packets?" troubleshooter
            // auto-opens, without needing a live (broken) Forza session. Toggle:
            // type STALL again to clear. The actual UI change lands on the next
            // refresh tick (re-arming the auto-expand latch each toggle).
            if (code.Equals("STALL", StringComparison.OrdinalIgnoreCase))
            {
                _forceForzaStall = !_forceForzaStall;
                _forzaTroubleshootAutoExpanded = false;
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _forceForzaStall
                        ? "Forza stall simulated: status reads stalled and the troubleshooter auto-opens (type STALL again to clear)."
                        : "Forza stall simulation cleared.";
                return;
            }

            // Dev-only: feel the stationary spring on the desk without a game.
            // Drives a synthetic centering force that flips direction every
            // ~1.5 s for ~6 s, bypassing the enabled/speed/steering gates.
            // Lets us verify strength + the force-vs-position direction. It
            // does NOT verify a given game's steering sign (that needs a
            // session); it confirms the spring's own mapping is correct.
            if (code.Equals("SPRING", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.StartStationarySpringTest();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Stationary-spring test (~6 s): hold the wheel; the motor pushes one way, then the other, every ~1.5 s, so you can feel the spring strength and direction. (Needs the wheel connected and streaming.)";
                return;
            }

            // Dev-only: exercise the rev limiter's RPM threshold + hold from
            // synthetic telemetry (the Test button only plays the buzz). Sweeps
            // below threshold (silent) -> over redline (buzz) -> off.
            if (code.Equals("REV", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugRevLimiterBounce();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Rev limiter test (~5 s): silent for ~1.5 s (below redline), buzzes ~2.7 s (at/over redline), then stops. Confirms it engages on RPM and holds, independent of the Test button.";
                return;
            }

            // Dev-only: re-show the "What's new" changelog banner and every
            // per-effect NEW badge by clearing the seen state. Lets us verify
            // the release UX (banner copy + the new RevLimiter badge) without
            // hand-editing the settings file.
            if (code.Equals("WHATSNEW", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugResetChangelogSeen();
                RefreshChangelogBanner();
                RefreshNewBadges();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Changelog state reset: the 'What's new' banner and all NEW effect badges are showing again (banner lists full history).";
                return;
            }

            // Dev-only: pretend a newer release exists so the update banner +
            // update modal can be verified locally. Appears on the next status
            // refresh tick (~1 s).
            if (code.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.UpdateChecker?.DebugSimulateUpdateAvailable();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _plugin.UpdateChecker != null
                        ? "Simulated update available: the update banner appears within ~1 s; click it to see the update modal."
                        : "Update checker unavailable.";
                return;
            }

            // Dev-only: force the wheel into the stream-fault state so the
            // recovery watchdog re-attaches it. Verifies the "Stream lost -
            // auto-reconnecting" status + transparent recovery without
            // physically unplugging.
            if (code.Equals("FAULT", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugForceStreamFault();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Stream fault forced: status should read 'Stream lost - auto-reconnecting', then recover within a few seconds (watchdog re-attaches the wheel).";
                return;
            }

            // Simulate "tap sees the wheel + FFB on the wire but can't extract
            // it." Drive after enabling: the tap retries in whole-bus mode after
            // ~8 s and surfaces the no-FFB notice after ~15 s. Toggle off to
            // restore real FFB pass-through. FFB will be limp while it's on.
            if (code.Equals("NOFFB", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleSimulateNoFfb();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Simulating no-FFB capture. Drive for ~15s: the FFB tap should switch to whole-bus capture (~8s), then the FFB-tap status should show a 'try a different USB port' notice. Type NOFFB again to stop (FFB stays limp until you do)."
                        : "Stopped simulating no-FFB capture. Force feedback pass-through restored.";
                return;
            }

            // Opt in to the experimental FFB-capture path (issue #8: HID++
            // very-long report 0x12 + lower index-resolve floor). Persisted and
            // applied live; drive to let it re-resolve the FFB index. Toggle.
            if (code.Equals("FFBX", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleExperimentalCapture();
                // Reflect the new state on both checkboxes without re-firing
                // their handlers (the plugin setter already ran above).
                var prevSuppress = _suppressEvents;
                _suppressEvents = true;
                try
                {
                    if (ExperimentalFfbCheck != null)     ExperimentalFfbCheck.IsChecked     = on;
                    if (ExperimentalFfbMainCheck != null) ExperimentalFfbMainCheck.IsChecked = on;
                }
                finally { _suppressEvents = prevSuppress; }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Experimental FFB capture ON (persists). Load a game and drive for a few seconds so the tap re-resolves the FFB index; check the FFB-tap status. Type FFBX again to turn it off."
                        : "Experimental FFB capture OFF. Back to the standard capture path.";
                return;
            }

            // Dev/test: force the "is your FFB working?" success banner on now,
            // so the Yes (prefilled report) and No (troubleshooter) paths can be
            // exercised without real hardware. Dismiss (or click either button)
            // clears it.
            if (code.Equals("FFBOK", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugShowSuccessBanner();
                RefreshExperimentalSuccessBanner();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Success banner forced on (test). Click 'Yes, it's working' to see the prefilled report, or 'No' for the troubleshooter. The x or either button clears it.";
                return;
            }

            bool ok = code.Equals("MAIRA", StringComparison.OrdinalIgnoreCase)
                   || code.Equals("TEST", StringComparison.OrdinalIgnoreCase);
            if (!ok) return;

            if (!_plugin.Settings.RpmLedUnlocked)
            {
                _plugin.Settings.RpmLedUnlocked = true;
                _plugin.PersistSettings();
            }
            AccessCodeBox.Text = string.Empty;
            // The unlock persists, but the section only appears in the iRacing
            // SimHub profile (its toggles apply to iRacing only). If they
            // unlock from another profile, say so plainly so the "nothing
            // happened" isn't a mystery.
            bool showNow = _plugin.ShouldShowRpmLedSection;
            if (AccessCodeStatus != null)
                AccessCodeStatus.Text = showNow
                    ? "Test features unlocked."
                    : "Test features unlocked (shows in the iRacing profile).";
            if (RpmLedSection != null)
                RpmLedSection.Visibility = showNow
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
        }

        // ---------- Rim rev/shift LEDs (iRacing) ----------

        private void RpmLedEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.RpmLedsEnabled = RpmLedEnabledCheck.IsChecked == true;
            _plugin.PersistSettings();
            if (!_plugin.Settings.RpmLedsEnabled) _plugin.TurnOffRpmLeds();
        }

        private void MairaPassthrough_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.MairaFfbPassthrough = MairaPassthroughCheck.IsChecked == true;
            _plugin.PersistSettings();
            // Takes effect on next device (re)start; surface that to the user.
            if (RpmLedStatusText != null)
                RpmLedStatusText.Text = "MAIRA auto-link "
                    + (_plugin.Settings.MairaFfbPassthrough ? "ON" : "OFF")
                    + " (restart plugin to apply)";
        }

        private void RpmLedTest_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.TestRpmLeds();
            // Live-poll the controller's status so the current effect mode is
            // visible in the panel while the sweep runs (the log timing is
            // hard to eyeball against the wheel). Stop a moment after the
            // test ends so the final "LEDs off" line shows.
            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(250) };
            int idleTicks = 0;
            t.Tick += (s2, e2) =>
            {
                if (RpmLedStatusText != null) RpmLedStatusText.Text = _plugin.RpmLedStatus;
                if (_plugin.RpmLedIsTesting) idleTicks = 0;
                else if (++idleTicks > 4) t.Stop();   // ~1s after test ends
            };
            t.Start();
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
            // Car-scoped sections: revert discards the unsaved per-car DRAFT,
            // restoring this car's saved override (or the game default if none),
            // rather than reverting the global section to the active preset.
            bool reverted;
            if (SectionUsesDraftModel(which) && !string.IsNullOrEmpty(_plugin.ActiveCarId))
            {
                _plugin.RevertSectionDraft((TrueforcePlugin.SectionKind)(int)which);
                reverted = true;
            }
            else
            {
                // EffectKind values mirror SectionKind, so pass through directly.
                reverted = _plugin.RevertSection((TrueforcePlugin.SectionKind)(int)which);
            }
            if (reverted)
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
        ///   • "For [car]": toggles the override on (snapshotting current
        ///     section values into the per-car override) when not already
        ///     overridden; just clears the section's dirty dot if it was.
        ///   • "Update preset 'X'": updates the active preset in place
        ///     (whole-snapshot save; clears global dirty too).
        ///   • "Save as new preset…": appears when there's no active preset;
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
                Height = (carScope && carDetected) ? 240 : 170,  // shorter when no per-car option
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode    = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = $"Save current {label} settings to…",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            // Choice 1: per-car override. Only when the section has a per-car
            // concept AND a car is actually identified, there's nothing to save
            // "for this car" with no car, so we hide the option rather than show
            // it disabled.
            Button carBtn = null;
            if (carScope && carDetected)
            {
                carBtn = new Button
                {
                    Content = $"For this car ({carId})",
                    Height = 32, Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = "Saves these settings just for this car. Won't affect global tuning or other cars.",
                };
                sp.Children.Add(carBtn);
                sp.Children.Add(new TextBlock
                {
                    Text = $"Toggles 'Override for this car' on and snapshots the current {label} values into the per-car override.",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }

            // Choice 1b: "Both" — set as the game default AND keep this car
            // pinned with its own override in one action.
            Button bothBtn = null;
            if (SectionUsesDraftModel(which) && carDetected)
            {
                bothBtn = new Button
                {
                    Content = "Both (game default + keep for this car)",
                    Height = 32, Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = "Saves these values as the game default for other cars AND pins them to this car as its own override, so this car won't follow future default changes.",
                };
                sp.Children.Add(bothBtn);
                sp.Children.Add(new TextBlock
                {
                    Text = "Updates the game default and keeps this car's override.",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }

            // Choice 1c: reset this car to the game default. A "reset" is itself
            // a draft (preview the default, then Save to commit or Revert to
            // restore the override), so this just enters the reset-draft and
            // closes. When already in a reset draft, offer to COMMIT it (drop
            // the car's saved override so it follows the default).
            var kind = (TrueforcePlugin.SectionKind)(int)which;
            if (SectionUsesDraftModel(which) && carDetected && _plugin.IsSectionResetDraft(kind))
            {
                var followBtn = new Button
                {
                    Content = "Follow game default (remove this car's override)",
                    Height = 32, Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = "Commits the reset: this car drops its own override and follows the game default from now on.",
                };
                sp.Children.Add(followBtn);
                followBtn.Click += (s, args) =>
                {
                    _plugin.CommitSectionFollowDefault(kind);
                    ClearEffectDirty(which);
                    RefreshFromPlugin();
                    win.DialogResult = true;
                };
            }
            else if (SectionUsesDraftModel(which) && carDetected && _plugin.IsSectionOverridden(kind))
            {
                var resetBtn = new Button
                {
                    Content = "Reset to game default (preview)",
                    Height = 32, Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = "Previews this car following the game default. Drive to try it, then Save to keep it (drops this car's override) or Revert to restore your override.",
                };
                sp.Children.Add(resetBtn);
                sp.Children.Add(new TextBlock
                {
                    Text = "Previews the game default for this car. Nothing is removed until you Save; Revert restores your override.",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                });
                resetBtn.Click += (s, args) =>
                {
                    _plugin.ResetSectionToDefaultDraft(kind);
                    RefreshFromPlugin();
                    win.DialogResult = true;
                };
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
            if (bothBtn != null)
            {
                bothBtn.Click += (s, args) =>
                {
                    _plugin.SaveSectionToBoth((TrueforcePlugin.SectionKind)(int)which);
                    ClearEffectDirty(which);
                    RefreshFromPlugin();
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

                // Per-section Save: save ONLY this section. The ★ Save all
                // button up top is what commits every dirty section, so we
                // don't prompt about other dirty sections here (that was
                // redundant with Save all). Patch only the targeted section
                // into the in-memory snapshot + write GeneralSettings; other
                // sections keep their saved values and their dirty bits remain.
                _plugin.PromoteSectionToGlobal((TrueforcePlugin.SectionKind)(int)which);
                _plugin.SaveSectionToActivePreset((TrueforcePlugin.SectionKind)(int)which);
                ClearEffectDirty(which);
                if (!string.IsNullOrEmpty(_plugin.ActiveGame) && !string.IsNullOrEmpty(_plugin.ActivePresetName))
                    _plugin.SetDefaultPresetForActiveGame(_plugin.ActivePresetName);   // save-to-game-defaults binds
                RefreshFromPlugin();
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
                // Overwrite existing user car preset: save ONLY this section.
                // The ★ Save all button handles every dirty section, so no
                // scope prompt here. Patch just the targeted section into the
                // on-disk override; other sections keep their saved values and
                // their dirty bits persist after RecomputeAllEffectDirty.
                _plugin.SnapshotSectionToCarOverride((TrueforcePlugin.SectionKind)(int)which);
                bool ok = _plugin.SaveSectionToActiveCarOverride(
                    (TrueforcePlugin.SectionKind)(int)which);
                if (!ok)
                {
                    MessageBox.Show("Save failed (see SimHub log for details).", "Trueforce");
                    return;
                }
            }
            RefreshFromPlugin();
            // Prompt only on Engine-section saves (that's where the user
            // committed cylinder/layout values worth submitting). Saves on
            // other sections (Bumps, Traction, etc.) shouldn't trigger a
            // form-submission ask; their data isn't what we're collecting.
            if (which == EffectKind.Engine) MaybePromptToSubmitEngineData(carId);
        }

        /// <summary>Update active preset in place. Whole-snapshot save.
        /// Clears all dirty indicators (global + every section).</summary>
        private void UpdateActivePresetFromUi()
        {
            string name = _plugin.ActivePresetName;
            if (string.IsNullOrEmpty(name)) return;
            _plugin.SavePresetAs(name);
            if (!string.IsNullOrEmpty(_plugin.ActiveGame))
                _plugin.SetDefaultPresetForActiveGame(name);   // save-to-game-defaults binds
            ClearDirty();
            RefreshFromPlugin();
        }

        /// <summary>Save current full state as a new named preset (same flow
        /// as the existing "Save as new" preset library button).</summary>
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
            // Bind it as this game's default so the save actually sticks across
            // sessions (a "save to game defaults" that didn't bind would leave
            // the preset orphaned and not auto-load next time).
            string game = _plugin.ActiveGame;
            if (!string.IsNullOrEmpty(game))
                _plugin.SetDefaultPresetForActiveGame(name);
            ClearDirty();
            RefreshFromPlugin();
            MessageBox.Show(
                string.IsNullOrEmpty(game)
                    ? $"Saved as '{name}'."
                    : $"Saved as '{name}' and bound as the default for '{game}'.",
                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------- Preset library ----------

        private void RefreshPresetSection()
        {
            if (_plugin == null) return;

            string game     = _plugin.ActiveGame;
            string activeP  = _plugin.ActivePresetName;

            UpdateHeaderPresetDisplay();

            // The active-preset dropdown + its inline buttons live in the header
            // context card. The pickers are rebuilt by RefreshGamePresetPicker /
            // RefreshCarPresetPicker (via UpdateHeaderPresetDisplay above); here
            // we just refresh the conditional "Set as default" button.
            UpdateSetDefaultButton();
        }

        /// <summary>Show the inline "Set as default" button (next to the game
        /// preset dropdown) only when a game is loaded AND the active preset is
        /// not already that game's auto-load default. Picking a preset applies
        /// it without binding it as the default, so this is the one remaining
        /// affordance to make the binding; it self-hides once bound.</summary>
        private void UpdateSetDefaultButton()
        {
            if (HeaderSetDefaultBtn == null || _plugin == null) return;
            string activeP = _plugin.ActivePresetName;
            bool gameDetected = !string.IsNullOrEmpty(_plugin.ActiveGame);
            bool hasActive    = !string.IsNullOrEmpty(activeP);
            bool isDefault    = hasActive
                && string.Equals(activeP, _plugin.DefaultPresetForActiveGame, StringComparison.Ordinal);
            HeaderSetDefaultBtn.Visibility = (gameDetected && hasActive && !isDefault)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private string SelectedPresetName => _plugin?.ActivePresetName;

        // The global "Save preset" button mirrors the per-section Save popover,
        // but operates on ALL dirty fields at once: it asks whether to save to
        // this car, the game defaults, or both. (Per-section Save offers the
        // same three choices for just its one section.)
        private void SavePreset_Click(object sender, RoutedEventArgs e) => ShowGlobalSavePopover();

        private void ShowGlobalSavePopover(bool preferCar = false)
        {
            if (_plugin == null) return;
            Button preferredBtn = null;

            // Deliberately no "nothing to save" guard: the user may want to push
            // the current tuning to a DIFFERENT target than they already saved
            // to (e.g. saved to this car, now also wants it as the game
            // default). After the first save the dirty bits are clean, so a
            // dirty-gate would wrongly block that. The popover just directs the
            // current tuning to a chosen target, which is valid either way.
            string carId       = _plugin.ActiveCarId;
            bool   carDetected = !string.IsNullOrEmpty(carId);
            string activeP     = _plugin.ActivePresetName;
            bool   hasPreset   = !string.IsNullOrEmpty(activeP);
            bool   builtin     = hasPreset && _plugin.IsBuiltinPreset(activeP);

            var win = new Window
            {
                Title  = "Save preset",
                Width  = 440,
                Height = carDetected ? 290 : 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode    = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = "Save all your current tuning to…",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            // Choice 1 + 2: per-car and both (only when a car is identified).
            if (carDetected)
            {
                var carBtn = new Button { Content = $"For this car ({carId})", Height = 32, Margin = new Thickness(0, 0, 0, 6) };
                sp.Children.Add(carBtn);
                sp.Children.Add(new TextBlock
                {
                    Text = "Saves your tuning just for this car (per-car override). Won't change other cars or the game default.",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                });
                carBtn.Click += (s, a) => { SaveAllForCar(); win.DialogResult = true; };
                if (preferCar) preferredBtn = carBtn;

                var bothBtn = new Button { Content = "Both (game default + keep for this car)", Height = 32, Margin = new Thickness(0, 0, 0, 6) };
                sp.Children.Add(bothBtn);
                sp.Children.Add(new TextBlock
                {
                    Text = "Saves as the game default AND pins it to this car as its own override.",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                });
                bothBtn.Click += (s, a) =>
                {
                    if (SaveActiveCarPresetWithFork()) { RecomputeAllEffectDirty(); SaveAllGameDefaults(); }
                    win.DialogResult = true;
                };
            }

            // Choice 3: game defaults (fork from a built-in / create one when
            // none, else overwrite the active default in place).
            string gameLabel = (!hasPreset || builtin)
                ? "Save as game defaults"
                : $"Update game defaults ('{ToBuiltinDisplay(activeP)}')";
            string gameHint  = (!hasPreset || builtin)
                ? "Saves your tuning as a new preset and binds it as this game's default."
                : $"Overwrites '{ToBuiltinDisplay(activeP)}' (this game's default) with your current tuning.";
            var gameBtn = new Button { Content = gameLabel, Height = 32, Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(gameBtn);
            sp.Children.Add(new TextBlock
            {
                Text = gameHint,
                FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            });
            gameBtn.Click += (s, a) => { SaveAllGameDefaults(); win.DialogResult = true; };
            if (preferredBtn == null) preferredBtn = gameBtn;   // game-side click, or no car

            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            btnRow.Children.Add(new Button { Content = "Cancel", Width = 80, IsCancel = true });
            sp.Children.Add(btnRow);

            win.Content = sp;
            // Lead with the target that matches the button the user clicked
            // (car-side vs game-side): focus it so Enter commits there and the
            // focus ring points at it, without reordering the choices.
            if (preferredBtn != null)
            {
                var target = preferredBtn;
                win.Loaded += (s, a) => target.Focus();
            }
            win.ShowDialog();
        }

        // Save all dirty tuning to the active game-default preset: forks from a
        // built-in / creates one when there's none, else overwrites in place.
        // Shows a confirmation. The popover choice is the user's intent, so no
        // extra "overwrite?" prompt. Shared by the global Save popover.
        private void SaveAllGameDefaults()
        {
            string activeP = _plugin.ActivePresetName;
            bool   builtin = !string.IsNullOrEmpty(activeP) && _plugin.IsBuiltinPreset(activeP);
            if (string.IsNullOrEmpty(activeP) || builtin)
            {
                ForkAndSaveAsGamePreset();   // shows its own "Saved as…" confirmation
                return;
            }
            _plugin.SavePresetAs(activeP);
            // A "save to game defaults" binds the result when a game is loaded,
            // so the saved preset is this game's auto-load default.
            if (!string.IsNullOrEmpty(_plugin.ActiveGame))
                _plugin.SetDefaultPresetForActiveGame(activeP);
            ClearDirty();
            RefreshFromPlugin();
            MessageBox.Show($"Saved to game default preset '{ToBuiltinDisplay(activeP)}'.",
                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Save all dirty car-scoped tuning to the active car's override (forks a
        // user car preset from a built-in if needed). Shows a confirmation.
        private void SaveAllForCar()
        {
            if (!SaveActiveCarPresetWithFork()) return;   // user cancelled fork prompt
            RecomputeAllEffectDirty();
            RefreshFromPlugin();
            MessageBox.Show("Saved your current tuning for this car.",
                "Trueforce", MessageBoxButton.OK, MessageBoxImage.Information);
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
                // No game and no built-in to fork from. Fall back to name prompt.
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

        // Car-side "Save as new…": save the active car's current tuning under a
        // new car-preset name (never overwrites the active one). Shown only when
        // this car has unsaved tuning (mirrors the car ★ Save all button).
        private void CarSaveAsNew_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string carId      = _plugin.ActiveCarId;
            string activeName = _plugin.GetActiveCarPresetName(carId);
            string suggestion = string.IsNullOrEmpty(activeName) ? carId : StripDefaultSuffix(activeName);
            string newName = PromptForCarPresetName(
                title: "Save as new car preset",
                body:  $"Save the current tuning as a new user preset for '{carId}':",
                initial: suggestion,
                existing: _plugin.GetCarPresets(carId));
            if (string.IsNullOrEmpty(newName)) return;   // cancelled
            _plugin.SaveActiveCarPresetAs(newName);
            ClearDirty();
            RefreshFromPlugin();
            MaybePromptToSubmitEngineData(carId);
        }

        // Per-preset Delete and Clear-default live in the Manage Presets dialog
        // now; the inline buttons were removed in the unified-picker refactor.

        // Inline "Set as default" next to the game preset dropdown. Binds the
        // active preset as this game's auto-load default; the button then
        // self-hides (active == default) via UpdateSetDefaultButton.
        private void HeaderSetDefault_Click(object sender, RoutedEventArgs e)
        {
            string name = SelectedPresetName;
            if (_plugin == null || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(_plugin.ActiveGame)) return;
            _plugin.SetDefaultPresetForActiveGame(name);
            RefreshFromPlugin();
        }

        // ---------- Export / Import (Backup & sync) ----------
        //
        // Two top-level entry points wired to the Backup & sync section:
        //   Export_Click → open PackPickerWindow filtered to user presets
        //     (built-ins hidden, active car preset pre-checked), save the
        //     selection as a .tfpack.
        //   Import_Click → open a file dialog, auto-detect the file kind
        //     by extension + the JSON "Type" marker, route to the matching
        //     ImportPack / ImportPreset / ImportCarPreset / ImportSettings.
        //
        // The individual export/import handlers that used to be wired to
        // dedicated buttons (preset, car preset, pack, all-settings) were
        // collapsed away when Backup & sync shrank to two buttons. The
        // underlying plugin APIs are still used by the smart import router
        // here and by ManagePresetsDialog's per-row Export buttons.

        // Pop the Author/Description/Version dialog. Author pre-fills from
        // SharingAuthor; on OK, persists the (possibly-edited) author back so
        // the next export pre-fills with what the user just typed. Returns
        // false on Cancel; true with the (possibly-blank) values on OK.
        // Static so ManagePresetsDialog can drive the same export flow from
        // its own Window context.
        internal static bool PromptForExportMetadata(Window owner, TrueforcePlugin plugin,
            string title, string subjectKind,
            out string author, out string description, out string authorVersion)
        {
            author = description = authorVersion = null;
            if (plugin?.Settings == null) return false;

            var dlg = new PresetMetadataDialog(title, subjectKind,
                plugin.Settings.SharingAuthor, "", "")
            {
                Owner = owner,
            };
            if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (dlg.ShowDialog() != true) return false;

            author        = dlg.Author;
            description   = dlg.Description;
            authorVersion = dlg.AuthorVersion;

            string newAuthor = author?.Trim() ?? "";
            if (newAuthor != (plugin.Settings.SharingAuthor ?? ""))
            {
                plugin.Settings.SharingAuthor = newAuthor;
                try { plugin.PersistSettings(); } catch { }
            }
            return true;
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
            ApplyDarkTheme(win);

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

        private static string MakeFileSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "preset";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(invalid, arr[i]) >= 0) arr[i] = '_';
            return new string(arr);
        }

        // Export: opens the pack picker (built-ins hidden, active car
        // preset pre-checked) and saves the selection as a .tfpack.
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            RunExportFlow(Window.GetWindow(this), _plugin);
        }

        // Import: routes based on file extension + JSON "Type" marker.
        //   .tfpack / .zip → ImportPack
        //   JSON with Type=trueforce-preset       → ImportPreset
        //   JSON with Type=trueforce-car-preset   → ImportCarPreset
        //   JSON with no recognized Type          → ImportSettings (destructive,
        //                                            confirmed first)
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (RunImportFlow(Window.GetWindow(this), _plugin))
            {
                ClearDirty();
                RefreshFromPlugin();
            }
        }

        // Static body of Export_Click so ManagePresetsDialog can run the
        // same flow with itself as the owner Window (otherwise nested modals
        // appear behind the manage dialog).
        internal static void RunExportFlow(Window owner, TrueforcePlugin plugin)
        {
            if (plugin == null) return;

            var presets = plugin.GetExportablePresetNames()
                .Where(n => !plugin.IsBuiltinPreset(n))
                .ToList();
            var cars = plugin.GetExportableCarPresets()
                .Where(c => !c.IsBuiltin)
                .ToList();
            if (presets.Count == 0 && cars.Count == 0)
            {
                MessageBox.Show(owner, "Nothing to share yet. Save a preset (or a per-car tuning) first.",
                                "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string preferCarId = plugin.ActiveCarId;

            // preset name -> set of GameNames it's a default for. Combines
            // shipped GameDefaultBindings with the user's saved GameDefaults
            // so checking a preset on the left filters the car list down to
            // that preset's games.
            var presetGameMappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            void AddMapping(string p, string g)
            {
                if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(g)) return;
                if (!presetGameMappings.TryGetValue(p, out var list))
                {
                    list = new List<string>();
                    presetGameMappings[p] = list;
                }
                if (list is List<string> mut && !mut.Contains(g)) mut.Add(g);
            }
            foreach (var kv in BuiltinPresets.GameDefaultBindings) AddMapping(kv.Value, kv.Key);
            if (plugin.Settings?.GameDefaults != null)
                foreach (var kv in plugin.Settings.GameDefaults) AddMapping(kv.Value, kv.Key);

            var picker = new PackPickerWindow(presets, cars, exportMode: true, preferCarId, presetGameMappings)
            {
                Owner = owner,
            };
            if (picker.Owner == null) picker.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (picker.ShowDialog() != true) return;

            var pickedPresets = picker.SelectedPresetNames;
            var pickedCars    = picker.SelectedCarPresets;
            if (pickedPresets.Count == 0 && pickedCars.Count == 0) return;

            if (!PromptForExportMetadata(owner, plugin, "Export", "pack",
                out string author, out string desc, out string ver)) return;

            string defaultName = $"TF4ALL-pack-{DateTime.Now:yyyy-MM-dd}.tfpack";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "TF4ALL pack (*.tfpack)|*.tfpack|Zip (*.zip)|*.zip",
                FileName = defaultName,
                Title    = "Export",
            };
            if (dlg.ShowDialog(owner) != true) return;
            try
            {
                var (p, c) = plugin.ExportPack(
                    dlg.FileName,
                    pickedPresets,
                    pickedCars.ConvertAll(e2 => (e2.CarId, e2.PresetName)),
                    author, desc, ver);
                MessageBox.Show(owner, $"Exported {p} preset(s) and {c} car preset(s) to:\n{dlg.FileName}",
                                "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Export failed:\n{ex.Message}", "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Static body of Import_Click. Returns true if anything was imported
        // so the caller can refresh its own UI (the main panel reapplies the
        // imported settings; the manage dialog reloads its lists).
        internal static bool RunImportFlow(Window owner, TrueforcePlugin plugin)
        {
            if (plugin == null) return false;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "TF4ALL files (*.tfpack;*.tfpreset.json;*.tfcar.json;*.zip;*.json)"
                         + "|*.tfpack;*.tfpreset.json;*.tfcar.json;*.zip;*.json"
                         + "|All files (*.*)|*.*",
                Title  = "Import",
            };
            if (dlg.ShowDialog(owner) != true) return false;
            string path = dlg.FileName;
            string ext  = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();

            try
            {
                if (ext == ".tfpack" || ext == ".zip")
                {
                    ImportPackAndReport(owner, plugin, path);
                    return true;
                }

                string json = System.IO.File.ReadAllText(path);
                string type = PeekJsonType(json);
                if (type == PresetFile.FileType)    { ImportPresetAndReport(owner, plugin, path);    return true; }
                if (type == CarPresetFile.FileType) { ImportCarPresetAndReport(owner, plugin, path); return true; }

                if (MessageBox.Show(owner,
                        "This looks like a full TF4ALL settings backup. Importing replaces all current settings (master, audio, every effect, all per-car overrides). Continue?",
                        "Trueforce For All", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;
                plugin.ImportSettings(path);
                MessageBox.Show(owner, "Settings imported.", "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Import failed:\n{ex.Message}", "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // Parse just the top-level "Type" string from a JSON file. Returns
        // null on parse failure or when the field is missing.
        private static string PeekJsonType(string json)
        {
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                return jo["Type"]?.ToString();
            }
            catch { return null; }
        }

        // Caller refreshes its own UI after these return (main panel runs
        // ClearDirty + RefreshFromPlugin; manage dialog reloads its lists).
        private static void ImportPresetAndReport(Window owner, TrueforcePlugin plugin, string path)
        {
            var r = plugin.ImportPreset(path);
            string body = $"Imported preset '{r.PresetName}' into your library. Select it from the dropdown and click Apply, or set it as a game default.";
            string meta = FormatMetadataLines(r.Author, r.AuthorVersion, r.Description);
            if (!string.IsNullOrEmpty(meta)) body = meta + "\n\n" + body;
            MessageBox.Show(owner, body, "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ImportCarPresetAndReport(Window owner, TrueforcePlugin plugin, string path)
        {
            var r = plugin.ImportCarPreset(path);
            bool applied = r.CarId == plugin.ActiveCarId;
            string body = applied
                ? $"Imported car preset '{r.PresetName}' for '{r.CarId}'. Applied (this is the active car)."
                : $"Imported car preset '{r.PresetName}' for '{r.CarId}'. Stored (will apply when you drive that car).";
            string meta = FormatMetadataLines(r.Author, r.AuthorVersion, r.Description);
            if (!string.IsNullOrEmpty(meta)) body = meta + "\n\n" + body;
            MessageBox.Show(owner, body, "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ImportPackAndReport(Window owner, TrueforcePlugin plugin, string path)
        {
            var r = plugin.ImportPack(path);
            string body = $"Imported {r.PresetsImported} preset(s) and {r.CarsImported} car preset(s) from:\n{path}";
            string meta = FormatMetadataLines(r.Author, r.AuthorVersion, r.Description);
            if (!string.IsNullOrEmpty(meta)) body = meta + "\n\n" + body;
            MessageBox.Show(owner, body, "Trueforce For All", MessageBoxButton.OK, MessageBoxImage.Information);
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
            // {8,16,24,32,40,48,56,64}. Round to nearest pow2 to avoid the
            // 24/40/48/56 in-betweens (Apply() also sanitizes defensively).
            int v = NearestPow2((int)Math.Round(e.NewValue), 8, 64);
            if (PerfTfRingText != null) PerfTfRingText.Text = FormatRing(v);
            // Only push down to the device in Manual mode (in Auto, the
            // ratchet owns ring sizes and slider edits would conflict).
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
            // Advance buckets. Clear any seconds we skipped (idle UI).
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

        // Inline ratchet-notice banner state. Per-ring "original" caps are
        // pinned to whatever was active when the banner first became
        // visible; "latest" updates with each subsequent bump. Both rings
        // share one banner so the user gets at most one consolidated
        // notice no matter how many bumps land in the same session.
        // Cleared on Revert / Dismiss. Not persisted (the resized caps are
        // already saved by Apply*RingSize; the banner itself is ephemeral).
        private int? _ratchetTfOriginalCap;
        private int  _ratchetTfLatestCap;
        private int? _ratchetAudioOriginalCap;
        private int  _ratchetAudioLatestCap;

        private void OnAutoRatchetBumped(bool isTf, int oldCap, int newCap)
        {
            // Fired on the producer thread. Marshal to UI to update the
            // inline banner and refresh dependent values. Don't block the
            // producer; BeginInvoke is fire-and-forget.
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshFromPlugin();
                    UpdateRatchetNotice(isTf, oldCap, newCap);
                }));
            }
            catch { }
        }

        /// <summary>Update (or first-time show) the inline ratchet notice
        /// banner. Per-ring "original" cap is pinned on first event so
        /// successive bumps consolidate into one banner that always shows
        /// the net delta from session start (or first event of this run).
        /// Handles both UP and DOWN events; if every tracked ring's
        /// latest cap returns to its pinned original, the banner auto-
        /// dismisses because there's nothing notable left to show.
        /// Replaces the old centered Window modal that stole foreground
        /// and re-opened on every bump.</summary>
        private void UpdateRatchetNotice(bool isTf, int oldCap, int newCap)
        {
            if (isTf)
            {
                if (_ratchetTfOriginalCap == null) _ratchetTfOriginalCap = oldCap;
                _ratchetTfLatestCap = newCap;
            }
            else
            {
                if (_ratchetAudioOriginalCap == null) _ratchetAudioOriginalCap = oldCap;
                _ratchetAudioLatestCap = newCap;
            }

            // If every tracked ring is back to where it started, there's
            // nothing to report. Auto-dismiss so the user isn't left
            // staring at a stale "ring was bumped" notice after the
            // ratchet itself decided to walk it back down.
            bool tfBackToStart = _ratchetTfOriginalCap is int tfStart
                                 && _ratchetTfLatestCap == tfStart;
            bool auBackToStart = _ratchetAudioOriginalCap is int auStart
                                 && _ratchetAudioLatestCap == auStart;
            bool anythingToShow =
                (_ratchetTfOriginalCap    is int && !tfBackToStart) ||
                (_ratchetAudioOriginalCap is int && !auBackToStart);
            if (!anythingToShow)
            {
                ClearRatchetNotice();
                return;
            }

            // Build the consolidated detail line. Each ring contributes a
            // "label: oldCap → newCap samples (oldMs → newMs ms)" segment;
            // segments are joined with " · ". Ms = samples * 0.25 (4 kHz
            // packet rate, 1 sample = 0.25 ms). Rings that have returned
            // to their original cap are omitted from this run's notice.
            var segments = new System.Collections.Generic.List<string>();
            if (_ratchetTfOriginalCap is int tfOrig && !tfBackToStart)
            {
                segments.Add(
                    $"Trueforce ring: {tfOrig} → {_ratchetTfLatestCap} samples " +
                    $"({tfOrig * 0.25:0.#} → {_ratchetTfLatestCap * 0.25:0.#} ms)");
            }
            if (_ratchetAudioOriginalCap is int auOrig && !auBackToStart)
            {
                segments.Add(
                    $"Audio ring: {auOrig} → {_ratchetAudioLatestCap} samples " +
                    $"({auOrig * 0.25:0.#} → {_ratchetAudioLatestCap * 0.25:0.#} ms)");
            }

            if (RatchetNoticeDetail != null)
            {
                RatchetNoticeDetail.Text = string.Join("  ·  ", segments)
                    + ". Persisted across sessions. Revert to restore the size(s) at the start of this run, or dismiss to keep the current size(s).";
            }
            if (RatchetNoticeBanner != null)
                RatchetNoticeBanner.Visibility = Visibility.Visible;
        }

        private void RatchetNoticeRevert_Click(object sender, RoutedEventArgs e)
        {
            // Revert every ring that bumped this session back to its
            // original pre-bump cap. Apply* persists and resizes live.
            if (_ratchetTfOriginalCap is int tfOrig && _plugin != null)
                _plugin.ApplyTfRingSize(tfOrig);
            if (_ratchetAudioOriginalCap is int auOrig && _plugin != null)
                _plugin.ApplyAudioRingSize(auOrig);
            ClearRatchetNotice();
            RefreshFromPlugin();
        }

        private void RatchetNoticeDismiss_Click(object sender, RoutedEventArgs e)
        {
            ClearRatchetNotice();
        }

        private void ClearRatchetNotice()
        {
            _ratchetTfOriginalCap = null;
            _ratchetTfLatestCap = 0;
            _ratchetAudioOriginalCap = null;
            _ratchetAudioLatestCap = 0;
            if (RatchetNoticeBanner != null)
                RatchetNoticeBanner.Visibility = Visibility.Collapsed;
        }

        // One-shot dispatcher-thread timer. Used by the RATCHET test code
        // to stage synthetic ratchet events with visible delays between
        // them so the consolidation behavior is observable.
        private void ScheduleDispatcherDelay(int delayMs, Action action)
        {
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs),
            };
            t.Tick += (_, __) =>
            {
                t.Stop();
                try { action(); } catch { }
            };
            t.Start();
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

        // ---------- Update CTA / modal ----------

        // Paint a fresh Window to match the host SimHub BaseDark theme. Code-
        // behind modals don't inherit the panel's theme styles automatically;
        // without this, every Window we open lands on the system default
        // (white background, black text) which is unreadable inside SimHub.
        // Internal because ManagePresetsDialog's nested modals call it too.
        internal static void ApplyDarkTheme(Window win)
        {
            win.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            // TextElement.Foreground is the inherited property that TextBlock,
            // Button content, etc all pick up by default. Setting it on the
            // Window propagates to every descendant that doesn't override.
            TextElement.SetForeground(win, new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)));
        }

        private void UpdateAvailableButton_Click(object sender, RoutedEventArgs e)
        {
            ShowUpdateModal();
        }

        // ---------- Advanced settings modal ----------

        // Performance, Sidechain ducking, and Diagnostics live invisibly in
        // AdvancedSettingsHost at the tail of SettingsControl.xaml. The host
        // is hidden by default. On click we move (re-parent) the host into
        // a Window's content for display; on close we put it back where it
        // came from. This keeps every existing per-control event handler in
        // this code-behind reachable as-is (no field-routing changes), and
        // means RefreshFromPlugin's per-tick text updates still hit the
        // diagnostic / performance / ducking widgets whether the modal is
        // open or closed.
        private void OpenAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            if (AdvancedSettingsHost == null) return;

            // Remember where the host lives in the main panel so we can
            // restore it exactly when the modal closes.
            var originalParent = AdvancedSettingsHost.Parent as Panel;
            int originalIndex = originalParent?.Children.IndexOf(AdvancedSettingsHost) ?? -1;

            var win = new Window
            {
                Title = "Trueforce For All: advanced settings",
                Width = 720,
                Height = 640,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            // Pull the host out of its current parent, wrap it in a scroller,
            // and hand the scroller to the Window.
            if (originalParent != null) originalParent.Children.Remove(AdvancedSettingsHost);
            AdvancedSettingsHost.Visibility = Visibility.Visible;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(16),
                Content = AdvancedSettingsHost,
            };
            win.Content = scroll;

            win.Closed += (_, __) =>
            {
                // Detach from the dying window and slot the host back into
                // the main panel at its original index. Re-collapsing keeps
                // it invisible in the panel between modal openings.
                scroll.Content = null;
                AdvancedSettingsHost.Visibility = Visibility.Collapsed;
                if (originalParent != null && !originalParent.Children.Contains(AdvancedSettingsHost))
                {
                    int idx = originalIndex >= 0 && originalIndex <= originalParent.Children.Count
                        ? originalIndex
                        : originalParent.Children.Count;
                    originalParent.Children.Insert(idx, AdvancedSettingsHost);
                }
            };

            win.ShowDialog();
        }

        // Render a GitHub-flavored Markdown release body as a stack of styled
        // TextBlocks. Supports headings (#..######) and bullets (- / *); other
        // syntax falls through as plain text. We don't pull in a real markdown
        // parser because the release notes only ever use these two constructs
        // and we want zero added dependencies in net48 plugin land.
        private static StackPanel RenderReleaseNotes(string body)
        {
            var panel = new StackPanel();
            if (string.IsNullOrWhiteSpace(body))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "(No release notes published.)",
                    FontSize = 12,
                    Opacity = 0.7,
                });
                return panel;
            }

            // Normalize line endings: GitHub bodies usually arrive with \r\n.
            string[] lines = body.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            bool prevWasBlank = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? "";
                string trimmed = raw.TrimStart();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // Collapse runs of blank lines into a single small gap.
                    if (!prevWasBlank && panel.Children.Count > 0)
                    {
                        panel.Children.Add(new TextBlock { Height = 6 });
                        prevWasBlank = true;
                    }
                    continue;
                }
                prevWasBlank = false;

                // Heading levels 1..3 (deeper levels fall through to plain).
                int hashCount = 0;
                while (hashCount < trimmed.Length && trimmed[hashCount] == '#') hashCount++;
                if (hashCount >= 1 && hashCount <= 3
                    && hashCount < trimmed.Length && trimmed[hashCount] == ' ')
                {
                    string text = trimmed.Substring(hashCount + 1).Trim();
                    double size = hashCount == 1 ? 16 : hashCount == 2 ? 14 : 13;
                    panel.Children.Add(new TextBlock
                    {
                        Text = text,
                        FontSize = size,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 8, 0, 4),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    continue;
                }

                // Bullet rows ("- foo" / "* foo"). Use a real bullet glyph
                // indented one step. Inline **bold** spans get rendered as
                // bold runs so release notes like "- **Headline.** desc"
                // don't show literal asterisks.
                if (trimmed.Length >= 2
                    && (trimmed[0] == '-' || trimmed[0] == '*')
                    && trimmed[1] == ' ')
                {
                    var tb = new TextBlock
                    {
                        FontSize = 12,
                        Margin = new Thickness(8, 2, 0, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    tb.Inlines.Add(new Run("• "));
                    AppendInlineMarkdown(tb, trimmed.Substring(2));
                    panel.Children.Add(tb);
                    continue;
                }

                // Plain paragraph line. Same **bold** treatment as bullets.
                var para = new TextBlock
                {
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                };
                AppendInlineMarkdown(para, trimmed);
                panel.Children.Add(para);
            }
            return panel;
        }

        // Append `text` to a TextBlock's Inlines, rendering **bold** runs in
        // bold. Anything outside ** ** pairs is plain. An unclosed `**` at
        // the end is treated as literal text rather than dropped, so a body
        // that opens bold without closing degrades gracefully.
        private static void AppendInlineMarkdown(TextBlock tb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int i = 0;
            while (i < text.Length)
            {
                int boldStart = text.IndexOf("**", i, StringComparison.Ordinal);
                if (boldStart < 0)
                {
                    tb.Inlines.Add(new Run(text.Substring(i)));
                    return;
                }
                if (boldStart > i)
                    tb.Inlines.Add(new Run(text.Substring(i, boldStart - i)));
                int boldEnd = text.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
                if (boldEnd < 0)
                {
                    tb.Inlines.Add(new Run(text.Substring(boldStart)));
                    return;
                }
                string boldText = text.Substring(boldStart + 2, boldEnd - boldStart - 2);
                tb.Inlines.Add(new Run(boldText) { FontWeight = FontWeights.Bold });
                i = boldEnd + 2;
            }
        }

        // Manual "Check for updates" link in the header. The plugin already
        // fires a one-shot check on Init, but users opening the panel hours
        // later need a way to re-poll without restarting SimHub. The
        // settings-panel timer tick (RefreshFromPlugin) picks up the new
        // result automatically, so this handler only has to drive the
        // transient status label next to the button.
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            var upd = _plugin?.UpdateChecker;
            if (upd == null) return;
            if (CheckForUpdatesButton == null || CheckForUpdatesStatus == null) return;

            CheckForUpdatesButton.IsEnabled = false;
            CheckForUpdatesStatus.Text = "Checking…";
            try
            {
                await upd.CheckAsync(_plugin.UpdateCheckerToken);
                if (upd.IsUpdateAvailable)
                    CheckForUpdatesStatus.Text = $"v{upd.LatestVersionDisplay} available";
                else if (!string.IsNullOrEmpty(upd.LastError))
                    CheckForUpdatesStatus.Text = "Couldn't reach GitHub";
                else
                    CheckForUpdatesStatus.Text = "You're up to date";
            }
            catch
            {
                CheckForUpdatesStatus.Text = "Check failed";
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }

            // Fade the status after a few seconds. Captured snapshot avoids
            // clobbering a newer status if the user clicks again quickly.
            string captured = CheckForUpdatesStatus.Text;
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (CheckForUpdatesStatus != null && CheckForUpdatesStatus.Text == captured)
                CheckForUpdatesStatus.Text = "";
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
                Title = "Trueforce For All: update available",
                Width = 600,
                Height = 520,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

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
            // Green "confirm" styling so the primary action stands out
            // against the dark modal chrome and the muted Dismiss button.
            var updateBtn = new System.Windows.Controls.Button
            {
                Content = "Update now",
                Width = 120,
                Height = 28,
                IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0x40)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x6E, 0x32)),
                FontWeight = FontWeights.SemiBold,
            };
            btnRow.Children.Add(dismissBtn);
            btnRow.Children.Add(updateBtn);
            footer.Children.Add(btnRow);

            root.Children.Add(footer);

            // Center: scrollable release notes. GitHub release bodies are
            // Markdown; we render a minimal subset (headers, bullets) so the
            // literal "## Heading" and "- item" prefixes don't leak through.
            var notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Padding = new Thickness(10),
            };
            notesScroll.Content = RenderReleaseNotes(upd.ReleaseNotes);
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

        /// <summary>Modal listing every release between the user's stamped
        /// LastSeenVersion and the running build. Prefers GitHub release
        /// notes (canonical source: GitHub release body for each version);
        /// falls back to the in-source EffectChangelog when the GH fetch
        /// hasn't completed or failed, so offline / first-launch flows
        /// still show something. Closing the modal stamps LastSeenVersion
        /// to the running build and hides the banner for this version.</summary>
        private void ShowWhatsNewModal()
        {
            if (_plugin == null) return;

            var ghReleases = _plugin.GetGitHubReleasesForBanner();
            var pending    = _plugin.GetPendingChangelog();
            bool useGitHub = ghReleases != null && ghReleases.Count > 0;
            bool useLocal  = !useGitHub && pending != null && pending.Count > 0;
            if (!useGitHub && !useLocal) return;

            var win = new Window
            {
                Title = "Trueforce For All: what's new",
                Width = 600,
                Height = 480,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

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

            var bodyStack = new StackPanel();
            if (useGitHub)
            {
                // Render each release's GitHub body as Markdown via the same
                // helper the update modal uses. Body is canonical; the version
                // header is added by us so users still see a clear divider
                // between releases even if a body forgets its own heading.
                for (int i = 0; i < ghReleases.Count; i++)
                {
                    var r = ghReleases[i];
                    string title = string.IsNullOrEmpty(r.Title) ? ("v" + r.Version.ToString(3)) : r.Title;
                    bodyStack.Children.Add(new TextBlock
                    {
                        Text = title,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 14,
                        Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 6),
                    });
                    bodyStack.Children.Add(RenderReleaseNotes(r.Body));
                }
            }
            else
            {
                // Offline fallback: EffectChangelog. Same rendering shape as
                // before this refactor so the in-source structured form still
                // looks right when network is unavailable.
                var ordered = new List<ChangelogVersion>(pending);
                ordered.Sort((a, b) => b.Version.CompareTo(a.Version));
                for (int i = 0; i < ordered.Count; i++)
                {
                    var ver = ordered[i];
                    bodyStack.Children.Add(new TextBlock
                    {
                        Text = "v" + ver.Version.ToString(3) + (string.IsNullOrEmpty(ver.Title) ? "" : "  ·  " + ver.Title),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 14,
                        Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 6),
                    });
                    if (ver.Entries != null)
                    {
                        string lastGroup = null;
                        foreach (var entry in ver.Entries)
                        {
                            if (entry == null) continue;
                            // Group subheader (e.g. "New effects", "Bug fixes")
                            // whenever the group changes.
                            if (!string.IsNullOrEmpty(entry.Group) && entry.Group != lastGroup)
                            {
                                bodyStack.Children.Add(new TextBlock
                                {
                                    Text = entry.Group,
                                    FontWeight = FontWeights.SemiBold,
                                    FontSize = 13,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x86, 0x0B)),
                                    Margin = new Thickness(0, 10, 0, 2),
                                });
                                lastGroup = entry.Group;
                            }
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
            }

            var notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
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

        // Open a file picker for USBPcapCMD.exe and tell the plugin to persist
        // it as the override path. Filtered to USBPcapCMD.exe specifically: the
        // file the FFB tap actually invokes. After Apply, the plugin restarts
        // the tap so the new path takes effect immediately.
        private void UsbPcapBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title       = "Locate USBPcapCMD.exe",
                Filter      = "USBPcapCMD.exe|USBPcapCMD.exe|All executables (*.exe)|*.exe",
                FileName    = "USBPcapCMD.exe",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;
            string leaf = System.IO.Path.GetFileName(path);
            if (!string.Equals(leaf, "USBPcapCMD.exe", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "That file isn't USBPcapCMD.exe. Pick USBPcapCMD.exe from your USBPcap install folder.",
                    "Trueforce", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _plugin.ApplyUsbPcapPathOverride(path);
        }

        // Launch the bundled USBPcap installer. Confirms first because it
        // triggers a UAC prompt and modifies a kernel driver. The plugin
        // runs the install + tap restart on a background thread; the
        // FFB pass-through status will update through the normal tick.
        private void UsbPcapReinstall_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (MessageBox.Show(
                    "Run the bundled USBPcap installer? This needs admin (UAC prompt) and reinstalls the USB capture driver. SimHub doesn't need to restart afterwards.",
                    "Trueforce", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            _plugin.ReinstallUsbPcapAsync();
        }
    }
}
