// CTM axle rollups (phase 2 of docs/haptic-engine-plan.md). Turns per-tire
// combined slip into per-axle grip utilization the rest of the engine can
// trust: FrontGrip01 / RearGrip01 / GripBalance.
//
// The whole point over TireQuad.FrontAvg: LOAD WEIGHTING. In a corner the
// outside tire carries the load and does the gripping; averaging it with the
// unloaded inside tire (which slips at a different, mostly meaningless rate)
// dilutes the breakaway signal the driver's hands need. Weighting by per-tire
// suspension compression reads the axle the way the road does.
//
// Weight source ladder (best available wins):
//   1. Per-tire suspension travel — direct load measurement, no sign
//      conventions to get wrong. More compression = more load.
//   2. Lateral acceleration — Forza convention: AccelerationSway positive =
//      accelerating right = turning right = LEFT tires are outside/loaded.
//   3. Neither → plain 50/50 average (exactly the legacy FrontAvg behavior).
//
// Pure math, no state, additive: frames without tire quads pass through with
// the rollups left null.

using System;

namespace TrueforceForAll.Core
{
    public static class CtmComposer
    {
        /// <summary>Weight clamp: even a fully lifted inside wheel keeps a
        /// sliver of representation so a single noisy channel can't own the
        /// whole axle reading.</summary>
        public const double MinTireWeight = 0.05;

        /// <summary>Lat-g at which the lat-accel fallback reaches full
        /// outside bias (weights hit the clamp).</summary>
        public const double LatGFullBias = 1.0;

        public static void Compose(ref TelemetryFrame f)
        {
            if (!f.HasTireQuads)
            {
                // No quads to compose from, but a source may supply the axle
                // rollups DIRECTLY: FS derives per-wheel slip from wheel
                // rotation and pre-averages per axle. Respect those instead
                // of erasing them; this null-out silently ate FS's axle
                // slip while every upstream number was correct (2026-08-08).
                if (f.FrontGrip01.HasValue || f.RearGrip01.HasValue)
                {
                    f.GripBalance = (f.FrontGrip01.HasValue && f.RearGrip01.HasValue)
                        ? f.RearGrip01.Value - f.FrontGrip01.Value
                        : (double?)null;
                    f.Caps |= SignalGroups.AxleRollups;
                }
                else
                {
                    f.GripBalance = null;
                }
                return;
            }

            // Source-provided rollups stay authoritative even when quads are
            // present: FS fills quads for the signed-ratio consumers (lockup)
            // but pre-scales its rollups to grip utilization, and its
            // SuspTravelM carries raw wheel Y positions the weight ladder
            // would misread as travel shares. Forza/AC leave the rollups
            // null, so their composed path is untouched.
            if (f.FrontGrip01.HasValue && f.RearGrip01.HasValue)
            {
                f.GripBalance = f.RearGrip01.Value - f.FrontGrip01.Value;
                f.Caps |= SignalGroups.AxleRollups;
                return;
            }

            GetAxleWeights(in f, out double wFL, out double wFR, out double wRL, out double wRR);

            double front = wFL * Math.Abs(f.TireCombinedSlip.FL) + wFR * Math.Abs(f.TireCombinedSlip.FR);
            double rear  = wRL * Math.Abs(f.TireCombinedSlip.RL) + wRR * Math.Abs(f.TireCombinedSlip.RR);

            f.FrontGrip01 = front;
            f.RearGrip01  = rear;
            f.GripBalance = rear - front;
            f.Caps |= SignalGroups.AxleRollups;
        }

        private static void GetAxleWeights(in TelemetryFrame f,
            out double wFL, out double wFR, out double wRL, out double wRR)
        {
            // 1. Suspension compression: weight each tire by its share of the
            // axle's total travel. Meaningful travel is centimeters; below the
            // noise floor (near-zero sum: airborne, spawn settle, source that
            // zeroes the quad) fall through to the next ladder rung.
            const double MinAxleTravel = 0.005; // 5 mm summed across the pair
            double fSum = f.SuspTravelM.FL + f.SuspTravelM.FR;
            double rSum = f.SuspTravelM.RL + f.SuspTravelM.RR;
            if (fSum > MinAxleTravel && rSum > MinAxleTravel)
            {
                wFL = Clamp(f.SuspTravelM.FL / fSum);
                wFR = 1.0 - wFL;
                wRL = Clamp(f.SuspTravelM.RL / rSum);
                wRR = 1.0 - wRL;
                return;
            }

            // 2. Lateral g: positive sway = turning right = left tires loaded.
            if (f.AccelerationSway.HasValue)
            {
                double latG = f.AccelerationSway.Value / 9.81;
                double bias = Math.Min(Math.Abs(latG) / LatGFullBias, 1.0) * (0.5 - MinTireWeight);
                double wOutside = 0.5 + bias;
                double wLeft = latG > 0 ? wOutside : 1.0 - wOutside;
                wFL = wLeft; wFR = 1.0 - wLeft;
                wRL = wLeft; wRR = 1.0 - wLeft;
                return;
            }

            // 3. Legacy behavior: plain pair average.
            wFL = wFR = wRL = wRR = 0.5;
        }

        private static double Clamp(double w)
            => w < MinTireWeight ? MinTireWeight
             : w > 1.0 - MinTireWeight ? 1.0 - MinTireWeight
             : w;
    }
}
