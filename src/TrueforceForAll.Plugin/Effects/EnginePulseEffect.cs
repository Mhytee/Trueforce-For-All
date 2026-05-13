// The signature Trueforce sensation: a periodic pulse at the engine's firing
// frequency. Scales amplitude with RPM relative to the redline so idle is a
// gentle hum and pulling toward redline gives meaningful kick.
//
// Synthesis runs at the engine cycle rate (RPM/120 × pitch). One synth cycle
// = one full 720° crank rotation, and within that cycle we lay down N pulses
// at the configured pattern's positions, weighted by per-pulse amplitudes.
// The cycle waveform is precomputed into a 2048-sample buffer at pattern-
// change time (a "wavetable" purely as an implementation detail) and walked
// at the cycle frequency. For even-fire patterns the output is a smooth
// periodic waveform at firing freq; for non-uniform patterns it produces the
// characterful timbre (cross-plane V8 lope, Ducati L-twin gap, etc.).

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class EnginePulseEffect : TelemetryEffect
    {
        public override string Name => "Engine pulse";

        /// <summary>User-chosen engine layout. <see cref="EngineLayout.Auto"/>
        /// defers to <see cref="AutoLayout"/> (set by the resolver / telemetry);
        /// any explicit value wins. Default Auto so a fresh per-car preset
        /// uses the resolver's answer for the active car.</summary>
        public EngineLayout Layout
        {
            get => _layout;
            set
            {
                if (_layout == value) return;
                _layout = value;
                _patternDirty = true;
            }
        }
        private EngineLayout _layout = EngineLayout.Auto;

        /// <summary>Layout auto-detected for the active car (from the
        /// CarCylinderResolver bake/heuristic or from telemetry-supplied
        /// NumCylinders). Plugin writes this on car change; cleared when no
        /// resolution is available. Consulted only when <see cref="Layout"/>
        /// is Auto.</summary>
        public EngineLayout? AutoLayout { get; set; }

        /// <summary>How <see cref="AutoLayout"/> was determined for the active
        /// car. Format: source token from CarCylinderResolver.Result.Source
        /// ("baked" / "codename" / "tag" / "chassis" / "cache" / "telemetry"
        /// etc.). Null when the car wasn't resolved at all. Cleared on car
        /// change; surfaced in the settings UI for transparency.</summary>
        public string AutoLayoutSource { get; set; }

        /// <summary>Effective layout actually being rendered. Cascade: explicit
        /// Layout (when not Auto) → AutoLayout (when set) → Auto (which
        /// ResolveLayout maps to a generic 6-cyl even-fire). Read-only helper
        /// for both the synthesis math and the UI readout.</summary>
        public EngineLayout EffectiveLayout
        {
            get
            {
                if (_layout != EngineLayout.Auto) return _layout;
                if (AutoLayout is EngineLayout a) return a;
                return EngineLayout.Auto;
            }
        }

        /// <summary>True when the rendered car is effectively electric: the
        /// user picked <see cref="EngineLayout.Electric"/>, the active custom
        /// engine is electric, or Layout=Auto and the resolver flagged the
        /// car as an EV. Drives <see cref="AutoGainScale"/>.</summary>
        public bool IsElectricEffective =>
            EffectiveLayout == EngineLayout.Electric
            || (EffectiveLayout == EngineLayout.Custom && ActiveCustomIsElectric);

        /// <summary>True when the active <see cref="EngineLayout.Custom"/>
        /// entry is an electric engine (set by ApplyEngineSettings from the
        /// looked-up CustomEngineDef). Lets a saved electric custom behave
        /// like the built-in Electric layout — synthesis silenced and
        /// AutoGainScale applied per <see cref="ElectricMode"/>.</summary>
        public bool ActiveCustomIsElectric { get; set; }

        /// <summary>What to do for EVs: attenuate to 50% (default,
        /// <see cref="ElectricCarMode.MutedHum"/>) or fully silence. Set by
        /// ApplyEngineSettings from EnginePulseSettings.ElectricMode so global
        /// default + per-car preset both work. Combustion layouts ignore
        /// this field.</summary>
        public ElectricCarMode ElectricMode { get; set; } = ElectricCarMode.MutedHum;

        /// <summary>Computed amplitude scale applied alongside the user's
        /// Gain. Combustion: 1.0. EVs: 0.5 in MutedHum, 0.0 in Silent.
        /// Computed (not stored) so changing ElectricMode in the UI takes
        /// effect on the next render without re-resolving the car.</summary>
        public float AutoGainScale =>
            !IsElectricEffective ? 1.0f
            : (ElectricMode == ElectricCarMode.Silent ? 0.0f : 0.5f);

        /// <summary>Multiplier on the firing-frequency calc; let per-car overrides shift the pitch.</summary>
        public float PitchMultiplier { get; set; } = 1.0f;

        /// <summary>Idle hum amplitude when the engine is on but throttle is closed.
        /// We can afford larger amplitudes now that the FFB pass-through tap
        /// writes AC's torque target into ep3 cur — the audio in the rolling
        /// window is purely additive on top, no longer competing with the
        /// wheel firmware's "Trueforce vs FFB priority" decision.</summary>
        public float IdleAmp { get; set; } = 0.05f;

        /// <summary>Peak amplitude near redline.</summary>
        public float PeakAmp { get; set; } = 0.25f;

        /// <summary>Extra amplitude scaled by throttle position (0..1). Real engines
        /// sound louder the moment the throttle opens, even before RPM has caught
        /// up — without this, throttle-stabs feel "delayed" because the user is
        /// only feeling the (slow) engine RPM ramp curve. 0.4 = up to 40% peak
        /// of immediate kick from throttle alone.</summary>
        public float ThrottleBoost { get; set; } = 0.4f;

        public Waveform Waveform
        {
            get => _osc.Waveform;
            set
            {
                if (_osc.Waveform == value) return;
                _osc.Waveform = value;
                _patternDirty = true;   // wavetable needs to be regenerated against the new shape
            }
        }

        /// <summary>1-pole low-pass cutoff (Hz) applied to the engine pulse
        /// output. Tonal waveforms like Square or Saw have lots of harmonics
        /// that feel buzzy through the wheel; a low-pass smooths that without
        /// changing the firing-frequency pitch. 0 = disabled (pure waveform).</summary>
        public double LowpassHz { get; set; } = 0.0;
        private float _lpY;

        // ---- High-RPM perceptibility helpers ----
        //
        // Wheel motors lowpass mechanically (rotor inertia + coil inductance),
        // so as firing frequency climbs past ~100 Hz the user feels less per
        // pulse even though the electrical signal is at full amplitude. Two
        // compensations applied here, both on by default:
        //
        //   LoadLayer: a sine at the engine cycle frequency (RPM/120 Hz)
        //     summed alongside the firing-rate wavetable. The cycle freq is
        //     by construction a subharmonic of the firing rate (firing rate =
        //     N x cycle freq for N-pulse patterns), so the layer is phase-
        //     compatible with the pulse and sweeps 7-58 Hz across idle-to-
        //     redline, squarely in the band the wheel can render.
        //
        //   HighRpmBoost: pre-emphasis gain on the firing-rate pulse, ramped
        //     from 0 boost at 50% RPM to (Amount) extra gain at redline.
        //     Partially compensates for the wheel's natural rolloff.
        public bool   LoadLayerEnabled    { get; set; } = true;
        public float  LoadLayerGain       { get; set; } = 0.80f;
        public bool   HighRpmBoostEnabled { get; set; } = true;
        public float  HighRpmBoostAmount  { get; set; } = 0.70f;
        private double _loadLayerAmp;
        private double _loadPhase;   // radians, [0, 2π)

        // _osc is kept purely as a Waveform + SampleRate carrier — the firing-
        // order wavetable path doesn't render through it directly. Its
        // SampleRate (4 kHz) is what the wavetable phase math reads.
        private readonly OscillatorSource _osc = new OscillatorSource
        {
            Waveform   = Waveform.Sine,
            Amp        = 0,
            Enabled    = true,
            SampleRate = 4000.0,
        };

        /// <summary>User-supplied custom pattern. Only consulted when
        /// <see cref="Layout"/> == Custom. Set via FiringPatternDb.ParseCustom
        /// from the advanced UI textbox; null falls back to even-fire.</summary>
        public FiringPattern CustomPattern
        {
            get => _customPattern;
            set
            {
                _customPattern = value;
                if (_layout == EngineLayout.Custom) _patternDirty = true;
            }
        }
        private FiringPattern _customPattern;

        /// <summary>The pattern actually being rendered right now. Read-only
        /// for the UI's "submit this pattern" diagnostic — shows what the
        /// resolver picked (or the user's custom). Null until the first
        /// render.</summary>
        public FiringPattern ActiveFiringPattern { get; private set; }

        // Wavetable + state. The wavetable is regenerated lazily inside
        // RenderAdd when _patternDirty is true (set by layout / waveform /
        // custom-pattern changes). Lazy regen avoids recomputing on every UI
        // tick when the user is dragging a slider.
        private const int WavetableSize = 2048;
        private float[]      _wavetable;
        private double       _wtPhase;        // [0, WavetableSize)
        private bool         _patternDirty = true;
        private EngineLayout _wtLayout;       // layout baked into the current wavetable
        private double       _cyclesPerSec;   // engine cycle freq, set from OnTelemetry / TestUpdate
        private double       _wavetableAmp;   // amp on the synth path

        public override bool IsActive => IsTesting || (Enabled && (_wavetableAmp > 0 || _loadLayerAmp > 0));

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            float dm = DuckMultiplier;
            if (dm <= 0f) return;

            // Electric: synth is silent, nothing to add. AutoGainScale has
            // already zeroed _wavetableAmp in OnTelemetry / TestUpdate when
            // ElectricMode == Silent, but we short-circuit here to skip the
            // wavetable walk in MutedHum too if amp ended up at zero.
            if (IsElectricEffective && ElectricMode == ElectricCarMode.Silent) return;

            // Lazy-regen the wavetable when the pattern fingerprint changes
            // (layout / waveform / custom-pattern). Cheap (one multiply-add
            // per wavetable cell, ~2048 ops, <10µs) and rare — only fires on
            // pattern change, not per render call.
            var layout = EffectiveLayout;
            if (_patternDirty || _wtLayout != layout || _wavetable == null)
            {
                RegenerateWavetable(layout);
                _patternDirty = false;
                _wtLayout = layout;
            }

            float amp     = (float)_wavetableAmp;
            float loadAmp = (float)_loadLayerAmp;
            bool playPulse = amp > 0f;
            bool playLoad  = LoadLayerEnabled && loadAmp > 0f;
            if (!playPulse && !playLoad) return;

            double phaseStep = _cyclesPerSec * WavetableSize / _osc.SampleRate;
            if (phaseStep <= 0) return;

            bool needLp = LowpassHz > 0 && LowpassHz < 2000;
            float alpha = needLp
                ? (float)(1.0 - Math.Exp(-2.0 * Math.PI * LowpassHz / _osc.SampleRate))
                : 0f;
            float y = _lpY;

            // Load layer steps at the engine cycle frequency (RPM/120 Hz). The
            // firing-rate wavetable's full cycle also takes WavetableSize steps
            // per cycle freq, so the two are inherently phase-locked.
            double loadPhaseStep = _cyclesPerSec * 2.0 * Math.PI / _osc.SampleRate;
            double loadPhase = _loadPhase;

            float[] wt = _wavetable;
            int wtLen = wt.Length;
            double phase = _wtPhase;
            const double TwoPi = 2.0 * Math.PI;
            for (int i = 0; i < count; i++)
            {
                float v = 0f;
                if (playPulse)
                {
                    int i0 = (int)phase;
                    if (i0 >= wtLen) i0 = wtLen - 1;
                    int i1 = i0 + 1; if (i1 >= wtLen) i1 = 0;
                    double frac = phase - i0;
                    v = (float)((1.0 - frac) * wt[i0] + frac * wt[i1]) * amp;
                    if (needLp)
                    {
                        y += alpha * (v - y);
                        v = y;
                    }
                }
                if (playLoad)
                {
                    v += (float)Math.Sin(loadPhase) * loadAmp;
                    loadPhase += loadPhaseStep;
                    if (loadPhase >= TwoPi) loadPhase -= TwoPi;
                }
                buffer[i] += v * dm;

                phase += phaseStep;
                if (phase >= wtLen) phase -= wtLen;
            }
            _wtPhase   = phase;
            _loadPhase = loadPhase;
            if (needLp) _lpY = y;
        }

        private void RegenerateWavetable(EngineLayout layout)
        {
            // Resolve the active pattern. Custom uses the user-supplied
            // pattern when available, else falls back to even-fire.
            FiringPattern pattern;
            if (layout == EngineLayout.Custom && _customPattern != null && _customPattern.Pulses > 0)
                pattern = _customPattern;
            else
                pattern = FiringPatternDb.ResolveLayout(layout);
            ActiveFiringPattern = pattern;

            int n = pattern.Pulses;
            if (n < 1) n = 1;

            if (_wavetable == null || _wavetable.Length != WavetableSize)
                _wavetable = new float[WavetableSize];
            else
                Array.Clear(_wavetable, 0, _wavetable.Length);

            // Each firing event renders one cycle of the user's chosen
            // Waveform with width 1/n of the wavetable. For even-fire
            // patterns this tiles continuously and the result is a smooth
            // periodic waveform at firing freq. For uneven patterns the
            // pulses overlap or gap as designed, producing distinctive timbre.
            int pulseWidth = WavetableSize / n;
            if (pulseWidth < 1) pulseWidth = 1;
            var pos  = pattern.Positions;
            var amps = pattern.Amplitudes;   // null => uniform 1.0
            Waveform w = _osc.Waveform;
            for (int p = 0; p < n; p++)
            {
                int start = (int)(pos[p] * WavetableSize);
                if (start < 0) start = 0;
                if (start >= WavetableSize) start = WavetableSize - 1;
                double a = amps != null ? amps[p] : 1.0;
                for (int s = 0; s < pulseWidth; s++)
                {
                    double phase01 = (double)s / pulseWidth;
                    float v = (float)(SampleWaveform(w, phase01) * a);
                    int idx = start + s;
                    if (idx >= WavetableSize) idx -= WavetableSize;
                    _wavetable[idx] += v;
                }
            }

            // Normalize to peak 1 so the configured amp covers [-1, 1] and
            // swapping patterns with overlapping pulses doesn't blow up the
            // output. Peak normalization preserves the relative pulse
            // weighting (cross-plane V8's lope envelope is intact, just
            // scaled to fit the available headroom).
            float peak = 0f;
            for (int i = 0; i < _wavetable.Length; i++)
            {
                float a = _wavetable[i];
                if (a < 0) a = -a;
                if (a > peak) peak = a;
            }
            if (peak > 1e-6f)
            {
                float scale = 1f / peak;
                for (int i = 0; i < _wavetable.Length; i++) _wavetable[i] *= scale;
            }
        }

        private static double SampleWaveform(Waveform w, double phase01)
        {
            switch (w)
            {
                case Waveform.Sine:     return Math.Sin(2.0 * Math.PI * phase01);
                case Waveform.Square:   return phase01 < 0.5 ? 1.0 : -1.0;
                case Waveform.Saw:      return 2.0 * phase01 - 1.0;
                case Waveform.Triangle: return phase01 < 0.5
                                            ? 4.0 * phase01 - 1.0
                                            : 3.0 - 4.0 * phase01;
                case Waveform.Noise:    // unstable as a wavetable seed; fall back to sine
                default:                return Math.Sin(2.0 * Math.PI * phase01);
            }
        }

        public override int TestPlay()
        {
            // TestUpdate drives the dynamic ramp; just open the test window.
            StartTest(3000);
            return 3000;
        }

        /// <summary>Test simulation: throttle stab from idle to redline,
        /// then short hold, then back-off. Uses the user's Layout + Pitch +
        /// Gain + ThrottleBoost so the test sounds like the configured car.</summary>
        public override void TestUpdate(double phase01)
        {
            double throttle, rpmNorm;
            if (phase01 < 0.7)
            {
                double t = phase01 / 0.7;
                throttle = Math.Min(1.0, t * 1.4);
                rpmNorm  = t;
            }
            else if (phase01 < 0.9)
            {
                throttle = 1.0;
                rpmNorm  = 1.0;
            }
            else
            {
                double t = (phase01 - 0.9) / 0.1;
                throttle = Math.Max(0.0, 1.0 - t);
                rpmNorm  = Math.Max(0.1, 1.0 - t);
            }

            const double idleRpm = 800.0, redlineRpm = 7500.0;
            double rpm = idleRpm + (redlineRpm - idleRpm) * rpmNorm;

            // Engine cycle freq = rpm/120 (one 720° cycle per 2 revolutions),
            // pitch shifts the whole pattern proportionally to firing rate.
            // The pattern's pulse count determines firings per cycle, so cyl
            // doesn't enter here — it's already baked into the wavetable.
            _cyclesPerSec = rpm / 120.0 * PitchMultiplier;

            double rpmAmp      = IdleAmp + (PeakAmp - IdleAmp) * rpmNorm;
            double throttleAmp = throttle * ThrottleBoost * PeakAmp;
            double baseAmp     = Math.Min(rpmAmp + throttleAmp, PeakAmp * 1.5);
            double boost       = HighRpmBoostEnabled
                                    ? 1.0 + HighRpmBoostAmount * Math.Max(0.0, rpmNorm - 0.5) * 2.0
                                    : 1.0;
            _wavetableAmp = baseAmp * boost * Gain * AutoGainScale;
            _loadLayerAmp = LoadLayerEnabled
                ? baseAmp * LoadLayerGain * Gain * AutoGainScale
                : 0.0;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;

            // Absorb auto-detected cylinder count from sources that supply it
            // (Forza UDP) into AutoLayout. Sticky — keep the last seen value
            // during pause / between frames so EffectiveLayout doesn't
            // flip-flop while the user is in a menu. Plugin clears AutoLayout
            // on car change so the next car re-populates fresh.
            //
            // AutoLayoutSource is intentionally NOT written here — the
            // plugin's car-change handler owns it. Single writer avoids the
            // cross-thread race that existed when both this method and the
            // plugin could write the field.
            if (f.NumCylinders is int n && n >= 1 && n <= 16)
            {
                AutoLayout = FiringPatternDb.LayoutFromLegacy(n, EngineConfig.Auto, false);
            }

            double rpm = f.Rpms;
            if (rpm < 100) { _wavetableAmp = 0; _loadLayerAmp = 0; return; }   // engine off

            _cyclesPerSec = rpm / 120.0 * PitchMultiplier;

            double maxRpm = f.MaxRpm > 0 ? f.MaxRpm : 8000.0;
            double rpmNorm = Math.Min(1.0, rpm / maxRpm);
            double rpmAmp  = IdleAmp + (PeakAmp - IdleAmp) * rpmNorm;

            // Throttle-driven boost responds instantly to user input, masking
            // the engine-RPM ramp curve. Source has already normalized 0..1.
            double throttleAmp = f.Throttle01 * ThrottleBoost * PeakAmp;

            double baseAmp = Math.Min(rpmAmp + throttleAmp, PeakAmp * 1.5);

            // Pre-emphasis: zero boost at <=50% RPM, ramping to (Amount) extra
            // gain at redline. Partially compensates for the wheel's mechanical
            // rolloff at high firing frequencies.
            double boost = HighRpmBoostEnabled
                            ? 1.0 + HighRpmBoostAmount * Math.Max(0.0, rpmNorm - 0.5) * 2.0
                            : 1.0;
            _wavetableAmp = baseAmp * boost * Gain * AutoGainScale;

            // Load layer mirrors the pulse envelope, scaled by LoadLayerGain.
            // Pre-emphasis is intentionally NOT applied — the load layer sits
            // in the responsive band, so it doesn't need help.
            _loadLayerAmp = LoadLayerEnabled
                ? baseAmp * LoadLayerGain * Gain * AutoGainScale
                : 0.0;
        }
    }
}
