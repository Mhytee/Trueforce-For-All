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
            // SimHub keeps re-delivering the last GameData for a while after
            // a game dies; emitting those zombie frames kept MeasuredHz alive
            // and telemetry freshness stamped through the game-close window,
            // holding force paths open against frozen data (2026-08-08,
            // trace-proven). No running game = no frames.
            if (data?.GameRunning != true) return;
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
            // Two distinct notions of "redline", kept separate on purpose:
            //   realRedline = the car's actual shift RPM IF the game reports one
            //     (CarSettings_RedLineRPM, else the per-gear redline). 0 when the
            //     game exposes none, never faked from MaxRpm. This is what the
            //     rev limiter thresholds against, so a game without a real
            //     redline takes the percentage-of-MaxRpm path (and the engage-%
            //     UI stays visible) instead of being treated as a redline game
            //     that can only fire at 100%.
            //   ledSpanTop = realRedline if present, else MaxRpm. The LED rev
            //     bar legitimately needs a span to fill against even when no
            //     redline is reported, so it keeps the MaxRpm fallback.
            double realRedline = d.CarSettings_RedLineRPM > 0 ? d.CarSettings_RedLineRPM
                               : d.CarSettings_CurrentGearRedLineRPM > 0 ? d.CarSettings_CurrentGearRedLineRPM
                               : 0.0;
            // Per-gear redline (no stable car-level value): used for the buzz but
            // kept OUT of the variant signature, since it changes every shift and
            // would otherwise spawn a junk variant per gear.
            bool redlinePerGear = d.CarSettings_RedLineRPM <= 0 && d.CarSettings_CurrentGearRedLineRPM > 0;
            double red = realRedline > 0 ? realRedline : d.MaxRpm;   // LED span top
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

            var frame = new TelemetryFrame
            {
                Rpms      = d.Rpms,
                MaxRpm    = d.MaxRpm,
                // SimHub reports 0..100; effects want 0..1. Clamp defensively
                // some games surface throttle outside 0..100 during clutch
                // engagement edge cases.
                Throttle01 = Clamp01(d.Throttle / 100.0),
                Brake01     = Clamp01(d.Brake / 100.0),
                Clutch01    = Clamp01(d.Clutch / 100.0),
                Handbrake01 = Clamp01(d.Handbrake / 100.0),

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
                // through StatusDataBase.
                PitLimiterActive = d.PitLimiterOn,
                DrsActive        = d.DRSEnabled,

                // See revPct computation above (redline-band, sim-matched).
                // RPMRedLineReached flickers like the SL flags; derive redline
                // from rpm vs the (stable) redline RPM instead.
                RpmPercent     = Clamp01(revPct),
                RedlineReached = red > 0 && d.Rpms >= red,
                // The car's REAL redline/shift RPM, or 0 when the game exposes
                // none (NOT faked from MaxRpm). The rev limiter falls back to
                // MaxRpm*Threshold itself when this is 0; see RevLimiterEffect.
                RedlineRpm        = realRedline,
                RedlineRpmPerGear = redlinePerGear,
            };
            LastRedlineRpm = realRedline;
            EmitFrame(frame);
        }

        // Most-recent REAL redline/shift RPM (CarSettings_RedLineRPM, else
        // per-gear redline). 0 when the game reports neither (no MaxRpm fallback,
        // unlike the LED rev-bar span). Cached so the plugin can overlay it onto
        // enhanced-source frames (AC, ...) that don't read a redline natively.
        public double LastRedlineRpm { get; private set; }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}
