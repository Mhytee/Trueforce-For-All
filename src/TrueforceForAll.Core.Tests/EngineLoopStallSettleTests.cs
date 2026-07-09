using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The stall settle must execute ON the engine thread at a tick boundary:
    // RequestStallSettle from the host's watchdog thread is consumed at the
    // top of the next RunOneTick, which resets the resampler and fans out
    // OnTelemetryStall BEFORE any sampling. After the settle, no further
    // OnTelemetry may arrive (the ring is empty), so a silenced amplitude can
    // never be re-latched by a held pre-stall frame.
    public class EngineLoopStallSettleTests
    {
        private sealed class RecordingEffect : TelemetryEffect
        {
            public int TelemetryCalls;
            public int StallCalls;
            public int TelemetryCallsAfterStall;

            public override string Name => "Recorder";
            public override bool IsActive => false;
            public override void RenderAdd(float[] buffer, int count) { }
            public override void OnTelemetry(TelemetryFrame f)
            {
                TelemetryCalls++;
                if (StallCalls > 0) TelemetryCallsAfterStall++;
            }
            public override void OnTelemetryStall() { StallCalls++; }
        }

        private sealed class NullSink : ISampleSink
        {
            public void Push(float[] samples, int count) { }
        }

        [Fact]
        public void StallSettle_RunsOnEngineTick_AndStopsTheTicks()
        {
            const long TicksPerSec = 1_000_000;   // virtual microsecond clock
            var fx = new RecordingEffect();
            var loop = new EngineLoop(new Mixer(), new DuckingController(),
                new NullSink(), new FrameResampler(TicksPerSec))
            {
                Effects = new TelemetryEffect[] { fx },
            };
            var buf = new float[EngineLoop.BatchSamples];

            // Live frame in the ring; tick until the effect has seen it.
            var frame = new TelemetryFrame
            {
                Rpms = 5000, MaxRpm = 8000, SpeedKmh = 100,
                CapturedAtTicks = TicksPerSec,
            };
            loop.IngestFrame(in frame);
            long now = TicksPerSec + 1000;
            for (int i = 0; i < 4; i++) loop.RunOneTick(buf, now += 2000, 0.5f, 5f, 80f);
            Assert.True(fx.TelemetryCalls > 0, "effect never ticked from the ring");

            // Watchdog thread requests the settle; the NEXT tick consumes it.
            loop.RequestStallSettle();
            Assert.Equal(0, fx.StallCalls);      // nothing happens off-tick
            for (int i = 0; i < 6; i++) loop.RunOneTick(buf, now += 2000, 0.5f, 5f, 80f);

            Assert.Equal(1, fx.StallCalls);                  // settled exactly once, on-tick
            Assert.Equal(0, fx.TelemetryCallsAfterStall);    // ring emptied: no held-frame re-latch
        }
    }
}
