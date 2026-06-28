// Telemetry source abstraction. Effects consume TelemetryFrame instead of
// SimHub's GameData so we can swap in a higher-rate source (game native
// shared memory, UDP, etc.) without touching effect code.
//
// The default fallback is SimHubTelemetrySource (lives in the Plugin
// assembly because it depends on GameReaderCommon). Per-game enhanced
// sources live alongside it and are selected by the plugin on game change.
//
// Threading: OnFrame fires on whichever thread the source polls on
// (SimHub's data tick for the SimHub source, a dedicated MMF thread for
// AC, etc.). Effects already tolerate cross-thread state mutation
// primitive double/float reads/writes are atomic on 64-bit .NET and the
// producer thread reads those fields with eventual-consistency semantics.

using System;
using System.Diagnostics;

namespace TrueforceForAll.Core
{
    public interface ITelemetrySource : IDisposable
    {
        /// <summary>Display name for UI / logs (e.g., "SimHub", "Assetto Corsa native").</summary>
        string Name { get; }

        /// <summary>True when the source is sampling at native physics rate
        /// (game shared memory / UDP). False for the SimHub fallback.
        /// Drives the "Enhanced Effects" badge in the UI.</summary>
        bool IsEnhanced { get; }

        /// <summary>True between Start() and Stop().</summary>
        bool IsRunning { get; }

        /// <summary>True when this source populates TelemetryFrame.NumCylinders
        /// each frame (Forza UDP today). Lets the plugin set EnginePulseEffect's
        /// AutoCylinderSource = "telemetry" eagerly on car change rather than
        /// waiting for the first frame to arrive, eliminating the brief null
        /// window between "car-change cleared the field" and "first telemetry
        /// frame populated it." False for sources that don't expose cyl
        /// (AC shared memory, SimHub fallback for most games).</summary>
        bool ProvidesNumCylinders { get; }

        /// <summary>Live measured frame rate based on inter-frame timing.
        /// Returns 0 when the source is idle (no frame in the last second).</summary>
        double MeasuredHz { get; }

        /// <summary>Milliseconds since the last emitted frame, or
        /// PositiveInfinity before the first frame. Detects telemetry that has
        /// actually stopped (pause / fast-travel / menu) with a tighter
        /// threshold than MeasuredHz's 1 s idle timeout. Forza freezes its
        /// IsRaceOn flag at the last value when it stops sending UDP, so
        /// IsSessionActive alone can't see a fast-travel pause; this can.</summary>
        double MsSinceLastFrame { get; }

        /// <summary>True when the game is in a state where force feedback should
        /// be flowing (on track / car live), vs menus, loading, replays, or
        /// pause. Drives the FFB-tap self-heal escalation so it only fires when
        /// FFB is genuinely expected. The base class infers this from physics
        /// (engine running / moving / pedal input) so it works for ANY game via
        /// the SimHub fallback; sources with an authoritative session flag
        /// (Forza IsRaceOn) override it.</summary>
        bool IsSessionActive { get; }

        /// <summary>True when IsSessionActive comes from the game's own
        /// pause/session flag (e.g. Forza IsRaceOn) rather than the physics
        /// proxy. The FFB pass-through uses this to release the wheel during a
        /// pause: it can only trust a !IsSessionActive reading to mean "paused"
        /// when the signal is authoritative, since the proxy also reads false at
        /// a legitimate standstill. False on the base class.</summary>
        bool HasAuthoritativeSessionState { get; }

        /// <summary>Subscribed by the plugin to fan out to effects. Set before
        /// Start(). Invoked on the source's polling thread.</summary>
        Action<TelemetryFrame> OnFrame { get; set; }

        void Start();
        void Stop();
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

        /// <summary>0 = KERS / energy-recovery deployment off, &gt;0 = deploying.
        /// F1 / hybrid-era sims only; null otherwise.</summary>
        public int? KersActive;

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

        // ---- Diagnostics ----
        /// <summary>Stopwatch ticks at which the source captured this frame. Set by EmitFrame.</summary>
        public long CapturedAtTicks;
    }

    /// <summary>Default base class for sources. Handles MeasuredHz tracking
    /// (EMA on inter-frame intervals) and stamps CapturedAtTicks. Subclasses
    /// build a TelemetryFrame and call EmitFrame.</summary>
    public abstract class TelemetrySourceBase : ITelemetrySource
    {
        public abstract string Name { get; }
        public abstract bool IsEnhanced { get; }
        public abstract bool IsRunning { get; }

        // Default false. Sources that surface NumCylinders override to true.
        public virtual bool ProvidesNumCylinders => false;

        public Action<TelemetryFrame> OnFrame { get; set; }

        public abstract void Start();
        public abstract void Stop();

        public virtual void Dispose() { Stop(); }

        // We smooth the inter-frame INTERVAL (an EMA on dt), then report its
        // reciprocal. Averaging 1/dt directly (the old approach) is biased
        // high: 1/x is convex, so mean(1/dt) >= 1/mean(dt) by Jensen's
        // inequality, and any timing jitter inflates the readout. UDP delivery
        // in particular is bursty: the OS hands the receive thread two
        // coalesced datagrams microseconds apart, producing a momentary
        // instantaneous rate of thousands of Hz that drags an EMA-of-rate well
        // above the true packet cadence (a real 60 Hz Forza stream read as
        // 130-150 Hz). Averaging dt linearly cancels those bursts against the
        // gaps that follow them, so 1/mean(dt) recovers the true throughput.
        // The public getter zeros out if the source has gone quiet so the UI
        // shows 0 Hz when a game is paused / unloaded rather than a stale value.
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastFrameTicks;
        private double _emaIntervalSec;
        private const double Alpha = 0.1;       // EMA smoothing factor
        private const double IdleTimeoutSec = 1.0;

        public double MeasuredHz
        {
            get
            {
                long last = System.Threading.Volatile.Read(ref _lastFrameTicks);
                if (last == 0) return 0;
                double sinceSec = (_sw.ElapsedTicks - last) / (double)Stopwatch.Frequency;
                if (sinceSec > IdleTimeoutSec) return 0;
                double interval = _emaIntervalSec;
                return interval > 0 ? 1.0 / interval : 0;
            }
        }

        public double MsSinceLastFrame
        {
            get
            {
                long last = System.Threading.Volatile.Read(ref _lastFrameTicks);
                if (last == 0) return double.PositiveInfinity;
                return (_sw.ElapsedTicks - last) * 1000.0 / Stopwatch.Frequency;
            }
        }

        protected void EmitFrame(TelemetryFrame frame)
        {
            long now = _sw.ElapsedTicks;
            long last = _lastFrameTicks;
            if (last != 0)
            {
                double dtSec = (now - last) / (double)Stopwatch.Frequency;
                if (dtSec > 0)
                {
                    _emaIntervalSec = _emaIntervalSec > 0
                        ? _emaIntervalSec * (1.0 - Alpha) + dtSec * Alpha
                        : dtSec;
                }
            }
            System.Threading.Volatile.Write(ref _lastFrameTicks, now);
            frame.CapturedAtTicks = now;
            _lastFrame = frame;
            OnFrame?.Invoke(frame);
        }

        // Last frame emitted, for the default IsSessionActive physics proxy.
        private TelemetryFrame _lastFrame;

        // Universal "force feedback should be flowing" signal, derived from the
        // last frame so it works for any game through the SimHub fallback. False
        // when no frames are arriving (idle / paused / menu where telemetry
        // stops), otherwise true when the car looks live: engine running,
        // moving, or pedal input. Sources with an explicit session flag override.
        public virtual bool IsSessionActive
        {
            get
            {
                if (MeasuredHz <= 0) return false;   // no telemetry flowing
                var f = _lastFrame;
                return f.Rpms > 1.0 || f.SpeedKmh > 2.0 || f.Throttle01 > 0.02;
            }
        }

        // Base sources infer IsSessionActive from physics, not an authoritative
        // pause flag. Sources with a real session signal (Forza) override this.
        public virtual bool HasAuthoritativeSessionState => false;
    }
}
