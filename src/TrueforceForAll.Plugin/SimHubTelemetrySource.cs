// Universal fallback telemetry source. Translates SimHub's GameData
// (delivered via the plugin's DataUpdate callback at ~60 Hz, capped by the
// IDataPlugin pipeline) into a TelemetryFrame and emits it via OnFrame.
//
// This source works for every SimHub-supported game. Per-game enhanced
// sources (e.g., AcSharedMemoryTelemetrySource) replace it on game change
// when they can deliver physics-rate data; otherwise this stays active.

using System;
using GameReaderCommon;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    public sealed class SimHubTelemetrySource : TelemetrySourceBase
    {
        public override string Name => "SimHub";
        public override bool   IsEnhanced => false;
        public override bool   IsRunning  => _running;
        private bool _running;

        public override void Start() { _running = true; }
        public override void Stop()  { _running = false; }

        /// <summary>Called by the plugin from SimHub's DataUpdate callback.
        /// Translates GameData into a TelemetryFrame and emits via OnFrame.
        /// No-op when not running or when NewData is null.</summary>
        public void PushFromGameData(GameData data)
        {
            if (!_running) return;
            var d = data?.NewData;
            if (d == null) return;

            // Rev-bar fill for the rim LEDs. iRacing's own rev lights use the
            // car's SHIFT-LIGHT band (first-light RPM -> shift RPM), a narrow
            // window near the top, NOT CurrentDisplayedRPMPercent (which spans
            // MinimumShownRPM..redline and reads high through normal driving,
            // so our bar lit ~8 while iRacing's was still 0). Prefer the
            // shift-light band so the fill matches the sim; fall back to the
            // displayed percent, then raw Rpms/MaxRpm, for games/cars that
            // don't publish shift points.
            double slFirst = d.CarSettings_RPMShiftLight1;
            double slShift = d.CarSettings_RPMShiftLight2;
            double revPct;
            if (slFirst > 0 && slShift > slFirst)
                revPct = (d.Rpms - slFirst) / (slShift - slFirst);
            else if (d.CarSettings_CurrentDisplayedRPMPercent > 0)
                revPct = d.CarSettings_CurrentDisplayedRPMPercent / 100.0;
            else if (d.MaxRpm > 0)
                revPct = d.Rpms / d.MaxRpm;
            else
                revPct = 0;

            var frame = new TelemetryFrame
            {
                Rpms      = d.Rpms,
                MaxRpm    = d.MaxRpm,
                // SimHub reports 0..100; effects want 0..1. Clamp defensively
                // some games surface throttle outside 0..100 during clutch
                // engagement edge cases.
                Throttle01 = Clamp01(d.Throttle / 100.0),

                SpeedKmh           = d.SpeedKmh,
                AccelerationHeave  = d.AccelerationHeave,
                AccelerationSway   = d.AccelerationSway,
                AccelerationSurge  = d.AccelerationSurge,
                // YawChangeVelocity is filled per-game by SimHub's reader; the
                // older OrientationYawVelocity is the universal fallback. Match
                // the precedence TractionLossEffect used previously.
                YawRateDegPerSec   = d.YawChangeVelocity ?? d.OrientationYawVelocity,

                Gear      = d.Gear,
                AbsActive = d.ABSActive,
                TcActive  = d.TCActive,
                // Modal-flag overlays. Each is null when the active game's
                // SimHub plugin doesn't expose it; effects gracefully skip
                // when they read null. SimHub maps these from per-game
                // shared memory / UDP in its own readers, so we just pass
                // through StatusDataBase. KERS / ERS deployment isn't on
                // the universal API (only ERSStored/Max/Percent storage
                // state), KersActive stays null until we add a per-game
                // overlay that derives it from the ERS-percent derivative.
                PitLimiterActive = d.PitLimiterOn,
                DrsActive        = d.DRSEnabled,

                // See revPct computation above (shift-light band, sim-matched).
                RpmPercent     = Clamp01(revPct),
                RedlineReached = d.CarSettings_RPMRedLineReached >= 0.5,
            };
            EmitFrame(frame);
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}
