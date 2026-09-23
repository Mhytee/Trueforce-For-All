// Phase 6 band arbiter: duck = class x activity x band overlap. The
// contracts that matter on the wheel: grip cues are never ducked, disjoint
// bands never mask, GripState ducks same-band Ambience at 0.85, and the
// duck releases when the activity stops.

using System;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class BandArbiterTests
    {
        /// <summary>Minimal stub effect with a fixed class/band/activity.</summary>
        private sealed class Stub : TelemetryEffect
        {
            private readonly EffectClass _cls;
            private readonly double _lo, _hi;
            public double Activity;

            public Stub(EffectClass cls, double lo, double hi, double activity = 0)
            {
                _cls = cls; _lo = lo; _hi = hi; Activity = activity;
            }

            public override string Name => "stub";
            public override bool IsActive => Activity > 0;
            public override void RenderAdd(float[] buffer, int count) { }
            public override EffectClass PriorityClass => _cls;
            public override double ActivityLevel => Activity;
            public override void GetCurrentBand(out double loHz, out double hiHz)
            {
                loHz = _lo; hiHz = _hi;
            }
        }

        private static void Settle(BandArbiter a, float depth = 0.6f, int ticks = 3000)
        {
            for (int i = 0; i < ticks; i++) a.Arbitrate(depth, 10f, 150f);
        }

        [Fact]
        public void SameBand_GripDucksAmbienceHard()
        {
            var scrub  = new Stub(EffectClass.GripState, 150, 250, activity: 1.0);
            var engine = new Stub(EffectClass.Ambience, 150, 250);
            var a = new BandArbiter();
            a.Bind(new TelemetryEffect[] { scrub, engine });
            Settle(a);
            Assert.InRange(a.MultiplierFor(engine), 0.10, 0.20);   // 1 - 0.85, full overlap
            Assert.Equal(1.0f, a.MultiplierFor(scrub), 3);          // grip is never ducked
        }

        [Fact]
        public void DisjointBands_NeverDuck()
        {
            // 35 Hz gear thump vs 200 Hz scrub: two octaves apart, no masking.
            var scrub = new Stub(EffectClass.GripState, 150, 250, activity: 1.0);
            var thud  = new Stub(EffectClass.Transient, 25, 55);
            var a = new BandArbiter();
            a.Bind(new TelemetryEffect[] { scrub, thud });
            Settle(a);
            Assert.Equal(1.0f, a.MultiplierFor(thud), 3);
        }

        [Fact]
        public void PartialOverlap_DucksProportionally()
        {
            // Ducker covers exactly half the target's band.
            var grip = new Stub(EffectClass.GripState, 100, 200, activity: 1.0);
            var amb  = new Stub(EffectClass.Ambience, 150, 250);
            var a = new BandArbiter();
            a.Bind(new TelemetryEffect[] { grip, amb });
            Settle(a);
            // duck = 0.85 * 1.0 * 0.5 = 0.425 → multiplier ≈ 0.575
            Assert.InRange(a.MultiplierFor(amb), 0.52, 0.63);
        }

        [Fact]
        public void ClassOrdering_TransientDucksAmbienceAtUserDepth_NotGrip()
        {
            var alert = new Stub(EffectClass.Transient, 60, 120, activity: 1.0);
            var amb   = new Stub(EffectClass.Ambience, 60, 120);
            var grip  = new Stub(EffectClass.GripState, 60, 120);
            var a = new BandArbiter();
            a.Bind(new TelemetryEffect[] { alert, amb, grip });
            Settle(a, depth: 0.6f);
            Assert.InRange(a.MultiplierFor(amb), 0.35, 0.45);   // 1 - 0.6
            Assert.Equal(1.0f, a.MultiplierFor(grip), 3);        // lower classes never duck up
        }

        [Fact]
        public void DuckReleases_WhenActivityStops()
        {
            var scrub  = new Stub(EffectClass.GripState, 150, 250, activity: 1.0);
            var engine = new Stub(EffectClass.Ambience, 140, 260);
            var a = new BandArbiter();
            a.Bind(new TelemetryEffect[] { scrub, engine });
            Settle(a);
            Assert.True(a.MultiplierFor(engine) < 0.3);
            scrub.Activity = 0;
            Settle(a);
            Assert.InRange(a.MultiplierFor(engine), 0.97, 1.0);
        }

        [Fact]
        public void AudioTarget_IsDuckedLikeAmbience()
        {
            var scrub = new Stub(EffectClass.GripState, 150, 250, activity: 1.0);
            var a = new BandArbiter();   // audio band default 35..250
            a.Bind(new TelemetryEffect[] { scrub });
            Settle(a);
            // Overlap = 100/215 of the audio band → duck ≈ 0.85 * 0.465 ≈ 0.40.
            Assert.InRange(a.AudioMultiplier, 0.50, 0.70);
        }

        [Fact]
        public void UnboundEffect_IsNeverDucked()
        {
            var a = new BandArbiter();
            a.Bind(Array.Empty<TelemetryEffect>());
            Settle(a);
            Assert.Equal(1.0f, a.MultiplierFor(new Stub(EffectClass.Ambience, 20, 400)), 3);
        }

        [Fact]
        public void Overlap01_Geometry()
        {
            Assert.Equal(0.0, BandArbiter.Overlap01(20, 50, 150, 250), 9);   // disjoint
            Assert.Equal(1.0, BandArbiter.Overlap01(100, 300, 150, 250), 9); // fully covered
            Assert.Equal(0.5, BandArbiter.Overlap01(100, 200, 150, 250), 9); // half
        }
    }
}
