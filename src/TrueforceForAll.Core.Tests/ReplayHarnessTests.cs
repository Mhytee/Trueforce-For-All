using System;
using System.IO;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase 0d: the replay harness itself. Round-trips the golden capture
    // format, proves the virtual-time replay source behaves like a live one,
    // and runs a synthetic drive end to end through the production pipeline
    // (enrich → effects → ducking → mixer → sink) asserting on output energy,
    // ducking envelopes and the no-clip invariant. Real recorded fixtures
    // freeze their golden baselines on top of this once the rig session
    // happens; these tests are the harness's own correctness floor.
    public class ReplayHarnessTests
    {
        // ---------- fixture synthesis ----------

        // A steady-state driving frame builder: 100 Hz rows, 6-cyl car at
        // constant RPM/speed, mutated per test.
        private static CsvFrameReader.Capture SyntheticCapture(
            int rows, Func<int, TelemetryFrame> frameAt, int frameGapUs = 10_000)
        {
            var cap = new CsvFrameReader.Capture { SchemaVersion = 2, SourceName = "synthetic", GameName = "test" };
            for (int i = 0; i < rows; i++)
            {
                cap.Rows.Add(new ReplayRow
                {
                    TUs = 1_000_000L + (long)i * frameGapUs,
                    SessionActive = true,
                    GameFfb = null,
                    Frame = frameAt(i),
                });
            }
            return cap;
        }

        private static TelemetryFrame DrivingFrame(double rpm = 3000, double speedKmh = 100,
                                                   string gear = "3", double slip = 0.0)
        {
            return new TelemetryFrame
            {
                Rpms = rpm, MaxRpm = 8000, Throttle01 = 0.5,
                SpeedKmh = speedKmh, Gear = gear,
                WheelSlip = slip, NumCylinders = 6,
                AccelerationSway = 0.0, AccelerationHeave = 0.0, AccelerationSurge = 0.0,
                YawRateDegPerSec = 0.0,
            };
        }

        // ---------- CSV round-trip ----------

        [Fact]
        public void Capture_RoundTrips_ThroughWriterAndReader()
        {
            string path = Path.Combine(Path.GetTempPath(), "tfcap_rt_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                var f = DrivingFrame(rpm: 4500, speedKmh: 123.4, gear: "4", slip: 0.62);
                f.HasTireQuads = true;
                f.TireSlipRatio    = TireQuad.Of(0.10, 0.11, 0.12, 0.13);
                f.TireSlipAngleRad = TireQuad.Of(-0.05, -0.06, 0.07, 0.08);
                f.TireCombinedSlip = TireQuad.Of(0.60, 0.61, 0.62, 0.63);
                f.SuspTravelM      = TireQuad.Of(0.02, 0.03, 0.04, 0.05);
                f.SurfaceRumbleQ   = TireQuad.Of(0.20, 0.21, 0.22, 0.23);
                f.WheelRotRadS     = TireQuad.Of(50, 51, 52, 53);
                f.SteeringAngle    = -0.25;
                f.CapturedAtTicks  = System.Diagnostics.Stopwatch.GetTimestamp();

                using (var log = new GoldenCaptureLog(path, "synthetic", "test"))
                {
                    log.LogRow(in f, true, 1234);
                    log.LogRow(in f, false, null);
                }

                var cap = CsvFrameReader.ReadFile(path);
                Assert.Equal(2, cap.SchemaVersion);
                Assert.Equal("synthetic", cap.SourceName);
                Assert.Equal("test", cap.GameName);
                Assert.Equal(2, cap.Rows.Count);

                var r = cap.Rows[0];
                Assert.True(r.SessionActive);
                Assert.Equal((short)1234, r.GameFfb);
                Assert.Equal(4500, r.Frame.Rpms, 6);
                Assert.Equal(123.4, r.Frame.SpeedKmh, 6);
                Assert.Equal("4", r.Frame.Gear);
                Assert.Equal(0.62, r.Frame.WheelSlip.GetValueOrDefault(), 6);
                Assert.Equal(-0.25, r.Frame.SteeringAngle.GetValueOrDefault(), 6);
                Assert.True(r.Frame.HasTireQuads);
                Assert.Equal(-0.06, r.Frame.TireSlipAngleRad.FR, 6);
                Assert.Equal(53.0, r.Frame.WheelRotRadS.RR, 6);
                Assert.Equal(6, r.Frame.NumCylinders);

                Assert.False(cap.Rows[1].SessionActive);
                Assert.Null(cap.Rows[1].GameFfb);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        // ---------- virtual-time source behavior ----------

        [Fact]
        public void ReplaySource_VirtualTime_MeasuresFixtureRate()
        {
            // 100 Hz fixture must read ~100 Hz through the SAME MeasuredHz
            // math live sources use — proves the virtual-time hooks are wired
            // through the base class, not bypassing it.
            var cap = SyntheticCapture(60, _ => DrivingFrame());
            var src = new ReplayFrameSource(cap, virtualTime: true);
            int emitted = 0;
            src.OnFrame = _ => emitted++;

            while (src.StepOne()) { }

            Assert.Equal(60, emitted);
            Assert.InRange(src.MeasuredHz, 90.0, 110.0);
        }

        [Fact]
        public void ReplaySource_StallAdvance_DecaysMeasuredHzToZero()
        {
            var cap = SyntheticCapture(10, _ => DrivingFrame());
            var src = new ReplayFrameSource(cap, virtualTime: true);
            src.OnFrame = _ => { };
            while (src.StepOne()) { }
            Assert.True(src.MeasuredHz > 0);

            src.AdvanceVirtualTimeUs(2_000_000);   // 2 s silence > 1 s idle timeout
            Assert.Equal(0.0, src.MeasuredHz, 6);
            Assert.True(src.MsSinceLastFrame >= 2000);
        }

        // ---------- Goertzel probe ----------

        [Fact]
        public void Goertzel_FindsToneAndRejectsOtherBins()
        {
            const double sr = 4000.0;
            var samples = new float[4000];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 150.0 * i / sr));

            double at150 = Goertzel.Power(samples, 0, samples.Length, 150.0, sr);
            double at40  = Goertzel.Power(samples, 0, samples.Length, 40.0, sr);

            Assert.InRange(at150, 0.25, 0.40);        // ~amp²/2 = 0.32
            Assert.True(at40 < 0.01, $"off-bin leakage too high: {at40}");
        }

        // ---------- end-to-end rig ----------

        [Fact]
        public void Rig_SlipFixture_ProducesOutput_NoClipping()
        {
            // 2 s at 100 Hz: second half slides hard (combined slip 0.9).
            var cap = SyntheticCapture(200, i => DrivingFrame(slip: i >= 100 ? 0.9 : 0.0));
            var rig = new HeadlessRig();

            long ticks = rig.Run(cap);

            Assert.True(ticks >= 1900, $"expected ~2000 engine ticks, got {ticks}");
            Assert.True(rig.Sink.Samples.Count >= ticks * EngineLoop.BatchSamples);

            // No clipping anywhere (the mixer clamps at ±1; hitting the clamp
            // exactly means something upstream is over-driving).
            Assert.DoesNotContain(rig.Sink.Samples, s => Math.Abs(s) >= 1.0f);

            // The slide half must carry more energy than the steady half:
            // traction loss is driven by WheelSlip and engine pulse runs
            // throughout, so slide RMS > steady RMS.
            int half = rig.Sink.Samples.Count / 2;
            double steadyRms = Rms(rig.Sink.Samples, 0, half);
            double slideRms  = Rms(rig.Sink.Samples, half, rig.Sink.Samples.Count - half);
            Assert.True(slideRms > steadyRms,
                $"slide RMS {slideRms:F5} not above steady RMS {steadyRms:F5}");
        }

        [Fact]
        public void Rig_GearShift_DucksEngineTier_ThenRecovers()
        {
            // 2 s at 100 Hz, upshift 3→4 at t=1 s. The shift thud (L3) must
            // duck the engine tier (L0) fast and release smoothly.
            var cap = SyntheticCapture(200, i => DrivingFrame(gear: i >= 100 ? "4" : "3"));
            var rig = new HeadlessRig();

            rig.Run(cap);

            var trace = rig.DuckL0Trace;
            Assert.True(trace.Count >= 1900);

            // Pre-shift: undisturbed.
            Assert.True(trace.Take(900).All(v => v > 0.97f), "pre-shift duck not clean");

            // Post-shift window (~1000..1300 ms): a real dip.
            float minDip = trace.Skip(1000).Take(300).Min();
            Assert.True(minDip < 0.85f, $"shift did not duck engine tier (min {minDip:F3})");

            // End of fixture: fully recovered.
            Assert.True(trace[trace.Count - 1] > 0.95f,
                $"duck did not recover (final {trace[trace.Count - 1]:F3})");
        }

        private static double Rms(System.Collections.Generic.List<float> s, int offset, int count)
        {
            double sum = 0;
            for (int i = 0; i < count; i++) { double v = s[offset + i]; sum += v * v; }
            return Math.Sqrt(sum / Math.Max(1, count));
        }
    }
}
