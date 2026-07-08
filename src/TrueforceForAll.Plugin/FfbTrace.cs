using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TrueforceForAll.Plugin
{
    /// <summary>
    /// High-rate diagnostic trace of the FFB provider's signal chain, for
    /// pinning down oscillation/spazz that 60 Hz telemetry capture can't see.
    ///
    /// The FFB provider runs at the stream rate (~250 Hz-1 kHz). Writing to disk
    /// in that hot path would stall it, so Record() only stamps a pre-allocated
    /// ring buffer (lock-free: one Interlocked increment, no allocation). Dump()
    /// serializes the captured window to CSV when the user stops the trace.
    ///
    /// Columns: t_ms, speed_kmh, game_steer, phys_steer, steer_vel, raw_tap
    /// (game's FFB before our processing), after_spring (post stationary-spring),
    /// final_out (what we hand the device as cur). Comparing raw_tap vs final_out
    /// vs steer shows whether an oscillation originates in the game's force, our
    /// boost, or the wheel/loop — and the phase between them.
    /// </summary>
    internal sealed class FfbTrace
    {
        private struct Row
        {
            public long Ticks;
            public float Speed, GameSteer, PhysSteer, SteerVel;
            public int RawTap, AfterSpring, FinalOut;   // int.MinValue = null (no value)
        }

        private readonly Row[] _buf;
        private long _count;                 // total Record() calls (mod capacity = slot)
        private readonly long _startTicks;

        public FfbTrace(int capacity)
        {
            if (capacity < 1024) capacity = 1024;
            _buf = new Row[capacity];
            _startTicks = Stopwatch.GetTimestamp();
        }

        public int Capacity => _buf.Length;
        public long Count => Interlocked.Read(ref _count);

        /// <summary>Stamp one provider tick. Hot path: no allocation, no I/O.</summary>
        public void Record(int? rawTap, int? afterSpring, int? finalOut,
                           float gameSteer, float physSteer, float steerVel, float speed)
        {
            long i = Interlocked.Increment(ref _count) - 1;
            int slot = (int)(i % _buf.Length);
            _buf[slot] = new Row
            {
                Ticks = Stopwatch.GetTimestamp(),
                Speed = speed,
                GameSteer = gameSteer,
                PhysSteer = physSteer,
                SteerVel = steerVel,
                RawTap = rawTap ?? int.MinValue,
                AfterSpring = afterSpring ?? int.MinValue,
                FinalOut = finalOut ?? int.MinValue,
            };
        }

        /// <summary>Write the captured window (oldest-first) to a CSV file.
        /// Returns the number of rows written.</summary>
        public int Dump(string path)
        {
            long total = Interlocked.Read(ref _count);
            int cap = _buf.Length;
            long start = total > cap ? total - cap : 0;   // ring may have wrapped

            var sb = new StringBuilder(1 << 20);
            sb.Append("t_ms,speed_kmh,game_steer,phys_steer,steer_vel,raw_tap,after_spring,final_out\n");

            var ci = CultureInfo.InvariantCulture;
            for (long i = start; i < total; i++)
            {
                Row r = _buf[(int)(i % cap)];
                double tms = (r.Ticks - _startTicks) * 1000.0 / Stopwatch.Frequency;
                sb.Append(tms.ToString("0.0", ci)).Append(',');
                sb.Append(r.Speed.ToString("0.##", ci)).Append(',');
                sb.Append(r.GameSteer.ToString("0.####", ci)).Append(',');
                sb.Append(r.PhysSteer.ToString("0.####", ci)).Append(',');
                sb.Append(r.SteerVel.ToString("0.###", ci)).Append(',');
                AppendCell(sb, r.RawTap); sb.Append(',');
                AppendCell(sb, r.AfterSpring); sb.Append(',');
                AppendCell(sb, r.FinalOut); sb.Append('\n');
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString());
            return (int)(total - start);
        }

        private static void AppendCell(StringBuilder sb, int v)
        {
            if (v != int.MinValue) sb.Append(v);   // blank cell for null
        }
    }
}
