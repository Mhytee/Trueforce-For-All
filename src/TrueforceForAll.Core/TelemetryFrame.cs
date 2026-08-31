// The physics-rate data contract every effect consumes. Sources translate
// from their native data shape (SimHub StatusDataBase, AC shared memory,
// Forza UDP) into this struct; see ITelemetrySource.cs for the source
// abstraction and threading notes.

using System;

namespace TrueforceForAll.Core
{
    /// <summary>Per-tire value quad (FL/FR/RL/RR). Non-nullable doubles;
    /// validity is group-level (per game these channels are all-or-nothing,
    /// never per-corner) via TelemetryFrame.HasTireQuads today and the
    /// capability flags once the CTM grows them. Rollup helpers live here so
    /// effects and the capture log never re-derive them inconsistently.</summary>
    public struct TireQuad
    {
        public double FL, FR, RL, RR;

        public double FrontAvg => (FL + FR) * 0.5;
        public double RearAvg  => (RL + RR) * 0.5;

        public double MaxAbs
        {
            get
            {
                double f = Math.Max(Math.Abs(FL), Math.Abs(FR));
                double r = Math.Max(Math.Abs(RL), Math.Abs(RR));
                return Math.Max(f, r);
            }
        }

        /// <summary>0=FL, 1=FR, 2=RL, 3=RR, matching Forza / AC array order.</summary>
        public double this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0: return FL;
                    case 1: return FR;
                    case 2: return RL;
                    case 3: return RR;
                    default: throw new ArgumentOutOfRangeException(nameof(i));
                }
            }
        }

        public static TireQuad Of(double fl, double fr, double rl, double rr)
            => new TireQuad { FL = fl, FR = fr, RL = rl, RR = rr };
    }

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

        /// <summary>Brake, clutch and handbrake, 0..1, null when the source
        /// does not report them. Nullable rather than defaulting to 0 so a
        /// consumer can tell "not pressed" from "this game never says", which
        /// matters for a display: a brake meter pinned at empty all session is
        /// worse than one that is honestly absent. Populated by the sources
        /// that have them (SimHub, Forza's dash packet); AC's shared memory
        /// reader does not currently read them.</summary>
        public double? Brake01, Clutch01, Handbrake01;

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

        /// <summary>MEASURED vehicle sideslip in degrees: the signed angle between
        /// where the car points and where it is actually travelling, from the
        /// velocity vector in body axes. Positive and negative are opposite
        /// directions of slide, so unlike an inferred magnitude this can tell a
        /// left slide from a right one.
        ///
        /// Only set by sources that publish the velocity vector directly. It is
        /// deliberately NOT the same quantity as the sideslip TractionLossEffect
        /// infers from lateral acceleration and yaw rate: that identity assumes
        /// steady-state circular motion, blows up through acos when the two
        /// inputs nearly cancel, and carries no sign. Where this is present it
        /// should be preferred outright.</summary>
        public double? SideslipDeg;

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

        /// <summary>How many FORWARD gears this car has, when the source knows.
        /// Null means unknown, and consumers must treat unknown as "no opinion"
        /// rather than guessing: a learned highest-gear-seen would wrongly read
        /// 5th as top until the driver first used 6th, which would silence the
        /// shift cue exactly when it is most wanted. iRacing publishes it
        /// outright as DriverCarGearNumForward in its session info; most other
        /// sources leave this null and keep their existing behaviour.</summary>
        public int? ForwardGearCount;
        /// <summary>0 = ABS not active, &gt;0 = active. Edge transitions drive AbsClick PerTick mode.</summary>
        public int AbsActive;

        /// <summary>0 = pit limiter off, &gt;0 = engaged. Drives PitLimiterEffect's
        /// pulse train. Universal across sims, almost every racing game with
        /// pit lanes exposes this. Null when the source can't read it.</summary>
        public int? PitLimiterActive;

        /// <summary>0 = DRS not active, &gt;0 = wing open. F1-style sims only;
        /// null when the source can't read it (most non-F1 games).</summary>
        public int? DrsActive;

        /// <summary>0 = KERS / energy-recovery deployment off, &gt;0 = deploying.
        /// F1 / hybrid-era sims only; null otherwise. No effect consumes it
        /// today; it stays because the tfcap capture format (CsvFrameReader /
        /// GoldenCaptureLog column "kers") records it and the frozen fixture
        /// CSVs carry the column.</summary>
        public int? KersActive;

        // ---- Tire grip ----
        /// <summary>Direct slip-ratio reading from a sim that exposes one
        /// (e.g. AC's wheelSlip[], Forza's TireCombinedSlip[]), LOAD-WEIGHTED
        /// across the four tyres: sum(|slip_i| * load_i) / sum(load_i), so an
        /// unloaded wheel (airborne, one-wheel lift) contributes nothing and a
        /// four-wheel slide reads full strength (issue #30, see LoadWeighting).
        /// ~0 = grip, &gt;0.05 = noticeable slip, &gt;0.5 = sliding hard. Null
        /// when the source can't measure slip directly; TractionLossEffect falls
        /// back to its yaw-rate / RPM-derivative heuristic in that case.</summary>
        public double? WheelSlip;

        /// <summary>Front-axle slip angle in radians, signed (sign follows the
        /// slip direction), averaged across the front-left/right tires. Forza's
        /// TireSlipAngle[] (packet offset 164), front pair only. This is the
        /// primary input to a self-aligning-torque (SAT) steering-force model:
        /// |force| rises with slip angle, peaks near the tire's optimal angle,
        /// then falls as the front saturates, the cue that tells you the front
        /// is washing into understeer. Null when the source can't provide it
        /// (AC native today, SimHub fallback); 0 during the spawn settle window
        /// so a placement transient can't jolt the wheel through the model.</summary>
        public double? FrontSlipAngleRad;

        /// <summary>Front-axle suspension travel in meters, averaged across the
        /// front-left/right tires. Forza's SuspensionTravelMeters[] (packet
        /// offset 196), front pair only. A vertical-load proxy for the SAT
        /// model (more compression = more load on the contact patch = more
        /// steering force). Null when the source can't provide it; 0 during the
        /// spawn settle window.</summary>
        public double? FrontSuspTravelMeters;

        // ---- Per-tire quads (CTM growth, additive) ----
        /// <summary>True when the source populated the per-tire quads below.
        /// Group-level validity: sources that carry per-wheel data (Forza's
        /// sled block, AC's physics page) set it every live frame; the SimHub
        /// fallback never does. Interim marker until the CTM capability flags
        /// land; consumers must not read the quads when this is false.</summary>
        public bool HasTireQuads;

        /// <summary>Longitudinal slip ratio per tire, signed (+ = wheelspin,
        /// - = lockup). Forza TireSlipRatio[4] @84.</summary>
        public TireQuad TireSlipRatio;

        /// <summary>Slip angle per tire in radians, signed. Forza
        /// TireSlipAngle[4] @164. The front pair average is what
        /// FrontSlipAngleRad carries today.</summary>
        public TireQuad TireSlipAngleRad;

        /// <summary>Combined slip per tire (lateral+longitudinal magnitude,
        /// ~1.0 = at the limit in Forza's normalization, calibrated to grip
        /// utilization later). Forza TireCombinedSlip[4] @180. WheelSlip
        /// carries this quad's MaxAbs today.</summary>
        public TireQuad TireCombinedSlip;

        /// <summary>Suspension travel per wheel in meters (vertical-load
        /// proxy). Forza SuspensionTravelMeters[4] @196.</summary>
        public TireQuad SuspTravelM;

        /// <summary>Surface-rumble magnitude per wheel, [0..1]-ish. Forza
        /// SurfaceRumble[4] @148. SurfaceRumble (scalar) carries this quad's
        /// MaxAbs today.</summary>
        public TireQuad SurfaceRumbleQ;

        /// <summary>Wheel rotation speed per wheel in rad/s. Forza
        /// WheelRotationSpeed[4] @100. Feeds the rear rotation-pulse texture
        /// (frequency tracks wheel revs) and wheelspin/lock detection.</summary>
        public TireQuad WheelRotRadS;

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
        /// pulse in RoadBumpsEffect on rising edge so kerb hits feel
        /// percussive even when the surface-rumble channel is also active.</summary>
        public bool? OnRumbleStrip;

        /// <summary>True while the vehicle or an attached implement is
        /// actually working: lowered into the ground or powered on (Farming
        /// Simulator's TF4ALL mod). Null on sources that don't report it.
        /// Edge-triggers ImplementThudEffect (the linkage clunk).</summary>
        public bool? ImplementWorking;

        /// <summary>True while implement hydraulics are in motion: a
        /// lower/raise animating through the attacher joints, or a fold
        /// deploying (mod 0.2.8+). Null on sources that don't report it.
        /// Drives the hydraulic hum; its falling edge lands the settle
        /// thud.</summary>
        public bool? ImplementMoving;

        /// <summary>How fast the implement hydraulics are traveling, as
        /// the fraction of full travel covered per second: ~0.5-1.0 for a
        /// three-point lower, ~0.1 for a big slow fold, 0 while idle (mod
        /// 0.2.14+). Null on sources that don't report it. Rides the
        /// hydraulic hum's pitch.</summary>
        public float? ImplementSpeed;

        /// <summary>Session-monotonic count of discrete mechanism toggles
        /// with no measurable travel: a combine's straw-swath flap and kin
        /// (mod 0.2.16+). Null on sources that don't report it. Each
        /// increment lands a light thud.</summary>
        public int? ImplementEvent;

        /// <summary>Session-monotonic count of STAGE ends within a running
        /// implement cycle: one part of a multi-part sequence finished while
        /// the cycle carries on, e.g. a wide cultivator's lift completing
        /// before its wings fold (mod 0.2.22+). Null on sources that don't
        /// report it. Each increment lands a momentum-weighted thud.</summary>
        public int? ImplementPhase;

        /// <summary>How fast the part that just finished its stage was
        /// travelling when it stopped, in the same fraction-of-full-travel
        /// per second unit as <see cref="ImplementSpeed"/> (mod 0.2.22+).
        /// Scales the stage thud.</summary>
        public float? ImplementPhaseSpeed;

        /// <summary>True while the current implement motion is MANUAL-ONLY:
        /// a stick-driven arm (loader, crane, telehandler) with no joint,
        /// fold or pipe cycle running (mod 0.2.19+). Null on sources that
        /// don't report it. Manual settles land quieter, scaled by the
        /// speed at the stop.</summary>
        public bool? ImplementManual;

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

        // ---- CTM rollups (phase 2, stamped by CtmComposer) ----
        /// <summary>Front-axle grip utilization in combined-slip units
        /// (~1.0 = at the limit), LOAD-WEIGHTED across the pair: the tire
        /// carrying the cornering load dominates the reading instead of the
        /// unloaded inside tire diluting it. Null until CtmComposer ran on a
        /// frame with tire quads.</summary>
        public double? FrontGrip01;

        /// <summary>Rear-axle grip utilization, same convention.</summary>
        public double? RearGrip01;

        /// <summary>RearGrip01 - FrontGrip01. Positive = the rear is closer
        /// to (or further past) its limit than the front — the oversteer
        /// direction. Null when the rollups are null.</summary>
        public double? GripBalance;

        /// <summary>Edge events derived by EventDeriver from frame-to-frame
        /// transitions (breakaway, lockup, wheelspin, gear, kerb, airborne).
        /// None on sources/paths that don't run the deriver — check
        /// <see cref="Caps"/> for <see cref="SignalGroups.DerivedEvents"/> to
        /// tell that apart from "no edge this frame".</summary>
        public FrameEvents Events;

        /// <summary>Capability flags: which signal groups this frame actually
        /// carries (Native, inferred from field presence) and which engine
        /// stages ran on it (Derived, stamped by the stage). Stamped on the
        /// engine thread's CTM stage; None on paths that bypass it.</summary>
        public SignalGroups Caps;

        // ---- Diagnostics ----
        /// <summary>Stopwatch ticks at which the source captured this frame. Set by EmitFrame.</summary>
        public long CapturedAtTicks;
    }

    /// <summary>Frame-to-frame edge events (phase 2 EventDeriver). Start/End
    /// pairs are hysteresis-gated in the deriver so a value hovering at a
    /// threshold can't machine-gun events.</summary>
    [Flags]
    public enum FrameEvents
    {
        None                = 0,
        FrontBreakawayStart = 1 << 0,   // front axle crossed past its grip limit
        FrontBreakawayEnd   = 1 << 1,
        RearBreakawayStart  = 1 << 2,   // rear stepped out
        RearBreakawayEnd    = 1 << 3,
        LockupStart         = 1 << 4,   // braking slip ratio collapsed negative
        LockupEnd           = 1 << 5,
        WheelspinStart      = 1 << 6,   // drive-wheel slip ratio ran away positive
        WheelspinEnd        = 1 << 7,
        GearChanged         = 1 << 8,
        RumbleStripStart    = 1 << 9,
        RumbleStripEnd      = 1 << 10,
        AirborneStart       = 1 << 11,
        AirborneEnd         = 1 << 12,
    }

    /// <summary>CTM capability flags (phase 2). One bit per signal GROUP, so
    /// consumers ask "does this frame carry X" once instead of null-checking
    /// individual fields — and, for the Derived bits, so effects know whether
    /// an engine stage ran on this frame at all. That second question is the
    /// one null checks cannot answer: <c>Events == None</c> means either "no
    /// edge this frame" or "nobody derived events", and an effect that guesses
    /// wrong either misses every shift or double-triggers. Native bits are
    /// inferred from field presence (SignalCaps.InferNative); Derived bits are
    /// stamped by the stage that computed the data (CtmComposer, EngineLoop's
    /// deriver step).</summary>
    [Flags]
    public enum SignalGroups
    {
        None = 0,

        // ---- Native: measured by the telemetry source ----
        Gear        = 1 << 0,   // Gear string present
        Steering    = 1 << 1,   // SteeringAngle
        AccelG      = 1 << 2,   // surge / sway / heave, any of them
        SlipScalars = 1 << 3,   // WheelSlip / FrontSlipAngleRad
        TireQuads   = 1 << 4,   // per-tire quads (HasTireQuads)
        SurfaceFeel = 1 << 5,   // SurfaceRumble / OnRumbleStrip
        Airborne    = 1 << 6,
        Collision   = 1 << 7,   // CollisionMagnitude

        // ---- Derived: computed by engine stages, not the source ----
        AxleRollups   = 1 << 16,  // CtmComposer ran (FrontGrip01/RearGrip01/GripBalance)
        DerivedEvents = 1 << 17,  // EventDeriver ran (Events field is authoritative)
    }

    /// <summary>Stamps the Native half of <see cref="SignalGroups"/> from
    /// field presence. Called on the engine thread on the RESAMPLED frame
    /// (EngineLoop's CTM stage) so the caps always describe exactly the frame
    /// the effects receive — no per-source stamping to keep in sync, and
    /// nothing for the resampler to carry through interpolation.</summary>
    public static class SignalCaps
    {
        public static SignalGroups InferNative(in TelemetryFrame f)
        {
            var caps = SignalGroups.None;
            if (!string.IsNullOrEmpty(f.Gear))            caps |= SignalGroups.Gear;
            if (f.SteeringAngle.HasValue)                 caps |= SignalGroups.Steering;
            if (f.AccelerationSurge.HasValue
                || f.AccelerationSway.HasValue
                || f.AccelerationHeave.HasValue)          caps |= SignalGroups.AccelG;
            if (f.WheelSlip.HasValue
                || f.FrontSlipAngleRad.HasValue)          caps |= SignalGroups.SlipScalars;
            if (f.HasTireQuads)                           caps |= SignalGroups.TireQuads;
            if (f.SurfaceRumble.HasValue
                || f.OnRumbleStrip.HasValue)              caps |= SignalGroups.SurfaceFeel;
            if (f.Airborne.HasValue)                      caps |= SignalGroups.Airborne;
            if (f.CollisionMagnitude.HasValue)            caps |= SignalGroups.Collision;
            return caps;
        }
    }
}
