using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TrueforceForAll.Core
{
    /// <summary>One physics-update snapshot of the fields we read from Le Mans
    /// Ultimate's official "LMU_Data" shared memory. Published by
    /// <see cref="LmuSharedMemoryReader"/> once per NEW mElapsedTime value on
    /// the player's TelemInfoV01, on the reader's own thread.</summary>
    public sealed class LmuFfbSample
    {
        /// <summary>TelemInfoV01.mID, the slot the player's telemetry sits in.</summary>
        public int SlotId;
        /// <summary>mElapsedTime, game session seconds. A changed value is what
        /// makes a snapshot worth publishing; it freezes while paused.</summary>
        public double ElapsedTime;
        public double DeltaTime;

        /// <summary>mSteeringShaftTorque: the torque around the steering shaft
        /// the sim's physics solved, in Nm. The sim's own force feedback is a
        /// scaled copy of this (its max-torque setting is the divisor), so it is
        /// pre-gain: the whole reason this reader exists.</summary>
        public double ShaftTorqueNm;
        /// <summary>SharedMemoryGeneric.FFBTorque: the finished force feedback
        /// value the sim hands its own device path. Units and rate unconfirmed
        /// until the LMUPROBE rig pass says; read for the probe only.</summary>
        public float FfbTorque;

        /// <summary>mUnfilteredSteering, -1..1 left to right: the raw controller
        /// axis.</summary>
        public float SteerUnfiltered;
        public float SteerFiltered;
        /// <summary>mPhysicalSteeringWheelRange: the rotation the sim asked the
        /// wheel for, lock to lock, degrees.</summary>
        public float PhysicalRangeDeg;
        /// <summary>mVisualSteeringWheelRange: the rotation the car's own
        /// steering wheel animates through, lock to lock, degrees.</summary>
        public float VisualRangeDeg;
        /// <summary>|mLocalVel|, m/s.</summary>
        public float SpeedMps;
        public int Gear;
        public float Rpm;
        public string VehicleName;
        /// <summary>SharedMemoryTelemetryData.playerHasVehicle.</summary>
        public bool PlayerHasVehicle;

        // Scoring, which the sim refreshes about five times a second. Matched
        // to the telemetry slot by mID.
        /// <summary>ScoringInfoV01.mInRealtime: in the car rather than at the
        /// monitor.</summary>
        public bool InRealtime;
        /// <summary>ScoringInfoV01.mGamePhase: 0 before the session, 1
        /// reconnaissance, 2 grid walk, 3 formation, 4 countdown, 5 green, 6
        /// full-course yellow, 7 stopped, 8 over, 9 paused.</summary>
        public int GamePhase;
        /// <summary>VehicleScoringInfoV01.mControl for the player's slot: -1
        /// nobody, 0 local player, 1 local AI, 2 remote, 3 replay. Anything but
        /// 0 must never reach the wheel.</summary>
        public int Control;
        public bool InGarageStall;
        public bool InPits;
        /// <summary>True when a scoring row carrying the telemetry slot's id was
        /// found; the three fields above are meaningless otherwise.</summary>
        public bool ScoringMatched;

        /// <summary>Stopwatch timestamp at the read, for freshness checks.</summary>
        public long Ticks;
    }

    /// <summary>Reads Le Mans Ultimate's official "LMU_Data" shared memory
    /// directly at high rate, so the FFB path is not limited to SimHub's copy
    /// of the same data. Opens lazily and retries forever while started (the
    /// sim may not be running yet), dedupes on the player's mElapsedTime and
    /// hands each new physics update to <see cref="OnSample"/> on the reader
    /// thread.
    ///
    /// Deliberately does NOT take the sim's cooperative lock
    /// ("LMU_SharedMemoryLockData"): a reader that died holding it would stall
    /// the sim's writer, and our reads are a few kilobytes that a torn update
    /// costs one sample, which the re-read below catches anyway. Nor does it
    /// wait on "LMU_Data_Event": that event is auto-reset, and a second reader
    /// on it would steal wake-ups from whichever tool the driver also runs.
    ///
    /// Field offsets are byte offsets into the SharedMemoryLayout the sim's
    /// SDK headers declare (Support\SharedMemoryInterface, pack(4)), computed
    /// by compiling offsetof() against those headers on 2026-09-20.</summary>
    public sealed class LmuSharedMemoryReader : IDisposable
    {
        private const string MapName = "LMU_Data";

        /// <summary>sizeof(SharedMemoryLayout). The mapping is rejected when it
        /// is smaller than this: the layout has changed underneath us.</summary>
        internal const int LayoutBytes = 324824;

        // SharedMemoryGeneric, at 0.
        internal const int OFF_GAME_VERSION = 64;      // long (4 bytes)
        internal const int OFF_FFB_TORQUE   = 68;      // float

        // SharedMemoryScoringData at 1632: ScoringInfoV01 first, then
        // scoringStreamSize, then VehicleScoringInfoV01[104].
        internal const int OFF_SCORING_INFO   = 1632;
        internal const int OFF_NUM_VEHICLES   = OFF_SCORING_INFO + 104;   // long
        internal const int OFF_GAME_PHASE     = OFF_SCORING_INFO + 108;   // unsigned char
        internal const int OFF_IN_REALTIME    = OFF_SCORING_INFO + 115;   // bool
        internal const int OFF_VEH_SCORING    = OFF_SCORING_INFO + 560;
        internal const int VEH_SCORING_STRIDE = 584;
        internal const int VS_ID              = 0;     // long
        internal const int VS_IS_PLAYER       = 196;   // bool
        internal const int VS_CONTROL         = 197;   // signed char
        internal const int VS_IN_PITS         = 198;   // bool
        internal const int VS_IN_GARAGE_STALL = 507;   // bool

        // SharedMemoryTelemetryData at 128464: three bytes of header, one of
        // padding, then TelemInfoV01[104].
        internal const int OFF_TELEMETRY          = 128464;
        internal const int OFF_ACTIVE_VEHICLES    = OFF_TELEMETRY + 0;   // uint8
        internal const int OFF_PLAYER_VEHICLE_IDX = OFF_TELEMETRY + 1;   // uint8
        internal const int OFF_PLAYER_HAS_VEHICLE = OFF_TELEMETRY + 2;   // bool
        internal const int OFF_TELEM_INFO         = OFF_TELEMETRY + 4;
        internal const int TELEM_STRIDE           = 1888;
        internal const int MaxVehicles            = 104;

        // Inside one TelemInfoV01.
        internal const int TI_ID                = 0;     // long
        internal const int TI_DELTA_TIME        = 4;     // double
        internal const int TI_ELAPSED_TIME      = 12;    // double
        internal const int TI_VEHICLE_NAME      = 32;    // char[64]
        internal const int TI_LOCAL_VEL         = 184;   // TelemVect3, three doubles
        internal const int TI_GEAR              = 352;   // long
        internal const int TI_ENGINE_RPM        = 356;   // double
        internal const int TI_STEER_UNFILTERED  = 404;   // double
        internal const int TI_STEER_FILTERED    = 436;   // double
        internal const int TI_SHAFT_TORQUE      = 452;   // double
        internal const int TI_VISUAL_RANGE      = 660;   // float
        internal const int TI_PHYSICAL_RANGE    = 692;   // float

        // Poll cadence. 1 ms polling against an elapsed-time-deduped block
        // means each new physics update is picked up within about a
        // millisecond of the sim writing it; while the map is missing we retry
        // at a human rate instead of spinning. Scoring only moves five times a
        // second, so it is re-read on its own slower cadence.
        private const int TickPeriodMs = 1;
        private const int ScoringPeriodMs = 50;
        private const int ReopenAfterConsecutiveErrors = 5;
        private const int RetryPeriodMs = 500;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private Thread _thread;
        private volatile bool _stopping;
        private volatile bool _open;
        private readonly byte[] _telem = new byte[TELEM_STRIDE];
        private readonly byte[] _veh = new byte[VEH_SCORING_STRIDE];
        private double _lastElapsed = double.NaN;
        private int _lastSlot = int.MinValue;
        private bool _loggedBadLayout;
        private volatile string _versionSeen = "";
        private long _scoringReadTicks;
        private bool _scInRealtime;
        private int _scGamePhase;
        private int _scControl = -1;
        private bool _scInGarage, _scInPits, _scMatched;
        private int _scMatchedSlot = int.MinValue;
        private string _vehicleName = "";
        private float _lastFfb = float.NaN;

        // Rolling rate estimates over ~1 s windows, for the probe and the
        // engage log line: one for the telemetry updates, one for how often
        // the sim's own FFB value changes.
        private long _hzWindowStartTicks;
        private int _hzWindowCount;
        private int _ffbWindowCount;
        private volatile float _measuredHz;
        private volatile float _measuredFfbHz;

        private long _samplesSeen;
        private volatile LmuFfbSample _last;

        /// <summary>Called on the reader thread, once per NEW physics update.</summary>
        public Action<LmuFfbSample> OnSample;
        public Action<string> Logger;

        public bool IsOpen => _open;
        /// <summary>The sim's gameVersion from the open block, as text; empty
        /// until the map has been opened.</summary>
        public string VersionSeen => _versionSeen;
        public double MeasuredHz => _measuredHz;
        /// <summary>How often the sim's own FFBTorque value changed, per
        /// second: says whether that channel runs faster than the telemetry.</summary>
        public double MeasuredFfbHz => _measuredFfbHz;
        public long SamplesSeen => Interlocked.Read(ref _samplesSeen);
        /// <summary>The most recent snapshot, new update or not; null before the
        /// first successful read. For status surfaces that must keep working
        /// while the sim is paused and the elapsed time is frozen.</summary>
        public LmuFfbSample LastSample => _last;

        public bool Start()
        {
            if (_thread != null) return true;
            _stopping = false;
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "LmuSharedMemoryReader",
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
            // session's elapsed time happens to match the last one seen.
            _lastElapsed = double.NaN;
            _lastSlot = int.MinValue;
            _hzWindowStartTicks = 0;
            _hzWindowCount = 0;
            _ffbWindowCount = 0;
            _measuredHz = 0f;
            _measuredFfbHz = 0f;
            _lastFfb = float.NaN;
            _scoringReadTicks = 0;
            _scMatched = false;
            _scMatchedSlot = int.MinValue;
            _loggedBadLayout = false;
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
                        long now = Stopwatch.GetTimestamp();
                        bool hasVehicle = _view.ReadByte(OFF_PLAYER_HAS_VEHICLE) != 0;
                        int idx = _view.ReadByte(OFF_PLAYER_VEHICLE_IDX);
                        float ffb = _view.ReadSingle(OFF_FFB_TORQUE);
                        if (!float.IsNaN(_lastFfb) && ffb != _lastFfb) _ffbWindowCount++;
                        _lastFfb = ffb;

                        if (!hasVehicle || idx >= MaxVehicles)
                        {
                            // At the monitor, or between sessions. Keep a
                            // snapshot for the probe, publish nothing.
                            var idle = _last;
                            if (idle == null || idle.PlayerHasVehicle
                                || now - idle.Ticks > Stopwatch.Frequency / 4)
                            {
                                _last = new LmuFfbSample { PlayerHasVehicle = false, Ticks = now, Control = -1 };
                            }
                            UpdateMeasuredHz(now, false);
                            consecutiveErrors = 0;
                            Thread.Sleep(TickPeriodMs);
                            continue;
                        }

                        long tOff = OFF_TELEM_INFO + (long)idx * TELEM_STRIDE;
                        _view.ReadArray(tOff, _telem, 0, TELEM_STRIDE);
                        // The sim writes without us holding its lock, so a
                        // block can be read mid-update. Its elapsed time is the
                        // last thing we care about being consistent: if it moved
                        // during the copy, copy again.
                        if (_view.ReadDouble(tOff + TI_ELAPSED_TIME) != BitConverter.ToDouble(_telem, TI_ELAPSED_TIME))
                            _view.ReadArray(tOff, _telem, 0, TELEM_STRIDE);

                        var s = ParseTelem(_telem);
                        s.PlayerHasVehicle = true;
                        s.FfbTorque = ffb;
                        s.Ticks = now;

                        if (_scoringReadTicks == 0
                            || now - _scoringReadTicks >= Stopwatch.Frequency * ScoringPeriodMs / 1000
                            || s.SlotId != _scMatchedSlot)
                        {
                            _scoringReadTicks = now;
                            RefreshScoring(s.SlotId);
                            _vehicleName = ReadCString(_telem, TI_VEHICLE_NAME, 64);
                        }
                        s.InRealtime = _scInRealtime;
                        s.GamePhase = _scGamePhase;
                        s.Control = _scControl;
                        s.InGarageStall = _scInGarage;
                        s.InPits = _scInPits;
                        s.ScoringMatched = _scMatched;
                        s.VehicleName = _vehicleName;

                        _last = s;
                        bool fresh = s.ElapsedTime != _lastElapsed || s.SlotId != _lastSlot;
                        UpdateMeasuredHz(now, fresh);
                        if (fresh)
                        {
                            _lastElapsed = s.ElapsedTime;
                            _lastSlot = s.SlotId;
                            Interlocked.Increment(ref _samplesSeen);
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
                            Log("LMU shared memory read failed (" + ex.GetType().Name + "), reopening.");
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

        /// <summary>Re-read the session state and find the scoring row for the
        /// telemetry slot. Scoring and telemetry are separate arrays in
        /// different orders, so the row is found by slot id, not index.</summary>
        private void RefreshScoring(int slotId)
        {
            _scInRealtime = _view.ReadByte(OFF_IN_REALTIME) != 0;
            _scGamePhase = _view.ReadByte(OFF_GAME_PHASE);
            int n = _view.ReadInt32(OFF_NUM_VEHICLES);
            if (n < 0) n = 0; else if (n > MaxVehicles) n = MaxVehicles;
            for (int i = 0; i < n; i++)
            {
                long off = OFF_VEH_SCORING + (long)i * VEH_SCORING_STRIDE;
                if (_view.ReadInt32(off + VS_ID) != slotId) continue;
                _view.ReadArray(off, _veh, 0, VEH_SCORING_STRIDE);
                var row = ParseVehicleScoring(_veh);
                _scControl = row.Control;
                _scInGarage = row.InGarageStall;
                _scInPits = row.InPits;
                _scMatched = true;
                _scMatchedSlot = slotId;
                return;
            }
            _scMatched = false;
            _scMatchedSlot = int.MinValue;
            _scControl = -1;
            _scInGarage = false;
            _scInPits = false;
        }

        private bool TryOpen()
        {
            try
            {
                var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                MemoryMappedViewAccessor view;
                try
                {
                    view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
                catch
                {
                    mmf.Dispose();
                    throw;
                }
                if (view.Capacity < LayoutBytes)
                {
                    long cap = view.Capacity;
                    view.Dispose();
                    mmf.Dispose();
                    if (!_loggedBadLayout)
                    {
                        _loggedBadLayout = true;
                        Log($"LMU shared memory is {cap} bytes, smaller than the {LayoutBytes}-byte layout this reader knows: not reading it.");
                    }
                    return false;
                }
                _mmf = mmf;
                _view = view;
                _open = true;
                try { _versionSeen = _view.ReadInt32(OFF_GAME_VERSION).ToString(); }
                catch { _versionSeen = "?"; }
                Log($"LMU shared memory opened (game version {_versionSeen}, {view.Capacity} bytes mapped).");
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

        /// <summary>Extract the consumed fields from a raw copy of one
        /// TelemInfoV01. Static and buffer-driven so the offset constants are
        /// guarded by unit tests without a live sim. Scoring fields and the
        /// vehicle name are filled by the caller.</summary>
        internal static LmuFfbSample ParseTelem(byte[] b)
        {
            double vx = BitConverter.ToDouble(b, TI_LOCAL_VEL);
            double vy = BitConverter.ToDouble(b, TI_LOCAL_VEL + 8);
            double vz = BitConverter.ToDouble(b, TI_LOCAL_VEL + 16);
            return new LmuFfbSample
            {
                SlotId           = BitConverter.ToInt32(b, TI_ID),
                DeltaTime        = BitConverter.ToDouble(b, TI_DELTA_TIME),
                ElapsedTime      = BitConverter.ToDouble(b, TI_ELAPSED_TIME),
                ShaftTorqueNm    = BitConverter.ToDouble(b, TI_SHAFT_TORQUE),
                SteerUnfiltered  = (float)BitConverter.ToDouble(b, TI_STEER_UNFILTERED),
                SteerFiltered    = (float)BitConverter.ToDouble(b, TI_STEER_FILTERED),
                PhysicalRangeDeg = BitConverter.ToSingle(b, TI_PHYSICAL_RANGE),
                VisualRangeDeg   = BitConverter.ToSingle(b, TI_VISUAL_RANGE),
                SpeedMps         = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz),
                Gear             = BitConverter.ToInt32(b, TI_GEAR),
                Rpm              = (float)BitConverter.ToDouble(b, TI_ENGINE_RPM),
            };
        }

        /// <summary>The player-state fields of one VehicleScoringInfoV01.</summary>
        internal struct VehicleScoringRow
        {
            public int SlotId;
            public bool IsPlayer;
            public int Control;
            public bool InPits;
            public bool InGarageStall;
        }

        internal static VehicleScoringRow ParseVehicleScoring(byte[] b)
        {
            return new VehicleScoringRow
            {
                SlotId        = BitConverter.ToInt32(b, VS_ID),
                IsPlayer      = b[VS_IS_PLAYER] != 0,
                Control       = (sbyte)b[VS_CONTROL],
                InPits        = b[VS_IN_PITS] != 0,
                InGarageStall = b[VS_IN_GARAGE_STALL] != 0,
            };
        }

        internal static string ReadCString(byte[] b, int off, int max)
        {
            int n = 0;
            while (n < max && b[off + n] != 0) n++;
            return n == 0 ? "" : Encoding.UTF8.GetString(b, off, n);
        }

        private void UpdateMeasuredHz(long nowTicks, bool fresh)
        {
            if (_hzWindowStartTicks == 0) { _hzWindowStartTicks = nowTicks; _hzWindowCount = 0; _ffbWindowCount = 0; }
            if (fresh) _hzWindowCount++;
            double elapsed = (nowTicks - _hzWindowStartTicks) / (double)Stopwatch.Frequency;
            if (elapsed >= 1.0)
            {
                _measuredHz = (float)(_hzWindowCount / elapsed);
                _measuredFfbHz = (float)(_ffbWindowCount / elapsed);
                _hzWindowStartTicks = nowTicks;
                _hzWindowCount = 0;
                _ffbWindowCount = 0;
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
