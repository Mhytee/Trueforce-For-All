using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase 4 Mode B per-axle composition: cornering-weight multiplier and
    // rear-breakaway counter torque on top of the pure SatForceModel term.
    // The composer owns the SHAPE (smoothstep onset/saturation, caps, clamp);
    // temporal smoothing of overExcess lives at the integration layer.
    public class ModeBComposerTests
    {
        private static double Smoothstep(double over)
        {
            double t = Math.Min(Math.Max(over, 0.0), ModeBComposer.OverCap) / ModeBComposer.OverCap;
            return t * t * (3.0 - 2.0 * t);
        }

        [Fact]
        public void ZeroGains_PassesSatThrough()
        {
            double f = ModeBComposer.Compose(
                satSigned: 0.4, dir: 1.0, overExcess: 0.9,
                latG: 1.0, latGain: 0.0, counterGain: 0.0, trail01: 1.0);
            Assert.Equal(0.4, f, 10);
        }

        [Fact]
        public void CorneringWeight_ScalesWithLatG_AndCaps()
        {
            double flat = ModeBComposer.Compose(0.3, 1, 0, 0.0, 0.8, 0, 1);
            double oneG = ModeBComposer.Compose(0.3, 1, 0, 1.0, 0.8, 0, 1);
            Assert.Equal(0.3, flat, 10);                 // no lat accel: unchanged
            Assert.Equal(0.3 * (1 + 0.8), oneG, 10);     // 1 g: full gain applied

            // Beyond the cap the multiplier stops growing (kerb-spike guard).
            double capped = ModeBComposer.Compose(0.3, 1, 0, ModeBComposer.LatGCap, 0.8, 0, 1);
            double insane = ModeBComposer.Compose(0.3, 1, 0, 5.0, 0.8, 0, 1);
            Assert.Equal(capped, insane, 10);
        }

        [Fact]
        public void CorneringWeight_PreservesSign()
        {
            double left  = ModeBComposer.Compose(-0.3, -1, 0, 1.0, 0.8, 0, 1);
            double right = ModeBComposer.Compose( 0.3,  1, 0, 1.0, 0.8, 0, 1);
            Assert.Equal(-right, left, 10);
        }

        [Fact]
        public void Counter_SilentWhenNoExcess()
        {
            // Understeer or neutral: zero (or negative) excess adds nothing.
            double f0 = ModeBComposer.Compose(0.2, 1, 0.0, 0, 0, 0.6, 1);
            double fn = ModeBComposer.Compose(0.2, 1, -0.4, 0, 0, 0.6, 1);
            Assert.Equal(0.2, f0, 10);
            Assert.Equal(0.2, fn, 10);
        }

        [Fact]
        public void Counter_SmoothstepShaped_OnRearBreakaway()
        {
            double catchF = ModeBComposer.Compose(0.15, 1, 0.8, 0, 0, 0.6, 1);
            Assert.Equal(0.15 + 0.6 * Smoothstep(0.8), catchF, 10);

            // Excess beyond OverCap saturates at full counter gain.
            double deep = ModeBComposer.Compose(0.15, 1, 3.0, 0, 0, 0.6, 1);
            Assert.Equal(0.15 + 0.6, deep, 10);
        }

        [Fact]
        public void Counter_OnsetIsGentle_NotLinear()
        {
            // The snap fix: a grazing slide (small excess) must produce far
            // less torque than a linear ramp would — zero slope at onset.
            double graze = ModeBComposer.Compose(0.0, 1, 0.1, 0, 0, 1.0, 1);
            Assert.True(graze < 0.1 * 0.5, $"onset too sharp: {graze}");

            // And the curve is monotone: more excess never means less pull.
            double prev = 0;
            for (double over = 0; over <= 1.2; over += 0.01)
            {
                double f = ModeBComposer.Compose(0.0, 1, over, 0, 0, 1.0, 1);
                Assert.True(f >= prev - 1e-12, $"non-monotone at over={over}");
                prev = f;
            }
        }

        [Fact]
        public void Counter_GatedByTrailRamp_QuietAtStandstill()
        {
            // Launch wheelspin at a stop: huge excess, but trail 0 → no torque.
            double f = ModeBComposer.Compose(0.0, 0.5, 1.9, 0, 0, 0.8, trail01: 0);
            Assert.Equal(0.0, f, 10);
        }

        [Fact]
        public void Counter_FollowsDirSign()
        {
            double pos = ModeBComposer.Compose(0, 1.0, 0.8, 0, 0, 0.5, 1);
            double neg = ModeBComposer.Compose(0, -1.0, 0.8, 0, 0, 0.5, 1);
            Assert.Equal(-pos, neg, 10);
        }

        [Fact]
        public void Output_ClampedToUnitRange()
        {
            double hi = ModeBComposer.Compose(0.9, 1, 1.5, 1.5, 2.0, 1.5, 1);
            double lo = ModeBComposer.Compose(-0.9, -1, 1.5, 1.5, 2.0, 1.5, 1);
            Assert.Equal(1.0, hi, 10);
            Assert.Equal(-1.0, lo, 10);
        }

        [Fact]
        public void NegativeInputs_TreatedAsZeroGains()
        {
            // Defensive: negative gains/latG never invert or explode the force.
            double f = ModeBComposer.Compose(0.3, 1, 0.6, -1.0, -0.8, -0.5, 1);
            Assert.Equal(0.3, f, 10);
        }

        // --- Braking-lockup gate (issue #38): fade the synthesized aligning
        // force out as the fronts lock. Utilization is combined slip, so a
        // locking front pumps it to peak and slams the force to full scale under
        // braking, which then swings the wheel left-right; a real locked tire
        // makes ~no aligning torque, so the force should go light there. ---

        [Fact]
        public void LockupGate_OpenForNonLockup()
        {
            // Coasting, wheelspin (positive ratio), lateral scrub (ratio ~0),
            // and light lockup up to the fade start all pass at unity.
            Assert.Equal(1.0, ModeBComposer.LockupGate(0.0), 12);
            Assert.Equal(1.0, ModeBComposer.LockupGate(0.5), 12);   // wheelspin
            Assert.Equal(1.0, ModeBComposer.LockupGate(-0.2), 12);  // exactly at start
            Assert.Equal(1.0, ModeBComposer.LockupGate(-0.1), 12);  // light lockup, not yet gated
        }

        [Fact]
        public void LockupGate_ClosedPastFull()
        {
            Assert.Equal(0.0, ModeBComposer.LockupGate(-0.5), 12);
            Assert.Equal(0.0, ModeBComposer.LockupGate(-1.0), 12);  // full lock
        }

        [Fact]
        public void LockupGate_MidFadeIsHalf()
        {
            // Midpoint of the -0.2 .. -0.5 ramp.
            Assert.Equal(0.5, ModeBComposer.LockupGate(-0.35), 12);
        }

        [Fact]
        public void LockupGate_MonotoneDownThroughFade()
        {
            double prev = 1.0;
            for (double r = -0.2; r >= -0.5; r -= 0.01)
            {
                double g = ModeBComposer.LockupGate(r);
                Assert.InRange(g, 0.0, 1.0);
                Assert.True(g <= prev + 1e-12, $"non-monotone at r={r}");
                prev = g;
            }
        }

        [Fact]
        public void LockupGate_CollapsesAligningForce_UnderLockup()
        {
            // The point of the fix: with utilization pegged at the limit (what a
            // locking front's combined slip produces), the SAT aligning force is
            // strong, but gating collapses it toward zero as the front locks,
            // while the ungated force stays strong. Uses the real SatForceModel
            // so the test exercises the force the gate actually multiplies.
            var sat = new SatForceModel { SatGain = 0.774, DropFloor = 0.2 };
            double ungated = sat.Force01(uFront: 1.0, signedFrontSlipRad: 0.05, load01: 1.0, speedKmh: 120);
            Assert.True(Math.Abs(ungated) > 0.4, $"expected a strong aligning force, got {ungated}");

            double deepLock = ungated * ModeBComposer.LockupGate(-0.5);
            double halfLock = ungated * ModeBComposer.LockupGate(-0.35);
            Assert.Equal(0.0, deepLock, 12);                              // fully muted at deep lockup
            Assert.Equal(Math.Abs(ungated) * 0.5, Math.Abs(halfLock), 6); // half muted mid-fade
        }

        // --- Friction-circle lateral share (the A/B alternative to the gate):
        // |slip ratio| spends the one grip budget and the aligning force scales
        // by the lateral share left, sqrt(1 - (r/full)^2). Continuous from
        // zero, sign-blind (wheelspin spends grip like lockup). ---

        [Fact]
        public void FrictionCircle_FullShareAtZeroSlip()
        {
            Assert.Equal(1.0, ModeBComposer.FrictionCircleLateralShare(0.0), 12);
        }

        [Fact]
        public void FrictionCircle_NearUnityForLightBraking()
        {
            // Normal trail braking (|ratio| ~0.1) sheds only ~2%.
            double s = ModeBComposer.FrictionCircleLateralShare(0.1);
            Assert.InRange(s, 0.97, 1.0);
        }

        [Fact]
        public void FrictionCircle_ZeroAtAndPastFullRatio()
        {
            Assert.Equal(0.0, ModeBComposer.FrictionCircleLateralShare(0.5), 12);
            Assert.Equal(0.0, ModeBComposer.FrictionCircleLateralShare(1.0), 12);   // full lock
            Assert.Equal(0.0, ModeBComposer.FrictionCircleLateralShare(-1.0), 12);  // sign-blind
        }

        [Fact]
        public void FrictionCircle_CircleShape()
        {
            // At r = full/sqrt(2) the share is exactly 1/sqrt(2) (the circle's
            // chord), the point where the two axes split the budget evenly.
            double s = ModeBComposer.FrictionCircleLateralShare(0.5 / Math.Sqrt(2.0));
            Assert.Equal(1.0 / Math.Sqrt(2.0), s, 10);
        }

        [Fact]
        public void FrictionCircle_SignBlind_WheelspinEqualsLockup()
        {
            double spin = ModeBComposer.FrictionCircleLateralShare(0.3);
            double lockup = ModeBComposer.FrictionCircleLateralShare(-0.3);
            Assert.Equal(spin, lockup, 12);
        }

        [Fact]
        public void FrictionCircle_MonotoneDecreasingInMagnitude()
        {
            double prev = 1.0;
            for (double r = 0.0; r <= 0.6; r += 0.01)
            {
                double s = ModeBComposer.FrictionCircleLateralShare(r);
                Assert.InRange(s, 0.0, 1.0);
                Assert.True(s <= prev + 1e-12, $"non-monotone at r={r}");
                prev = s;
            }
        }

        [Fact]
        public void LockupPoint_HigherKeepsMoreForceAtAGivenSlip()
        {
            // The light-braking-goes-limp fix: pushing the "full lockup" reference
            // deeper must keep MORE steering force at a given slip ratio, on both
            // laws. At ratio 0.3 the shallow reference already sheds a lot; the
            // deeper one barely touches it.
            double r = 0.3;
            double circShallow = ModeBComposer.FrictionCircleLateralShare(r, 0.5);
            double circDeep    = ModeBComposer.FrictionCircleLateralShare(r, 1.0);
            Assert.True(circDeep > circShallow, $"circle: deep {circDeep} !> shallow {circShallow}");

            double gateShallow = ModeBComposer.LockupGate(-r, -0.4 * 0.5, -0.5);
            double gateDeep    = ModeBComposer.LockupGate(-r, -0.4 * 1.0, -1.0);
            Assert.True(gateDeep > gateShallow, $"gate: deep {gateDeep} !> shallow {gateShallow}");
        }

        // --- Reversal softening (drift-catch instability, issue #38's lateral
        // twin): fade the composed force down while a slide COLLAPSES toward
        // center, scaled by how fast, so the full-scale sign flip stops snapping
        // and the wheel→slip→force ring loses its loop gain. Digging deeper into a
        // slide is untouched, so loaded drift weight survives. ---

        [Fact]
        public void ReversalSoften_OffWhenStrengthZero()
        {
            // A hard fast catch, but strength 0 → identity.
            Assert.Equal(1.0, ModeBComposer.ReversalSoften(0.2, -5.0, 0.0), 12);
        }

        [Fact]
        public void ReversalSoften_UntouchedWhenDiggingIntoSlide()
        {
            // |slip| GROWING (angle and rate share a sign): full weight, both ways.
            Assert.Equal(1.0, ModeBComposer.ReversalSoften(0.2, 5.0, 1.0), 12);   // deepening right
            Assert.Equal(1.0, ModeBComposer.ReversalSoften(-0.2, -5.0, 1.0), 12); // deepening left
        }

        [Fact]
        public void ReversalSoften_UntouchedWhenBarelyMoving()
        {
            // Steady cornering: near-zero rate → negligible cut.
            double g = ModeBComposer.ReversalSoften(0.15, -0.02, 1.0);
            Assert.InRange(g, 0.99, 1.0);
        }

        [Fact]
        public void ReversalSoften_CutsWhileCollapsingTowardCenter()
        {
            // Catching a slide: angle +, rate − (|slip| shrinking) → force fades.
            double g = ModeBComposer.ReversalSoften(0.2, -5.0, 1.0);
            Assert.True(g < 0.5, $"expected a strong cut catching a hard slide, got {g}");
        }

        [Fact]
        public void ReversalSoften_HalfCutAtReferenceFlux()
        {
            // returning flux = refFlux → t = 0.5 → cut is half of strength.
            // angle·(-rate) = refFlux with angle 0.3, rate -1.0 → flux 0.3.
            double g = ModeBComposer.ReversalSoften(0.3, -1.0, 0.8, refFlux: 0.3);
            Assert.Equal(1.0 - 0.8 * 0.5, g, 10);
        }

        [Fact]
        public void ReversalSoften_SignInvariant()
        {
            // Mirroring both inputs leaves the returning flux (a product) unchanged,
            // so BSIGN never has to be undone before the call.
            double right = ModeBComposer.ReversalSoften(0.2, -4.0, 0.7);
            double left  = ModeBComposer.ReversalSoften(-0.2, 4.0, 0.7);
            Assert.Equal(right, left, 12);
        }

        [Fact]
        public void ReversalSoften_NeverInvertsOrExplodes()
        {
            // Bounded to (0, 1] over an extreme sweep: only ever a partial cut,
            // never a sign flip or a gain, and NaN inputs fail safe to unity.
            for (double a = -0.5; a <= 0.5; a += 0.05)
                for (double rate = -30; rate <= 30; rate += 2)
                {
                    double g = ModeBComposer.ReversalSoften(a, rate, 1.0);
                    Assert.InRange(g, 0.0, 1.0);
                }
            Assert.Equal(1.0, ModeBComposer.ReversalSoften(double.NaN, -5.0, 1.0), 12);
            Assert.Equal(1.0, ModeBComposer.ReversalSoften(0.2, double.NaN, 1.0), 12);
        }

        // --- Slide-adaptive trail-spring window: the direction ramp saturates at
        // ~1.7° of slip angle, so past that the aligning force is a constant shove
        // with no proportional restoring (the wheel slews to the lock instead of
        // settling into a countersteer). Widen the ramp during a slide so the force
        // eases off proportionally as the front realigns = a stable equilibrium. ---

        [Fact]
        public void AdaptiveDirWindow_BaseWhenGripping()
        {
            // Gate 0 (no rear breakaway): the shipped narrow window, untouched.
            Assert.Equal(0.03, ModeBComposer.AdaptiveDirWindow(0.03, 0.14, 0.0), 12);
        }

        [Fact]
        public void AdaptiveDirWindow_MaxAtFullSlide()
        {
            // Gate 1 (full slide): the wide "trail range" window.
            Assert.Equal(0.14, ModeBComposer.AdaptiveDirWindow(0.03, 0.14, 1.0), 12);
        }

        [Fact]
        public void AdaptiveDirWindow_MonotoneAndBounded()
        {
            double prev = 0.03;
            for (double g = 0.0; g <= 1.0; g += 0.05)
            {
                double w = ModeBComposer.AdaptiveDirWindow(0.03, 0.14, g);
                Assert.InRange(w, 0.03, 0.14);
                Assert.True(w >= prev - 1e-12, $"non-monotone at gate={g}");
                prev = w;
            }
        }

        [Fact]
        public void AdaptiveDirWindow_GentleOnset_GripFeelUntouched()
        {
            // C1 onset: a whiff of rear slip barely widens the window, so light
            // trailing-throttle rotation keeps the strong on-center grip feel.
            double w = ModeBComposer.AdaptiveDirWindow(0.03, 0.14, 0.1);
            double frac = (w - 0.03) / (0.14 - 0.03);
            Assert.True(frac < 0.1, $"onset too eager: {frac}");
        }

        [Fact]
        public void AdaptiveDirWindow_WiderWindowGivesProportionalDir()
        {
            // The point of the fix: at a mid-slide slip angle the narrow (gripping)
            // window pins dir at full ±1 (bang-bang), while the widened (full-slide)
            // window leaves dir PROPORTIONAL, so the force still has a restoring
            // gradient to settle on.
            double slip = 0.07;   // ~4°, a caught-slide front angle
            double narrow = ModeBComposer.AdaptiveDirWindow(0.03, 0.14, 0.0);
            double wide   = ModeBComposer.AdaptiveDirWindow(0.03, 0.14, 1.0);
            double dirNarrow = ModeBComposer.CenterSoftDir(slip / narrow);
            double dirWide   = ModeBComposer.CenterSoftDir(slip / wide);
            Assert.Equal(1.0, dirNarrow, 6);          // saturated: no restoring slope
            Assert.True(dirWide < 0.9, $"expected proportional dir, got {dirWide}");
        }

        [Fact]
        public void AdaptiveDirWindow_DegenerateInputsClampSafely()
        {
            // maxWindow below base can't invert the window; gate outside [0,1] clamps.
            Assert.Equal(0.03, ModeBComposer.AdaptiveDirWindow(0.03, 0.01, 1.0), 12);
            Assert.Equal(0.14, ModeBComposer.AdaptiveDirWindow(0.03, 0.14, 5.0), 12);
            Assert.Equal(0.03, ModeBComposer.AdaptiveDirWindow(0.03, 0.14, -1.0), 12);
        }

        // --- Phase lead: cancel the telemetry-loop lag that makes the synthesized
        // SAT spring ring near its target (unlike AC's zero-lag physics FFB). Push
        // the slip angle feeding dir forward by its rate so the force anticipates. ---

        [Fact]
        public void PhaseLeadSlip_IdentityWhenNoLead()
        {
            Assert.Equal(0.1, ModeBComposer.PhaseLeadSlip(0.1, 5.0, 0.0), 12);
        }

        [Fact]
        public void PhaseLeadSlip_ExtrapolatesForward()
        {
            // Returning toward center (slip +, rate -): the led slip is nearer center
            // than the raw slip, so the force eases sooner = the lag recovered.
            double led = ModeBComposer.PhaseLeadSlip(0.10, -1.0, 0.04);
            Assert.Equal(0.10 - 0.04, led, 10);
            Assert.True(led < 0.10);
        }

        [Fact]
        public void PhaseLeadSlip_CapsRateSpike()
        {
            // A huge rate can't fling the prediction more than the cap past the slip.
            double led = ModeBComposer.PhaseLeadSlip(0.05, 50.0, 0.04);
            Assert.Equal(0.05 + 0.15, led, 10);
        }

        [Fact]
        public void PhaseLeadSlip_SignSafeAndNanSafe()
        {
            double pos = ModeBComposer.PhaseLeadSlip(0.10, -2.0, 0.03);
            double neg = ModeBComposer.PhaseLeadSlip(-0.10, 2.0, 0.03);
            Assert.Equal(-pos, neg, 12);
            Assert.Equal(0.10, ModeBComposer.PhaseLeadSlip(0.10, double.NaN, 0.03), 12);
        }

        // --- Slide duck: ease centering OUT as the rear breaks away, so it stays for
        // grip driving but stops fighting the trail spring's countersteer in a slide. ---

        [Fact]
        public void SlideDuck_UntouchedWhenGripping()
        {
            Assert.Equal(1.0, ModeBComposer.SlideDuck(1.0, 0.0), 12);   // gate 0 = full centering
        }

        [Fact]
        public void SlideDuck_FullDuckAtFullSlide()
        {
            Assert.Equal(0.0, ModeBComposer.SlideDuck(1.0, 1.0), 12);   // amount 1, full slide = gone
        }

        [Fact]
        public void SlideDuck_PartialAmountKeepsSome()
        {
            // amount 0.5 at full slide leaves half the centering.
            Assert.Equal(0.5, ModeBComposer.SlideDuck(0.5, 1.0), 12);
        }

        [Fact]
        public void SlideDuck_AmountZeroIsIdentity()
        {
            for (double g = 0.0; g <= 1.0; g += 0.1)
                Assert.Equal(1.0, ModeBComposer.SlideDuck(0.0, g), 12);
        }

        [Fact]
        public void SlideDuck_GentleOnset_GripCenteringUntouched()
        {
            // C1 onset: a whiff of rear slip barely touches centering.
            double m = ModeBComposer.SlideDuck(1.0, 0.1);
            Assert.True(m > 0.95, $"onset too eager: {m}");
        }

        [Fact]
        public void SlideDuck_MonotoneAndBounded()
        {
            double prev = 1.0;
            for (double g = 0.0; g <= 1.0; g += 0.05)
            {
                double m = ModeBComposer.SlideDuck(1.0, g);
                Assert.InRange(m, 0.0, 1.0);
                Assert.True(m <= prev + 1e-12, $"non-monotone at gate={g}");
                prev = m;
            }
        }

        [Fact]
        public void SlideDuck_ClampsDegenerateInputs()
        {
            Assert.Equal(0.0, ModeBComposer.SlideDuck(5.0, 2.0), 12);    // over-range amount + gate clamp
            Assert.Equal(1.0, ModeBComposer.SlideDuck(-1.0, 1.0), 12);   // negative amount = no duck
        }
    }
}
