// Reads AC's physics shared memory page (Local\acpmf_physics) at 1 kHz.
// AC's native solver runs at 333 Hz, so polling faster than that re-reads
// the same data — but at 1 kHz any new physics tick is observed within
// ≤1 ms of being written and lines up with our 1 kHz Trueforce packet
// cadence, so events never get aliased against packet boundaries. The
// fidelity gain over the 60 Hz SimHub IDataPlugin tick is most audible
// in RoadBumpsEffect (sharp curb leading edges) and TractionLossEffect
// (direct wheelSlip[] reading instead of the heuristic SimHub falls back
// to).
//
// Fields that don't need physics-rate fidelity — MaxRpm (static per car),
// AbsActive (slow pump events) — are deliberately left for the SimHub
// fallback to fill in via DispatchFrame's overlay step. AC's `physics.abs`
// is the player's ABS *configuration level* (0..1), not pump activity, and
// reading it would be a misleading-data hazard; reading the static page for
// MaxRpm just duplicates work SimHub already does correctly.
//
// Field offsets are derived from the AC SDK's SPageFilePhysics struct as
// documented by the modding community (mdjarv/assettocorsasharedmemory and
// equivalents). Pack=4 layout, cumulative offsets verified against
// Marshal.SizeOf reasoning of the full struct.

using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class AcSharedMemoryTelemetrySource : TelemetrySourceBase
    {
        public override string Name => "Assetto Corsa";
        public override bool   IsEnhanced => true;
        public override bool   IsRunning  => _running != 0;

        private const string PhysicsName = "Local\\acpmf_physics";

        // Physics page field offsets (Pack=4 sequential layout).
        private const int OFF_PACKET_ID       = 0;     // int, increments each AC physics tick
        private const int OFF_GAS             = 4;     // float, 0..1
        private const int OFF_GEAR            = 16;    // int, 0=R 1=N 2..N=fwd
        private const int OFF_RPMS            = 20;    // int
        private const int OFF_SPEED_KMH       = 28;    // float
        private const int OFF_ACC_G_X         = 44;    // float, lateral, g
        private const int OFF_ACC_G_Y         = 48;    // float, vertical, g
        // wheelSlip[4] — float[4] starting at offset 56. Order is FL/FR/RL/RR;
        // 0 = perfect grip, larger magnitude = more slip. We take max-abs
        // across all four (any slipping tire shakes the wheel).
        private const int OFF_WHEEL_SLIP_FL   = 56;
        private const int OFF_WHEEL_SLIP_FR   = 60;
        private const int OFF_WHEEL_SLIP_RL   = 64;
        private const int OFF_WHEEL_SLIP_RR   = 68;
        private const int OFF_LOCAL_ANG_VEL_Y = 300;   // float, yaw rad/s

        // 1 kHz poll cadence — see header comment for rationale. Requires
        // timeBeginPeriod(1), set explicitly inside PollLoop, for Thread.Sleep
        // to honor 1 ms instead of the OS default ~15 ms.
        private const int TickPeriodMs = 1;

        private MemoryMappedFile         _physicsMmf;
        private MemoryMappedViewAccessor _physicsView;

        private Thread _thread;
        private volatile bool _stopping;
        private int _running;

        // PacketId-based deduping: AC writes a fresh packetId each physics
        // tick (~333 Hz), but we poll at 1 kHz. Without deduping, EmitFrame
        // would fire on every poll, inflating MeasuredHz to the poll rate
        // instead of AC's actual update rate. -1 sentinel ensures the first
        // observed packet (whatever its id) always emits.
        private int _lastPacketId = -1;

        public Action<string> Logger { get; set; }

        /// <summary>Opens the AC physics page and starts the polling thread.
        /// Throws if Local\acpmf_physics is unavailable (AC not running) —
        /// the plugin's swap logic catches this and falls back to SimHub.</summary>
        public override void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

            try
            {
                _physicsMmf  = MemoryMappedFile.OpenExisting(PhysicsName, MemoryMappedFileRights.Read);
                _physicsView = _physicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
            catch
            {
                Interlocked.Exchange(ref _running, 0);
                CleanupMmf();
                throw;
            }

            _stopping = false;
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "AcSharedMemoryTelemetrySource",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            Log("AC shared memory source started.");
        }

        public override void Stop()
        {
            _stopping = true;
            try { _thread?.Join(2000); } catch { }
            _thread = null;
            CleanupMmf();
            // Reset so a fresh Start() doesn't accidentally suppress its first
            // frame if the new AC session's packetId happens to match the
            // last one we observed.
            _lastPacketId = -1;
            Interlocked.Exchange(ref _running, 0);
        }

        private void CleanupMmf()
        {
            try { _physicsView?.Dispose(); } catch { }
            try { _physicsMmf?.Dispose();  } catch { }
            _physicsView = null;
            _physicsMmf  = null;
        }

        private void PollLoop()
        {
            // Bump the system timer to 1 ms granularity for the duration of
            // the loop. Mirrors what TrueforceDevice.StreamLoop does — without
            // it, Thread.Sleep(1) below decays to the default ~15 ms tick and
            // our 1 kHz cadence collapses. timeBeginPeriod is reference-
            // counted on Windows, so this nests safely with the stream
            // thread's call.
            TimeBeginPeriod(1);
            try
            {
                var sw = Stopwatch.StartNew();
                long nextTickMs = 0;

                while (!_stopping)
                {
                    try
                    {
                        // Only emit when AC has actually written new physics data.
                        // AC's solver runs at ~333 Hz; polling at 1 kHz keeps
                        // event detection latency ≤1 ms but produces 2-3 polls
                        // per real frame — emitting on every poll would re-run
                        // every effect for the same inputs and inflate MeasuredHz.
                        int pktId = _physicsView.ReadInt32(OFF_PACKET_ID);
                        if (pktId != _lastPacketId)
                        {
                            _lastPacketId = pktId;
                            EmitFrame(ReadFrame());
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"AC poll error: {ex.GetType().Name}: {ex.Message}");
                    }

                    // Stopwatch-paced cadence. If we fall behind (GC pause, etc.),
                    // reset the phase so we don't spin trying to catch up.
                    nextTickMs += TickPeriodMs;
                    long elapsed = sw.ElapsedMilliseconds;
                    int sleepMs = (int)(nextTickMs - elapsed);
                    if (sleepMs <= 0)
                    {
                        nextTickMs = elapsed + TickPeriodMs;
                        sleepMs = TickPeriodMs;
                    }
                    Thread.Sleep(sleepMs);
                }
            }
            finally
            {
                TimeEndPeriod(1);
            }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);

        private TelemetryFrame ReadFrame()
        {
            float gas      = _physicsView.ReadSingle(OFF_GAS);
            int   gear     = _physicsView.ReadInt32 (OFF_GEAR);
            int   rpms     = _physicsView.ReadInt32 (OFF_RPMS);
            float speedKmh = _physicsView.ReadSingle(OFF_SPEED_KMH);
            float accGX    = _physicsView.ReadSingle(OFF_ACC_G_X);
            float accGY    = _physicsView.ReadSingle(OFF_ACC_G_Y);
            float wsFL     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_FL);
            float wsFR     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_FR);
            float wsRL     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_RL);
            float wsRR     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_RR);
            float yawRadS  = _physicsView.ReadSingle(OFF_LOCAL_ANG_VEL_Y);

            const float  G        = 9.80665f;
            const double RadToDeg = 180.0 / Math.PI;

            double throttle01 = gas;
            if (throttle01 < 0) throttle01 = 0;
            else if (throttle01 > 1) throttle01 = 1;

            double maxSlip = Math.Max(
                Math.Max(Math.Abs(wsFL), Math.Abs(wsFR)),
                Math.Max(Math.Abs(wsRL), Math.Abs(wsRR)));

            return new TelemetryFrame
            {
                Rpms       = rpms,
                Throttle01 = throttle01,

                SpeedKmh          = speedKmh,
                AccelerationSway  = accGX * G,
                AccelerationHeave = accGY * G,
                YawRateDegPerSec  = yawRadS * RadToDeg,

                Gear      = GearString(gear),
                WheelSlip = maxSlip,
                // MaxRpm and AbsActive are deliberately left at their defaults;
                // TrueforcePlugin.DispatchFrame overlays them from the latest
                // SimHub reading, which is the right authority for both.
            };
        }

        // AC convention: 0=R, 1=N, 2=1st, 3=2nd, ... Matches SimHub's
        // StatusDataBase.Gear string convention so effects compare unchanged.
        private static string GearString(int gear)
        {
            if (gear == 0) return "R";
            if (gear == 1) return "N";
            return (gear - 1).ToString();
        }

        private void Log(string msg)
        {
            var l = Logger;
            if (l != null) try { l(msg); } catch { }
        }
    }
}
