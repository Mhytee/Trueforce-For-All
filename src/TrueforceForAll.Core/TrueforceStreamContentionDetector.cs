using System;

namespace TrueforceForAll.Core
{
    /// <summary>Tells a second Trueforce writer on the wheel apart from our own
    /// stream, from two monotonic counters sampled together: the packets the
    /// USB capture saw on the wheel's stream endpoint (everyone's) and the
    /// packets we wrote ourselves. What the capture saw beyond what we wrote
    /// is somebody else's stream: a game's native Trueforce, running beside us.
    ///
    /// Why it matters: the endpoint takes one packet per millisecond in total
    /// and holds its torque bytes as a level, so two 1 kHz writers alternate
    /// frames and the motor steps between two targets every millisecond.
    /// mescon measured that on an RS50 as a 500 Hz square wave on the torque
    /// field with the audio at half rate (logitech-trueforce-linux-driver,
    /// TRUEFORCE_PROTOCOL.md, "One writer at a time"). Users hear it as a loud
    /// whine the moment a native-Trueforce game and the plugin both stream.
    ///
    /// Pure and clock-free (the caller passes the time) so it can be unit
    /// tested. Two guards against the capture's own timing, which can only ever
    /// LAG the wire (a packet is counted before it is written): the verdict
    /// needs the cumulative excess to be real (a lag that catches up in a burst
    /// nets to zero) AND the excess to keep growing over consecutive windows
    /// (a single catch-up burst is one window). Clearing needs several quiet
    /// windows, so a game's session teardown does not flap it. Either counter
    /// going backwards (a fresh capture process, a re-attached device)
    /// re-baselines silently rather than reading as a burst, and a slow
    /// negative drift from dropped capture records is floored so it cannot
    /// hide a real writer for long.</summary>
    public sealed class TrueforceStreamContentionDetector
    {
        public const int WindowMs = 1000;
        /// <summary>Foreign packets per second that count as a writer. A native
        /// SDK streams at 1 kHz (about 500/s each reach the wire when two
        /// writers alternate), so this sits well below either and well above
        /// the capture's sampling jitter.</summary>
        public const int OnPerSec = 150;
        public const int OnWindows = 2;
        /// <summary>Foreign packets since baseline the verdict also needs, so
        /// a capture catching up on its own lag never declares.</summary>
        public const int OnPackets = 300;
        public const int OffPerSec = 30;
        public const int OffWindows = 5;
        /// <summary>How far the excess may drift negative (capture records
        /// lost) before the baseline is pulled up to it.</summary>
        public const int DriftFloor = 3000;

        private bool _baselined;
        private long _lastTap, _lastOurs, _lastMs, _offset, _lastExcess;
        private int _onRun, _offRun;

        public bool Detected { get; private set; }
        /// <summary>Foreign packets per second over the last completed window.</summary>
        public int LastForeignPerSec { get; private set; }

        public void Reset()
        {
            _baselined = false;
            _onRun = _offRun = 0;
            Detected = false;
            LastForeignPerSec = 0;
        }

        /// <summary>Feed one joint sample of the two counters. Cheap enough to
        /// call every tick; the rate work happens once per window. Returns
        /// true when the verdict flipped on this call.</summary>
        public bool Observe(long tapStreamPackets, long ourWrites, long nowMs)
        {
            if (!_baselined || tapStreamPackets < _lastTap || ourWrites < _lastOurs)
            {
                _baselined = true;
                _lastTap = tapStreamPackets;
                _lastOurs = ourWrites;
                _lastMs = nowMs;
                _offset = tapStreamPackets - ourWrites;
                _lastExcess = 0;
                _onRun = _offRun = 0;
                return false;
            }
            _lastTap = tapStreamPackets;
            _lastOurs = ourWrites;

            long excess = (tapStreamPackets - ourWrites) - _offset;
            if (excess < -DriftFloor)
            {
                _offset = tapStreamPackets - ourWrites;
                excess = 0;
                _lastExcess = 0;
            }

            long elapsed = nowMs - _lastMs;
            if (elapsed < WindowMs) return false;

            long grew = excess - _lastExcess;
            if (grew < 0) grew = 0;
            LastForeignPerSec = (int)(grew * 1000 / elapsed);
            _lastExcess = excess;
            _lastMs = nowMs;

            bool was = Detected;
            if (!Detected)
            {
                _onRun = LastForeignPerSec >= OnPerSec ? _onRun + 1 : 0;
                if (_onRun >= OnWindows && excess >= OnPackets)
                {
                    Detected = true;
                    _offRun = 0;
                }
            }
            else
            {
                _offRun = LastForeignPerSec <= OffPerSec ? _offRun + 1 : 0;
                if (_offRun >= OffWindows)
                {
                    Detected = false;
                    _onRun = 0;
                    // Fresh baseline: the foreign packets already counted belong
                    // to the session that just ended.
                    _offset = tapStreamPackets - ourWrites;
                    _lastExcess = 0;
                }
            }
            return was != Detected;
        }
    }
}
