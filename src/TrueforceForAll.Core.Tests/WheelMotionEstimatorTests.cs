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
    }
}
