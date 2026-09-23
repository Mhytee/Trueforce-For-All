// Airborne ducking coordinator. Unlike every other entry in the effects list
// this voice produces NO audio of its own: RenderAdd is a no-op. Its job is to
// detect when the car is off the ground (via TelemetryFrame.Airborne, set by
// the enhanced sources that can tell: AC, Forza, and the Farming Simulator
// telemetry mod) and tell the plugin's
// ducker to pull down the OTHER effects, so a jump or a crest pop doesn't fire
// a phantom slide, engine roar, or road rumble while the wheels free-spin in
// the air.
//
// Policy lives here (which voices to duck, and by how much); the actual
// per-tick multiplier application lives in TrueforcePlugin.UpdateDucking,
// which reads AirborneActive + the toggles and folds an airborne factor into
// each target's DuckMultiplier on top of the sidechain ducking. The two
// compose multiplicatively, so airborne ducking and sidechain ducking stack
// cleanly.
//
// Detection requires a source that surfaces wheel load / suspension travel (or
// FS's per-wheel contact flags), so this only engages in AC, Forza and Farming
// Simulator today. On the universal SimHub fallback
// TelemetryFrame.Airborne is null and AirborneActive stays false (a no-op),
// the same way the stationary spring no-ops where steering isn't reported.

using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class AirborneEffect : TelemetryEffect
    {
        public override string Name => "Airborne ducking";

        /// <summary>How hard to pull the ducked voices down while airborne.
        /// 0 = no change, 1 = full silence. Applied as (1 - Reduction) to each
        /// enabled target's output.</summary>
        public float Reduction { get; set; } = 1.0f;

        // Per-target opt-in: which voices get ducked while airborne. The point
        // is the weightless feeling of road / grip vibrations cutting out when
        // the tyres leave the ground, so the defaults duck the road-contact
        // voices (road bumps, traction-loss slide, surface) plus the rev
        // limiter buzz (free-spin pins it to the limiter mid-air). The engine
        // pulse is deliberately NOT ducked: the engine keeps revving in the
        // air, so its pulse reads as "still driving, just light", not a
        // cut-out. Collision stays on the wheel so a hard landing still
        // registers; the alert voices (gear shift, ABS, pit limiter, DRS)
        // can't fire airborne anyway.
        public bool DuckEngine       { get; set; } = false;
        public bool DuckAudio        { get; set; } = true;
        public bool DuckRoadBumps    { get; set; } = true;
        public bool DuckTractionLoss { get; set; } = true;
        public bool DuckRevLimiter   { get; set; } = true;
        public bool DuckGearShift    { get; set; } = false;
        public bool DuckAbs          { get; set; } = false;
        public bool DuckPitLimiter   { get; set; } = false;
        public bool DuckDrs          { get; set; } = false;
        public bool DuckCollision    { get; set; } = false;

        // Set on the telemetry thread, read on the producer thread. A bool
        // read/write is atomic; eventual consistency is fine for haptics.
        private volatile bool _airborne;

        /// <summary>True while the active source reports the car off the ground
        /// AND this coordinator is enabled. The producer loop reads this to
        /// apply the airborne duck; the UI reads it for a live indicator.</summary>
        public bool AirborneActive => Enabled && _airborne;

        public override bool IsActive => AirborneActive;

        // Produces no audio; the ducking it requests is applied to the OTHER
        // voices in UpdateDucking. It also sits outside the sidechain tiers, so
        // it never ducks anything via ActivityLevel.
        public override double ActivityLevel => 0;
        public override void RenderAdd(float[] buffer, int count) { }

        public override void OnTelemetry(TelemetryFrame f)
        {
            // Null (universal source can't tell) is treated as "not airborne".
            _airborne = f.Airborne.GetValueOrDefault();
        }

        public override void Reset()
        {
            _airborne = false;
        }

        // Game closed / telemetry froze while the car was off the ground. The
        // _airborne latch is set only by OnTelemetry, so without this it would
        // stay true and keep ducking the other voices after the feed stops.
        // Drop it so the next real frame re-evaluates from grounded.
        public override void OnTelemetryStall()
        {
            _airborne = false;
        }
    }
}
