using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // FXTEST's synthetic downloads must exercise the exact decode-and-render
    // path a real game's 0x8123 download does: build a payload, hand it to
    // the engine, and the rendered force must match the requested effect.
    public class FxTestPayloadsTests
    {
        private const double TicksPerSec = 1000.0;

        [Fact]
        public void UnknownKind_ReturnsNull()
        {
            Assert.Equal(0, FxTestPayloads.TypeForKind("WOBBLE"));
            Assert.Null(FxTestPayloads.Build("WOBBLE", 50, 0));
        }

        [Fact]
        public void Damper_RoundTrips_ThroughTheEngine()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };
            var p = FxTestPayloads.Build("DAMPER", 75, 0);
            Assert.True(e.HandleDownload(p, 0, p.Length, 0));
            Assert.True(e.AnyDamperPlayingAt(10, TicksPerSec));
            // vel +1 with coeff 0.75 and damperGain 1: pull left, 0.75.
            float f = e.Evaluate(0f, 1f, 0f, 1f, 1f, 10, TicksPerSec, out bool playing);
            Assert.True(playing);
            Assert.InRange(f, 0.72f, 0.78f);
        }

        [Fact]
        public void Spring_RoundTrips_ThroughTheEngine()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };
            var p = FxTestPayloads.Build("SPRING", 50, 0);
            e.HandleDownload(p, 0, p.Length, 0);
            // pos 0.8 with coeff 0.5: pull left, 0.4.
            Assert.InRange(e.Evaluate(0.8f, 0f, 0f, 1f, 1f, 10, TicksPerSec, out _), 0.38f, 0.42f);
        }

        [Fact]
        public void Sine_RoundTrips_ThroughTheEngine()
        {
            var e = new HidppEffectEngine();
            var p = FxTestPayloads.Build("SINE", 50, 1000);
            e.HandleDownload(p, 0, p.Length, 0);
            // Quarter period at magnitude 0.5.
            Assert.InRange(e.Evaluate(0f, 0f, 0f, 1f, 1f, 250, TicksPerSec, out _), 0.48f, 0.52f);
        }

        [Fact]
        public void Ramp_HasAFiniteDuration()
        {
            var e = new HidppEffectEngine();
            var p = FxTestPayloads.Build("RAMP", 50, 0);
            e.HandleDownload(p, 0, p.Length, 0);
            // Start: +0.5 sliding toward -0.5; expired after 3 s.
            Assert.InRange(e.Evaluate(0f, 0f, 0f, 1f, 1f, 10, TicksPerSec, out _), 0.4f, 0.52f);
            e.Evaluate(0f, 0f, 0f, 1f, 1f, 3500, TicksPerSec, out bool playing);
            Assert.False(playing);
        }
    }
}
