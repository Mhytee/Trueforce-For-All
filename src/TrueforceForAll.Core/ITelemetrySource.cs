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
//
// The TelemetryFrame data contract lives in TelemetryFrame.cs; the shared
// base class (rate tracking, session proxy) in TelemetrySourceBase.cs.

using System;

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
}
