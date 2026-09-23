// Which HID++ report carries the game's force?
//
// The G-series force write (page 0x8123, function 2) can arrive on the long
// report 0x11 or the very-long report 0x12, and which one carries the real
// stream is a driver-state fact, not a per-wheel constant. The RS50 puts most
// of it on 0x12 (issue #8). The G PRO used 0x11 in every capture until
// 2026-08-28, when the owner's G PRO ran two whole sessions on 0x12 only and
// went back to 0x11 after an Assetto Corsa restart. In either state the OTHER
// report keeps carrying occasional writes of the same fn2 shape whose bytes
// 10-11 are not a force (a G PRO read those as a fallback in May 2026 and
// jerked at every standstill), so the two must never be merged: exactly one
// report is the live channel and the other is ignored.
//
// What counts is CHANGE, not rate. The Logitech force path only sends when
// the target moves (owner's captures: an unchanged value is never resent),
// so a report that repeats the same value packet after packet is not the
// force path no matter how fast it runs. An RS50 in Assetto Corsa streams a
// constant 32767 on 0x12 at 100+ a second while its real force is on 0x11 at
// ~26 a second (tester trace, 2026-08-29); read by rate alone, 0x12 would
// win and the wheel would be pinned at full lock. So a packet only counts
// toward a report's claim when its value is non-trivial AND differs from
// that report's previous value.
//
// The rule is symmetric. A report becomes live once it shows a short run of
// such packets (InitialMinPacketsPerSec within a second). After that the
// other report can only take over if the live one has said nothing for
// SwitchSilenceMs AND the challenger is streaming changing values at
// SwitchMinPacketsPerSec or better. A parked car's live report goes quiet
// for seconds at a time, while the other report trickles at a few packets a
// second, which never reaches the switch rate, so a quiet spell cannot flip
// the channel. A real transport change (the driver moving the whole stream)
// clears both bars within a few seconds of driving. Packets on the live
// report are always accepted, changed or not, so a game that re-emits a
// frozen force (FH6 through a pause) still reaches the pause handling
// downstream exactly as before.
//
// Pure: the clock is passed in, so the timing rules are unit-tested directly.

using System;

namespace TrueforceForAll.Core
{
    public sealed class FfbReportArbiter
    {
        /// <summary>|force| above this counts as a real value. The RS50 idles
        /// its 0x11 at +/-3, which must never make 0x11 look live.</summary>
        public const int RealForceFloorLsb = 64;

        /// <summary>Changing, non-trivial packets within one second before an
        /// undecided arbiter picks a report. Four matches the old
        /// sustained-0x12 gate; a lone management write at game open is one
        /// or two, and a repeated constant is zero however fast it runs.</summary>
        public const int InitialMinPacketsPerSec = 4;

        /// <summary>Rate of changing packets a challenger must show to
        /// displace the live report. A parked car's trickle on the other
        /// report runs at 2 to 7 a second (owner's captures), a moved stream
        /// at 25 or more.</summary>
        public const int SwitchMinPacketsPerSec = 15;

        /// <summary>How long the live report must have been silent (no
        /// non-trivial value) before a challenger may take over.</summary>
        public const int SwitchSilenceMs = 3000;

        private const int WindowMs = 1000;
        private const int RingSize = 32;

        private readonly int[] _t11 = new int[RingSize];
        private readonly int[] _t12 = new int[RingSize];
        private int _n11, _h11, _n12, _h12;
        // Previous value seen on each report, so only a CHANGE counts toward
        // that report's claim (a constant repeated at any rate is not force).
        private const int NoValue = int.MinValue;
        private int _prev11 = NoValue, _prev12 = NoValue;

        /// <summary>0 while undecided, else 0x11 or 0x12.</summary>
        public byte LiveReport { get; private set; }

        /// <summary>Clock reading of the live report's last non-trivial packet.</summary>
        public int LiveLastRealMs { get; private set; }

        private string _decision;

        /// <summary>Whether a packet on <paramref name="reportId"/> should be
        /// extracted as force. Call once per fn2 packet on the FFB feature.</summary>
        public bool Accept(byte reportId, short force, int nowMs)
        {
            int prev = reportId == 0x12 ? _prev12 : _prev11;
            if (reportId == 0x12) _prev12 = force; else _prev11 = force;
            bool real = Math.Abs((int)force) > RealForceFloorLsb && force != prev;
            int rate = real ? Note(reportId, nowMs) : CountWithin(reportId, nowMs);

            if (LiveReport == 0)
            {
                if (rate < InitialMinPacketsPerSec) return false;
                LiveReport = reportId;
                LiveLastRealMs = nowMs;
                _decision = $"force is on HID++ report 0x{reportId:X2} ({Name(reportId)}), {rate}+ a second";
                return true;
            }

            if (reportId == LiveReport)
            {
                if (real) LiveLastRealMs = nowMs;
                return true;
            }

            int quietMs = unchecked(nowMs - LiveLastRealMs);
            if (quietMs > SwitchSilenceMs && rate >= SwitchMinPacketsPerSec)
            {
                _decision = $"force moved to HID++ report 0x{reportId:X2} ({Name(reportId)}), {rate}+ a second; " +
                            $"0x{LiveReport:X2} had been quiet for {quietMs} ms";
                LiveReport = reportId;
                LiveLastRealMs = nowMs;
                return true;
            }
            return false;
        }

        /// <summary>The log line for the most recent live-report change, once;
        /// null when nothing changed since the last call.</summary>
        public string TakeDecision()
        {
            string d = _decision;
            _decision = null;
            return d;
        }

        public void Reset()
        {
            LiveReport = 0;
            LiveLastRealMs = 0;
            _n11 = _h11 = _n12 = _h12 = 0;
            _prev11 = _prev12 = NoValue;
            _decision = null;
        }

        private static string Name(byte reportId) => reportId == 0x12 ? "very long" : "long";

        private int Note(byte reportId, int nowMs)
        {
            if (reportId == 0x12)
            {
                _t12[_h12] = nowMs; _h12 = (_h12 + 1) % RingSize; if (_n12 < RingSize) _n12++;
            }
            else
            {
                _t11[_h11] = nowMs; _h11 = (_h11 + 1) % RingSize; if (_n11 < RingSize) _n11++;
            }
            return CountWithin(reportId, nowMs);
        }

        private int CountWithin(byte reportId, int nowMs)
        {
            int[] ring = reportId == 0x12 ? _t12 : _t11;
            int n = reportId == 0x12 ? _n12 : _n11;
            int c = 0;
            for (int i = 0; i < n; i++)
            {
                int age = unchecked(nowMs - ring[i]);
                if (age >= 0 && age <= WindowMs) c++;
            }
            return c;
        }
    }
}
