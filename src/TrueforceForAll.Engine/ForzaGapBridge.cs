// Short-gap bridge for the SimHub forward path. Forza flips IsRaceOn to 0
// (and zeroes the whole packet) at every in-game replay loop, rewind, and
// menu blip. SimHub keys "Game connected" off that flag, so each blip costs
// a disconnect/reconnect — and ShakeIt reacts to every reconnect by tearing
// down and rebuilding its audio output 5 s into the next pass, cutting the
// shakers mid-replay (the "Connected (2 channels available)" drop).
//
// The bridge masks SHORT raceOn=0 gaps on the FORWARDED copy only: raceOn
// is patched back to 1 and the static identity fields (engine rpm range,
// car ordinal/class/PI/drivetrain/cylinders) are restored from the last
// live packet, so SimHub sees a connected, idling, stationary car instead
// of a disconnect — and crucially never sees a car CHANGE, which would
// reload per-car profiles. All dynamics stay zero (the game already zeroed
// them): effects fall naturally silent for the gap.
//
// Gaps longer than MaxGapSeconds stop being bridged and SimHub disconnects
// once, honestly — a long menu stay should read as "not racing".
//
// The plugin's own pipeline parses the ORIGINAL packet (the bridge runs on
// a copy), so pause detection / Mode B zero-force behavior is unaffected.

using System;

namespace TrueforceForAll.Core
{
    public sealed class ForzaGapBridge
    {
        /// <summary>Master switch (settings-backed). Default on.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Longest raceOn=0 gap that gets masked. Replay loops and
        /// rewinds are 2-10 s; real menu stays run far longer.</summary>
        public double MaxGapSeconds { get; set; } = 15.0;

        // Sled layout constants (stable across every Forza title we parse):
        private const int OffMaxRpm     = 8;    // EngineMaxRpm f32, IdleRpm f32 at 12
        private const int RpmBlockLen   = 8;
        private const int OffCarOrdinal = 212;  // CarOrdinal..NumCylinders, 5 x i32
        private const int CarBlockLen   = 20;
        private const int MinLen        = 232;  // full sled

        private readonly byte[] _snapshot = new byte[512];
        private int  _snapshotLen;
        private bool _hasSnapshot;
        private long _lastLiveTicks;

        /// <summary>Inspect (and possibly patch) the packet in <paramref name="buf"/>
        /// destined for the forward target. Returns true when the packet was
        /// rewritten as a bridge packet. Call with the caller's monotonic
        /// clock; <paramref name="ticksPerSecond"/> converts it.</summary>
        public bool Process(byte[] buf, int len, long nowTicks, double ticksPerSecond)
        {
            if (!Enabled || len < MinLen || len > _snapshot.Length) return false;

            bool live = buf[0] != 0 || buf[1] != 0 || buf[2] != 0 || buf[3] != 0;
            if (live)
            {
                Buffer.BlockCopy(buf, 0, _snapshot, 0, len);
                _snapshotLen   = len;
                _hasSnapshot   = true;
                _lastLiveTicks = nowTicks;
                return false;
            }

            // raceOn = 0. Bridge only when we have a same-shape live packet
            // recently enough — a different length means the title (and thus
            // layout) changed under us, so pass the honest packet through.
            if (!_hasSnapshot || len != _snapshotLen) return false;
            double gapSec = (nowTicks - _lastLiveTicks) / Math.Max(1.0, ticksPerSecond);
            if (gapSec > MaxGapSeconds) return false;

            buf[0] = 1; buf[1] = 0; buf[2] = 0; buf[3] = 0;
            Buffer.BlockCopy(_snapshot, OffMaxRpm,     buf, OffMaxRpm,     RpmBlockLen);
            Buffer.BlockCopy(_snapshot, OffCarOrdinal, buf, OffCarOrdinal, CarBlockLen);
            return true;
        }
    }
}
