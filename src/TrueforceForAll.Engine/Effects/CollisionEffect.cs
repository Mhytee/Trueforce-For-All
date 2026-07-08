// Collision thud: a low-frequency envelope-decay burst whose amplitude
// scales with the impact's magnitude. Driven by TelemetryFrame.
// CollisionMagnitude, null on sources that don't expose any collision
// signal (effect stays silent), 0 = no impact, 1.0 = moderate, 2.0+ =
// hard wreck.
//
// Triggering: rising-edge above MinThreshold fires one envelope. Same
// magnitude held does NOT refire. A REFRACTORY period suppresses
// re-triggers within RefractoryMs to avoid stuttering on multi-frame
// crashes. A bigger hit during the refractory window IS allowed to
// re-fire (so a small bounce followed by a hard impact still gets
// proper feedback).
//
// Amplitude curve: soft-knee log so a 10x harder hit feels stronger
// without being 10x more violent (would be unsafe for the wheel and the
// user's wrists). Hard-capped at MaxAmp regardless of magnitude.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class CollisionEffect : TelemetryEffect
    {
        public override string Name => "Collision";

        /// <summary>Thud frequency (Hz). 50 Hz = deep impact thud, lower
        /// than gear-shift's 40 Hz default so they're perceptually
        /// distinct when both fire close together.</summary>
        public float Freq { get; set; } = 50.0f;

        /// <summary>Envelope length in milliseconds. 120 ms = perceptible
        /// thud with enough body to feel weighty.</summary>
        public int EnvelopeMs { get; set; } = 120;

        /// <summary>Minimum normalized magnitude to fire at all. Below
        /// this, treat as background noise (light scrapes, slow contact).
        /// 0.20 default keeps the effect quiet for taps.</summary>
        public float MinThreshold { get; set; } = 0.20f;

        /// <summary>Smallest amplitude when above MinThreshold. Even a
        /// just-over-threshold hit produces something perceptible.</summary>
        public float MinAmp { get; set; } = 0.20f;

        /// <summary>Cap. No collision exceeds this regardless of how big
        /// the magnitude is. Safety knob for wrists and wheelbase.</summary>
        public float MaxAmp { get; set; } = 0.85f;

        /// <summary>Magnitude at which we hit MaxAmp. Above this, the
        /// curve clamps. 2.0 = "hard wreck" in the normalized scale.</summary>
        public float NormalizationScale { get; set; } = 2.0f;

        /// <summary>Suppress retrigger for this many ms after a fire.
        /// Bigger hits override (see refractory logic in OnTelemetry).</summary>
        public int RefractoryMs { get; set; } = 250;

        public Waveform Waveform { get; set; } = Waveform.Square;

        private const double SampleRateHz = 4000.0;

        private int    _envelopeRemaining;
        private int    _envelopeTotal = 1;
        private float  _envelopeAmpScale = 1.0f;
        private double _phase;
        private double _lastMagnitude;
        private long   _lastFireTicks;
        private float  _lastFireMagnitude;
        private static readonly long TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000;

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
            // DuckMultiplier is 1.0 except when the airborne ducker pulls it
            // down (collision sits above the sidechain tiers, so nothing else
            // touches it). Lets a tumbling-in-the-air accel spike be suppressed
            // when the user opts collision into airborne ducking.
            float scale = Gain * _envelopeAmpScale * DuckMultiplier;
            int total = _envelopeTotal;
            Waveform w = Waveform;

            for (int i = 0; i < count && remaining > 0; i++)
            {
                float env = (float)remaining / total;            // linear 1 → 0
                float v   = WaveformMath.SampleAt(w, _phase);
                buffer[i] += v * env * scale;
                _phase += phaseStep;
                if (_phase >= 1.0) _phase -= Math.Floor(_phase);
                remaining--;
            }
            _envelopeRemaining = remaining;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            double magNullable = f.CollisionMagnitude ?? 0;
            // A non-finite magnitude (source restart / garbage frame) must
            // not reach the gate: an Infinity fires a full MaxAmp thud, and
            // a NaN poisons _lastMagnitude permanently (NaN fails every
            // comparison, so the rising-edge test can never be true again
            // and the effect goes silent for the rest of the session).
            if (double.IsNaN(magNullable) || double.IsInfinity(magNullable))
                magNullable = 0;
            // Rising-edge gate: only fire when magnitude crosses MinThreshold
            // AND wasn't above threshold last frame. Held-high state doesn't
            // refire (avoids stuttering on multi-frame crashes).
            bool risingEdge = magNullable >= MinThreshold && _lastMagnitude < MinThreshold;
            _lastMagnitude = magNullable;

            if (!risingEdge) return;

            // Refractory: suppress retrigger within RefractoryMs UNLESS the
            // new hit is significantly harder than the last one (>1.5x), in
            // which case we override and play the bigger hit.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long elapsedMs = (now - _lastFireTicks) / TicksPerMs;
            if (_lastFireTicks != 0
                && elapsedMs < RefractoryMs
                && magNullable < _lastFireMagnitude * 1.5)
            {
                return;
            }

            FireCollision((float)magNullable);
            _lastFireTicks = now;
            _lastFireMagnitude = (float)magNullable;
        }

        // Fire one envelope at amplitude derived from magnitude. The amp
        // curve is a soft-knee log: linear interpolation between MinAmp
        // (at MinThreshold) and MaxAmp (at NormalizationScale), clamped.
        private void FireCollision(float magnitude)
        {
            float t = (magnitude - MinThreshold) / Math.Max(0.001f, NormalizationScale - MinThreshold);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            // Soft-knee: sqrt(t) gives more dynamic range to lighter hits
            // (so a moderate bump and a hard wreck are distinguishable),
            // while still hitting the cap on truly violent impacts.
            float curved = (float)Math.Sqrt(t);
            float amp = MinAmp + (MaxAmp - MinAmp) * curved;
            if (amp > MaxAmp) amp = MaxAmp;

            _envelopeTotal     = Math.Max(1, (int)(EnvelopeMs * SampleRateHz / 1000.0));
            _envelopeRemaining = _envelopeTotal;
            _envelopeAmpScale  = amp;
            _phase             = 0;
        }

        public override int TestPlay()
        {
            // Test plays a moderate-strength hit (~0.6 normalized) so the
            // user gets a representative feel rather than the maximum.
            FireCollision(0.6f);
            int duration = EnvelopeMs + 100;
            StartTest(duration);
            return duration;
        }

        public override void Reset()
        {
            _envelopeRemaining = 0;
            _phase = 0;
            _lastMagnitude = 0;
            _lastFireTicks = 0;
            _lastFireMagnitude = 0;
        }
    }
}
