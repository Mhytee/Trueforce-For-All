// Turning published per-car dash data into something a Logitech rev strip can
// render. Pure math, no device access: the wire writes live in the LED channel,
// and these results are their input.
//
// Two jobs:
//
//   1. LEVEL. Our strip is driven by a single level (0..stripLength), so a car's
//      per-LED switch-on RPMs collapse into "how many steps are lit". This is
//      the whole point of consuming the dataset: the built-in ramp is linear
//      between an engage point and the redline and is identical for every car,
//      while a real dash is markedly nonlinear (across the published corpus the
//      switch-on band spans a median of about 15% of redline, and GT3 cars crawl
//      through the first lights then sprint through the last few).
//
//   2. LOOK. A car's dash fills in one of four directions, and that direction
//      plus its colors is what a LIGHTSYNC custom slot stores. Both are derived
//      here from the published thresholds and colors.
//
// Everything is deterministic and side-effect free so it unit-tests without a
// wheel, which matters because the slot-write protocol is still unverified on a
// G PRO.

using System;
using System.Collections.Generic;
using System.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>The four ways a rev strip fills. These are the only animation
    /// directions the hardware stores in a custom slot, and a real dash always
    /// maps onto one of them.</summary>
    public enum LightDirection
    {
        LeftToRight = 0,
        RightToLeft = 1,
        InsideOut   = 2,
        OutsideIn   = 3,
    }

    public static class LovelyLightMath
    {
        /// <summary>What is currently on the strip, captured for surfaces that
        /// only need to DRAW it (the dash mirror). A snapshot rather than a live
        /// read so a property poll never touches the wheel.</summary>
        public sealed class LightProfileSnapshot
        {
            public byte[] Rgb;
            public LightDirection Direction;
            public string Name;
        }

        /// <summary>Inside-out and outside-in drive the strip as mirrored pairs,
        /// so they have half as many progression steps as they have LEDs.</summary>
        public static bool IsMirrored(LightDirection layout) =>
            layout == LightDirection.InsideOut || layout == LightDirection.OutsideIn;

        /// <summary>Progression steps a strip of <paramref name="stripLength"/>
        /// LEDs has under <paramref name="layout"/>. An odd strip in a mirrored
        /// layout keeps its middle LED as its own final step.</summary>
        public static int StepCount(LightDirection layout, int stripLength)
        {
            if (stripLength <= 0) return 0;
            return IsMirrored(layout) ? (stripLength + 1) / 2 : stripLength;
        }

        // ---------------- Level ----------------

        /// <summary>How many steps are lit at <paramref name="rpm"/>, as a level
        /// in 0..<paramref name="maxLevel"/>.
        ///
        /// Counting is done over ACTIVE switch-on points only: a dead slot is a
        /// physical gap in the car's bar, not a step in its sequence, so counting
        /// it would stretch the ramp and light our strip early. Mirror pairs
        /// publish the same threshold twice and are deliberately left in, because
        /// a pair is genuinely two lit LEDs on the car and the fraction stays
        /// faithful either way.</summary>
        public static int LevelForRpm(LovelyGearRamp ramp, double rpm, int maxLevel)
        {
            if (ramp == null || maxLevel <= 0) return 0;
            // Faithful cars return their own points; coarse ones return a filled
            // ramp between their first light and their redline (see RenderThresholds).
            var active = ramp.RenderThresholds(maxLevel);
            if (active.Length == 0) return 0;

            int lit = 0;
            for (int i = 0; i < active.Length; i++)
                if (rpm >= active[i]) lit++;

            if (lit <= 0) return 0;
            if (lit >= active.Length) return maxLevel;

            int level = (int)Math.Round((double)maxLevel * lit / active.Length,
                                        MidpointRounding.AwayFromZero);
            return Math.Max(0, Math.Min(maxLevel, level));
        }

        /// <summary>True once the car is at or past this gear's own redline, which
        /// is where a dash starts flashing. Per-gear because the published
        /// redline genuinely moves between gears on the cars that carry it.</summary>
        public static bool IsAtRedline(LovelyGearRamp ramp, double rpm) =>
            ramp != null && ramp.RedlineRpm > 0 && rpm >= ramp.RedlineRpm;

        // ---------------- Layout ----------------

        /// <summary>Work out which of the four directions a car's dash fills,
        /// from the shape of its switch-on RPMs alone.
        ///
        /// The signal is which physical positions light EARLIEST. Ascending
        /// thresholds across the bar means left to right, descending means right
        /// to left, lowest-at-the-edges means outside in, and lowest-in-the-middle
        /// means inside out. Rather than pattern-matching those four cases, score
        /// two independent axes by correlation (position, and closeness to the
        /// centre) and take whichever is stronger, with the sign choosing between
        /// its pair. An asymmetric or unusual bar therefore still lands on its
        /// nearest honest match instead of being rejected.
        ///
        /// Falls back to left to right when there is too little to go on, which is
        /// both the commonest real layout and the least surprising default.</summary>
        public static LightDirection ClassifyLayout(int[] thresholds)
        {
            if (thresholds == null || thresholds.Length < 3) return LightDirection.LeftToRight;

            int n = thresholds.Length;
            var pos = new List<double>();
            var val = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (thresholds[i] <= 0) continue;                 // gap: no position to score
                pos.Add(n == 1 ? 0.5 : (double)i / (n - 1));      // 0..1 across the bar
                val.Add(thresholds[i]);
            }
            if (pos.Count < 3) return LightDirection.LeftToRight;

            // Centrality: 1 at the middle of the bar, 0 at either end.
            var centrality = pos.Select(p => 1.0 - 2.0 * Math.Abs(p - 0.5)).ToList();

            double rLinear = Correlation(pos, val);
            double rMirror = Correlation(centrality, val);

            // A dead flat bar correlates with nothing; treat as unclassifiable.
            if (double.IsNaN(rLinear) && double.IsNaN(rMirror)) return LightDirection.LeftToRight;
            if (double.IsNaN(rLinear)) rLinear = 0;
            if (double.IsNaN(rMirror)) rMirror = 0;

            if (Math.Abs(rMirror) > Math.Abs(rLinear))
            {
                // Thresholds RISE toward the centre: the edges light first.
                return rMirror > 0 ? LightDirection.OutsideIn : LightDirection.InsideOut;
            }
            return rLinear >= 0 ? LightDirection.LeftToRight : LightDirection.RightToLeft;
        }

        /// <summary>Pearson correlation. NaN when either side has no variance,
        /// which the caller reads as "this axis says nothing".</summary>
        private static double Correlation(IList<double> xs, IList<double> ys)
        {
            int n = Math.Min(xs.Count, ys.Count);
            if (n < 2) return double.NaN;

            double mx = xs.Take(n).Average(), my = ys.Take(n).Average();
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - mx, dy = ys[i] - my;
                sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
            }
            if (sxx <= double.Epsilon || syy <= double.Epsilon) return double.NaN;
            return sxy / Math.Sqrt(sxx * syy);
        }

        // ---------------- Color ----------------

        /// <summary>The car's colors in the order its dash lights them: one entry
        /// per distinct switch-on RPM, earliest first. Mirror pairs collapse to a
        /// single step here (they are one step of the sequence lighting two LEDs),
        /// which is exactly what a mirrored strip needs.</summary>
        public static LovelyColor[] ProgressionColors(LovelyCarProfile profile, LovelyGearRamp ramp)
        {
            if (profile == null || ramp == null) return new LovelyColor[0];
            var thresholds = ramp.Thresholds ?? new int[0];
            var colors = profile.LedColors ?? new LovelyColor[0];

            var steps = new SortedDictionary<int, LovelyColor>();
            for (int i = 0; i < thresholds.Length; i++)
            {
                if (thresholds[i] <= 0) continue;
                if (steps.ContainsKey(thresholds[i])) continue;   // first LED of a pair wins
                // Color and threshold arrays share an index. A car whose color
                // array is short (six files upstream break their own length rule)
                // simply contributes no color for that step.
                if (i >= colors.Length) continue;
                if (colors[i].IsGap) continue;
                steps[thresholds[i]] = colors[i];
            }
            return steps.Values.ToArray();
        }

        /// <summary>Stretch or squeeze <paramref name="source"/> onto exactly
        /// <paramref name="count"/> entries by nearest-neighbour sampling, so a
        /// 12-LED dash renders recognisably on a 10-LED strip.</summary>
        public static LovelyColor[] Resample(LovelyColor[] source, int count)
        {
            if (count <= 0) return new LovelyColor[0];
            if (source == null || source.Length == 0) return new LovelyColor[0];

            var outp = new LovelyColor[count];
            for (int i = 0; i < count; i++)
            {
                int src = (int)Math.Floor((double)i * source.Length / count);
                outp[i] = source[Math.Max(0, Math.Min(source.Length - 1, src))];
            }
            return outp;
        }

        /// <summary>Lay progression-ordered colors onto physical LED positions
        /// for <paramref name="layout"/>. Mirrored layouts write each step to both
        /// LEDs of its pair, so the strip stays symmetric by construction.</summary>
        public static LovelyColor[] PlaceOnStrip(LovelyColor[] steps, LightDirection layout, int stripLength)
        {
            var strip = new LovelyColor[Math.Max(0, stripLength)];
            if (strip.Length == 0 || steps == null || steps.Length == 0) return strip;

            int stepCount = StepCount(layout, stripLength);
            for (int i = 0; i < stripLength; i++)
            {
                int step = StepIndexForLed(i, layout, stripLength);
                strip[i] = steps[Math.Max(0, Math.Min(steps.Length - 1,
                             (int)Math.Floor((double)step * steps.Length / Math.Max(1, stepCount))))];
            }
            return strip;
        }

        /// <summary>Which progression step physical LED <paramref name="led"/>
        /// belongs to. Step 0 is always the first to light.</summary>
        public static int StepIndexForLed(int led, LightDirection layout, int stripLength)
        {
            if (stripLength <= 0) return 0;
            int last = stripLength - 1;
            switch (layout)
            {
                case LightDirection.RightToLeft:
                    return last - led;
                case LightDirection.OutsideIn:
                    // Edges first, so a LED's step is its distance from the nearer end.
                    return Math.Min(led, last - led);
                case LightDirection.InsideOut:
                    // Centre first: invert the outside-in distance.
                    return (StepCount(layout, stripLength) - 1) - Math.Min(led, last - led);
                default:
                    return led;
            }
        }

        /// <summary>The fallback palette for a car with no usable color data:
        /// the conventional green to yellow to red climb, laid out for this
        /// layout. Red is capped at the final two LEDs; a longer red band reads as
        /// "already at the limit" for most of the useful range.
        ///
        /// Mirrored layouts band by PAIR rather than by LED, because a strip that
        /// advances two LEDs at a time cannot show an odd-sized band without
        /// splitting a pair across two colors.</summary>
        public static LovelyColor[] DefaultRampColors(LightDirection layout, int stripLength)
        {
            var strip = new LovelyColor[Math.Max(0, stripLength)];
            if (strip.Length == 0) return strip;

            var green  = LovelyColor.Rgb(0x00, 0xFF, 0x00);
            var yellow = LovelyColor.Rgb(0xFF, 0xFF, 0x00);
            var red    = LovelyColor.Rgb(0xFF, 0x00, 0x00);

            int steps = StepCount(layout, stripLength);
            int redSteps, greenSteps;
            if (IsMirrored(layout))
            {
                // Five pairs on a ten-LED strip give green, green, yellow, yellow,
                // red: two LEDs of red, and no color ever split across a pair.
                redSteps   = steps >= 3 ? 1 : 0;
                greenSteps = (int)Math.Ceiling((steps - redSteps) / 2.0);
            }
            else
            {
                // Ten LEDs give five green, three yellow, two red.
                redSteps   = steps >= 5 ? 2 : (steps >= 3 ? 1 : 0);
                greenSteps = (int)Math.Ceiling(steps / 2.0);
                if (greenSteps + redSteps > steps) greenSteps = Math.Max(0, steps - redSteps);
            }
            int yellowSteps = Math.Max(0, steps - greenSteps - redSteps);

            for (int i = 0; i < stripLength; i++)
            {
                int step = StepIndexForLed(i, layout, stripLength);
                strip[i] = step < greenSteps ? green
                         : step < greenSteps + yellowSteps ? yellow
                         : red;
            }
            return strip;
        }

        /// <summary>Compose the full slot payload for a car: the direction its
        /// dash fills and the colors to store, falling back to the conventional
        /// ramp when the car publishes no usable colors. This is the object the
        /// slot writer will hand to the wheel once the write path is verified on a
        /// G PRO.</summary>
        public static LightProfile BuildProfile(LovelyCarProfile car, LovelyGearRamp ramp, int stripLength)
        {
            var layout = ClassifyLayout(ramp?.Thresholds);
            var steps  = ProgressionColors(car, ramp);
            var colors = steps.Length > 0
                ? PlaceOnStrip(Resample(steps, StepCount(layout, stripLength)), layout, stripLength)
                : DefaultRampColors(layout, stripLength);

            return new LightProfile
            {
                Layout            = layout,
                Colors            = colors,
                UsedCarColors     = steps.Length > 0,
                BlinkIntervalMs   = car?.RedlineBlinkIntervalMs ?? 0,
                RedlineRpm        = ramp?.RedlineRpm ?? 0,
            };
        }
    }

    /// <summary>A renderable lighting profile: what to store in a slot and when
    /// to blink. Device-agnostic on purpose, so the same object serves a G PRO
    /// slot write, a preview, and a saved user profile.</summary>
    public sealed class LightProfile
    {
        public LightDirection Layout { get; set; }
        public LovelyColor[] Colors { get; set; } = new LovelyColor[0];

        /// <summary>False when the colors are our conventional fallback rather
        /// than the car's own, so the UI can say which the user is looking at.</summary>
        public bool UsedCarColors { get; set; }

        public int BlinkIntervalMs { get; set; }
        public int RedlineRpm { get; set; }

        /// <summary>Wire value for the slot-config direction byte. The device
        /// numbers the directions 1..4 in its own order, which is not our enum
        /// order, so the mapping is written out rather than cast. Documented in
        /// docs/lightsync-slot-protocol.md; RS50-verified, G PRO unverified.</summary>
        public byte DirectionWire
        {
            get
            {
                switch (Layout)
                {
                    case LightDirection.InsideOut:   return 1;
                    case LightDirection.OutsideIn:   return 2;
                    case LightDirection.LeftToRight: return 3;
                    case LightDirection.RightToLeft: return 4;
                    default:                      return 3;
                }
            }
        }
    }
}
