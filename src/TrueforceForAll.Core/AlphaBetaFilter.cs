// Alpha-beta tracker for the phase-1 resampler's live-edge prediction path.
// FM8's Data Out is a fixed 60 Hz; the engine ticks at 500 Hz and the force
// path at 1 kHz. Rather than buying smoothness with jitter-buffer latency, at
// the live edge we track (value, velocity) per continuous channel and
// extrapolate up to one source frame ahead. Alpha-beta over Kalman: constant
// cost, two intuition-sized knobs, adequate for 16 ms horizons.
//
// Usage per channel: Update(z, dtSec) on every source sample, then
// PredictAt(horizonSec) any number of times between samples. Reset on
// discontinuities (session start, teleport) so a stale velocity can't sling
// the first prediction.

using System;

namespace TrueforceForAll.Core
{
    public sealed class AlphaBetaFilter
    {
        /// <summary>Position correction gain (0..1]. Higher = trust
        /// measurements more, smooth less.</summary>
        public double Alpha { get; set; } = 0.85;

        /// <summary>Velocity correction gain (0..1]. Higher = velocity adapts
        /// faster (snappier prediction, more overshoot on noise).</summary>
        public double Beta { get; set; } = 0.35;

        /// <summary>Extrapolation cap in seconds. Predictions beyond this
        /// horizon hold the capped extrapolation — a late/dropped packet must
        /// not sling the estimate off to infinity. Default = 1.5 source frames
        /// at 60 Hz.</summary>
        public double MaxHorizonSec { get; set; } = 0.025;

        public bool   HasState { get; private set; }
        public double Value    { get; private set; }
        public double Velocity { get; private set; }

        public void Reset()
        {
            HasState = false;
            Value = 0;
            Velocity = 0;
        }

        /// <summary>Ingest a measurement. First sample after Reset seeds the
        /// state with zero velocity (no ghost slew toward the first value).</summary>
        public void Update(double z, double dtSec)
        {
            if (!HasState || dtSec <= 0)
            {
                Value = z;
                Velocity = 0;
                HasState = true;
                return;
            }
            double predicted = Value + Velocity * dtSec;
            double residual  = z - predicted;
            Value    = predicted + Alpha * residual;
            Velocity = Velocity + Beta * residual / dtSec;
        }

        /// <summary>Extrapolate the current state <paramref name="horizonSec"/>
        /// past the last Update. Clamped to MaxHorizonSec.</summary>
        public double PredictAt(double horizonSec)
        {
            if (!HasState) return 0;
            double h = Math.Min(Math.Max(0, horizonSec), MaxHorizonSec);
            return Value + Velocity * h;
        }
    }
}
