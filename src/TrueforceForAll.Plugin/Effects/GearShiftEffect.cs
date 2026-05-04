// Gear-shift thud: a short low-freq waveform burst with a linear-decay
// envelope, retriggered each time GameData.Gear changes.
//
// The synth is inline (per-sample envelope is simpler hand-rolled than
// pumping the OscillatorSource) but the waveform shape is selectable so
// users can pick the feel they prefer.

using System;
using GameReaderCommon;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class GearShiftEffect : TelemetryEffect
    {
        public override string Name => "Gear shift";

        /// <summary>Thud frequency (Hz). 40 Hz = solid mechanical clunk feel.</summary>
        public float Freq { get; set; } = 40.0f;

        /// <summary>Envelope length in milliseconds.</summary>
        public int EnvelopeMs { get; set; } = 80;

        /// <summary>Peak amplitude at the start of the envelope. Sized to the
        /// FFB pass-through writes AC's torque target into ep3 cur, so the
        /// audio in the rolling window is purely additive — no longer
        /// constrained to small amplitudes for FFB coexistence.</summary>
        public float PeakAmp { get; set; } = 0.35f;

        public Waveform Waveform { get; set; } = Waveform.Sine;

        /// <summary>Amplitude scale applied when the destination gear is "N"
        /// (going to neutral). 0.4 default = ~40% of a normal bump, so
        /// sequential shifts feel like a soft "approach" tap into neutral
        /// followed by a full bump landing in the destination gear. 0.0
        /// disables the neutral bump entirely; 1.0 makes it equal weight.</summary>
        public float NeutralAmp { get; set; } = 0.4f;

        private const double SampleRateHz = 4000.0;

        private string _lastGear;
        private int    _envelopeRemaining;   // samples
        private int    _envelopeTotal = 80;
        private float  _envelopeAmpScale = 1.0f;
        private double _phase;
        private readonly Random _rng = new Random();

        public override bool IsActive => IsTesting || (Enabled && _envelopeRemaining > 0);

        public override double ActivityLevel
        {
            get
            {
                int total = _envelopeTotal;
                int rem   = _envelopeRemaining;
                if (total <= 0 || rem <= 0) return 0;
                return (double)rem / total;
            }
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            int remaining = _envelopeRemaining;
            if (remaining <= 0) return;

            double phaseStep = Freq / SampleRateHz;
            float scale = PeakAmp * Gain * _envelopeAmpScale;
            int total = _envelopeTotal;
            Waveform w = Waveform;

            for (int i = 0; i < count && remaining > 0; i++)
            {
                float env = (float)remaining / total;            // linear 1 → 0
                float v   = SampleAt(w, _phase, _rng);
                buffer[i] += v * env * scale;
                _phase += phaseStep;
                if (_phase >= 1.0) _phase -= Math.Floor(_phase);
                remaining--;
            }
            _envelopeRemaining = remaining;
        }

        private static float SampleAt(Waveform w, double phase, Random rng)
        {
            switch (w)
            {
                case Waveform.Sine:     return (float)Math.Sin(2.0 * Math.PI * phase);
                case Waveform.Square:   return phase < 0.5 ? 1f : -1f;
                case Waveform.Saw:      return (float)(2.0 * phase - 1.0);
                case Waveform.Triangle: return phase < 0.5
                                            ? (float)(4.0 * phase - 1.0)
                                            : (float)(3.0 - 4.0 * phase);
                case Waveform.Noise:    return (float)(rng.NextDouble() * 2.0 - 1.0);
                default:                return 0f;
            }
        }

        public override int TestPlay()
        {
            // Trigger one envelope at full amp — decays naturally over EnvelopeMs.
            // StartTest() keeps IsTesting=true so IsActive returns true even
            // when Enabled=false in settings (otherwise the Mixer would skip
            // RenderAdd and the test would be silent).
            _envelopeTotal     = Math.Max(1, (int)(EnvelopeMs * SampleRateHz / 1000.0));
            _envelopeRemaining = _envelopeTotal;
            _envelopeAmpScale  = 1.0f;
            _phase             = 0;
            int duration = EnvelopeMs + 100;
            StartTest(duration);
            return duration;
        }

        public override void OnTelemetry(GameData data)
        {
            string gear = data?.NewData?.Gear;
            if (string.IsNullOrEmpty(gear))
            {
                _lastGear = gear;
                return;
            }

            if (_lastGear != null && _lastGear != gear)
            {
                // Sequential gearboxes pass through "N" between every gear, so
                // every shift triggers two transitions: gear→N then N→gear.
                // We scale the gear→N bump by NeutralAmp (default 0.4) so the
                // destination-gear landing feels dominant, and let users tune
                // the neutral component down to 0 (disabled) or up to 1 (equal).
                bool goingToNeutral = string.Equals(gear, "N", StringComparison.OrdinalIgnoreCase);
                float ampScale = goingToNeutral ? NeutralAmp : 1.0f;
                if (ampScale > 0f)
                {
                    _envelopeTotal = Math.Max(1, (int)(EnvelopeMs * SampleRateHz / 1000.0));
                    _envelopeRemaining = _envelopeTotal;
                    _envelopeAmpScale = ampScale;
                    _phase = 0;
                }
            }
            _lastGear = gear;
        }
    }
}
