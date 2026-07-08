// Kerb-entry thump (test layer 2): a single percussive hit fired on the
// EventDeriver's RumbleStripStart edge — the moment a wheel FIRST touches
// the strip — distinct from RoadBumpsEffect's sustained kerb texture that
// rides the surface-rumble channel while you stay on it. Together they read
// as "WHACK, brrrrr": the impact, then the ride.
//
// Amplitude scales with speed (hitting a kerb at 200 is a bigger event than
// creeping over it at 40) and the envelope is a fast-attack sine burst like
// GearShiftEffect's thud, pitched lower so the two never read as the same
// cue.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class KerbThumpEffect : TelemetryEffect
    {
        public override string Name => "Kerb thump";

        /// <summary>Thump frequency (Hz). 30 = deeper than the 40 Hz gear
        /// thud so the cues stay distinguishable.</summary>
        public float Freq { get; set; } = 30f;

        // Phase 6 contract: a momentary alert around its thump frequency.
        public override EffectClass PriorityClass => EffectClass.Transient;
        public override void GetCurrentBand(out double loHz, out double hiHz)
        {
            loHz = Freq * 0.7;
            hiHz = Freq * 1.6;
        }

        public int   EnvelopeMs { get; set; } = 60;
        public float PeakAmp    { get; set; } = 0.40f;

        /// <summary>Speed (km/h) at which the thump reaches full amplitude;
        /// scales linearly from MinSpeedKmh below that.</summary>
        public float FullSpeedKmh { get; set; } = 120f;

        /// <summary>Below this speed kerb entries don't thump at all
        /// (parking-lot curbs aren't haptic events).</summary>
        public float MinSpeedKmh { get; set; } = 15f;

        private const double SampleRateHz = 4000.0;

        private int    _envelopeRemaining;
        private int    _envelopeTotal = 1;
        private float  _envelopeAmp;
        private double _phase;

        public override bool IsActive => IsTesting || (Enabled && _envelopeRemaining > 0);

        public override double ActivityLevel
        {
            get
            {
                int total = _envelopeTotal, rem = _envelopeRemaining;
                return (total <= 0 || rem <= 0) ? 0 : (double)rem / total;
            }
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            if ((f.Events & FrameEvents.RumbleStripStart) == 0) return;

            float kmh = (float)f.SpeedKmh;
            if (kmh < MinSpeedKmh) return;
            float speed01 = Math.Min((kmh - MinSpeedKmh) / Math.Max(1f, FullSpeedKmh - MinSpeedKmh), 1f);

            Trigger(PeakAmp * (0.35f + 0.65f * speed01));
        }

        private void Trigger(float amp)
        {
            _envelopeTotal     = Math.Max(1, (int)(EnvelopeMs * SampleRateHz / 1000.0));
            _envelopeRemaining = _envelopeTotal;
            _envelopeAmp       = amp;
            _phase             = 0;
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            int remaining = _envelopeRemaining;
            if (remaining <= 0) return;

            double phaseStep = Freq / SampleRateHz;
            float scale = _envelopeAmp * Gain * DuckMultiplier;
            int total = _envelopeTotal;

            for (int i = 0; i < count && remaining > 0; i++)
            {
                float env = (float)remaining / total;
                buffer[i] += (float)Math.Sin(2.0 * Math.PI * _phase) * env * scale;
                _phase += phaseStep;
                if (_phase >= 1.0) _phase -= 1.0;
                remaining--;
            }
            _envelopeRemaining = remaining;
        }

        public override int TestPlay()
        {
            Trigger(PeakAmp);
            int duration = EnvelopeMs + 100;
            StartTest(duration);
            return duration;
        }

        public override void Reset()
        {
            _envelopeRemaining = 0;
            _phase = 0;
        }
    }
}
