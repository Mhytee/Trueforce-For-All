// Frame enrichment: the pure derivation step between "a source emitted a
// frame" and "effects consume it". Extracted from TrueforcePlugin.DispatchFrame
// in phase 0c of docs/haptic-engine-plan.md so the replay harness can run the
// exact production logic headless. Two jobs today (the CTM composer grows here
// in phase 2):
//
//   1. Enhanced-source overlay: AC/Forza-native frames deliberately skip
//      slow-rate fields whose physics-rate fidelity wouldn't be perceptible;
//      fill them from the host's cached SimHub readings so effects always see
//      a complete frame.
//   2. Universal collision derivation: when the source didn't populate
//      CollisionMagnitude, derive it from a sudden three-axis accel spike.
//
// Pure static over (ref frame, overlay snapshot): no clocks, no host types,
// no allocation. The host builds a SimHubOverlay from whatever it cached on
// its own tick; the harness builds one straight from fixture data.

using System;

namespace TrueforceForAll.Core
{
    /// <summary>Snapshot of the slow-rate SimHub-side readings the enrichment
    /// overlay draws from. Value type so the telemetry thread reads a coherent
    /// copy the host published (struct assignment, no locking — same eventual-
    /// consistency contract as the rest of the frame pipeline).</summary>
    public struct SimHubOverlay
    {
        public double MaxRpm;
        public int    AbsActive;
        public double RedlineRpm;
        public int?   PitLimiterActive;
        public int?   DrsActive;
    }

    public static class FrameEnricher
    {
        // Collision-from-accel constants. Threshold ≈ 5g (≈49 m/s²), well
        // above hard cornering (~1.5-2g) and hard braking (~1g), squarely in
        // "something hit something" territory. Normalized: each ~50 m/s² over
        // the threshold = 1.0 magnitude unit, capped in the effect.
        private const double CollisionThresholdMps2 = 49.0;
        private const double NormalizePerMps2       = 0.02;

        /// <summary>Enrich a source frame in place. <paramref name="sourceIsEnhanced"/>
        /// gates the overlay (SimHub-fallback frames are already complete).
        /// <paramref name="suppressRedlineOverlay"/> keeps the unreliable SimHub
        /// Forza redline out of the rev limiter (see the caller's comment
        /// history: a bogus-but-in-range redline silently disables engagement).
        /// <paramref name="collisionThresholdMps2"/> lets slow-vehicle games
        /// lower the collision bar: the 5g default is tuned for racing
        /// speeds, but a farm tractor into a tree at 25 km/h peaks near
        /// 3.5g and deserves its thud (FS passes ~2g; its normal driving
        /// never brakes past ~0.7g, so the margin holds).</summary>
        public static void Enrich(ref TelemetryFrame frame, bool sourceIsEnhanced,
                                  in SimHubOverlay overlay, bool suppressRedlineOverlay,
                                  double collisionThresholdMps2 = CollisionThresholdMps2,
                                  bool sourcePublishesPhysics = true)
        {
            // A source can be enhanced and still carry no physics at all. The
            // arcade readers are exactly that: they publish force, not telemetry,
            // and emit an empty frame purely so the rate tracking stays alive.
            //
            // Overlaying SimHub's cached flags onto one of those is actively
            // harmful rather than merely useless. AbsActive is copied
            // unconditionally, so a flag left set by the last game SimHub DID
            // read becomes a permanent ABS buzz on an arcade cabinet. The
            // overlaid MaxRpm is worse still: it makes the resampler classify an
            // empty frame as physics, which is the very thing that tells the
            // effects to stay quiet.
            //
            // The SOURCE is asked, not the frame. Judging the frame looks
            // tempting and is wrong: a car parked in the pits with the engine off
            // carries no revs, no speed and no throttle either, and AC's whole
            // contract is that it leaves MaxRpm at 0 for this overlay to fill.
            if (sourceIsEnhanced && sourcePublishesPhysics)
            {
                // MaxRpm: fill-only, never clobber. AC leaves it at 0 on
                // purpose (SimHub already does that work correctly), but
                // Forza's sled carries EngineMaxRpm and our source parses
                // it. Assigning unconditionally threw that value away and
                // substituted SimHub's, which is ZERO whenever we own the
                // Forza Data Out port and forwarding is off (SimHub then
                // receives nothing). A zero MaxRpm silently stalled the
                // whole variant pipeline: the signature collapsed to
                // cyl-only, EnsureVariantForLiveSignature refused to
                // create a row for want of a rev-range discriminator, and
                // "Set redline" / the engine picker rejected every save
                // with "couldn't identify this car's engine variant yet"
                // while the panel still showed a correctly-resolved engine
                // from the bake. Same fill-only rule as the three fields
                // below; this one just never got the guard.
                if (frame.MaxRpm <= 0) frame.MaxRpm = overlay.MaxRpm;
                frame.AbsActive = overlay.AbsActive;
                // Redline: only fill when the enhanced source didn't supply its
                // own (none do today), and never when suppressed (Forza).
                if (frame.RedlineRpm <= 0 && !suppressRedlineOverlay)
                    frame.RedlineRpm = overlay.RedlineRpm;
                // Only overlay PitLimiter/DRS when the enhanced source itself
                // didn't populate them — preserves any future enhanced source
                // that reads them natively.
                if (frame.PitLimiterActive == null) frame.PitLimiterActive = overlay.PitLimiterActive;
                if (frame.DrsActive        == null) frame.DrsActive        = overlay.DrsActive;
            }

            // Universal collision derivation: only PC2's opponent-collision
            // signal populates CollisionMagnitude directly; everyone else gets
            // the accel-spike heuristic. Surge catches head-on / rear-end,
            // sway catches T-bones, heave catches hard landings / kerb slams.
            if (frame.CollisionMagnitude == null)
            {
                double sway  = frame.AccelerationSway  ?? 0;
                double heave = frame.AccelerationHeave ?? 0;
                double surge = frame.AccelerationSurge ?? 0;
                double peak  = Math.Max(Math.Abs(surge),
                               Math.Max(Math.Abs(sway), Math.Abs(heave)));
                if (peak > collisionThresholdMps2)
                    frame.CollisionMagnitude = (peak - collisionThresholdMps2) * NormalizePerMps2;
            }
        }
    }
}
