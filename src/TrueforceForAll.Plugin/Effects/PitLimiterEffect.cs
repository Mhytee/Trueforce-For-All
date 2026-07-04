// Pit limiter haptic. Modal, fires a continuous pulse train at a slow
// modulation rate while the limiter is engaged, to mimic the engine being
// cut periodically (which is what a pit limiter does mechanically).
//
// Most racing sims expose the limiter state directly: TelemetryFrame.
// PitLimiterActive is non-null when the source surfaces it (SimHub
// fallback for most games). When null, the effect is silent, no false
// positives for sources that don't read the flag.
//
// Defaults are tuned to feel like a hard pulsing rev cut: low-pitched
// carrier (50 Hz square) with a 6 Hz pulse modulator and a 60% duty
// cycle. Users can tune all axes in the settings panel.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class PitLimiterEffect : TelemetryEffect
    {
        public override string Name => "Pit limiter";

        /// <summary>Carrier tone within each pulse (Hz). 50 Hz reads as a
        /// deep "thud", close to the perceived note of a pit-limited
        /// engine being cut at idle.</summary>
        public float Freq { get; set; } = 50.0f;

        /// <summary>How fast the pulse modulator opens and closes the carrier
        /// (Hz). 6 Hz matches the audible cadence of most factory pit
        /// limiters when you hold throttle against them.</summary>
        public float PulseFreq { get; set; } = 6.0f;

        /// <summary>Fraction of each pulse period during which the carrier
        /// is audible. Higher = more sustained / less stutter; lower = more
        /// punctuated. 0.6 = comfortable middle.</summary>
        public float DutyCycle { get; set; } = 0.6f;

        public Waveform Waveform { get; set; } = Waveform.Square;

        /// <summary>Amplitude while the limiter is engaged.</summary>
        public float ActiveAmp { get; set; } = 0.30f;

        private const double SampleRate = 4000.0;
        private const int HoldMs = 80;   // post-disengage decay window
        private static readonly long HoldStopwatchTicks =
            HoldMs * System.Diagnostics.Stopwatch.Frequency / 1000;

        // State
        private float  _amp;
        private long   _lastActiveTicks;     // Stopwatch.GetTimestamp() units
        private double _carrierPhase;
        private double _pulsePhase;

        public override bool IsActive => IsTesting || (Enabled && _amp > 0);

        // Activity level for sidechain ducking. Returns 0.6 (instead of 1.0)
        // while engaged so the pit limiter pulse train ducks audio + engine
        // pulse perceptibly without fully muting them, the user still wants
        // to feel the engine note over the limiter cadence.
        public override double ActivityLevel => _amp > 0 ? 0.6 : 0;

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            if (_amp <= 0) return;

            double cStep = Math.Max(0.0, Freq) / SampleRate;
            double pStep = Math.Max(0.0, PulseFreq) / SampleRate;
            double duty  = Math.Min(1.0, Math.Max(0.0, (double)DutyCycle));
            float amp = _amp * Gain * DuckMultiplier;
            Waveform w = Waveform;

            for (int i = 0; i < count; i++)
            {
                if (_pulsePhase < duty)
                    buffer[i] += SampleAt(w, _carrierPhase) * amp;
                _carrierPhase += cStep;
                if (_carrierPhase >= 1.0) _carrierPhase -= Math.Floor(_carrierPhase);
                _pulsePhase   += pStep;
                if (_pulsePhase   >= 1.0) _pulsePhase   -= Math.Floor(_pulsePhase);
            }
        }

        public override int TestPlay()
        {
            _amp = ActiveAmp;
            StartTest(2000);
            return 2000;
        }

        // Standstill suppression threshold (km/h). Some games / SimHub
        // mappings flag PitLimiterOn whenever the car sits in the pit-lane
        // geometry rather than only when the limiter button is engaged
        // (e.g., AC's spawn box on a non-traditional touge map sits inside
        // the pit area and trips PitLimiterOn while parked). A real pit
        // limiter does nothing at zero speed anyway, so suppressing the
        // haptic below ~1 km/h removes that false positive without
        // affecting normal pit-in / pit-out where you're rolling.
        private const float StandstillKmh = 1.0f;

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            int active = f.PitLimiterActive ?? 0;
            if (active > 0 && f.SpeedKmh < StandstillKmh) active = 0;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (active > 0) _lastActiveTicks = now;
            // Brief hold so a single dropped flag tick doesn't kill the pulse;
            // most games are flag-stable but a few flicker around the limiter
            // engagement boundary. 80 ms ≈ half a pulse period at 6 Hz.
            bool stillEngaged = _lastActiveTicks != 0
                && (now - _lastActiveTicks) < HoldStopwatchTicks;
            _amp = stillEngaged ? ActiveAmp : 0;
        }

        // Telemetry stopped: if the game closed with the pit limiter on, the
        // pulse would hold forever. Drop the amplitude and clear the hold.
        public override void OnTelemetryStall()
        {
            _amp = 0;
            _lastActiveTicks = 0;
        }

        public override void Reset()
        {
            _amp = 0;
            _lastActiveTicks = 0;
            _carrierPhase = 0;
            _pulsePhase = 0;
        }

        private static float SampleAt(Waveform w, double phase)
        {
            switch (w)
            {
                case Waveform.Sine:     return (float)Math.Sin(2.0 * Math.PI * phase);
                case Waveform.Square:   return phase < 0.5 ? 1f : -1f;
                case Waveform.Saw:      return (float)(2.0 * phase - 1.0);
                case Waveform.Triangle:
                    return phase < 0.5
                        ? (float)(4.0 * phase - 1.0)
                        : (float)(3.0 - 4.0 * phase);
                default:                return 0f;
            }
        }
    }
}
