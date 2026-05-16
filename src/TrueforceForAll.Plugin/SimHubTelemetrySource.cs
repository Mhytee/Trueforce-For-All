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
        private int  _lastRpmDiagMs;

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
            // iRacing telemetry reality (from the diag capture): SL1/SL2 are
            // BOOLEAN rev-light-stage flags that FLICKER 0/1 every sample near
            // their thresholds (SL2 is the blink stage), and
            // CurrentDisplayedRPMPercent is just rpm/maxRpm (lights far too
            // early). Driving off the booleans caused the bar to loop
            // (10->5->10...). RedLineRPM is the one stable signal. So derive
            // a smooth, monotonic curve purely from rpm vs redline: first LED
            // at RevBandStart*red (~ where the car's first shift light came
            // on in the diag, ~0.86*red), all 10 at redline. No booleans.
            const double RevBandStart = 0.87;
            double red = d.CarSettings_RedLineRPM > 0 ? d.CarSettings_RedLineRPM
                       : d.CarSettings_CurrentGearRedLineRPM > 0 ? d.CarSettings_CurrentGearRedLineRPM
                       : d.MaxRpm;
            double revPct;
            if (red > 0)
            {
                double lo = red * RevBandStart;
                revPct = red > lo ? (d.Rpms - lo) / (red - lo) : 0;
            }
            else if (d.CarSettings_CurrentDisplayedRPMPercent > 0)
                revPct = d.CarSettings_CurrentDisplayedRPMPercent / 100.0;
            else if (d.MaxRpm > 0)
                revPct = d.Rpms / d.MaxRpm;
            else
                revPct = 0;

            // Diagnostic: once/sec, dump the raw iRacing RPM fields so we can
            // see which the sim actually publishes and why the bar mismatches.
            // Temporary, removed once the mapping is dialled in.
            int nowMs = Environment.TickCount;
            if (d.Rpms > 0 && unchecked(nowMs - _lastRpmDiagMs) >= 1000)
            {
                _lastRpmDiagMs = nowMs;
                SimHub.Logging.Current.Info(
                    $"[RPM-LED-DIAG] rpm={d.Rpms:F0} max={d.MaxRpm:F0} " +
                    $"dispPct={d.CarSettings_CurrentDisplayedRPMPercent:F1} " +
                    $"SL1={d.CarSettings_RPMShiftLight1:F0} SL2={d.CarSettings_RPMShiftLight2:F0} " +
                    $"redRPM={d.CarSettings_RedLineRPM:F0} gearRed={d.CarSettings_CurrentGearRedLineRPM:F0} " +
                    $"minShown={d.CarSettings_MinimumShownRPM:F0} redReached={d.CarSettings_RPMRedLineReached:F0} " +
                    $"-> revPct={Clamp01(revPct):F2} level={(int)Math.Floor(Clamp01(revPct)*10+0.5)}");
            }

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

                // See revPct computation above (redline-band, sim-matched).
                // RPMRedLineReached flickers like the SL flags; derive redline
                // from rpm vs the (stable) redline RPM instead.
                RpmPercent     = Clamp01(revPct),
                RedlineReached = red > 0 && d.Rpms >= red,
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
