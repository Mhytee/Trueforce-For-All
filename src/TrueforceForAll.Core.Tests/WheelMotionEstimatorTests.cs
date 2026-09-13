using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The motion estimator behind the DirectInput effect renderer. Its
    // acceleration output feeds the inertia effect, which is the one effect
    // that AMPLIFIES loop noise rather than suppressing it (it cancels
    // damping instead of adding it), so a ripple the damper never notices
    // reaches the wheel as grain (rig, 2026-09-01).
    public class WheelMotionEstimatorTests
    {
        private const double TicksPerSec = 1000.0;   // ticks are milliseconds

        [Fact]
        public void Acceleration_TracksASteadyRamp()
        {
            var e = new WheelMotionEstimator { VelocityCutoffHz = 0 };
            // Velocity ramping at 4 range/s^2 for 1 s.
            double vel = 0, pos = 0;
            for (int ms = 0; ms <= 1000; ms++)
            {
                e.UpdateWithVelocity(pos, vel, ms, TicksPerSec);
                vel += 4.0 * 0.001;
                pos += vel * 0.001;
            }
            // Exact for steady acceleration once the washout has settled.
            Assert.InRange(e.Acceleration, 3.5, 4.5);
        }

        [Fact]
        public void Acceleration_StaysSmoothThroughAQuantizedVelocity()
        {
            // A real encoder delivers velocity in steps. Differencing that
            // per tick gives a train of impulses one step tall divided by
            // 1 ms; the washout differentiator must not pass that ripple.
            var e = new WheelMotionEstimator { VelocityCutoffHz = 0 };
            const double step = 0.05;    // coarse velocity quantum
            double trueVel = 0, pos = 0;
            double min = double.MaxValue, max = double.MinValue;
            for (int ms = 0; ms <= 1500; ms++)
            {
                double quantized = Math.Round(trueVel / step) * step;
                e.UpdateWithVelocity(pos, quantized, ms, TicksPerSec);
                if (ms > 700)   // after the washout settles
                {
                    if (e.Acceleration < min) min = e.Acceleration;
                    if (e.Acceleration > max) max = e.Acceleration;
                }
                trueVel += 2.0 * 0.001;
                pos += trueVel * 0.001;
            }
            // The true acceleration is 2.0. Naive dv/dt would swing between
            // 0 and 50 (one 0.05 step per millisecond); the ripple here has
            // to stay a fraction of the signal, not a multiple of it.
            Assert.InRange(min, 1.0, 3.0);
            Assert.InRange(max, 1.0, 3.0);
            Assert.True(max - min < 1.5, $"acceleration ripple {max - min:F2} is too coarse");
        }

        [Fact]
        public void Acceleration_IsZeroAtConstantSpeed()
        {
            var e = new WheelMotionEstimator { VelocityCutoffHz = 0 };
            double pos = 0;
            for (int ms = 0; ms <= 800; ms++)
            {
                e.UpdateWithVelocity(pos, 1.5, ms, TicksPerSec);
                pos += 1.5 * 0.001;
            }
            // Added inertia must cost nothing while coasting at speed.
            Assert.InRange(e.Acceleration, -0.05, 0.05);
        }

        [Fact]
        public void StaleGap_ResetsInsteadOfSpiking()
        {
            var e = new WheelMotionEstimator { VelocityCutoffHz = 0 };
            for (int ms = 0; ms <= 200; ms++) e.UpdateWithVelocity(0.2, 1.0, ms, TicksPerSec);
            // A pause longer than MaxGapSec: no acceleration spike on return.
            e.UpdateWithVelocity(-0.4, -2.0, 5000, TicksPerSec);
            Assert.InRange(e.Acceleration, -0.5, 0.5);
            Assert.Equal(-0.4, e.Position, 3);
        }

        // Ticks at 0.1 ms, so a 2.5 ms report interval and a 1 ms pump tick
        // are both whole numbers.
        private const double FineTicksPerSec = 10000.0;

        [Fact]
        public void Update_IgnoresAStampThatHasNotAdvanced()
        {
            // The condition renderer resamples a HELD position on every pump
            // tick and stamps it with the time it ARRIVED, so the same stamp
            // comes back many times over. Re-feeding one must change nothing:
            // that no-op is what holds velocity between reports, and the
            // renderer relies on getting it for free.
            var e = new WheelMotionEstimator { VelocityCutoffHz = 0 };
            e.Update(0.0, 0, FineTicksPerSec);
            e.Update(0.0025, 25, FineTicksPerSec);
            double vel = e.Velocity, pos = e.Position;
            for (int i = 0; i < 5; i++) e.Update(0.0025, 25, FineTicksPerSec);
            Assert.Equal(vel, e.Velocity, 9);
            Assert.Equal(pos, e.Position, 9);
        }

        [Fact]
        public void Update_ArrivalStampsKeepVelocitySteadyWhereThePumpClockRipples()
        {
            // A wheel turning at a constant 1 range/s, reporting every 2.5 ms,
            // resampled by the 1 kHz pump. Stamping each resample with the pump
            // clock tells the tracker that a held position is a fresh one which
            // did not move, so velocity bleeds away between reports and returns
            // as a kick on each: the ripple a damper renders as motor whine.
            const double trueVel = 1.0;
            const double reportTicks = 25;   // 2.5 ms
            const long   pumpTicks   = 10;   // 1 ms

            var pump    = new WheelMotionEstimator { VelocityCutoffHz = 0 };
            var arrival = new WheelMotionEstimator { VelocityCutoffHz = 0 };
            double pumpMin = double.MaxValue, pumpMax = double.MinValue;
            double arrMin  = double.MaxValue, arrMax  = double.MinValue;

            for (long t = 0; t <= 4000; t += pumpTicks)          // 400 ms
            {
                long lastReport = (long)(Math.Floor(t / reportTicks) * reportTicks);
                double pos = trueVel * (lastReport / FineTicksPerSec);
                pump.Update(pos, t, FineTicksPerSec);
                arrival.Update(pos, lastReport, FineTicksPerSec);
                if (t < 2000) continue;                          // let both settle
                pumpMin = Math.Min(pumpMin, pump.Velocity);
                pumpMax = Math.Max(pumpMax, pump.Velocity);
                arrMin  = Math.Min(arrMin,  arrival.Velocity);
                arrMax  = Math.Max(arrMax,  arrival.Velocity);
            }

            // Both average out near the truth. Only the arrival-stamped one is
            // steady enough to hand a damper without filtering it first, which
            // is the point: the filtering costs lag, and a lagged damper damps
            // less.
            // Measured on this input: pump ripple 0.2662 on a true velocity of
            // 1.0 (13 % peak to peak, at the report rate), arrival ripple 0.
            Assert.InRange((arrMin + arrMax) / 2, 0.9, 1.1);
            Assert.True(arrMax - arrMin < (pumpMax - pumpMin) / 4,
                $"arrival ripple {arrMax - arrMin:F4} vs pump ripple {pumpMax - pumpMin:F4}");
        }
    }
}
