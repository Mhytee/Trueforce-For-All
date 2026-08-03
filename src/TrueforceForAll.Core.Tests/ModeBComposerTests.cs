using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase 4 Mode B composition: the cornering-weight multiplier on top of the
    // pure SatForceModel term, plus the shaping curves the force chain applies
    // around it. The composer owns the SHAPE (onset/saturation, caps, clamp);
    // temporal smoothing of every input lives at the integration layer. The
    // rear-breakaway counter torque this class also used to compose was retired
    // 2026-08-02; see the ModeBComposer header.
    public class ModeBComposerTests
    {
        [Fact]
        public void ZeroGains_PassesSatThrough()
        {
            double f = ModeBComposer.Compose(satSigned: 0.4, latG: 1.0, latGain: 0.0);
            Assert.Equal(0.4, f, 10);
        }

        [Fact]
        public void CorneringWeight_ScalesWithLatG_AndCaps()
        {
            double flat = ModeBComposer.Compose(0.3, 0.0, 0.8);
            double oneG = ModeBComposer.Compose(0.3, 1.0, 0.8);
            Assert.Equal(0.3, flat, 10);                 // no lat accel: unchanged
            Assert.Equal(0.3 * (1 + 0.8), oneG, 10);     // 1 g: full gain applied

            // Beyond the cap the multiplier stops growing (kerb-spike guard).
            double capped = ModeBComposer.Compose(0.3, ModeBComposer.LatGCap, 0.8);
            double insane = ModeBComposer.Compose(0.3, 5.0, 0.8);
            Assert.Equal(capped, insane, 10);
        }

        [Fact]
        public void CorneringWeight_PreservesSign()
        {
            double left  = ModeBComposer.Compose(-0.3, 1.0, 0.8);
            double right = ModeBComposer.Compose( 0.3, 1.0, 0.8);
            Assert.Equal(-right, left, 10);
        }

        [Fact]
        public void Output_ClampedToUnitRange()
        {
            double hi = ModeBComposer.Compose(0.9, 1.5, 2.0);
            double lo = ModeBComposer.Compose(-0.9, 1.5, 2.0);
            Assert.Equal(1.0, hi, 10);
            Assert.Equal(-1.0, lo, 10);
        }

        [Fact]
        public void NegativeInputs_TreatedAsZeroGains()
        {
            // Defensive: negative gains/latG never invert or explode the force.
            double f = ModeBComposer.Compose(0.3, -1.0, -0.8);
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

        // --- Minimum-force floor (belt-wheel stiction): the last shaping
        // stage. Everything nonzero clears the motor's floor, exact zero
        // stays exact, and the remap is continuous and monotone so gate
        // fades stay fades. ---

        [Fact]
        public void MinForce_OffIsExactIdentity()
        {
            Assert.Equal(0.1234, ModeBComposer.MinForceRemap(0.1234, 0.0), 12);
            Assert.Equal(-0.9, ModeBComposer.MinForceRemap(-0.9, 0.0), 12);
            Assert.Equal(0.0, ModeBComposer.MinForceRemap(0.0, 0.0), 12);
        }

        [Fact]
        public void MinForce_ExactZeroStaysSilent()
        {
            // The center/parked silence built upstream must survive: a true
            // zero never becomes a floor-sized torque.
            Assert.Equal(0.0, ModeBComposer.MinForceRemap(0.0, 0.15), 12);
            Assert.Equal(0.0, ModeBComposer.MinForceRemap(double.NaN, 0.15), 12);
        }

        [Fact]
        public void MinForce_SmallForcesClearTheFloor()
        {
            // Anything past the eps band lands at or above the floor.
            double f = ModeBComposer.MinForceRemap(0.02, 0.10);
            Assert.True(f >= 0.10, $"small force below the floor: {f}");
            Assert.Equal(0.10 + 0.90 * 0.02, f, 12);
        }

        [Fact]
        public void MinForce_PreservesSignAndFullScale()
        {
            double pos = ModeBComposer.MinForceRemap(0.4, 0.10);
            double neg = ModeBComposer.MinForceRemap(-0.4, 0.10);
            Assert.Equal(-pos, neg, 12);
            // Full scale is unchanged (the remap tops out at 1).
            Assert.Equal(1.0, ModeBComposer.MinForceRemap(1.0, 0.10), 12);
            Assert.Equal(-1.0, ModeBComposer.MinForceRemap(-1.0, 0.10), 12);
        }

        [Fact]
        public void MinForce_ContinuousAtEps_AndMonotone()
        {
            const double m = 0.12, eps = 0.005;
            // No step across the eps boundary.
            double below = ModeBComposer.MinForceRemap(eps * 0.999, m, eps);
            double above = ModeBComposer.MinForceRemap(eps * 1.001, m, eps);
            Assert.True(Math.Abs(above - below) < 0.002, $"step at eps: {below} -> {above}");

            // Monotone across the whole range: a gate fading the force down
            // always fades the output down too (no plateaus that reverse).
            double prev = -1e-9;
            for (double f = 0.0; f <= 1.0; f += 0.001)
            {
                double outF = ModeBComposer.MinForceRemap(f, m, eps);
                Assert.True(outF >= prev - 1e-12, $"non-monotone at f={f}");
                prev = outF;
            }
        }

        [Fact]
        public void MinForce_BelowEps_RampsThroughZero()
        {
            // Inside the eps band the output ramps linearly to a true zero
            // (continuity guard; smallness comes from the caller scaling the
            // floor by the silence gates, tested below).
            const double m = 0.15, eps = 0.005;
            double tiny = ModeBComposer.MinForceRemap(eps * 0.2, m, eps);
            Assert.True(tiny < m * 0.5, $"eps band not ramping: {tiny}");
            Assert.True(tiny > 0.0);
        }

        [Fact]
        public void MinForce_ScaledFloor_KeepsSilenceGatesQuiet()
        {
            // Caller contract (the crawl-buzz regression the adversarial
            // review caught): the floor is scaled by the silence-gate factors
            // (low-speed gate, crash duck). At an 8 km/h off-road crawl the
            // gate passes ~0.055 of the pegged slip garbage; a RAW 0.25 floor
            // would lift that residual straight back to floor level, while
            // the gate-scaled floor keeps it around 2% of full scale.
            double gate = ModeBComposer.LowSpeedGate(8.0);        // ~0.055
            double residual = 0.13 * gate;                        // trail-limited garbage after the gate
            double raw = ModeBComposer.MinForceRemap(residual, 0.25);
            double scaled = ModeBComposer.MinForceRemap(residual, 0.25 * gate);
            Assert.True(raw > 0.25, $"premise: an unscaled floor lifts crawl garbage ({raw})");
            Assert.True(scaled < 0.025, $"scaled floor must stay imperceptible: {scaled}");
        }
    }
}
