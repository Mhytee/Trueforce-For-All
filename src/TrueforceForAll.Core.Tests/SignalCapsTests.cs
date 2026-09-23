// Phase 2 caps bitset: SignalCaps.InferNative (field presence → Native
// bits), CtmComposer's AxleRollups stamp, and the effects' events-vs-legacy
// dual path. The Derived bits answer the question null checks can't:
// "did the deriver run on this frame at all".

using System;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class SignalCapsTests
    {
        [Fact]
        public void InferNative_EmptyFrameHasNoCaps()
        {
            Assert.Equal(SignalGroups.None, SignalCaps.InferNative(new TelemetryFrame()));
        }

        [Fact]
        public void InferNative_MapsEachGroupFromFieldPresence()
        {
            var f = new TelemetryFrame
            {
                Gear = "3",
                SteeringAngle = 0.1,
                AccelerationSway = 2.0,
                FrontSlipAngleRad = 0.02,
                HasTireQuads = true,
                OnRumbleStrip = false,      // present (false) still means "carried"
                Airborne = false,
                CollisionMagnitude = 0.0,
            };
            var caps = SignalCaps.InferNative(in f);
            Assert.Equal(
                SignalGroups.Gear | SignalGroups.Steering | SignalGroups.AccelG
                | SignalGroups.SlipScalars | SignalGroups.TireQuads
                | SignalGroups.SurfaceFeel | SignalGroups.Airborne | SignalGroups.Collision,
                caps);
        }

        [Fact]
        public void InferNative_NeverSetsDerivedBits()
        {
            var f = new TelemetryFrame { Gear = "2", HasTireQuads = true };
            var caps = SignalCaps.InferNative(in f);
            Assert.Equal(SignalGroups.None,
                caps & (SignalGroups.AxleRollups | SignalGroups.DerivedEvents));
        }

        [Fact]
        public void CtmComposer_StampsAxleRollupsOnlyWhenItComposed()
        {
            var with = new TelemetryFrame
            {
                HasTireQuads = true,
                TireCombinedSlip = TireQuad.Of(0.5, 0.5, 0.4, 0.4),
            };
            CtmComposer.Compose(ref with);
            Assert.True((with.Caps & SignalGroups.AxleRollups) != 0);

            var without = new TelemetryFrame();
            CtmComposer.Compose(ref without);
            Assert.Equal(SignalGroups.None, without.Caps & SignalGroups.AxleRollups);
        }

        // ---- Effects consume Events when the deriver ran ----

        private static TelemetryFrame GearFrame(string gear, bool derived, bool changedEvent)
        {
            var f = new TelemetryFrame { Gear = gear };
            if (derived)
            {
                f.Caps = SignalGroups.Gear | SignalGroups.DerivedEvents;
                if (changedEvent) f.Events = FrameEvents.GearChanged;
            }
            return f;
        }

        [Fact]
        public void GearShift_WithDerivedEvents_FiresOnTheFlagNotTheStringDiff()
        {
            var e = new GearShiftEffect { Enabled = true, Gain = 1f };

            // Deriver says nothing changed — even across a string difference
            // (e.g. an effect Reset() mid-drive re-seeding _lastGear), no bump.
            e.OnTelemetry(GearFrame("2", derived: true, changedEvent: false));
            e.OnTelemetry(GearFrame("3", derived: true, changedEvent: false));
            Assert.False(e.IsActive, "no GearChanged flag -> no bump");

            // Deriver says changed -> bump.
            e.OnTelemetry(GearFrame("4", derived: true, changedEvent: true));
            Assert.True(e.IsActive, "GearChanged flag must trigger the bump");
        }

        [Fact]
        public void GearShift_WithoutDerivedEvents_LegacyStringDiffStillWorks()
        {
            var e = new GearShiftEffect { Enabled = true, Gain = 1f };
            e.OnTelemetry(GearFrame("2", derived: false, changedEvent: false));
            Assert.False(e.IsActive);
            e.OnTelemetry(GearFrame("3", derived: false, changedEvent: false));
            Assert.True(e.IsActive, "legacy path must still detect the shift");
        }
    }
}
