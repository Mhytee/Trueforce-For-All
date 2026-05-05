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

        /// <summary>Number of engine cylinders (1-12). Default 4 (typical sim car).</summary>
        public int Cylinders { get; set; } = 4;

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
            set => _osc.Waveform = value;
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

        public override bool IsActive => IsTesting || (Enabled && _osc.IsActive);

        public override double ActivityLevel
        {
            // Engine pulse is a continuous effect — it doesn't trigger ducking,
            // but we still report its current amplitude relative to peak so the
            // UI's live activity meter can show what it's doing.
            get
            {
                double max = PeakAmp * Math.Max(0.01, Gain) * 1.5; // ThrottleBoost can push to 1.5×
                return Math.Min(1.0, Math.Max(0.0, _osc.Amp / max));
            }
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            float dm = DuckMultiplier;
            if (dm <= 0f) return;

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

            int cyl = Cylinders;
            if (cyl < 1) cyl = 1; else if (cyl > 12) cyl = 12;
            _osc.Freq = rpm / 60.0 * cyl / 2.0 * PitchMultiplier;

            double rpmAmp      = IdleAmp + (PeakAmp - IdleAmp) * rpmNorm;
            double throttleAmp = throttle * ThrottleBoost * PeakAmp;
            double amp = Math.Min(rpmAmp + throttleAmp, PeakAmp * 1.5) * Gain;
            _osc.Amp = amp;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;

            double rpm = f.Rpms;
            if (rpm < 100) { _osc.Amp = 0; return; }   // engine off

            int cyl = Cylinders;
            if (cyl < 1) cyl = 1; else if (cyl > 12) cyl = 12;
            _osc.Freq = rpm / 60.0 * cyl / 2.0 * PitchMultiplier;

            double maxRpm = f.MaxRpm > 0 ? f.MaxRpm : 8000.0;
            double rpmNorm = Math.Min(1.0, rpm / maxRpm);
            double rpmAmp  = IdleAmp + (PeakAmp - IdleAmp) * rpmNorm;

            // Throttle-driven boost responds instantly to user input, masking
            // AC's engine-RPM ramp curve. Source has already normalized 0..1.
            double throttleAmp = f.Throttle01 * ThrottleBoost * PeakAmp;

            double amp = Math.Min(rpmAmp + throttleAmp, PeakAmp * 1.5);
            _osc.Amp = amp * Gain;
        }
    }
}
