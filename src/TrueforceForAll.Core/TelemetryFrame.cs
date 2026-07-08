// The physics-rate data contract every effect consumes. Sources translate
// from their native data shape (SimHub StatusDataBase, AC shared memory,
// Forza UDP) into this struct; see ITelemetrySource.cs for the source
// abstraction and threading notes.

namespace TrueforceForAll.Core
{
    /// <summary>Snapshot of the physics-rate signals every effect consumes.
    /// Sources translate from their native data shape into this struct and
    /// emit one per native-source tick.</summary>
    public struct TelemetryFrame
    {
        // ---- Engine ----
        public double Rpms;
        public double MaxRpm;
        /// <summary>Throttle pedal, normalized 0..1.</summary>
        public double Throttle01;

        // ---- Motion ----
        public double SpeedKmh;
        /// <summary>Vertical acceleration in m/s². Null when source doesn't surface it.</summary>
        public double? AccelerationHeave;
        /// <summary>Lateral acceleration in m/s². Null when source doesn't surface it.</summary>
        public double? AccelerationSway;
        /// <summary>Longitudinal acceleration in m/s². Positive = forward.
        /// Null when source doesn't surface it. Drives the head-on / rear-end
        /// branch of CollisionEffect's spike detection, frontal impacts
        /// register here, not in sway/heave.</summary>
        public double? AccelerationSurge;
        /// <summary>Yaw rate in deg/s. Null when source doesn't surface it.</summary>
        public double? YawRateDegPerSec;

        /// <summary>Steering input normalized to roughly [-1, 1]: 0 = centered,
        /// -1 / +1 = full lock either way (may slightly exceed on countersteer).
        /// Sign convention is the source's, not the wheel's. Only the enhanced
        /// sources that read it natively populate this (AC's physics page
        /// today); the universal SimHub fallback leaves it null because
        /// StatusDataBase exposes no universal steering field. Consumed by the
        /// stationary-spring FFB floor, which no-ops when this is null.</summary>
        public double? SteeringAngle;

        // ---- Driveline ----
        /// <summary>"R", "N", "1", "2", …, string convention matches SimHub's
        /// StatusDataBase.Gear so existing effect code compares unchanged.</summary>
        public string Gear;
        /// <summary>0 = ABS not active, &gt;0 = active. Edge transitions drive AbsClick PerTick mode.</summary>
        public int AbsActive;

        /// <summary>0 = pit limiter off, &gt;0 = engaged. Drives PitLimiterEffect's
        /// pulse train. Universal across sims, almost every racing game with
        /// pit lanes exposes this. Null when the source can't read it.</summary>
        public int? PitLimiterActive;

        /// <summary>0 = DRS not active, &gt;0 = wing open. F1-style sims only;
        /// null when the source can't read it (most non-F1 games).</summary>
        public int? DrsActive;

        // ---- Tire grip ----
        /// <summary>Direct slip-ratio reading from a sim that exposes one
        /// (e.g. AC's wheelSlip[], Forza's TireCombinedSlip[]), max-abs across
        /// all four tires. ~0 = grip, &gt;0.05 = noticeable slip, &gt;0.5 =
        /// sliding hard. Null when the source can't measure slip directly
        /// TractionLossEffect falls back to its yaw-rate / RPM-derivative
        /// heuristic in that case.</summary>
        public double? WheelSlip;

        /// <summary>True when the car is off the ground (all wheels unloaded):
        /// Forza when suspension travel collapses to full droop on all four,
        /// AC when every wheel's vertical load reads ~0. Null when the source
        /// can't tell (the universal SimHub fallback has no wheel-load or
        /// suspension field). AirborneEffect reads this to duck the configured
        /// voices so jumps don't fire phantom slip / engine / road feedback.</summary>
        public bool? Airborne;

        /// <summary>1 = the game's traction control is actively intervening
        /// (cutting power because the wheels are slipping). 0 = TC not firing
        /// (or no TC system on this car). Used by TractionLossEffect's
        /// heuristic path as a confidence boost: when the game itself says
        /// the wheels are slipping, raise the slip estimate to a moderate
        /// floor even if the RPM/yaw heuristic didn't catch it. Only useful
        /// when WheelSlip is null (SimHub fallback), direct-slip sources
        /// already have ground truth.</summary>
        public int TcActive;

        // ---- Surface / road-feel (Forza-rich) ----
        /// <summary>Per-frame surface-rumble magnitude in [0..1], max-abs across
        /// all four tires. Forza's SurfaceRumble[] channel: a low-frequency
        /// vibration signal scaled by surface coarseness, the same one Turn 10's
        /// own Trueforce path uses inside Forza Motorsport. RoadBumpsEffect
        /// folds this in when present so dirt / gravel / asphalt textures
        /// drive haptic output even without strong vertical-accel transients.
        /// Null when the source doesn't surface it (AC, SimHub fallback).</summary>
        public double? SurfaceRumble;

        /// <summary>True if any wheel is currently on a rumble strip. Forza's
        /// WheelOnRumbleStrip[] booleans OR'd together. Drives an extra kerb
        /// pulse in RoadBumpsEffect on rising edge so curb hits feel
        /// percussive even when the surface-rumble channel is also active.</summary>
        public bool? OnRumbleStrip;

        // ---- Collision ----
        /// <summary>Normalized collision magnitude this frame. ~0 = no
        /// impact, 1.0 = moderate hit, 2.0+ = hard wreck. Source-defined
        /// scale: PC2 populates from mLastOpponentCollisionMagnitude
        /// directly; other sources derive from sudden lateral/vertical
        /// accel spikes in DispatchFrame's overlay step. Null when the
        /// source can't provide one (no accel data available). Effects
        /// fire on rising edge above their MinThreshold and scale
        /// amplitude by this value.</summary>
        public double? CollisionMagnitude;

        // ---- Engine config (auto-detected from telemetry) ----
        /// <summary>Cylinder count reported by the sim for the active car
        /// (Forza's NumCylinders). When non-null and the user has no per-car
        /// engine override, EnginePulseEffect uses this for firing-frequency
        /// instead of the user's globally-configured Cylinders setting.
        /// Null when the source doesn't expose it (AC, SimHub fallback).</summary>
        public int? NumCylinders;

        // ---- Rev / shift LEDs (SimHub-only) ----
        /// <summary>Rev-bar fill, 0..1, mapped over the meaningful idle→shift
        /// band the same way SimHub's dashboard rev bars are (from
        /// CarSettings_CurrentDisplayedRPMPercent). 0 on raw UDP sources that
        /// don't compute it; RpmLedController falls back to Rpms/MaxRpm then.</summary>
        public double RpmPercent;

        /// <summary>True when the sim says the shift point / redline has been
        /// reached (CarSettings_RPMRedLineReached). Drives the all-LED flash.
        /// False on sources that don't surface it.</summary>
        public bool RedlineReached;

        /// <summary>The car's redline / shift RPM when the game exposes one
        /// (SimHub's CarSettings_RedLineRPM, else per-gear redline), else the
        /// hard rev limit (MaxRpm). 0 when unknown. This is the most accurate
        /// reference for "near the limiter" haptics: a linear RPM value (unlike
        /// RpmPercent, which is a compressed LED-bar curve), so RevLimiterEffect
        /// thresholds against it and falls back to MaxRpm only where it's 0
        /// (e.g. Forza, whose UDP exposes no separate redline).</summary>
        public double RedlineRpm;

        /// <summary>True when <see cref="RedlineRpm"/> came from a PER-GEAR redline
        /// (the game exposes no stable car-level redline, only the current gear's
        /// shift point), so it changes on every gear shift. Used for the buzz the
        /// same as any redline, but EXCLUDED from the engine-variant signature so
        /// shifting through the gearbox doesn't spawn a new variant per gear.</summary>
        public bool RedlineRpmPerGear;

        // ---- Diagnostics ----
        /// <summary>Stopwatch ticks at which the source captured this frame. Set by EmitFrame.</summary>
        public long CapturedAtTicks;
    }
}
