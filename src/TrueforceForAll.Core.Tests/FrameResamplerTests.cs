using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase-1 resampler: live-edge prediction for slow sources (FM8), buffered
    // interpolation for fast ones (AC), sample-and-hold discretes, keepalive
    // immunity, pause pass-through, dropout clamping. All on a microsecond
    // virtual clock (ticksPerSecond = 1e6) so every assertion is exact-ish.
    public class FrameResamplerTests
    {
        private const long Tps = 1_000_000;   // µs tick base
        private const long T0  = 10_000_000;  // arbitrary start

        private static TelemetryFrame Frame(long tUs, double speed, string gear = "3",
                                            double maxRpm = 8000, bool quads = false, double quadVal = 0)
        {
            var f = new TelemetryFrame
            {
                CapturedAtTicks = tUs, SpeedKmh = speed, Rpms = 5000, MaxRpm = maxRpm,
                Throttle01 = 0.5, Gear = gear,
                HasTireQuads = quads,
            };
            if (quads)
            {
                f.TireCombinedSlip = TireQuad.Of(quadVal, quadVal, quadVal, quadVal);
                f.WheelRotRadS     = TireQuad.Of(50, 50, 50, 50);
            }
            return f;
        }

        // Feed a speed ramp (speedPerSec) at the given rate for n frames;
        // returns the timestamp of the last frame.
        private static long FeedRamp(FrameResampler r, double hz, int n, double speedPerSec = 10.0)
        {
            long t = T0;
            long stepUs = (long)(1_000_000 / hz);
            for (int i = 0; i < n; i++)
            {
                t = T0 + i * stepUs;
                r.Ingest(Frame(t, speed: (t - T0) / 1e6 * speedPerSec + 50));
            }
            return t;
        }

        [Fact]
        public void SlowSource_StaysLiveEdge_AndPredictsBetweenFrames()
        {
            var r = new FrameResampler(Tps);
            long last = FeedRamp(r, hz: 60, n: 60);   // 1s of 60 Hz ramp
            Assert.True(r.IsLiveEdge);
            Assert.InRange(r.MeasuredHz, 55, 65);

            // Sample 8 ms past the last frame (mid-gap at 60 Hz): the
            // prediction should land on the ramp, not hold the stale value.
            long sampleAt = last + 8_000;
            Assert.True(r.TrySample(sampleAt, out var s));
            double trueSpeed = (sampleAt - T0) / 1e6 * 10.0 + 50;
            double heldSpeed = (last - T0) / 1e6 * 10.0 + 50;
            Assert.True(Math.Abs(s.SpeedKmh - trueSpeed) < Math.Abs(heldSpeed - trueSpeed),
                $"prediction {s.SpeedKmh:F3} not better than hold {heldSpeed:F3} (true {trueSpeed:F3})");
            Assert.True(Math.Abs(s.SpeedKmh - trueSpeed) < 0.05, $"prediction off: {s.SpeedKmh:F3} vs {trueSpeed:F3}");
        }

        [Fact]
        public void FastSource_SwitchesToBuffered_AndInterpolates()
        {
            var r = new FrameResampler(Tps);
            long last = FeedRamp(r, hz: 333, n: 200);
            Assert.False(r.IsLiveEdge);

            Assert.True(r.TrySample(last + 1_000, out var s));
            // Buffered mode samples ~half an interval behind the newest frame;
            // the value must sit ON the ramp for some time <= newest.
            double impliedTimeSec = (s.SpeedKmh - 50) / 10.0;
            double newestSec = (last - T0) / 1e6;
            Assert.InRange(impliedTimeSec, newestSec - 0.010, newestSec + 0.0001);
        }

        [Fact]
        public void ModeSwitch_HasHysteresis()
        {
            var r = new FrameResampler(Tps);
            FeedRamp(r, hz: 160, n: 100);   // between the 150/170 thresholds
            Assert.True(r.IsLiveEdge, "160 Hz from live-edge start must not flip (needs >= 170)");

            var r2 = new FrameResampler(Tps);
            FeedRamp(r2, hz: 200, n: 100);
            Assert.False(r2.IsLiveEdge, "200 Hz must run buffered");
        }

        [Fact]
        public void DiscreteFields_SampleAndHold_NeverEarly()
        {
            // Buffered mode; gear flips on the newest frame. The sample point
            // sits behind newest, so the OLD gear must be reported — an edge
            // must never arrive before its own timestamp.
            var r = new FrameResampler(Tps);
            long t = T0;
            for (int i = 0; i < 200; i++)
            {
                t = T0 + i * 3_000;                     // 333 Hz
                r.Ingest(Frame(t, 100, gear: i == 199 ? "4" : "3"));
            }
            Assert.True(r.TrySample(t + 500, out var s));
            Assert.Equal("3", s.Gear);
        }

        [Fact]
        public void KeepaliveInterleave_DoesNotDipSamples()
        {
            // 100 Hz real frames with an all-zero keepalive after each one
            // (FH6's pattern). Samples between frames must track the ramp and
            // never collapse toward zero.
            var r = new FrameResampler(Tps);
            long t = T0;
            for (int i = 0; i < 100; i++)
            {
                t = T0 + i * 10_000;
                r.Ingest(Frame(t, speed: 100 + i * 0.1));
                r.Ingest(Frame(t + 5_000, speed: 0, maxRpm: 0));   // keepalive mid-gap
            }
            for (long probe = t - 30_000; probe <= t + 5_000; probe += 2_500)
            {
                Assert.True(r.TrySample(probe, out var s));
                Assert.True(s.SpeedKmh > 90, $"sample dipped to {s.SpeedKmh:F1} at {probe}");
            }
        }

        [Fact]
        public void SustainedKeepalives_BecomeThePause_Output()
        {
            var r = new FrameResampler(Tps);
            long last = FeedRamp(r, hz: 60, n: 30);

            // Physics stops; keepalives continue for 0.5 s (> the 0.35 s hold).
            long ka = last;
            for (int i = 1; i <= 30; i++)
            {
                ka = last + i * 16_667;
                r.Ingest(Frame(ka, speed: 0, maxRpm: 0));
            }
            Assert.True(r.TrySample(ka + 1_000, out var s));
            Assert.Equal(0.0, s.SpeedKmh, 6);
            Assert.Equal(0.0, s.MaxRpm, 6);
        }

        [Fact]
        public void Dropout_LiveEdge_PredictionIsClamped()
        {
            var r = new FrameResampler(Tps);
            long last = FeedRamp(r, hz: 60, n: 60, speedPerSec: 50.0);   // fast ramp

            // 200 ms of silence (no keepalives — a stall). The prediction may
            // extrapolate at most the filter's horizon cap (25 ms worth).
            Assert.True(r.TrySample(last + 200_000, out var s));
            double lastSpeed = (last - T0) / 1e6 * 50.0 + 50;
            Assert.True(s.SpeedKmh <= lastSpeed + 50.0 * 0.026,
                $"runaway extrapolation: {s.SpeedKmh:F1} from {lastSpeed:F1}");
        }

        [Fact]
        public void OutOfOrderFrame_IsDropped()
        {
            var r = new FrameResampler(Tps);
            r.Ingest(Frame(T0, 100));
            r.Ingest(Frame(T0 + 10_000, 110));
            r.Ingest(Frame(T0 + 5_000, 999));    // stale straggler
            Assert.True(r.TrySample(T0 + 10_000, out var s));
            Assert.True(s.SpeedKmh < 200, $"stale frame leaked: {s.SpeedKmh}");
        }

        [Fact]
        public void Quads_InterpolateInBufferedMode()
        {
            var r = new FrameResampler(Tps);
            long t = T0;
            for (int i = 0; i < 200; i++)
            {
                t = T0 + i * 3_000;   // 333 Hz, slip ramps 0 -> 1.99
                r.Ingest(Frame(t, 100, quads: true, quadVal: i * 0.01));
            }
            Assert.True(r.TrySample(t + 500, out var s));
            Assert.True(s.HasTireQuads);
            // Sampled behind newest: strictly between the ramp's recent values.
            Assert.InRange(s.TireCombinedSlip.FL, 1.90, 1.99);
        }
    }
}
