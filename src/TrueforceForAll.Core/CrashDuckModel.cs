// Crash duck — Mode B impact protection (eleventh wheel test: "I hit a
// wall and my wheel buzzed tf out").
//
// A collision slams every synthesis input to full scale at once: slip
// angle thrashes through zero, the grip metric pegs, suspension slams.
// The composed force whipsaws at saturation — a violent buzz with real
// hardware-yank potential. No center flat-spot or smoothing fixes this,
// because during an impact nothing is small; the correct behavior (and
// what commercial FFB stacks do) is to go SOFT on impact and hand the
// force back smoothly.
//
// Detection is pure magnitude: horizontal chassis acceleration beyond
// anything driving can produce. Cornering peaks ~1.5-2 g, braking ~1.3 g,
// combined trail-braking ~2.5 g; a wall hit reads 5-20 g for a few
// frames. Vertical is deliberately excluded so kerbs and crests never
// trigger it.
//
// Shape: instant full duck on trigger (the impact is already happening —
// any attack time is too slow), exponential release so the wheel firms
// back up over ~half a second instead of snapping to full force while
// the car is still bouncing off the wall.
//
// Pure model: no clocks, no telemetry types. Tick from the FFB thread
// with the cached g magnitude; multiply the returned factor into the
// synthesized force.

using System;

namespace TrueforceForAll.Core
{
    public sealed class CrashDuckModel
    {
        /// <summary>Horizontal acceleration (g) that counts as an impact.
        /// Must sit above combined braking+cornering (~2.5 g) with margin.</summary>
        public double TriggerG { get; set; } = 3.5;

        /// <summary>Release time constant (ms): how fast force returns
        /// after the impact reading clears.</summary>
        public double ReleaseTauMs { get; set; } = 600.0;

        /// <summary>How much force the duck removes at full level.
        /// 0.85 = the wheel keeps 15% during the impact — still alive,
        /// no longer violent.</summary>
        public double DuckDepth { get; set; } = 0.85;

        private double _level;   // 1 = just impacted, decays toward 0

        /// <summary>Current duck level, 0 (inactive) .. 1 (impact now).</summary>
        public double Level => _level;

        /// <summary>Advance one tick and return the force multiplier in
        /// [1-DuckDepth, 1]. Instant attack, exponential release.</summary>
        public double Tick(double gMag, double dtMs)
        {
            if (double.IsNaN(gMag)) gMag = 0.0;
            if (dtMs < 0.0) dtMs = 0.0; else if (dtMs > 100.0) dtMs = 100.0;

            if (gMag >= TriggerG) _level = 1.0;
            else if (_level > 0.0)
            {
                _level *= Math.Exp(-dtMs / Math.Max(50.0, ReleaseTauMs));
                if (_level < 0.001) _level = 0.0;
            }

            double depth = Math.Min(Math.Max(DuckDepth, 0.0), 1.0);
            return 1.0 - depth * _level;
        }

        public void Reset() => _level = 0.0;
    }
}
