// MotorSweepEffect: the SWEEP / SWEEP1..6 motor-characterization tool.
// The band codes reconfigure StartHz/EndHz/DurationMs before TestPlay, so
// the effect must honor reconfiguration — duration drives the sample count
// and the rendered frequency must actually live inside the requested band.

using System;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class MotorSweepEffectTests
    {
        private const double SampleRate = 4000.0;

        private static int CountZeroCrossings(MotorSweepEffect fx, int totalSamples)
        {
            var buf = new float[4];
            int crossings = 0;
            float prev = 0f;
            for (int i = 0; i < totalSamples / buf.Length; i++)
            {
                Array.Clear(buf, 0, buf.Length);
                fx.RenderAdd(buf, buf.Length);
                foreach (var v in buf)
                {
                    if ((prev < 0 && v >= 0) || (prev > 0 && v <= 0)) crossings++;
                    prev = v;
                }
            }
            return crossings;
        }

        [Fact]
        public void TestPlay_ReturnsConfiguredDurationPlusFade()
        {
            var fx = new MotorSweepEffect { StartHz = 16f, EndHz = 32f, DurationMs = 5000 };
            Assert.Equal(5200, fx.TestPlay());
        }

        [Fact]
        public void BandSweep_RendersFrequenciesInsideTheBand()
        {
            // 16→32 Hz log sweep over 5 s. Mean frequency of a log sweep is
            // (f2-f1)/ln(f2/f1) ≈ 23.1 Hz, so a full render should show
            // ~2 * 23.1 * 5 ≈ 231 zero crossings. A sweep that ignored the
            // band config (8→300 defaults) would show ~560+.
            var fx = new MotorSweepEffect { StartHz = 16f, EndHz = 32f, DurationMs = 5000 };
            fx.TestPlay();
            int crossings = CountZeroCrossings(fx, (int)(5.0 * SampleRate));
            Assert.InRange(crossings, 180, 280);
        }

        [Fact]
        public void SweepGoesSilentAfterConfiguredDuration()
        {
            var fx = new MotorSweepEffect { StartHz = 16f, EndHz = 32f, DurationMs = 1000 };
            fx.TestPlay();
            // Drain the configured second, then the next batch must add nothing.
            var buf = new float[4];
            for (int i = 0; i < (int)SampleRate / 4; i++) fx.RenderAdd(buf, buf.Length);
            Array.Clear(buf, 0, buf.Length);
            fx.RenderAdd(buf, buf.Length);
            Assert.All(buf, v => Assert.Equal(0f, v));
        }
    }
}
