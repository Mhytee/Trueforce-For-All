// The signature Trueforce sensation: a periodic pulse at the engine's firing
// frequency. Scales amplitude with RPM relative to the redline so idle is a
// gentle hum and pulling toward redline gives meaningful kick.
//
// Firing frequency for a 4-stroke: RPM/60 × cyl/2 — each cylinder fires once
// every two crankshaft revolutions. At 6000 RPM 4-cyl that's 200 Hz; at 1000
// RPM 4-cyl, 33 Hz. Right in the haptic sweet spot.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class EnginePulseEffect : TelemetryEffect
    {
        public override string Name => "Engine pulse";

        /// <summary>Configured cylinder count. 0 is the "use auto" sentinel —
        /// when 0, <see cref="EffectiveCylinders"/> uses <see cref="AutoCylinders"/>
        /// (or a sane fallback if no auto value is available). 1-16 is a
        /// manual override that wins over auto regardless of resolver state.
        /// The UI's "Auto-detect" checkbox toggles between 0 and a real value.</summary>
        public int Cylinders { get; set; } = 0;

        // Sane fallback when Cylinders=0 (auto requested) but the resolver
        // produced no value for this car — a generic 6 covers most modern
        // engines and at least gives a plausible firing frequency.
        private const int AutoFallbackCylinders = 6;

        /// <summary>Cylinder count auto-detected from telemetry (Forza
        /// NumCylinders) or from CarCylinderResolver (AC bake / heuristic).
        /// Set by the plugin on car change, cleared when no resolution is
        /// available. UI shows it alongside the slider for transparency.</summary>
        public int? AutoCylinders { get; set; }

        /// <summary>True when the user has chosen auto-detection for this
        /// car (Cylinders == 0). Derived rather than separately stored so
        /// changing the saved value via the UI immediately flips the state
        /// without a separate flag write. The UI's "Auto-detect" checkbox
        /// is bound to this — toggling it sets Cylinders to 0 (on) or to
        /// the current EffectiveCylinders (off).</summary>
        public bool UseAutoCylinders => Cylinders == 0;

        /// <summary>Effective cylinder count actually being used right now.
        /// Cascade: configured Cylinders (when non-zero) → AutoCylinders
        /// (when in auto mode) → AutoFallbackCylinders. Read-only helper
        /// for both the firing-frequency math and the UI readout.</summary>
        public int EffectiveCylinders
        {
            get
            {
                if (Cylinders >= 1 && Cylinders <= 16) return Cylinders;
                if (AutoCylinders is int auto && auto >= 1 && auto <= 16) return auto;
                return AutoFallbackCylinders;
            }
        }

        /// <summary>Set by the plugin when the resolver flags the active
        /// car as a pure EV. Cleared on every car change. Combined with
        /// <see cref="ElectricMode"/> to decide whether to attenuate or
        /// silence the pulse for electric cars — see AutoGainScale.</summary>
        public bool IsElectric { get; set; }

        /// <summary>What to do for EV cars: attenuate to 50% (default,
        /// MutedHum — synthetic-engine-style) or fully silence (Silent).
        /// Set by ApplyEngineSettings from EnginePulseSettings.ElectricMode
        /// so global default + per-car preset both work. Combustion cars
        /// ignore this field.</summary>
        public ElectricCarMode ElectricMode { get; set; } = ElectricCarMode.MutedHum;

        /// <summary>Computed amplitude scale applied alongside the user's
        /// Gain. Combustion cars: 1.0. EVs: 0.5 in MutedHum, 0.0 in Silent.
        /// Computed (not stored) so changing ElectricMode in the UI takes
        /// effect on the next render without re-resolving the car.</summary>
        public float AutoGainScale =>
            !IsElectric ? 1.0f
            : (ElectricMode == ElectricCarMode.Silent ? 0.0f : 0.5f);

        /// <summary>How AutoCylinders was determined for the active car,
        /// surfaced in the settings UI so users see why we picked a value.
        /// Format: source token from CarCylinderResolver.Result.Source —
        /// "baked" / "codename" / "tag" / "chassis" / "rotor-phrase" /
        /// "tag-rotary" / "chassis-rotary" / "desc-rotary" / "cache" /
        /// "telemetry" (Forza-style direct telemetry) etc. Null when the
        /// car wasn't resolved at all (user should configure manually).
        /// Cleared on car change.</summary>
        public string AutoCylinderSource { get; set; }

        /// <summary>True when AutoCylinderSource indicates a rotary engine
        /// (mapping rotors to effective cyl). UI uses this to clarify that
        /// "4 cylinders" actually means a 2-rotor rotary etc., so the user
        /// doesn't think we got the engine layout wrong.</summary>
        public bool AutoCylinderIsRotary =>
            !string.IsNullOrEmpty(AutoCylinderSource)
            && AutoCylinderSource.IndexOf("rotary", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(AutoCylinderSource, "rotor-phrase", StringComparison.OrdinalIgnoreCase);

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

        private readonly OscillatorSource _osc = new OscillatorSource
        {
            Waveform   = Waveform.Sine,
            Amp        = 0,
            Enabled    = true,
            SampleRate = 4000.0,
        };

        // -------------------- firing-order pattern synthesis --------------------
        //
        // When FiringOrderEnabled is false, the legacy path runs: a single
        // OscillatorSource at firing frequency (RPM/60 × cyl/2). One cycle
        // of the oscillator = one firing event, and every firing event is
        // identical. Fine for I4 / V12 / any even-fire engine, but
        // flat-feeling for cross-plane V8s, Ducati L-twins, V6 odd-fire, etc.
        //
        // When true (the default for new installs), we synthesize at the
        // *engine cycle* rate (RPM/120 × pitch) instead of the firing rate.
        // One cycle of the synth = one full 720° crank rotation, and within
        // that cycle we lay down N pulses at the configured pattern's
        // positions, weighted by per-pulse amplitudes. The cycle waveform
        // is precomputed into a 2048-sample buffer at pattern-change time
        // (a "wavetable" purely as an implementation detail) and walked at
        // the cycle frequency. For even-fire patterns the output is
        // mathematically identical to the legacy path; for non-uniform
        // patterns it produces the characterful timbre.
        //
        // The toggle is still wired so existing users can A/B against the
        // legacy synthesis; long-term plan is to remove it entirely once
        // the firing-order path has been validated in the wild.

        /// <summary>When true, render through the firing-order wavetable
        /// (positional pulses per <see cref="EngineConfig"/>). When false,
        /// use the existing uniform-sinusoid path. Default true; persisted
        /// per-preset so existing setups with FiringOrderEnabled=false
        /// continue to behave as before.</summary>
        public bool FiringOrderEnabled
        {
            get => _firingOrderEnabled;
            set
            {
                if (_firingOrderEnabled == value) return;
                _firingOrderEnabled = value;
                _patternDirty = true;
            }
        }
        private bool _firingOrderEnabled = true;

        /// <summary>Engine layout selector. Auto picks from cylinder count
        /// using the most common modern config (V6 60° / V8 cross-plane /
        /// V12 60°). Explicit values are required to get the characterful
        /// patterns: V8 flat-plane, V-twin variants, V6 odd-fire, rotary.</summary>
        public EngineConfig EngineConfig
        {
            get => _engineConfig;
            set
            {
                if (_engineConfig == value) return;
                _engineConfig = value;
                _patternDirty = true;
            }
        }
        private EngineConfig _engineConfig = EngineConfig.Auto;

        /// <summary>User-supplied custom pattern. Only consulted when
        /// EngineConfig == Custom. Set via FiringPatternDb.ParseCustom from
        /// the advanced UI textbox; null falls back to even-fire.</summary>
        public FiringPattern CustomPattern
        {
            get => _customPattern;
            set
            {
                _customPattern = value;
                if (_engineConfig == EngineConfig.Custom) _patternDirty = true;
            }
        }
        private FiringPattern _customPattern;

        /// <summary>The pattern actually being rendered right now. Read-only
        /// for the UI's "submit this pattern" diagnostic — shows what the
        /// resolver picked (or the user's custom). Null until the first
        /// resolution; never null while FiringOrderEnabled is true.</summary>
        public FiringPattern ActiveFiringPattern { get; private set; }

        // Wavetable + state. The wavetable is regenerated lazily inside
        // RenderAdd when _patternDirty is true (set by config / cyl /
        // waveform / custom-pattern changes). Lazy regen avoids recomputing
        // on every UI tick when the user is dragging a slider.
        private const int WavetableSize = 2048;
        private float[]   _wavetable;
        private double    _wtPhase;        // [0, WavetableSize)
        private bool      _patternDirty = true;
        private int       _wtCyl;          // cyl baked into the current wavetable (for rebuild detection)
        private double    _cyclesPerSec;   // engine cycle freq, set from OnTelemetry / TestUpdate
        private double    _wavetableAmp;   // amp set on the synth path (parallel to _osc.Amp)

        public override bool IsActive
            => IsTesting
            || (Enabled && (_firingOrderEnabled
                              ? _wavetableAmp > 0
                              : _osc.IsActive));

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            float dm = DuckMultiplier;
            if (dm <= 0f) return;

            if (_firingOrderEnabled)
            {
                RenderAddFiringOrder(buffer, count, dm);
                return;
            }

            bool needLp = LowpassHz > 0 && LowpassHz < 2000;
            bool needScratch = needLp || dm < 0.999f;

            if (!needScratch)
            {
                _osc.RenderAdd(buffer, count);
                return;
            }

            // Render the oscillator into a scratch buffer first so we can
            // optionally lowpass and/or duck-scale before adding to the mix.
            if (_scratch == null || _scratch.Length < count) _scratch = new float[count];
            else Array.Clear(_scratch, 0, count);
            _osc.RenderAdd(_scratch, count);

            if (needLp)
            {
                // 1-pole IIR. SampleRate is the OscillatorSource's rate (4 kHz).
                float alpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * LowpassHz / _osc.SampleRate));
                float y = _lpY;
                for (int i = 0; i < count; i++)
                {
                    y += alpha * (_scratch[i] - y);
                    buffer[i] += y * dm;
                }
                _lpY = y;
            }
            else
            {
                for (int i = 0; i < count; i++) buffer[i] += _scratch[i] * dm;
            }
        }
        private float[] _scratch;

        // -------------------- firing-order render --------------------

        private void RenderAddFiringOrder(float[] buffer, int count, float dm)
        {
            // Lazy-regen the wavetable when the pattern fingerprint changes
            // (config / cyl / waveform / custom-pattern). Cheap (one
            // multiply-add per wavetable cell, ~2048 ops, <10µs) and rare —
            // only fires on config change, not per render call.
            int cyl = EffectiveCylinders;
            if (_patternDirty || _wtCyl != cyl || _wavetable == null)
            {
                RegenerateWavetable(cyl);
                _patternDirty = false;
                _wtCyl = cyl;
            }

            float amp = (float)_wavetableAmp;
            if (amp <= 0f) return;

            double phaseStep = _cyclesPerSec * WavetableSize / _osc.SampleRate;
            if (phaseStep <= 0) return;

            bool needLp = LowpassHz > 0 && LowpassHz < 2000;
            float alpha = needLp
                ? (float)(1.0 - Math.Exp(-2.0 * Math.PI * LowpassHz / _osc.SampleRate))
                : 0f;
            float y = _lpY;

            float[] wt = _wavetable;
            int wtLen = wt.Length;
            double phase = _wtPhase;
            for (int i = 0; i < count; i++)
            {
                int i0 = (int)phase;
                if (i0 >= wtLen) i0 = wtLen - 1;
                int i1 = i0 + 1; if (i1 >= wtLen) i1 = 0;
                double frac = phase - i0;
                float v = (float)((1.0 - frac) * wt[i0] + frac * wt[i1]) * amp;
                if (needLp)
                {
                    y += alpha * (v - y);
                    v = y;
                }
                buffer[i] += v * dm;

                phase += phaseStep;
                if (phase >= wtLen) phase -= wtLen;
            }
            _wtPhase = phase;
            if (needLp) _lpY = y;
        }

        private void RegenerateWavetable(int cyl)
        {
            // Resolve the active pattern. EngineConfig.Custom uses the user-
            // supplied pattern when available, else falls back to even-fire
            // for the configured cyl count.
            FiringPattern pattern;
            if (_engineConfig == EngineConfig.Custom && _customPattern != null && _customPattern.Pulses > 0)
                pattern = _customPattern;
            else
                pattern = FiringPatternDb.Resolve(cyl, _engineConfig);
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
            // periodic waveform at firing freq — same as the legacy single-
            // oscillator path. For uneven patterns the pulses overlap or
            // gap as designed, producing distinctive timbre.
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
        /// then short hold, then back-off. Uses the user's Cylinders +
        /// PitchMultiplier + Gain + ThrottleBoost so the test sounds like
        /// their actual configured car.</summary>
        public override void TestUpdate(double phase01)
        {
            // Phase plan: 0.0-0.7 throttle ramp + RPM rising; 0.7-0.9 hold redline;
            // 0.9-1.0 back to idle. Throttle leads RPM slightly so the user feels
            // the throttle-boost component on top of the rpm ramp.
            double throttle, rpmNorm;
            if (phase01 < 0.7)
            {
                double t = phase01 / 0.7;
                throttle = Math.Min(1.0, t * 1.4);     // throttle hits 100% by ~70% of ramp
                rpmNorm  = t;                           // rpm follows linearly
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
                rpmNorm  = Math.Max(0.1, 1.0 - t);     // rpm decays back near idle
            }

            const double idleRpm = 800.0, redlineRpm = 7500.0;
            double rpm = idleRpm + (redlineRpm - idleRpm) * rpmNorm;

            int cyl = EffectiveCylinders;
            if (cyl < 1) cyl = 1; else if (cyl > 12) cyl = 12;
            _osc.Freq = rpm / 60.0 * cyl / 2.0 * PitchMultiplier;
            // Engine cycle freq = rpm/120 (one 720° cycle per 2 revolutions),
            // pitch shifts the whole pattern proportionally to firing rate.
            _cyclesPerSec = rpm / 120.0 * PitchMultiplier;

            double rpmAmp      = IdleAmp + (PeakAmp - IdleAmp) * rpmNorm;
            double throttleAmp = throttle * ThrottleBoost * PeakAmp;
            double amp = Math.Min(rpmAmp + throttleAmp, PeakAmp * 1.5) * Gain * AutoGainScale;
            _osc.Amp      = amp;
            _wavetableAmp = amp;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;

            // Absorb auto-detected cylinder count into AutoCylinders. Sticky
            // — keep the last seen value during pause / between frames so
            // EffectiveCylinders doesn't flip-flop while the user is in a
            // menu. Plugin clears AutoCylinders on car change so the next
            // car's NumCylinders re-populates fresh.
            //
            // AutoCylinderSource is intentionally NOT written here — the
            // plugin's car-change handler owns it. For sources that provide
            // NumCylinders (Forza), the plugin sets source="telemetry"
            // eagerly on car change. Single writer avoids the cross-thread
            // race that existed when both this method and the plugin could
            // write the field.
            if (f.NumCylinders is int n && n >= 1 && n <= 12)
            {
                AutoCylinders = n;
            }

            double rpm = f.Rpms;
            if (rpm < 100) { _osc.Amp = 0; _wavetableAmp = 0; return; }   // engine off

            int cyl = EffectiveCylinders;
            if (cyl < 1) cyl = 1; else if (cyl > 12) cyl = 12;
            _osc.Freq = rpm / 60.0 * cyl / 2.0 * PitchMultiplier;
            _cyclesPerSec = rpm / 120.0 * PitchMultiplier;

            double maxRpm = f.MaxRpm > 0 ? f.MaxRpm : 8000.0;
            double rpmNorm = Math.Min(1.0, rpm / maxRpm);
            double rpmAmp  = IdleAmp + (PeakAmp - IdleAmp) * rpmNorm;

            // Throttle-driven boost responds instantly to user input, masking
            // AC's engine-RPM ramp curve. Source has already normalized 0..1.
            double throttleAmp = f.Throttle01 * ThrottleBoost * PeakAmp;

            double amp = Math.Min(rpmAmp + throttleAmp, PeakAmp * 1.5);
            _osc.Amp = amp * Gain * AutoGainScale;
            _wavetableAmp = _osc.Amp;
        }
    }
}
