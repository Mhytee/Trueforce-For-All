// The idle sweep's shape.
//
// It has no state, so every property here is a property of one function of the
// clock. That is the point of testing it: the sweep is driven from two callers
// at two different rates (the telemetry path and the DataUpdate tick), and the
// only thing keeping them in phase is that both are asking the same question
// about the same time.

using System.Collections.Generic;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LedSweepTests
    {
        private const int Period = 2000;

        [Fact]
        public void StaysInsideTheStrip()
        {
            for (int steps = 1; steps <= 10; steps++)
                for (long t = 0; t < Period * 3; t += 7)
                {
                    int lvl = LedSweep.Level(t, Period, steps);
                    Assert.InRange(lvl, 0, steps);
                }
        }

        [Fact]
        public void ReachesBothEnds()
        {
            bool dark = false, full = false;
            for (long t = 0; t < Period; t++)
            {
                int lvl = LedSweep.Level(t, Period, 10);
                if (lvl == 0) dark = true;
                if (lvl == 10) full = true;
            }
            Assert.True(dark, "the sweep never reaches a dark strip");
            Assert.True(full, "the sweep never fills the strip");
        }

        [Fact]
        public void NeverJumpsMoreThanOneLed()
        {
            // What separates a sweep from a flicker. Sampled far finer than the
            // wheel is written, so any skipped level shows up.
            int prev = LedSweep.Level(0, Period, 10);
            for (long t = 1; t < Period * 2; t++)
            {
                int lvl = LedSweep.Level(t, Period, 10);
                Assert.InRange(lvl - prev, -1, 1);
                prev = lvl;
            }
        }

        [Fact]
        public void GivesEveryPositionAnEqualSliceOfThePeriod()
        {
            // One period is 2 * steps equally timed positions. The ends are
            // visited once each and every level between them twice, so full and
            // dark hold for half as long as the middle: that is what reversing
            // at the ends means, and it is pinned here so a later change of feel
            // (pausing at the ends) has to be deliberate rather than a drift.
            var dwell = new Dictionary<int, int>();
            for (long t = 0; t < Period; t++)
            {
                int lvl = LedSweep.Level(t, Period, 10);
                dwell.TryGetValue(lvl, out int n);
                dwell[lvl] = n + 1;
            }
            Assert.Equal(11, dwell.Count);          // 0..10 inclusive
            // Ends are visited once per period, the levels between them twice.
            Assert.Equal(Period / 20, dwell[0]);
            Assert.Equal(Period / 20, dwell[10]);
            for (int lvl = 1; lvl < 10; lvl++)
                Assert.Equal(Period / 10, dwell[lvl]);
        }

        [Fact]
        public void RepeatsOnThePeriod()
        {
            for (long t = 0; t < Period; t += 13)
                Assert.Equal(LedSweep.Level(t, Period, 10),
                             LedSweep.Level(t + Period * 5, Period, 10));
        }

        [Fact]
        public void SurvivesAClockThatHasGoneNegative()
        {
            // Environment.TickCount wraps through the sign bit, and C# leaves
            // the sign on a remainder. Without the second modulo this returned
            // a negative level, which the caller would clamp to a dark strip:
            // a sweep that stops for 24 days.
            for (long t = -Period * 3; t < 0; t += 11)
                Assert.InRange(LedSweep.Level(t, Period, 10), 0, 10);
        }

        [Fact]
        public void ClampsAnAbsurdPeriodRatherThanDividingByIt()
        {
            Assert.Equal(LedSweep.MinPeriodMs, LedSweep.ClampPeriodMs(0));
            Assert.Equal(LedSweep.MinPeriodMs, LedSweep.ClampPeriodMs(-5));
            Assert.Equal(LedSweep.MaxPeriodMs, LedSweep.ClampPeriodMs(int.MaxValue));
            for (long t = 0; t < 5000; t += 3)
                Assert.InRange(LedSweep.Level(t, 0, 10), 0, 10);
        }

        [Fact]
        public void ScalesToAG923sFiveStepBar()
        {
            var seen = new HashSet<int>();
            for (long t = 0; t < Period; t++) seen.Add(LedSweep.Level(t, Period, 5));
            Assert.Equal(6, seen.Count);   // 0..5 inclusive, nothing above
        }

        [Fact]
        public void AStripWithNoStepsIsDarkRatherThanADivideByZero()
        {
            Assert.Equal(0, LedSweep.Level(1234, Period, 0));
            Assert.Equal(0, LedSweep.Level(1234, Period, -3));
        }
    }
}
