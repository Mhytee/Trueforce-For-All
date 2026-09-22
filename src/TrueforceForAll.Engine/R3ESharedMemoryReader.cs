using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrueforceForAll.Core
{
    /// <summary>One physics-tick snapshot of the fields we read from RaceRoom's
    /// "$R3E" shared memory. Published by <see cref="R3ESharedMemoryReader"/>
    /// once per NEW GameSimulationTicks value, on the reader's own thread.</summary>
    public sealed class R3EFfbSample
    {
        /// <summary>The sim's own physics tick counter; a changed value is what
        /// makes a snapshot worth publishing.</summary>
        public int SimTicks;
        public double SimTimeSeconds;

        /// <summary>Player.SteeringForce: the physics steering force, believed
        /// to be pre-gain (the whole reason this reader exists); units
        /// unconfirmed until the R3EPROBE rig pass says otherwise.</summary>
        public double SteeringForce;

        /// <summary>Player.SteeringForcePercentage: the same force as a fraction
        /// of full scale (drives the sim's FFB meter; expected -1..1).</summary>
        public double SteeringForcePct;

        /// <summary>0 = player driving (r3e Constant.Control). 1 = AI, 2 =
        /// remote, 3 = replay, -1 = unavailable. Anything but 0 must never
        /// reach the wheel: a replay's force on a parked wheel is a runaway.</summary>
        public int ControlType;
        public bool GamePaused;
        public bool GameInMenus;
        public bool GameInReplay;
        public bool InGarage;

        public float CarSpeedMps;
        /// <summary>Raw steering input. Normalised to the CAR's lock, not the
        /// wheel's rotation: +/-1 at full lock, and it clamps there while the
        /// wheel turns on (rig, 2026-09-13). Used for the sign A/B and to
        /// measure the axis against the physical position; the soft lock builds
        /// from the physical position for that reason.</summary>
        public float SteerInputRaw;
        /// <summary>steer_lock_degrees. The header says "centre to full lock",
        /// and the rig says which lock: the ROAD wheel's (a Formula RaceRoom 3
        /// publishes 17). Not the steering wheel's, so the soft lock does not
        /// use it; kept for the log line.</summary>
        public int RoadWheelLockDeg;
        /// <summary>steer_wheel_range_degrees: the CAR's steering wheel rotation,
        /// full left to full right (that same car publishes 350). Not the
        /// physical wheel's range.</summary>
        public int CarRotationDeg;
        /// <summary>steer_wheel_max_rotation: the wheel rotation in the game's
        /// controller profile. -1 = N/A, 0 = Auto (the game sets the wheel's own
        /// range to the car's), 180..1800 = Manual, the range the raw axis spans.</summary>
        public int WheelMaxRotationDeg;
        public int Gear;
        /// <summary>VehicleInfo.ModelId, the sim's car identity.</summary>
        public int ModelId;
        /// <summary>VehicleInfo.EngineType: 0 combustion, 1 electric, 2 hybrid
        /// (-1 when the sim has not filled the block yet). The only engine
        /// CONFIGURATION the sim publishes: there is no cylinder count anywhere
        /// in the block, so this says whether a firing pattern applies at all,
        /// not what it is. See R3EEngineSeed for the count and the crank.</summary>
        public int EngineType;

        /// <summary>Stopwatch timestamp at the read, for freshness checks.</summary>
        public long Ticks;
    }

    /// <summary>Reads RaceRoom's "$R3E" shared memory directly at high rate, so
    /// the FFB path is not limited to SimHub's ~60 Hz copy of the same block.
    /// Opens lazily and retries forever while started (the sim may not be
    /// running yet), validates the layout's major version, dedupes on the sim's
    /// own tick counter and hands each new physics tick to
    /// <see cref="OnSample"/> on the reader thread.
    ///
    /// Field offsets are byte offsets into the block laid out by the public
    /// r3e-api Shared struct (Pack = 1), verified 2026-08-31 by marshaling the
    /// same struct the user's SimHub build ships (layout version 3.5).</summary>
    public sealed class R3ESharedMemoryReader : IDisposable
    {
        private const string MapName = "$R3E";

        /// <summary>The layout major version this reader understands. SimHub's
        /// own RaceRoom reader gates on the same number.</summary>
        internal const int ExpectedVersionMajor = 3;

        // Everything we consume sits in the first 1632 bytes of the ~44 KB
        // block (the last field read is steer_wheel_max_rotation at 1624), so
        // only that much is copied per poll.
        internal const int BlockBytes = 1632;

        internal const int OFF_VERSION_MAJOR    = 0;
        internal const int OFF_VERSION_MINOR    = 4;
        internal const int OFF_GAME_PAUSED      = 20;
        internal const int OFF_GAME_IN_MENUS    = 24;
        internal const int OFF_GAME_IN_REPLAY   = 28;
        internal const int OFF_IN_GARAGE        = 36;    // GamePlayerInGarage
        internal const int OFF_SIM_TICKS        = 44;    // Player.GameSimulationTicks
        internal const int OFF_SIM_TIME         = 48;    // Player.GameSimulationTime (double)
        internal const int OFF_STEERING_FORCE   = 320;   // Player.SteeringForce (double)
        internal const int OFF_STEERING_PCT     = 328;   // Player.SteeringForcePercentage (double)
        internal const int OFF_MODEL_ID         = 1268;  // VehicleInfo.ModelId
        // VehicleInfo sits at 1196 and DriverInfo.EngineType 100 bytes into it.
        internal const int OFF_ENGINE_TYPE      = 1296;  // VehicleInfo.EngineType
        internal const int OFF_CONTROL_TYPE     = 1388;
        internal const int OFF_CAR_SPEED        = 1392;  // m/s (float)
        internal const int OFF_GEAR             = 1408;
        internal const int OFF_STEER_INPUT_RAW  = 1524;  // float
        internal const int OFF_STEER_LOCK_DEG   = 1528;  // int32, road wheel lock, centre to full
        internal const int OFF_STEER_WHEEL_RANGE_DEG = 1532;  // int32, car rotation, full left to full right
        // After the two above: aid_settings (5 int32), drs (4 int32), pit_limiter,
        // push_to_pass (3 int32 + 2 float), brake_bias, two activation counters,
        // battery_soc, water_left, abs_setting, headlights, then this. All
        // 4-byte scalars under Pack = 1: 1536 + 22 * 4 = 1624.
        internal const int OFF_STEER_WHEEL_MAX_ROTATION = 1624;  // int32, -1 N/A, 0 Auto, 180..1800 Manual

        // Poll cadence. 1 ms polling against a counter-deduped block means each
        // new physics tick is picked up within about a millisecond of the sim
        // writing it; while the map is missing we retry at a human rate instead
        // of spinning.
        private const int TickPeriodMs = 1;
        private const int ReopenAfterConsecutiveErrors = 5;
        private const int RetryPeriodMs = 500;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private Thread _thread;
        private volatile bool _stopping;
        private volatile bool _open;
        private readonly byte[] _buf = new byte[BlockBytes];
        private int _lastSimTicks = int.MinValue;
        private bool _loggedBadVersion;
        private volatile string _versionSeen = "";

        // Rolling tick-rate estimate over ~1 s windows, for the probe and the
        // engage log line.
        private long _hzWindowStartTicks;
        private int _hzWindowCount;
        private volatile float _measuredHz;

        private long _samplesSeen;
        private volatile R3EFfbSample _last;

        /// <summary>Called on the reader thread, once per NEW physics tick.</summary>
        public Action<R3EFfbSample> OnSample;
        public Action<string> Logger;

        public bool IsOpen => _open;
        /// <summary>Layout version of the open block, e.g. "3.5"; empty until
        /// the map has been opened.</summary>
        public string VersionSeen => _versionSeen;
        public double MeasuredHz => _measuredHz;
        public long SamplesSeen => Interlocked.Read(ref _samplesSeen);
        /// <summary>The most recent snapshot, new tick or not; null before the
        /// first successful read. For status surfaces that must keep working
        /// while the sim is paused and the tick counter is frozen.</summary>
        public R3EFfbSample LastSample => _last;

        public bool Start()
        {
            if (_thread != null) return true;
            _stopping = false;
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "R3ESharedMemoryReader",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            return true;
        }

        public void Stop()
        {
            _stopping = true;
            try { _thread?.Join(2000); } catch { }
            _thread = null;
            CleanupMmf();
            // A fresh Start() must not suppress its first sample if the new
            // session's tick counter happens to match the last one seen.
            _lastSimTicks = int.MinValue;
            _hzWindowStartTicks = 0;
            _hzWindowCount = 0;
            _measuredHz = 0f;
            _loggedBadVersion = false;
        }

        public void Dispose() => Stop();

        private void PollLoop()
        {
            TimeBeginPeriod(1);
            try
            {
                int consecutiveErrors = 0;
                while (!_stopping)
                {
                    if (_view == null)
                    {
                        if (!TryOpen()) { SleepInterruptible(RetryPeriodMs); continue; }
                    }

                    try
                    {
                        _view.ReadArray(0, _buf, 0, BlockBytes);
                        int vMajor = BitConverter.ToInt32(_buf, OFF_VERSION_MAJOR);
                        if (vMajor != ExpectedVersionMajor)
                        {
                            if (!_loggedBadVersion)
                            {
                                _loggedBadVersion = true;
                                Log($"R3E shared memory has layout version {vMajor}.{BitConverter.ToInt32(_buf, OFF_VERSION_MINOR)}, expected major {ExpectedVersionMajor}: not reading it.");
                            }
                            SleepInterruptible(1000);
                            continue;
                        }

                        var s = Parse(_buf);
                        s.Ticks = Stopwatch.GetTimestamp();
                        _last = s;
                        if (s.SimTicks != _lastSimTicks)
                        {
                            _lastSimTicks = s.SimTicks;
                            Interlocked.Increment(ref _samplesSeen);
                            UpdateMeasuredHz(s.Ticks);
                            OnSample?.Invoke(s);
                        }
                        consecutiveErrors = 0;
                    }
                    catch (Exception ex)
                    {
                        // The sim closing mid-read surfaces here; a burst of
                        // failures means the view is dead, so drop it and let
                        // the open retry take over.
                        if (++consecutiveErrors >= ReopenAfterConsecutiveErrors)
                        {
                            consecutiveErrors = 0;
                            Log("R3E shared memory read failed (" + ex.GetType().Name + "), reopening.");
                            CleanupMmf();
                        }
                    }

                    Thread.Sleep(TickPeriodMs);
                }
            }
            finally
            {
                TimeEndPeriod(1);
                CleanupMmf();
            }
        }

        private bool TryOpen()
        {
            try
            {
                var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                MemoryMappedViewAccessor view;
                try
                {
                    view = mmf.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.Read);
                }
                catch
                {
                    mmf.Dispose();
                    throw;
                }
                _mmf = mmf;
                _view = view;
                _open = true;
                try
                {
                    _view.ReadArray(0, _buf, 0, BlockBytes);
                    _versionSeen = BitConverter.ToInt32(_buf, OFF_VERSION_MAJOR)
                        + "." + BitConverter.ToInt32(_buf, OFF_VERSION_MINOR);
                }
                catch { _versionSeen = "?"; }
                Log($"R3E shared memory opened (layout {_versionSeen}).");
                return true;
            }
            catch
            {
                // Not running yet, or gone again. Quietly retry.
                return false;
            }
        }

        private void CleanupMmf()
        {
            _open = false;
            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _view = null;
            _mmf = null;
        }

        /// <summary>Extract the consumed fields from a raw copy of the block's
        /// first <see cref="BlockBytes"/> bytes. Static and buffer-driven so the
        /// offset constants are guarded by unit tests without a live sim.</summary>
        internal static R3EFfbSample Parse(byte[] b)
        {
            return new R3EFfbSample
            {
                SimTicks         = BitConverter.ToInt32(b, OFF_SIM_TICKS),
                SimTimeSeconds   = BitConverter.ToDouble(b, OFF_SIM_TIME),
                SteeringForce    = BitConverter.ToDouble(b, OFF_STEERING_FORCE),
                SteeringForcePct = BitConverter.ToDouble(b, OFF_STEERING_PCT),
                ControlType      = BitConverter.ToInt32(b, OFF_CONTROL_TYPE),
                GamePaused       = BitConverter.ToInt32(b, OFF_GAME_PAUSED) > 0,
                GameInMenus      = BitConverter.ToInt32(b, OFF_GAME_IN_MENUS) > 0,
                GameInReplay     = BitConverter.ToInt32(b, OFF_GAME_IN_REPLAY) > 0,
                InGarage         = BitConverter.ToInt32(b, OFF_IN_GARAGE) > 0,
                CarSpeedMps      = BitConverter.ToSingle(b, OFF_CAR_SPEED),
                SteerInputRaw    = BitConverter.ToSingle(b, OFF_STEER_INPUT_RAW),
                RoadWheelLockDeg = BitConverter.ToInt32(b, OFF_STEER_LOCK_DEG),
                CarRotationDeg   = BitConverter.ToInt32(b, OFF_STEER_WHEEL_RANGE_DEG),
                WheelMaxRotationDeg = BitConverter.ToInt32(b, OFF_STEER_WHEEL_MAX_ROTATION),
                Gear             = BitConverter.ToInt32(b, OFF_GEAR),
                ModelId          = BitConverter.ToInt32(b, OFF_MODEL_ID),
                EngineType       = BitConverter.ToInt32(b, OFF_ENGINE_TYPE),
            };
        }

        private void UpdateMeasuredHz(long nowTicks)
        {
            if (_hzWindowStartTicks == 0) { _hzWindowStartTicks = nowTicks; _hzWindowCount = 0; }
            _hzWindowCount++;
            double elapsed = (nowTicks - _hzWindowStartTicks) / (double)Stopwatch.Frequency;
            if (elapsed >= 1.0)
            {
                _measuredHz = (float)(_hzWindowCount / elapsed);
                _hzWindowStartTicks = nowTicks;
                _hzWindowCount = 0;
            }
        }

        private void SleepInterruptible(int ms)
        {
            for (int i = 0; i < ms && !_stopping; i += 50)
                Thread.Sleep(Math.Min(50, ms - i));
        }

        private void Log(string msg) => Logger?.Invoke(msg);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint ms);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint ms);
    }
}
