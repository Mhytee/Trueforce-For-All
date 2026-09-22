// A level walking up the strip and back down, on repeat, for the rim LEDs
// with nothing else to draw.
//
// Pure arithmetic on a clock, deliberately: nothing is stored between calls, so
// the pattern is a function of the time and the caller can ask for it as often
// or as rarely as it likes without the animation drifting. Two callers at
// different rates (the telemetry path and the DataUpdate tick) stay in phase
// with each other for free.
//
// Computed in whole levels with integer arithmetic rather than as a 0..1
// triangle rounded downstream. The two draw the same picture, to within a
// rounding LSB, so this is not the faster or the smoother of them: it just
// makes the structure explicit (one period is exactly 2 * steps positions) and
// keeps the boundaries exact, which is what the tests pin.
//
// Worth knowing either way: the two ends are visited ONCE per period and every
// level between them twice, so full and dark each hold for half as long as the
// levels in between. That is simply what a bar reversing at its ends does, not
// an artefact of how it is computed. Making the sweep pause at the ends instead
// means adding a position at each one, which is a deliberate change of feel and
// not a fix.

using System;

namespace TrueforceForAll.Core
{
    public static class LedSweep
    {
        /// <summary>One full pass up and back down, in milliseconds.</summary>
        public const int DefaultPeriodMs = 2500;
        /// <summary>Fast end of the slider. Below this the strip is a blur and,
        /// on a ten-step wheel, each level would get under 30 ms, which is
        /// inside the channel's own push-rate limit and so would simply be
        /// dropped.</summary>
        public const int MinPeriodMs = 600;
        /// <summary>Slow end. Past about six seconds a sweep stops reading as
        /// motion and starts reading as a fault.</summary>
        public const int MaxPeriodMs = 6000;

        public static int ClampPeriodMs(int periodMs) =>
            periodMs < MinPeriodMs ? MinPeriodMs
          : periodMs > MaxPeriodMs ? MaxPeriodMs
          : periodMs;

        /// <summary>Where the sweep stands at <paramref name="nowMs"/>, in whole
        /// levels from 0 to <paramref name="steps"/> inclusive.
        ///
        /// One period visits 2 * steps positions: 0 up to steps, then back down
        /// to 1. The top and bottom are each visited once per period rather than
        /// twice, so the turnarounds do not stall.</summary>
        public static int Level(long nowMs, int periodMs, int steps)
        {
            if (steps <= 0) return 0;
            periodMs = ClampPeriodMs(periodMs);

            // Modulo twice: a caller's clock may be negative (Environment.TickCount
            // wraps through the sign bit), and C# leaves the sign on the remainder.
            long phase = ((nowMs % periodMs) + periodMs) % periodMs;

            int span = 2 * steps;
            int pos = (int)(phase * span / periodMs);
            if (pos >= span) pos = span - 1;   // guards the boundary, not reachable
            return pos <= steps ? pos : span - pos;
        }
    }
}
