// Telemetry source that replays a captured fixture. Two modes:
//
//   Virtual (harness): the caller pumps StepOne() in a loop; time is the
//   fixture's own microsecond timeline, exposed through the virtual-time
//   hooks so MeasuredHz / staleness math behave exactly as live. A 60 s
//   capture replays in however long the assertions take — deterministic,
//   no threads, no sleeps.
//
//   Real-time (rig): Start() spawns a thread that paces rows by their
//   original inter-frame gaps against the wall clock. This is the
//   replay-to-wheel mode: feed a recorded slide through the live plugin
//   and feel a code change on the exact same input, corner after corner.
//
// CapturedAtTicks NOTE: in virtual mode TicksPerSecond is 1e6 (the fixture's
// µs timeline) so everything downstream that computes dt from CapturedAtTicks
// stays consistent; in real-time mode the base class stamps wall-clock ticks
// like any live source.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class ReplayFrameSource : TelemetrySourceBase
    {
        public override string Name => "Replay (" + _capture.GameName + ")";
        public override bool   IsEnhanced => true;
        public override bool   IsRunning  => _realTimeThread != null || _virtualCursor >= 0;
        public override bool   HasAuthoritativeSessionState => true;
        public override bool   IsSessionActive => _lastSessionActive;

        private readonly CsvFrameReader.Capture _capture;
        private readonly bool _virtualTime;

        // Virtual mode state.
        private int  _virtualCursor = -1;       // index of last emitted row
        private long _virtualNowUs;
        private bool _lastSessionActive;

        // Real-time mode state.
        private Thread _realTimeThread;
        private volatile bool _stopping;

        public ReplayFrameSource(CsvFrameReader.Capture capture, bool virtualTime = true)
        {
            _capture = capture ?? throw new ArgumentNullException(nameof(capture));
            _virtualTime = virtualTime;
        }

        protected override long TimestampTicks => _virtualTime ? _virtualNowUs : base.TimestampTicks;
        protected override long TicksPerSecond => _virtualTime ? 1_000_000L : base.TicksPerSecond;

        public IReadOnlyList<ReplayRow> Rows => _capture.Rows;
        public int RowCount => _capture.Rows.Count;

        /// <summary>Virtual-mode time now, µs on the fixture timeline. The rig
        /// uses this to know how many engine ticks to run between frames.</summary>
        public long VirtualNowUs => _virtualNowUs;

        /// <summary>Virtual mode: advance to the next row, stamp virtual time
        /// to its capture timestamp, and emit it. Returns false at end of
        /// fixture. Throws in real-time mode.</summary>
        public bool StepOne()
        {
            if (!_virtualTime) throw new InvalidOperationException("StepOne is virtual-mode only");
            int next = _virtualCursor + 1;
            if (next >= _capture.Rows.Count) return false;
            _virtualCursor = next;
            Emit(_capture.Rows[next]);
            return true;
        }

        /// <summary>Virtual mode: advance virtual time WITHOUT emitting (lets
        /// the rig model a telemetry stall: staleness grows, MeasuredHz decays
        /// to 0 past the idle timeout, exactly as live).</summary>
        public void AdvanceVirtualTimeUs(long deltaUs)
        {
            if (!_virtualTime) throw new InvalidOperationException("virtual-mode only");
            if (deltaUs > 0) _virtualNowUs += deltaUs;
        }

        private void Emit(ReplayRow row)
        {
            _virtualNowUs = row.TUs;
            _lastSessionActive = row.SessionActive;
            EmitFrame(row.Frame);   // base stamps CapturedAtTicks from TimestampTicks
        }

        /// <summary>Real-time mode entry: replays the fixture once, pacing rows
        /// by their original gaps, then stops. Virtual mode needs no Start
        /// (pump StepOne instead).</summary>
        public override void Start()
        {
            if (_virtualTime) { _virtualCursor = Math.Max(_virtualCursor, -1); return; }
            if (_realTimeThread != null) return;
            _stopping = false;
            _realTimeThread = new Thread(RealTimeLoop)
            {
                IsBackground = true,
                Name = "ReplayFrameSource",
            };
            _realTimeThread.Start();
        }

        public override void Stop()
        {
            _stopping = true;
            try { _realTimeThread?.Join(2000); } catch { }
            _realTimeThread = null;
            _virtualCursor = -1;
        }

        private void RealTimeLoop()
        {
            var rows = _capture.Rows;
            if (rows.Count == 0) return;
            long startTick = Stopwatch.GetTimestamp();
            long firstUs = rows[0].TUs;

            for (int i = 0; i < rows.Count && !_stopping; i++)
            {
                // Wall-clock target for this row, relative to replay start.
                long dueUs = rows[i].TUs - firstUs;
                while (!_stopping)
                {
                    long elapsedUs = (Stopwatch.GetTimestamp() - startTick) * 1_000_000L / Stopwatch.Frequency;
                    long waitMs = (dueUs - elapsedUs) / 1000;
                    if (waitMs <= 0) break;
                    Thread.Sleep((int)Math.Min(waitMs, 20));
                }
                if (_stopping) break;
                _lastSessionActive = rows[i].SessionActive;
                EmitFrame(rows[i].Frame);
            }
        }
    }
}
