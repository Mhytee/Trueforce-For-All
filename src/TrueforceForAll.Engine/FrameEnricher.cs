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
        /// history: a bogus-but-in-range redline silently disables engagement).</summary>
        public static void Enrich(ref TelemetryFrame frame, bool sourceIsEnhanced,
                                  in SimHubOverlay overlay, bool suppressRedlineOverlay)
        {
            if (sourceIsEnhanced)
            {
                frame.MaxRpm    = overlay.MaxRpm;
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
            // sway catches T-bones, heave catches hard landings / curb slams.
            if (frame.CollisionMagnitude == null)
            {
                double sway  = frame.AccelerationSway  ?? 0;
                double heave = frame.AccelerationHeave ?? 0;
                double surge = frame.AccelerationSurge ?? 0;
                double peak  = Math.Max(Math.Abs(surge),
                               Math.Max(Math.Abs(sway), Math.Abs(heave)));
                if (peak > CollisionThresholdMps2)
                    frame.CollisionMagnitude = (peak - CollisionThresholdMps2) * NormalizePerMps2;
            }
        }
    }
}
