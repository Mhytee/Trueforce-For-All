// How full the rev-light strip is at a given rpm.
//
// The strip is dark below a start point and full at the redline, rising evenly
// between the two. The start is the one thing the driver chooses, as a percent of
// the redline: 85 keeps every light for the last stretch before the shift, the
// way a shift light does, and 20 spreads them over nearly the whole rev range,
// the way a rev counter does. The top is always the redline, because that is
// where the flash and the rev limiter buzz fire, and the three are one "shift
// now" signal.
//
// One function for every caller. The wheel's own bar, the dash mirror and the
// OLED's rev readout each used to carry a copy of this arithmetic with its own
// constant (0.85 in one place, 0.87 in two others), so the same car could read
// differently on the wheel and on the screen.

namespace TrueforceForAll.Core
{
    public static class RevBarFill
    {
        /// <summary>Where the strip starts when the driver has chosen nothing:
        /// the last 15 percent of the revs, the window the bar has always used.</summary>
        public const int DefaultStartPct = 85;

        /// <summary>Lowest start the slider offers. A car at idle sits somewhere
        /// around 10 to 20 percent of its redline, and a start below that would
        /// keep a light or two burning in the pits (owner, 2026-11-15).</summary>
        public const int MinStartPct = 20;

        /// <summary>Highest start. Past this the whole strip fills in a sliver
        /// of revs that telemetry jitter alone can cross.</summary>
        public const int MaxStartPct = 95;

        public static int ClampStartPct(int startPct) =>
            startPct < MinStartPct ? MinStartPct
          : startPct > MaxStartPct ? MaxStartPct
          : startPct;

        /// <summary>The rpm where the strip starts to fill, for a given redline.</summary>
        public static double StartRpm(double redlineRpm, int startPct) =>
            redlineRpm * ClampStartPct(startPct) / 100.0;

        /// <summary>0..1 fill for the strip: 0 at or below the start point, 1 at
        /// or above <paramref name="redlineRpm"/>, linear in rpm between. A
        /// missing or nonsense redline gives a dark bar rather than a full one,
        /// since "full" would read as a shift cue.</summary>
        public static double Fraction(double rpm, double redlineRpm, int startPct)
        {
            if (!(redlineRpm > 1.0) || double.IsNaN(rpm)) return 0.0;
            double start = StartRpm(redlineRpm, startPct);
            if (rpm <= start) return 0.0;
            double bar = (rpm - start) / (redlineRpm - start);
            return bar > 1.0 ? 1.0 : bar;
        }
    }
}
