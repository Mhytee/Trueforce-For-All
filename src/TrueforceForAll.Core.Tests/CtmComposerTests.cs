using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase 2 CTM rollups: load-weighted axle grip utilization.
    public class CtmComposerTests
    {
        private static TelemetryFrame QuadFrame(
            TireQuad combined, TireQuad? susp = null, double? sway = null)
        {
            return new TelemetryFrame
            {
                HasTireQuads     = true,
                TireCombinedSlip = combined,
                SuspTravelM      = susp ?? default,
                AccelerationSway = sway,
            };
        }

        [Fact]
        public void NoQuads_LeavesRollupsNull()
        {
            var f = new TelemetryFrame { HasTireQuads = false };
            CtmComposer.Compose(ref f);
            Assert.Null(f.FrontGrip01);
            Assert.Null(f.RearGrip01);
            Assert.Null(f.GripBalance);
        }

        [Fact]
        public void NoLoadInfo_FallsBackToPlainAverage()
        {
            var f = QuadFrame(TireQuad.Of(0.4, 0.8, 0.2, 0.6));
            CtmComposer.Compose(ref f);
            Assert.Equal(0.6, f.FrontGrip01.Value, 10);   // (0.4+0.8)/2
            Assert.Equal(0.4, f.RearGrip01.Value, 10);    // (0.2+0.6)/2
            Assert.Equal(-0.2, f.GripBalance.Value, 10);
        }

        [Fact]
        public void SuspensionWeighting_LoadedTireDominates()
        {
            // Right-hand corner: left tires compressed 3x the right.
            var susp = TireQuad.Of(0.06, 0.02, 0.06, 0.02);
            var f = QuadFrame(TireQuad.Of(1.2, 0.3, 1.0, 0.2), susp);
            CtmComposer.Compose(ref f);

            // wFL = 0.06/0.08 = 0.75 → front = 0.75*1.2 + 0.25*0.3 = 0.975
            Assert.Equal(0.975, f.FrontGrip01.Value, 10);
            Assert.Equal(0.8, f.RearGrip01.Value, 10);    // 0.75*1.0 + 0.25*0.2
            // Plain average would read 0.75 front — the loaded tire's
            // breakaway is visibly stronger in the rollup.
            Assert.True(f.FrontGrip01.Value > 0.9);
        }

        [Fact]
        public void SuspensionWeighting_ClampsAtLiftedInsideWheel()
        {
            // Inside wheel fully lifted: its share clamps to MinTireWeight,
            // never to zero, so one noisy channel can't own the axle.
            var susp = TireQuad.Of(0.08, 0.0, 0.08, 0.0);
            var f = QuadFrame(TireQuad.Of(1.0, 5.0, 1.0, 5.0), susp);
            CtmComposer.Compose(ref f);
            double expected = (1.0 - CtmComposer.MinTireWeight) * 1.0
                            + CtmComposer.MinTireWeight * 5.0;
            Assert.Equal(expected, f.FrontGrip01.Value, 10);
        }

        [Fact]
        public void LatGFallback_WeightsOutsideTires()
        {
            // No usable suspension data; sway positive = turning right =
            // LEFT tires outside/loaded.
            var f = QuadFrame(TireQuad.Of(1.0, 0.2, 0.8, 0.2), sway: 9.81);
            CtmComposer.Compose(ref f);
            double wL = 1.0 - CtmComposer.MinTireWeight;   // full bias at 1 g
            Assert.Equal(wL * 1.0 + (1 - wL) * 0.2, f.FrontGrip01.Value, 10);

            // Mirror: sway negative loads the right tires.
            var g = QuadFrame(TireQuad.Of(0.2, 1.0, 0.2, 0.8), sway: -9.81);
            CtmComposer.Compose(ref g);
            Assert.Equal(f.FrontGrip01.Value, g.FrontGrip01.Value, 10);
        }

        [Fact]
        public void StraightLine_LatGZero_IsPlainAverage()
        {
            var f = QuadFrame(TireQuad.Of(0.4, 0.6, 0.1, 0.3), sway: 0.0);
            CtmComposer.Compose(ref f);
            Assert.Equal(0.5, f.FrontGrip01.Value, 10);
            Assert.Equal(0.2, f.RearGrip01.Value, 10);
        }

        [Fact]
        public void GripBalance_PositiveMeansOversteer()
        {
            var f = QuadFrame(TireQuad.Of(0.5, 0.5, 1.3, 1.3));
            CtmComposer.Compose(ref f);
            Assert.Equal(0.8, f.GripBalance.Value, 10);
        }
    }
}
