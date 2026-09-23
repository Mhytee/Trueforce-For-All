// Frozen-feed detector for the SimHub fallback source. Games whose reader
// reports a real pause flag are handled upstream (GameData.GamePaused);
// this catches the rest. A reader that stubs IsGamePaused() keeps
// re-reading the game's frozen memory through a pause, so SimHub keeps
// delivering fresh-looking frames whose VALUES are photocopies of one
// instant. Sustained effects latch amplitude from each copy, so the wheel
// hums the pre-pause engine forever (RaceRoom pause report, 2026-08-31;
// the 500 ms stall watchdog never fires because frames keep arriving).
//
// The values themselves are the packet counter we are not given: live
// physics never repeats itself. Idle RPM, the acceleration components and
// yaw rate are floats coming out of a running integrator; they differ
// every frame even in a parked car with the engine on. Bit-identical
// repeats across all of them for seconds mean nothing is simulating.

using System;

namespace TrueforceForAll.Core
{
    /// <summary>Watches a handful of jittery physics channels for exact
    /// repetition. Feed it every candidate frame; a true return means the
    /// feed is replaying one frozen snapshot and the frame should not be
    /// emitted (the host's stall machinery then settles the effects).
    /// Single-threaded: call from the source's emit path only.</summary>
    public sealed class FrozenTelemetryDetector
    {
        // Two seconds, not the stall watchdog's 500 ms, on purpose. This
        // path has no authoritative flag, and the failure costs are wildly
        // asymmetric: a late verdict is half a breath of extra hum after
        // ESC, while a false one mutes a live wheel at every stop light
        // (the shipped-and-reverted RS50 regression class). The realistic
        // false positive is a game with quantized telemetry holding
        // bit-stable values at idle; demanding two full seconds of exact
        // stillness across every channel buys that safety cheaply.
        public const double DefaultFreezeAfterSeconds = 2.0;

        private readonly double _freezeAfterSeconds;
        private bool _seeded;
        private double _rpms, _speedKmh, _heave, _sway, _surge, _yawRate;
        private long _lastChangeTicks;

        public FrozenTelemetryDetector(double freezeAfterSeconds = DefaultFreezeAfterSeconds)
        {
            _freezeAfterSeconds = freezeAfterSeconds;
        }

        /// <summary>Records one frame's channels and answers whether the feed
        /// is frozen. Channels a game never populates arrive as a constant
        /// (0): they carry no signal either way, and any single live channel
        /// is enough to keep the verdict "live".</summary>
        public bool Note(double rpms, double speedKmh,
                         double heave, double sway, double surge, double yawRate,
                         long nowTicks, long ticksPerSecond)
        {
            // Exact equality is the point, not an accident: we are detecting
            // copies of one snapshot, not values that drifted close together.
            bool same = _seeded
                && rpms == _rpms && speedKmh == _speedKmh
                && heave == _heave && sway == _sway && surge == _surge
                && yawRate == _yawRate;
            if (!same)
            {
                _seeded = true;
                _rpms = rpms; _speedKmh = speedKmh;
                _heave = heave; _sway = sway; _surge = surge;
                _yawRate = yawRate;
                _lastChangeTicks = nowTicks;
                return false;
            }

            // A parked car with the engine off repeats legitimately (all
            // zeros), and that snapshot drives no effect anyway. Only act
            // when the frozen values would keep something audible running.
            if (_rpms <= 0 && _speedKmh <= 0) return false;

            return nowTicks - _lastChangeTicks
                >= (long)(_freezeAfterSeconds * ticksPerSecond);
        }
    }
}
