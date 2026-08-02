using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;
using Xunit.Abstractions;

namespace TrueforceForAll.Core.Tests
{
    // Phase 2 proof ("CTM invariants; event-count parity"): run the composer,
    // event deriver and the full Mode B force pipeline over the REAL recorded
    // fixtures and assert the properties that must hold on real driving data,
    // not just synthetic frames:
    //   - rollups finite, non-negative, balance = rear - front
    //   - event streams well-formed (Start/End alternate, no chatter)
    //   - synthesized force bounded, finite, no full-scale sign slams
    public class CtmRealDataInvariantsTests
    {
        private readonly ITestOutputHelper _out;
        public CtmRealDataInvariantsTests(ITestOutputHelper output) => _out = output;

        public static IEnumerable<object[]> FixtureFiles()
        {
            // Fixtures live in the test PROJECT dir (checked in), not the
            // output dir — walk up like FixtureParityTests does.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "Fixtures");
                if (Directory.Exists(candidate))
                {
                    foreach (var f in Directory.GetFiles(candidate, "*.csv"))
                        if (!f.EndsWith(".golden.csv", StringComparison.OrdinalIgnoreCase))
                            yield return new object[] { f };
                    yield break;
                }
                dir = dir.Parent;
            }
        }

        [Theory]
        [MemberData(nameof(FixtureFiles))]
        public void RealCapture_CtmAndModeB_HoldInvariants(string path)
        {
            var cap = CsvFrameReader.ReadFile(path);
            Assert.True(cap.Rows.Count > 100, "fixture unexpectedly small");

            var deriver = new EventDeriver();
            var sat = new SatForceModel { SatGain = 1.0, RiseGamma = 0.5, DropFloor = 0.20 };
            var comp = new SoftKneeCompressor { Gain = 1.0, Threshold01 = 0.70, Ceiling01 = 0.95 };

            // Mode B state mirrors the plugin's EMAs (40 ms at the capture rate).
            double slipEma = 0, gripEma = 0, gripRearEma = 0, overEma = 0;
            double prevTUs = cap.Rows[0].TUs;

            var starts = new Dictionary<FrameEvents, int>();
            var ends = new Dictionary<FrameEvents, int>();
            bool frontOpen = false, rearOpen = false;

            int quadFrames = 0;
            double prevForce = 0;
            int slamCount = 0;          // full-scale sign flips frame-to-frame
            int forceFrames = 0;
            double maxAbsForce = 0;
            double durationSec = (cap.Rows[cap.Rows.Count - 1].TUs - cap.Rows[0].TUs) / 1e6;

            foreach (var row in cap.Rows)
            {
                var f = row.Frame;
                CtmComposer.Compose(ref f);
                f.Events = deriver.Derive(in f);

                if (f.FrontGrip01.HasValue)
                {
                    quadFrames++;
                    Assert.True(IsFiniteNonNeg(f.FrontGrip01.Value), $"FrontGrip01 bad: {f.FrontGrip01}");
                    Assert.True(IsFiniteNonNeg(f.RearGrip01.Value), $"RearGrip01 bad: {f.RearGrip01}");
                    Assert.Equal(f.RearGrip01.Value - f.FrontGrip01.Value, f.GripBalance.Value, 9);
                }

                CountEdges(f.Events, starts, ends);
                // Start/End must alternate per pair, never double-fire.
                CheckAlternation(f.Events, FrameEvents.FrontBreakawayStart, FrameEvents.FrontBreakawayEnd, ref frontOpen);
                CheckAlternation(f.Events, FrameEvents.RearBreakawayStart, FrameEvents.RearBreakawayEnd, ref rearOpen);

                // ---- Mode B pipeline on real data ----
                if (!f.FrontGrip01.HasValue || !f.FrontSlipAngleRad.HasValue) continue;
                double dtMs = Math.Min(Math.Max((row.TUs - prevTUs) / 1000.0, 0.1), 50.0);
                prevTUs = row.TUs;
                double alpha = 1.0 - Math.Exp(-dtMs / 40.0);
                slipEma     += (f.FrontSlipAngleRad.Value - slipEma) * alpha;
                gripEma     += (f.FrontGrip01.Value - gripEma) * alpha;
                gripRearEma += (f.RearGrip01.Value - gripRearEma) * alpha;

                double dir = Math.Min(Math.Max(slipEma / 0.03, -1.0), 1.0);
                double u = gripEma, uR = gripRearEma;
                double rawOver = Math.Max(0.0, uR - u);
                double overTau = 40.0 * (rawOver > overEma ? 1.0 : 4.0);
                overEma += (rawOver - overEma) * (1.0 - Math.Exp(-dtMs / overTau));

                double load01 = 1.0 - 0.35 * ((f.AccelerationSurge ?? 0) / 9.81);
                double satF = sat.Force01(u, 1.0, load01, f.SpeedKmh) * dir;
                double trail = Math.Min(f.SpeedKmh / 60.0, 1.0);
                double force = ModeBComposer.Compose(
                    satF, Math.Abs(f.AccelerationSway ?? 0) / 9.81, 0.6);
                force = comp.Apply(force);

                Assert.False(double.IsNaN(force) || double.IsInfinity(force), "force NaN/Inf");
                Assert.InRange(force, -1.0, 1.0);
                maxAbsForce = Math.Max(maxAbsForce, Math.Abs(force));
                forceFrames++;
                if (Math.Sign(force) != Math.Sign(prevForce)
                    && Math.Abs(force) > 0.30 && Math.Abs(prevForce) > 0.30)
                    slamCount++;
                prevForce = force;
            }

            Assert.True(quadFrames > cap.Rows.Count / 2, "most frames should carry quads");

            // Chatter guard: hysteresis must keep breakaway event rates in
            // driving territory (a slide episode is seconds, not frames).
            double frontStartsPerSec = Get(starts, FrameEvents.FrontBreakawayStart) / Math.Max(1.0, durationSec);
            double rearStartsPerSec  = Get(starts, FrameEvents.RearBreakawayStart) / Math.Max(1.0, durationSec);
            Assert.True(frontStartsPerSec < 2.0, $"front breakaway chatter: {frontStartsPerSec:F2}/s");
            Assert.True(rearStartsPerSec < 2.0, $"rear breakaway chatter: {rearStartsPerSec:F2}/s");

            // Start/End counts may differ by at most 1 (episode open at EOF).
            foreach (var pair in new[] {
                (FrameEvents.FrontBreakawayStart, FrameEvents.FrontBreakawayEnd),
                (FrameEvents.RearBreakawayStart, FrameEvents.RearBreakawayEnd),
                (FrameEvents.LockupStart, FrameEvents.LockupEnd),
                (FrameEvents.WheelspinStart, FrameEvents.WheelspinEnd),
                (FrameEvents.RumbleStripStart, FrameEvents.RumbleStripEnd) })
            {
                int s = Get(starts, pair.Item1), e = Get(ends, pair.Item2);
                Assert.True(Math.Abs(s - e) <= 1, $"{pair.Item1}: {s} starts vs {e} ends");
            }

            // Full-scale sign slams frame-to-frame are the oscillation seed;
            // real driving data through the smoothed pipeline should have none.
            double slamPct = 100.0 * slamCount / Math.Max(1, forceFrames);
            Assert.True(slamPct < 0.5, $"sign-slam rate {slamPct:F2}% (osc seed)");

            _out.WriteLine($"=== {Path.GetFileName(path)} ({durationSec:F1}s, {cap.Rows.Count} frames, {quadFrames} with quads) ===");
            _out.WriteLine($"events: " + string.Join(", ",
                starts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
            _out.WriteLine($"gearChanges={Get(starts, FrameEvents.GearChanged)}");
            _out.WriteLine($"modeB force: frames={forceFrames} maxAbs={maxAbsForce:F3} slams={slamCount} ({slamPct:F3}%)");
        }

        private static bool IsFiniteNonNeg(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

        private static void CountEdges(FrameEvents ev, Dictionary<FrameEvents, int> starts, Dictionary<FrameEvents, int> ends)
        {
            foreach (FrameEvents flag in Enum.GetValues(typeof(FrameEvents)))
            {
                if (flag == FrameEvents.None || (ev & flag) == 0) continue;
                var name = flag.ToString();
                if (name.EndsWith("End", StringComparison.Ordinal))
                    ends[flag] = (ends.TryGetValue(flag, out var e) ? e : 0) + 1;
                else
                    starts[flag] = (starts.TryGetValue(flag, out var s) ? s : 0) + 1;
            }
        }

        private static void CheckAlternation(FrameEvents ev, FrameEvents start, FrameEvents end, ref bool open)
        {
            if ((ev & start) != 0)
            {
                Assert.False(open, $"{start} fired while already open");
                open = true;
            }
            if ((ev & end) != 0)
            {
                Assert.True(open, $"{end} fired while closed");
                open = false;
            }
        }

        private static int Get(Dictionary<FrameEvents, int> d, FrameEvents k) => d.TryGetValue(k, out var v) ? v : 0;
    }
}
