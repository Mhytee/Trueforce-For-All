// Brake-lockup flat-spot judder (test layer 3): active between the
// EventDeriver's LockupStart and LockupEnd edges. A locked (or near-locked)
// tire grinds a flat spot that slaps the road once per wheel revolution, so
// the judder frequency TRACKS VEHICLE SPEED (equivalent wheel-rev rate),
// falling as you scrub speed off — the signature "brrrRRRAP" that tells you
// to ease the pedal. Amplitude follows lockup severity (how far negative
// the worst slip ratio is).
//
// Distinct from TractionLossEffect's generic buzz: this is a pulse TRAIN
// with per-rev thumps, not a continuous texture, and it lives/dies strictly
// on the lockup event pair.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class LockupJudderEffect : TelemetryEffect
    {
        public override string Name => "Lockup judder";

        public float PeakAmp { get; set; } = 0.40f;

        /// <summary>Effective rolling radius (m) for speed → wheel-rev-rate.
        /// 0.33 m ≈ a typical performance tire; precision doesn't matter,
        /// only that pitch falls believably as speed bleeds off.</summary>
        public float RollingRadiusM { get; set; } = 0.33f;

        /// <summary>Pulse-rate clamp (Hz). ~13 Hz at 100 km/h with the
        /// default radius.</summary>
        public float MinPulseHz { get; set; } = 4f;
        public float MaxPulseHz { get; set; } = 28f;

        /// <summary>Slip ratio at which severity saturates (a fully locked
        /// wheel reads around -1).</summary>
        public float FullSeverityRatio { get; set; } = 0.9f;

        // Phase 6 contract: lockup is grip information. The pulse train's
        // energy spans the live pulse rate through its first few harmonics
        // (a slap, not a sine).
        public override EffectClass PriorityClass => EffectClass.GripState;
        public override void GetCurrentBand(out double loHz, out double hiHz)
        {
            loHz = Math.Max(2.0, _pulseHz * 0.8);
            hiHz = Math.Min(400.0, _pulseHz * 4.0);
        }

        private const double SampleRateHz = 4000.0;

        // Telemetry → render state.
        private volatile bool _active;
        private float _targetAmp;
        private float _pulseHz = 10f;

        // Render state.
        private float  _amp;
        private double _phase;

        public override bool IsActive => IsTesting || (Enabled && (_active || _amp > 0.005f));

        public override double ActivityLevel => _amp;

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;

            if ((f.Events & FrameEvents.LockupStart) != 0) _active = true;
            if ((f.Events & FrameEvents.LockupEnd) != 0)   _active = false;

            if (!_active || !f.HasTireQuads)
            {
                _targetAmp = 0f;
                return;
            }

            double worst = Math.Min(
                Math.Min(f.TireSlipRatio.FL, f.TireSlipRatio.FR),
                Math.Min(f.TireSlipRatio.RL, f.TireSlipRatio.RR));
            float severity = (float)Math.Min(Math.Max(-worst, 0.0) / Math.Max(0.1f, FullSeverityRatio), 1.0);
            _targetAmp = severity * PeakAmp;

            // Wheel-rev rate the tire WOULD roll at for current speed: the
            // flat spot slaps once per rev while the wheel still turns.
            double vMs = f.SpeedKmh / 3.6;
            float revHz = (float)(vMs / (2.0 * Math.PI * Math.Max(0.1f, RollingRadiusM)));
            _pulseHz = Math.Min(Math.Max(revHz, MinPulseHz), MaxPulseHz);
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            float target = _targetAmp;
            if (target <= 0.0001f && _amp <= 0.0001f) return;

            double step = _pulseHz / SampleRateHz;
            float scale = Gain * DuckMultiplier;

            for (int i = 0; i < count; i++)
            {
                _amp += (target - _amp) * 0.012f;   // ~20 ms tau, click-free
                if (_amp > 0.0001f)
                {
                    // Raised-cosine spike train: sharp per-rev slaps, not a
                    // sine hum. pow8 narrows each pulse to ~1/4 of the cycle.
                    double c = 0.5 + 0.5 * Math.Cos(2.0 * Math.PI * _phase);
                    float pulse = (float)(c * c * c * c * c * c * c * c);
                    buffer[i] += (pulse * 2f - 0.3f) * _amp * scale;
                }
                _phase += step;
                if (_phase >= 1.0) _phase -= 1.0;
            }
        }

        public override int TestPlay()
        {
            _active    = true;
            _targetAmp = PeakAmp;
            _pulseHz   = 16f;
            const int duration = 1000;
            StartTest(duration);
            return duration;
        }

        public override void TestUpdate(double phase01)
        {
            // Pitch falls through the test like speed bleeding off under
            // a locked brake.
            _pulseHz   = 22f - (float)(phase01 * 14.0);
            _targetAmp = phase01 > 0.9 ? 0f : PeakAmp;
        }

        public override void OnTelemetryStall()
        {
            _active = false;
            _targetAmp = 0f;
        }

        public override void Reset()
        {
            _active = false;
            _targetAmp = 0f;
            _amp = 0f;
            _phase = 0;
        }
    }
}
