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
// AC, etc.). Effects already tolerate cross-thread state mutation —
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

        /// <summary>Live measured frame rate based on inter-frame timing.
        /// Returns 0 when the source is idle (no frame in the last second).</summary>
        double MeasuredHz { get; }

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
        /// <summary>Yaw rate in deg/s. Null when source doesn't surface it.</summary>
        public double? YawRateDegPerSec;

        // ---- Driveline ----
        /// <summary>"R", "N", "1", "2", … — string convention matches SimHub's
        /// StatusDataBase.Gear so existing effect code compares unchanged.</summary>
        public string Gear;
        /// <summary>0 = ABS not active, &gt;0 = active. Edge transitions drive AbsClick PerTick mode.</summary>
        public int AbsActive;

        // ---- Tire grip ----
        /// <summary>Direct slip-ratio reading from a sim that exposes one
        /// (e.g. AC's wheelSlip[], Forza's TireCombinedSlip[]), max-abs across
        /// all four tires. ~0 = grip, &gt;0.05 = noticeable slip, &gt;0.5 =
        /// sliding hard. Null when the source can't measure slip directly —
        /// TractionLossEffect falls back to its yaw-rate / RPM-derivative
        /// heuristic in that case.</summary>
        public double? WheelSlip;

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

        // ---- Engine config (auto-detected from telemetry) ----
        /// <summary>Cylinder count reported by the sim for the active car
        /// (Forza's NumCylinders). When non-null and the user has no per-car
        /// engine override, EnginePulseEffect uses this for firing-frequency
        /// instead of the user's globally-configured Cylinders setting.
        /// Null when the source doesn't expose it (AC, SimHub fallback).</summary>
        public int? NumCylinders;

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

        public Action<TelemetryFrame> OnFrame { get; set; }

        public abstract void Start();
        public abstract void Stop();

        public virtual void Dispose() { Stop(); }

        // EMA on instantaneous rate. _measuredHz is updated on every frame;
        // the public getter zeros it out if the source has gone quiet so the
        // UI shows 0 Hz when a game is paused / unloaded rather than a stale
        // last-known value.
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastFrameTicks;
        private double _measuredHz;
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
                return _measuredHz;
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
                    double instHz = 1.0 / dtSec;
                    _measuredHz = _measuredHz * (1.0 - Alpha) + instHz * Alpha;
                }
            }
            System.Threading.Volatile.Write(ref _lastFrameTicks, now);
            frame.CapturedAtTicks = now;
            OnFrame?.Invoke(frame);
        }
    }
}
