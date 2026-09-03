using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrueforceForAll.Core
{
    // Assetto Corsa telemetry source: reads the game's "acpmf_physics" shared
    // memory page at 1 kHz and turns each new physics packet into a
    // TelemetryFrame for the effect pipeline. It also carries two force-feedback
    // side channels that only matter to the FFB provider, not to effects:
    //
    //   1. finalFF latch (the ACFFB path): the game's own post-gain force from
    //      offset 308, packed with a wrap-safe timestamp so the provider can
    //      re-inject it over ep3 instead of tapping USB. A zero-pin detector
    //      hands back to the USB tap when the in-game gain is 0.
    //   2. CSP bridge (the CSPFFB path): the pre/post-tweak force the TF4ALL CSP
    //      script publishes, read through AcCspBridgeReader, plus a control
    //      channel (AcCspControlWriter) that tells the script to hand the wheel
    //      to us so the HID++ pipe is free for the rev lights and screen.
    //
    // NOTE: this file was reconstructed by decompiling a shipped build after its
    // source was accidentally truncated (2026-08-29); the logic is exactly the
    // built code, re-documented and reformatted. The static helpers it exposes
    // (FfbToLsb / PackFfb / UnpackFfbValue / AgeTicks and the slip-derivation
    // quads) are covered by AcFfbLatchTests and AcQuadDerivationTests.
    public sealed class AcSharedMemoryTelemetrySource : TelemetrySourceBase
    {
        private const string PhysicsName = "Local\\acpmf_physics";

        // Byte offsets into SPageFilePhysics. Only the fields we consume are named.
        private const int OFF_PACKET_ID     = 0;
        private const int OFF_GAS           = 4;
        private const int OFF_BRAKE         = 8;
        private const int OFF_GEAR          = 16;
        private const int OFF_RPMS          = 20;
        private const int OFF_STEER_ANGLE   = 24;
        private const int OFF_SPEED_KMH     = 28;
        private const int OFF_ACC_G_X       = 44;
        private const int OFF_ACC_G_Y       = 48;
        private const int OFF_ACC_G_Z       = 52;
        private const int OFF_WHEEL_SLIP_FL = 56;
        private const int OFF_WHEEL_SLIP_FR = 60;
        private const int OFF_WHEEL_SLIP_RL = 64;
        private const int OFF_WHEEL_SLIP_RR = 68;
        private const int OFF_WHEEL_LOAD_FL = 72;
        private const int OFF_WHEEL_LOAD_FR = 76;
        private const int OFF_WHEEL_LOAD_RL = 80;
        private const int OFF_WHEEL_LOAD_RR = 84;
        private const int OFF_WHEEL_ANG_FL  = 104;
        private const int OFF_WHEEL_ANG_FR  = 108;
        private const int OFF_WHEEL_ANG_RL  = 112;
        private const int OFF_WHEEL_ANG_RR  = 116;
        private const int OFF_SUSP_TRAVEL_FL = 184;
        private const int OFF_SUSP_TRAVEL_FR = 188;
        private const int OFF_SUSP_TRAVEL_RL = 192;
        private const int OFF_SUSP_TRAVEL_RR = 196;
        private const int OFF_TC            = 204;
        private const int OFF_PIT_LIMITER_ON = 248;
        private const int OFF_LOCAL_ANG_VEL_Y = 300;
        private const int OFF_FINAL_FF      = 308;   // post-gain output force

        // Poll cadence. Normal is 1 kHz; while waiting for AC to reappear after a
        // restart we slow to 200 ms so we do not spin logging the same failure.
        private const int TickPeriodMs = 1;
        private const int ReopenAfterConsecutiveErrors = 5;
        private const int RetryPeriodMs = 200;

        private MemoryMappedFile         _physicsMmf;
        private MemoryMappedViewAccessor _physicsView;

        private Thread _thread;
        private volatile bool _stopping;
        private int _running;

        // PacketId dedupe: AC writes a fresh id each physics tick (~333 Hz) but
        // we poll at 1 kHz, so we only emit on a changed id. -1 forces the first
        // observed packet to emit whatever its id.
        private int _lastPacketId = -1;

        // Airborne / slip-weighting state, all reset on Stop so a new session
        // starts clean.
        private bool _seenWheelLoad;
        private bool _prevAirborne;
        private const float GroundedLoadN = 100f;
        private const float AirborneLoadN = 1f;
        internal const double MinTotalLoadN = 4.0;
        internal const double FrozenGuardSpeedKmh = 10.0;
        private TireQuad _prevSlip;
        private bool _prevSlipValid;

        // finalFF latch (ACFFB). Low 16 bits of the packed long are the int16
        // force LSB; the high 48 bits are Stopwatch ticks masked to 48 bits, so
        // the freshness check is wrap-safe across the QPC wrap (same scheme the
        // USB tap uses). A packed value of 0 means "nothing latched".
        private const long FfbTimestampMask = 0x0000_FFFF_FFFF_FFFFL;
        private long _ffbPacked;
        private readonly Stopwatch _ffbSw = Stopwatch.StartNew();
        private volatile bool _ffbLatchEnabled;
        // finalFF is post-gain: with the in-game gain at 0 it reads exactly zero
        // forever, and re-injecting that would mute the wheel while the player
        // thinks the plugin is broken. The pin detector notices "driving, and
        // the value has been zero the whole time" and the latch hands back to
        // the USB tap until a non-zero value shows up.
        private readonly FinalFfZeroPin _ffbZeroPin = new FinalFfZeroPin();
        private volatile bool _ffbPinnedAtZero;

        // CSP bridge latch (CSPFFB): the sim's force from the TF4ALL CSP script's
        // shared-memory block, packed exactly like the finalFF latch above so the
        // provider consults either the same way. Its clock is the script's own
        // seq counter, so it runs every poll tick; a frozen writer (paused game,
        // AC gone) simply stops re-stamping and the value ages out.
        private long _cspPacked;
        private volatile bool _cspLatchEnabled;
        private readonly AcCspBridgeReader _cspBridge = new AcCspBridgeReader();
        // Plugin -> script control channel: while armed it tells the script to
        // return 0 to the wheel so the game stops driving it (the plugin renders
        // the exported force). Once the bridge has been armed this session, it
        // heartbeats every tick so the script's liveness check stays fresh.
        private readonly AcCspControlWriter _cspControl = new AcCspControlWriter();
        private bool _cspControlActive;
        private volatile bool _cspSuppressOutput = true;   // false = read but let the game keep the wheel (diagnostic)
        private volatile bool _cspZeroDamper;               // true = zero the damper too (CSPFFB DAMP A/B; pre-fix feel)
        private float _cspDamperSeen = float.NaN;           // last damper logged, for the change detector
        private long  _cspDamperLogTicks;
        private AcCspBridgeSample _cspLast;                 // poll thread writes; status reads (display only)
        private volatile bool _cspHaveSample;
        private long _cspLastNewTicks;
        private volatile int _cspFieldSel;                 // (int)AcCspField, default Pure = 0
        private float _cspMaxNm = 10f;                      // steerTorque -> normalized full scale
        private bool _cspLoggedLive;
        private bool _cspLoggedBadHeader;
        private long _cspLastPeriodicLogTicks;
        private static readonly long CspPeriodicLogTicks = Stopwatch.Frequency * 2;
        private static readonly long CspFrozenReopenTicks = Stopwatch.Frequency * 10;

        internal const float RollingRadiusM = 0.33f;
        internal const double SlipRatioMinSpeedMs = 3.0;

        public override string Name => "Assetto Corsa";
        public override bool IsEnhanced => true;
        public override bool IsRunning => _running != 0;

        /// <summary>When false, the control channel tells the script NOT to zero
        /// the wheel (the game drives it normally) while the plugin still reads
        /// and logs the bridge. Used to test whether our zero output is what
        /// collapses AC's FFB signal.</summary>
        public bool CspSuppressOutput
        {
            get => _cspSuppressOutput;
            set => _cspSuppressOutput = value;
        }

        /// <summary>A/B for the damper pass-through: true tells the script to
        /// zero the damper channel too (the pre-fix behaviour), so the fix can
        /// be felt against the bug in one session. Session only.</summary>
        public bool CspZeroDamper
        {
            get => _cspZeroDamper;
            set => _cspZeroDamper = value;
        }

        // CSPFFB DAMPTEST: a scripted damper wiggle so the whole question is
        // answered from the driver's seat: type the code, alt-tab in, feel.
        // 0..12 s: full damper off/on, three seconds per state (does the
        // wheel execute the channel at all, and does a change cut the force);
        // 12..28 s: two 8 s triangle ramps (does it scale smoothly). Restores
        // itself when done. Session only, rides the control heartbeat.
        private long _cspDampTestStartTicks;

        public void StartCspDamperTest()
        {
            Interlocked.Exchange(ref _cspDampTestStartTicks, _ffbSw.ElapsedTicks);
            Log("CSP damper test started: 12 s of three-second off/on flips, then two 8 s ramps, then back to normal.");
        }

        private int CspDamperTestScale255()
        {
            long start = Interlocked.Read(ref _cspDampTestStartTicks);
            if (start == 0) return -1;
            double t = (double)(_ffbSw.ElapsedTicks - start) / Stopwatch.Frequency;
            if (t >= 28)
            {
                Interlocked.Exchange(ref _cspDampTestStartTicks, 0);
                Log("CSP damper test done; damper pass-through restored.");
                return -1;
            }
            double scale;
            if (t < 12) scale = ((int)(t / 3) % 2 == 0) ? 0.0 : 1.0;
            else
            {
                double ph = (t - 12) % 8;
                scale = ph <= 4 ? 1 - ph / 4 : (ph - 4) / 4;
            }
            return (int)Math.Round(scale * 255);
        }

        // The wheel IGNORES the classic damper channel while the Trueforce
        // stream is live (proven by parked flick, 2026-08-31: 0.75 handed
        // back, wheel light either way), so the plugin SYNTHESIZES the damper
        // into the stream from DirectInput wheel speed (game-agnostic; see
        // TrueforcePlugin.AddSynthesizedDamper). This source only supplies the
        // COEFFICIENT: the latest bridge value, shaped by the DAMPTEST wiggle
        // and zeroed by CSPFFB DAMP.
        private int   _cspDampTestScaleNow = -1;
        private float _cspDamperNow;

        public float CspDamperCoefficientNow
        {
            get
            {
                if (_cspZeroDamper) return 0f;
                float c = _cspDamperNow;
                int scale = _cspDampTestScaleNow;
                if (scale >= 0) c *= scale / 255f;
                return c;
            }
        }

        /// <summary>Arms the CSP bridge latch. Off = the map is closed and
        /// TryGetFreshCspFfbTarget always returns null.</summary>
        public bool CspBridgeLatchEnabled
        {
            get => _cspLatchEnabled;
            set => _cspLatchEnabled = value;
        }

        public bool CspBridgeOpen => _cspBridge.IsOpen;
        public bool CspBridgeBadHeader => _cspBridge.BadHeader;
        public AcCspBridgeSample CspLastSample => _cspLast;

        /// <summary>The active car's own shift-light switch-on RPMs, ascending,
        /// or null when the car models none. Constant for a session, so the
        /// reader hands back the same array until the values change.</summary>
        public float[] CspAcLedStepRpms => _cspBridge.AcLedStepRpms;

        /// <summary>The active car's own shift-light colours, three bytes per
        /// LED, or null when the car gives none.</summary>
        public byte[] CspAcLedStepColors => _cspBridge.AcLedStepColors;

        /// <summary>Which bridge field the force comes from. Live-settable from
        /// any thread; the poll thread reads it next tick.</summary>
        public AcCspField CspField
        {
            get => (AcCspField)_cspFieldSel;
            set => _cspFieldSel = (int)value;
        }

        /// <summary>Full-scale column torque in Nm for the Torque field: this
        /// many Nm maps to full device force. Clamped to a sane floor.</summary>
        public double CspMaxNm
        {
            get => _cspMaxNm;
            set => _cspMaxNm = (float)(value < 1.0 ? 1.0 : value);
        }

        /// <summary>One line on what the bridge is doing, for the status strip:
        /// not found, wrong header, live (with the current values and active
        /// field), or frozen.</summary>
        public string CspBridgeStatus
        {
            get
            {
                if (!_cspLatchEnabled) return "";
                if (!_cspBridge.IsOpen)
                    return "CSP bridge not found: install gamemods/AssettoCorsaCsp/tf4all and enable it in CSP's FFB Tweaks, then drive.";
                if (_cspBridge.BadHeader)
                    return "CSP bridge found but its header does not match this plugin build.";
                if (!_cspHaveSample) return "CSP bridge open, waiting for the first sample.";
                var l = _cspLast;
                string vals = $"pure {l.FfbPure:+0.00;-0.00} / final {l.FfbFinal:+0.00;-0.00} / gain {l.FfbMultiplier:0.00} / {l.SteerTorque:0.0} Nm";
                string field = $"[{(AcCspField)_cspFieldSel}]";
                return TryGetFreshCspFfbTarget(1000).HasValue
                    ? $"Force read from CSP {field}: {vals}."
                    : $"CSP bridge frozen (last {vals})";
            }
        }

        /// <summary>Arms the finalFF latch. Off = the poll loop never touches
        /// offset 308 and TryGetFreshFfbTarget always returns null.</summary>
        public bool FfbLatchEnabled
        {
            get => _ffbLatchEnabled;
            set => _ffbLatchEnabled = value;
        }

        /// <summary>True while AC has reported exactly zero force for the whole
        /// of the last few seconds of driving: the in-game gain is at 0, so
        /// finalFF cannot carry the force and the wire tap is the better source.
        /// Clears on the first non-zero value.</summary>
        public bool FfbPinnedAtZero => _ffbPinnedAtZero;

        public Action<string> Logger { get; set; }

        // Picks the CSP field the latch injects. Pure/Torque carry force at
        // gain 0; Final/Value are post-gain (0 at gain 0). Torque is the raw
        // column torque scaled to full device force by CspMaxNm.
        private float SelectCspValue(AcCspBridgeSample s)
        {
            switch ((AcCspField)_cspFieldSel)
            {
                case AcCspField.Final: return s.FfbFinal;
                case AcCspField.Value: return s.FfbValue;
                case AcCspField.Torque:
                    float n = s.SteerTorque / _cspMaxNm;
                    return n > 1f ? 1f : n < -1f ? -1f : n;
                default: return s.FfbPure;
            }
        }

        public override void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try
            {
                _physicsMmf = MemoryMappedFile.OpenExisting(PhysicsName, MemoryMappedFileRights.Read);
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
            // A fresh Start() must not accidentally suppress its first frame if
            // the new session's packetId happens to match the last one we saw,
            // and the airborne/slip detectors re-arm from scratch for the new
            // car and track.
            _lastPacketId = -1;
            _seenWheelLoad = false;
            _prevAirborne  = false;
            _prevSlipValid = false;
            // Drop both FFB latches so a fresh Start() can't hand the provider a
            // force from the previous session, and close the CSP channels.
            Interlocked.Exchange(ref _ffbPacked, 0);
            Interlocked.Exchange(ref _cspPacked, 0);
            _cspBridge.Close();
            _cspControl.Close();
            _cspControlActive = false;
            _cspHaveSample = false;
            _cspLoggedLive = false;
            _cspLoggedBadHeader = false;
            Interlocked.Exchange(ref _running, 0);
        }

        private void CleanupMmf()
        {
            try { _physicsView?.Dispose(); } catch { }
            try { _physicsMmf?.Dispose(); } catch { }
            _physicsView = null;
            _physicsMmf = null;
        }

        private bool TryReopenMmf()
        {
            try
            {
                _physicsMmf = MemoryMappedFile.OpenExisting(PhysicsName, MemoryMappedFileRights.Read);
                _physicsView = _physicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return true;
            }
            catch
            {
                CleanupMmf();
                return false;
            }
        }

        private void PollLoop()
        {
            TimeBeginPeriod(1);
            try
            {
                var sw = Stopwatch.StartNew();
                long nextTickMs = 0;
                int consecutiveErrors = 0;
                bool reopenPending = false;

                while (!_stopping)
                {
                    int periodMs = TickPeriodMs;

                    // The CSP bridge has its own clock (the script's seq), so it
                    // is polled every tick, physics page or not.
                    if (_cspLatchEnabled)
                    {
                        LatchCspBridge();
                        _cspControlActive = true;
                    }
                    else if (_cspBridge.IsOpen)
                    {
                        _cspBridge.Close();
                        _cspHaveSample = false;
                    }
                    // Once armed this session, heartbeat the control channel
                    // every tick so the script's liveness check stays fresh; the
                    // flag follows the current arm + suppress state.
                    if (_cspControlActive)
                    {
                        _cspDampTestScaleNow = CspDamperTestScale255();
                        _cspControl.Write(_cspLatchEnabled && _cspSuppressOutput, _cspZeroDamper,
                                          _cspDampTestScaleNow);
                    }

                    if (reopenPending)
                    {
                        // AC restarted (or never started since the failure): try
                        // to reopen. Success resets us to 1 kHz; failure stays in
                        // the 200 ms retry cadence.
                        if (TryReopenMmf())
                        {
                            consecutiveErrors = 0;
                            reopenPending = false;
                            _lastPacketId = -1;
                            Log("AC shared memory reopened after restart.");
                        }
                        else
                        {
                            periodMs = RetryPeriodMs;
                        }
                    }
                    else
                    {
                        try
                        {
                            int pktId = _physicsView.ReadInt32(OFF_PACKET_ID);
                            if (pktId != _lastPacketId)
                            {
                                _lastPacketId = pktId;
                                // Latch finalFF BEFORE the frame dispatch so the
                                // FFB value's freshness never trails the effect
                                // pipeline's work for the same tick.
                                if (_ffbLatchEnabled) LatchFfb();
                                EmitFrame(ReadFrame());
                            }
                            consecutiveErrors = 0;
                        }
                        catch (Exception ex)
                        {
                            // AC quitting mid-session leaves the view valid as a
                            // CLR object but every read throws. Count the run and
                            // reopen so the source is not a silent zombie.
                            consecutiveErrors++;
                            if (consecutiveErrors == 1)
                                Log("AC poll error: " + ex.GetType().Name + ": " + ex.Message);
                            if (consecutiveErrors >= ReopenAfterConsecutiveErrors)
                            {
                                Log("AC shared memory unresponsive; will attempt reopen.");
                                CleanupMmf();
                                reopenPending = true;
                                Interlocked.Exchange(ref _ffbPacked, 0);
                            }
                        }
                    }

                    // Stopwatch-paced cadence; if we fall behind (GC pause), reset
                    // the phase rather than spin catching up.
                    nextTickMs += periodMs;
                    long elapsed = sw.ElapsedMilliseconds;
                    int sleepMs = (int)(nextTickMs - elapsed);
                    if (sleepMs <= 0)
                    {
                        nextTickMs = elapsed + periodMs;
                        sleepMs = periodMs;
                    }
                    Thread.Sleep(sleepMs);
                }
            }
            finally
            {
                TimeEndPeriod(1);
            }
        }

        // Reads finalFF and publishes it for the 1 kHz FFB provider. Poll thread
        // only, and only on a new packetId with the latch armed, so a paused game
        // (frozen packetId) stops re-timestamping and the value ages out.
        private void LatchFfb()
        {
            float finalFf = _physicsView.ReadSingle(OFF_FINAL_FF);
            float speedKmh = _physicsView.ReadSingle(OFF_SPEED_KMH);
            long now = _ffbSw.ElapsedTicks & FfbTimestampMask;
            _ffbPinnedAtZero = _ffbZeroPin.Note(finalFf, speedKmh, _ffbSw.ElapsedTicks, Stopwatch.Frequency);
            Interlocked.Exchange(ref _ffbPacked, PackFfb(FfbToLsb(finalFf), now));
        }

        /// <summary>The latest latched game force (finalFF) if it is no older
        /// than <paramref name="maxAgeMs"/>, else null. Same contract, packing
        /// and wrap-safe age math as UsbPcapFfbTap.TryGetFreshFfbTarget, so the
        /// provider chain can consult either interchangeably. Use a SHORT window
        /// (~250 ms): a ~333 Hz source that missed 80+ ticks is paused or gone.
        /// Returns null while pinned at zero (gain 0: let the wire tap have it).</summary>
        public short? TryGetFreshFfbTarget(int maxAgeMs)
        {
            if (_ffbPinnedAtZero) return null;
            long packed = Interlocked.Read(ref _ffbPacked);
            if (packed == 0) return null;
            long now = _ffbSw.ElapsedTicks & FfbTimestampMask;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if (AgeTicks(packed, now) > maxAgeTicks) return null;
            return UnpackFfbValue(packed);
        }

        /// <summary>Drop the latched finalFF so TryGetFreshFfbTarget returns null
        /// until a genuinely fresh physics tick latches a new one.</summary>
        public void ClearLastFfbTarget()
        {
            Interlocked.Exchange(ref _ffbPacked, 0);
        }

        // Poll-thread only. Latches the selected CSP field on every seq advance;
        // after 10 s without one on an open map, closes it so the next attempt
        // reopens (AC may have restarted with a fresh mapping). The reader keeps
        // the last seq across that, so a merely-paused game does not replay.
        private void LatchCspBridge()
        {
            long ticks = _ffbSw.ElapsedTicks;
            if (_cspBridge.TryReadNew(ticks, out var s))
            {
                _cspLast = s;
                _cspHaveSample = true;
                _cspLastNewTicks = ticks;
                // Change detector: the 2 s sampler would miss a lock-transient
                // or speed-dependent damper. Logs the moment the INCOMING
                // damper moves (rate-limited), so a drive answers whether
                // anything in AC/CSP modulates it.
                if (float.IsNaN(_cspDamperSeen)) _cspDamperSeen = s.FfbDamper;
                else if (Math.Abs(s.FfbDamper - _cspDamperSeen) > 0.005f
                         && ticks - _cspDamperLogTicks > Stopwatch.Frequency / 5)
                {
                    Log($"CSP damper changed {_cspDamperSeen:F3} -> {s.FfbDamper:F3}.");
                    _cspDamperSeen = s.FfbDamper;
                    _cspDamperLogTicks = ticks;
                }
                _cspDamperNow = s.FfbDamper;
                Interlocked.Exchange(ref _cspPacked, PackFfb(FfbToLsb(SelectCspValue(s)), ticks & FfbTimestampMask));
                if (!_cspLoggedLive || ticks - _cspLastPeriodicLogTicks > CspPeriodicLogTicks)
                {
                    _cspLoggedLive = true;
                    _cspLastPeriodicLogTicks = ticks;
                    Log($"CSP sample (field={(AcCspField)_cspFieldSel}, suppress={_cspSuppressOutput}): "
                      + $"ffbValue={s.FfbValue:F3} ffbPure={s.FfbPure:F3} ffbFinal={s.FfbFinal:F3} "
                      + $"gain={s.FfbMultiplier:F2} steerTorque={s.SteerTorque:F2} Nm "
                      + $"damper={s.FfbDamper:F3} steerSpd={s.SteerInputSpeed:F2}.");
                }
                return;
            }
            if (_cspBridge.BadHeader && !_cspLoggedBadHeader)
            {
                _cspLoggedBadHeader = true;
                Log("CSP bridge map found but the header does not match (magic/version); ignoring it.");
            }
            if (_cspBridge.IsOpen && _cspHaveSample && ticks - _cspLastNewTicks > CspFrozenReopenTicks)
            {
                _cspBridge.Close();
                _cspLastNewTicks = ticks;
                _cspLoggedLive = false;
                Log("CSP bridge silent for 10 s; closed it, will reopen when it writes again.");
            }
        }

        /// <summary>The latest CSP bridge force if it is no older than
        /// <paramref name="maxAgeMs"/>, else null. Same contract as
        /// TryGetFreshFfbTarget; no zero pin, because the pre-gain fields are the
        /// point (they carry force with the in-game gain at 0).</summary>
        public short? TryGetFreshCspFfbTarget(int maxAgeMs)
        {
            long packed = Interlocked.Read(ref _cspPacked);
            if (packed == 0) return null;
            long now = _ffbSw.ElapsedTicks & FfbTimestampMask;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if (AgeTicks(packed, now) > maxAgeTicks) return null;
            return UnpackFfbValue(packed);
        }

        public void ClearLastCspFfbTarget()
        {
            Interlocked.Exchange(ref _cspPacked, 0);
        }

        /// <summary>finalFF (normalized, +-1.0 = full device force; AC lets it
        /// run past +-1 to signal clipping) to the signed int16 LSB scale the FFB
        /// pipeline speaks, the same scale the wire tap decodes from HID++ 0x8123,
        /// so downstream sign/scale/smoothing is identical for both sources.
        /// Clamps the clipping range to full scale and maps NaN to silence.</summary>
        internal static short FfbToLsb(float finalFf)
        {
            if (float.IsNaN(finalFf)) return 0;
            double scaled = Math.Round(finalFf * 32767.0);
            if (scaled >  32767.0) return  32767;
            if (scaled < -32767.0) return -32767;
            return (short)scaled;
        }

        /// <summary>Low 16 bits = value bit pattern, high 48 = ticks (masked). A
        /// packed value of 0 is the "nothing latched" sentinel; a genuine zero
        /// latch at tick 0 colliding with it lasts one physics frame and reads as
        /// "no data", which is the safe direction.</summary>
        internal static long PackFfb(short value, long ticksMasked)
            => (ticksMasked << 16) | (ushort)value;

        internal static short UnpackFfbValue(long packed) => (short)(packed & 0xffff);

        /// <summary>Wrap-safe age: modular subtraction in the 48-bit tick space,
        /// so freshness survives the QPC wrap (same scheme as UsbPcapFfbTap).</summary>
        internal static long AgeTicks(long packed, long nowTicksMasked)
            => (nowTicksMasked - (long)((ulong)packed >> 16)) & FfbTimestampMask;

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);

        // Reads one physics packet into a TelemetryFrame. Beyond the plain field
        // reads, it derives an airborne flag and load-weighted slip scalars; see
        // the helpers below for the reasoning behind each.
        private TelemetryFrame ReadFrame()
        {
            float gas        = _physicsView.ReadSingle(OFF_GAS);
            float brake      = _physicsView.ReadSingle(OFF_BRAKE);
            int   gearRaw    = _physicsView.ReadInt32(OFF_GEAR);
            int   rpms       = _physicsView.ReadInt32(OFF_RPMS);
            float steerAngle = _physicsView.ReadSingle(OFF_STEER_ANGLE);
            float speedKmh   = _physicsView.ReadSingle(OFF_SPEED_KMH);
            float accGx      = _physicsView.ReadSingle(OFF_ACC_G_X);
            float accGy      = _physicsView.ReadSingle(OFF_ACC_G_Y);
            float accGz      = _physicsView.ReadSingle(OFF_ACC_G_Z);
            float slipFL     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_FL);
            float slipFR     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_FR);
            float slipRL     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_RL);
            float slipRR     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_RR);
            float loadFL     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_FL);
            float loadFR     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_FR);
            float loadRL     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_RL);
            float loadRR     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_RR);
            float angFL      = _physicsView.ReadSingle(OFF_WHEEL_ANG_FL);
            float angFR      = _physicsView.ReadSingle(OFF_WHEEL_ANG_FR);
            float angRL      = _physicsView.ReadSingle(OFF_WHEEL_ANG_RL);
            float angRR      = _physicsView.ReadSingle(OFF_WHEEL_ANG_RR);
            float suspFL     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_FL);
            float suspFR     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_FR);
            float suspRL     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_RL);
            float suspRR     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_RR);
            float tc         = _physicsView.ReadSingle(OFF_TC);
            int   pitLimiter = _physicsView.ReadInt32(OFF_PIT_LIMITER_ON);
            float yawRateRad = _physicsView.ReadSingle(OFF_LOCAL_ANG_VEL_Y);

            double throttle = gas;
            if (throttle < 0.0) throttle = 0.0;
            else if (throttle > 1.0) throttle = 1.0;

            var slip = TireQuad.Of(slipFL, slipFR, slipRL, slipRR);

            // Airborne: AC freezes every wheelSlip entry when no wheel touches
            // the ground. Detected against the previous frame's slip (so this is
            // read BEFORE _prevSlip is updated below).
            bool airborne = AllSlipFrozen(slip, _prevSlip, _prevSlipValid, speedKmh);
            if (airborne != _prevAirborne)
            {
                Log(airborne ? "AC: airborne (all wheelSlip entries frozen)." : "AC: grounded.");
                _prevAirborne = airborne;
            }

            // Once we have ever seen a real load, the load guards arm; before
            // that (a car/mod that never reports load) we fall back to raw slip.
            if (Math.Max(Math.Max(Math.Abs(loadFL), Math.Abs(loadFR)),
                         Math.Max(Math.Abs(loadRL), Math.Abs(loadRR))) > GroundedLoadN && !_seenWheelLoad)
                _seenWheelLoad = true;

            var loadN = TireQuad.Of(loadFL, loadFR, loadRL, loadRR);
            TireQuad effLoad = ZeroFrozenLoads(slip, _prevSlip, _prevSlipValid, speedKmh, loadN);

            _prevSlip = slip;
            _prevSlipValid = true;

            double scalarSlip = DirectScalarSlip(slip, effLoad, _seenWheelLoad);
            TireQuad combinedSlip = GuardByLoad(TireQuad.Of(slipFL, slipFR, slipRL, slipRR), loadN, _seenWheelLoad);
            var wheelRotRadS = TireQuad.Of(angFL, angFR, angRL, angRR);
            TireQuad slipRatio = GuardByLoad(DeriveSlipRatio(wheelRotRadS, speedKmh, gearRaw == 0), loadN, _seenWheelLoad);

            TelemetryFrame frame = default(TelemetryFrame);
            frame.Rpms = rpms;
            frame.Throttle01 = throttle;
            frame.Brake01 = brake < 0f ? 0.0 : (brake > 1f ? 1.0 : (double)brake);
            frame.SpeedKmh = speedKmh;
            frame.AccelerationSway  = accGx * 9.80665f;
            frame.AccelerationHeave = accGy * 9.80665f;
            frame.AccelerationSurge = accGz * 9.80665f;
            frame.YawRateDegPerSec = (double)yawRateRad * (180.0 / Math.PI);
            frame.SteeringAngle = steerAngle;
            frame.Gear = GearString(gearRaw);
            frame.WheelSlip = scalarSlip;
            frame.Airborne = airborne;
            frame.HasTireQuads = true;
            frame.TireCombinedSlip = combinedSlip;
            frame.SuspTravelM = TireQuad.Of(suspFL, suspFR, suspRL, suspRR);
            frame.WheelRotRadS = wheelRotRadS;
            frame.TireSlipRatio = slipRatio;
            frame.TcActive = tc > 0.01f ? 1 : 0;
            frame.PitLimiterActive = pitLimiter;
            return frame;
        }

        // Longitudinal slip ratio per wheel from wheel angular velocity, road
        // speed, and the rolling radius. Undefined below a walking pace or in
        // reverse (returns zeros), and clamped to +-2 so a spinning or locked
        // wheel cannot produce an absurd value.
        internal static TireQuad DeriveSlipRatio(TireQuad wheelRotRadS, double speedKmh, bool reverseGear)
        {
            double v = Math.Abs(speedKmh) / 3.6;
            if (reverseGear || v < SlipRatioMinSpeedMs) return default(TireQuad);
            return TireQuad.Of(R(wheelRotRadS.FL), R(wheelRotRadS.FR), R(wheelRotRadS.RL), R(wheelRotRadS.RR));

            double R(double omega)
            {
                double ratio = (omega * RollingRadiusM - v) / v;
                if (ratio > 2.0) ratio = 2.0;
                else if (ratio < -2.0) ratio = -2.0;
                return ratio;
            }
        }

        // Zeroes the per-wheel load for any wheel whose slip is frozen (that
        // wheel is off the ground), so the load-weighted scalar ignores it.
        // Below walking pace or before a valid previous frame it trusts the raw
        // load (freezing is normal when stationary).
        internal static TireQuad ZeroFrozenLoads(TireQuad slip, TireQuad prevSlip, bool prevValid, double speedKmh, TireQuad loadN)
        {
            if (!prevValid || speedKmh <= FrozenGuardSpeedKmh) return loadN;
            return TireQuad.Of(
                slip.FL == prevSlip.FL ? 0.0 : loadN.FL,
                slip.FR == prevSlip.FR ? 0.0 : loadN.FR,
                slip.RL == prevSlip.RL ? 0.0 : loadN.RL,
                slip.RR == prevSlip.RR ? 0.0 : loadN.RR);
        }

        // The airborne test: every wheel's slip is identical to the previous
        // frame while moving. Below walking pace or before a valid previous
        // frame it is never airborne (frozen slip is expected at rest).
        internal static bool AllSlipFrozen(TireQuad slip, TireQuad prevSlip, bool prevValid, double speedKmh)
        {
            if (!prevValid || speedKmh <= FrozenGuardSpeedKmh) return false;
            return slip.FL == prevSlip.FL && slip.FR == prevSlip.FR
                && slip.RL == prevSlip.RL && slip.RR == prevSlip.RR;
        }

        // The single slip scalar effects read: a load-weighted blend of the four
        // wheels (so a lightly loaded wheel counts less), falling back to the
        // peak raw slip when no load has been seen.
        internal static double DirectScalarSlip(TireQuad slip, TireQuad effLoad, bool seenLoad)
        {
            double fallback = seenLoad ? 0.0 : slip.MaxAbs;
            return LoadWeighting.Weighted(slip.FL, slip.FR, slip.RL, slip.RR,
                                          effLoad.FL, effLoad.FR, effLoad.RL, effLoad.RR,
                                          MinTotalLoadN, fallback);
        }

        // Zeroes any quad entry whose wheel is effectively unloaded (off the
        // ground), once the load guards have armed.
        internal static TireQuad GuardByLoad(TireQuad q, TireQuad loadN, bool armed)
        {
            if (!armed) return q;
            return TireQuad.Of(
                Math.Abs(loadN.FL) < AirborneLoadN ? 0.0 : q.FL,
                Math.Abs(loadN.FR) < AirborneLoadN ? 0.0 : q.FR,
                Math.Abs(loadN.RL) < AirborneLoadN ? 0.0 : q.RL,
                Math.Abs(loadN.RR) < AirborneLoadN ? 0.0 : q.RR);
        }

        // AC gear index: 0 = reverse, 1 = neutral, 2+ = forward gears 1..n.
        private static string GearString(int gear)
        {
            switch (gear)
            {
                case 0:  return "R";
                case 1:  return "N";
                default: return (gear - 1).ToString();
            }
        }

        private void Log(string msg)
        {
            var logger = Logger;
            if (logger == null) return;
            try { logger(msg); } catch { }
        }
    }
}
