// Reads AC's physics shared memory page (Local\acpmf_physics) at 1 kHz.
// AC's native solver runs at 333 Hz, so polling faster than that re-reads
// the same data, but at 1 kHz any new physics tick is observed within
// ≤1 ms of being written and lines up with our 1 kHz Trueforce packet
// cadence, so events never get aliased against packet boundaries. The
// fidelity gain over the 60 Hz SimHub IDataPlugin tick is most audible
// in RoadBumpsEffect (sharp kerb leading edges) and TractionLossEffect
// (direct wheelSlip[] reading instead of the heuristic SimHub falls back
// to).
//
// Fields that don't need physics-rate fidelity, MaxRpm (static per car),
// AbsActive (slow pump events), are deliberately left for the SimHub
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
        // steerAngle: normalized steering input, ~[-1, 1] (0 = centered).
        // Feeds the stationary-spring FFB floor; AC is the only source that
        // currently surfaces steering, see TelemetryFrame.SteeringAngle.
        private const int OFF_STEER_ANGLE     = 24;    // float, normalized
        private const int OFF_SPEED_KMH       = 28;    // float
        private const int OFF_ACC_G_X         = 44;    // float, lateral, g
        private const int OFF_ACC_G_Y         = 48;    // float, vertical, g
        private const int OFF_ACC_G_Z         = 52;    // float, longitudinal, g (positive = forward)
        // wheelSlip[4], float[4] starting at offset 56. Order is FL/FR/RL/RR;
        // 0 = perfect grip, larger magnitude = more slip. Collapsed to the
        // scalar WheelSlip for TractionLossEffect by load-weighting across the
        // four tyres (issue #30, see ReadFrame): a wheel carrying no load
        // contributes nothing, so an airborne or kerb-lifted tyre's slip spike
        // drops out. (The TireCombinedSlip QUAD uses the binary GuardByLoad
        // instead; the two serve different consumers.)
        private const int OFF_WHEEL_SLIP_FL   = 56;
        private const int OFF_WHEEL_SLIP_FR   = 60;
        private const int OFF_WHEEL_SLIP_RL   = 64;
        private const int OFF_WHEEL_SLIP_RR   = 68;
        // wheelLoad[4], float[4] immediately after wheelSlip. Vertical tyre
        // load in Newtons; ~0 on every wheel means no tyre is touching the
        // ground (airborne). A grounded car always carries hundreds-plus N per
        // wheel, so the airborne threshold is absolute and car-independent.
        private const int OFF_WHEEL_LOAD_FL   = 72;
        private const int OFF_WHEEL_LOAD_FR   = 76;
        private const int OFF_WHEEL_LOAD_RL   = 80;
        private const int OFF_WHEEL_LOAD_RR   = 84;
        // pitLimiterOn (int) tracks the LIMITER BUTTON state, not pit-lane
        // geometry. Reading this directly bypasses SimHub's mapping, which
        // surfaces "in pit lane" as PitLimiterOn for AC and produces false
        // positives whenever the car sits in the pit area (e.g., spawn box
        // on touge maps that the AC track defines as pit lane).
        // wheelAngularSpeed[4], float[4] at 104, rad/s per wheel. Unit-exact
        // for TelemetryFrame.WheelRotRadS and the slip-ratio derivation.
        private const int OFF_WHEEL_ANG_FL    = 104;
        private const int OFF_WHEEL_ANG_FR    = 108;
        private const int OFF_WHEEL_ANG_RL    = 112;
        private const int OFF_WHEEL_ANG_RR    = 116;
        // suspensionTravel[4], float[4] at 184, meters. Feeds the CTM
        // composer's load-weighted axle rollups.
        private const int OFF_SUSP_TRAVEL_FL  = 184;
        private const int OFF_SUSP_TRAVEL_FR  = 188;
        private const int OFF_SUSP_TRAVEL_RL  = 192;
        private const int OFF_SUSP_TRAVEL_RR  = 196;
        // tc, float at 204: the game's traction-control intervention level
        // (0 = idle, >0 = actively cutting). Not the TC configuration.
        private const int OFF_TC              = 204;
        private const int OFF_PIT_LIMITER_ON  = 248;
        private const int OFF_LOCAL_ANG_VEL_Y = 300;   // float, yaw rad/s

        // 1 kHz poll cadence, see header comment for rationale. Requires
        // timeBeginPeriod(1), set explicitly inside PollLoop, for Thread.Sleep
        // to honor 1 ms instead of the OS default ~15 ms.
        private const int TickPeriodMs = 1;

        // After this many consecutive read failures (~5 ms of bad reads),
        // assume AC has restarted / closed and try to reopen the MMF. Without
        // this the source becomes a zombie when the user restarts AC mid-
        // session: the view accessor stays valid as a CLR object but every
        // read throws, exceptions are swallowed, and no telemetry ever
        // reaches effects.
        private const int ReopenAfterConsecutiveErrors = 5;
        // While in the reopen-retry state, slow the poll cadence so we don't
        // spin at 1 kHz logging the same OpenExisting failure forever.
        private const int RetryPeriodMs = 200;

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

        // Airborne detection (wheelLoad). Some AC builds leave wheelLoad
        // unpopulated (stuck at 0); if we treated that as "airborne" we'd
        // suppress slip for the whole session. _seenWheelLoad gates the
        // detector: it only arms once we've observed a clearly-grounded load,
        // proving the field is live. Failsafe to current behavior otherwise.
        // _prevAirborne is for one-shot edge logging.
        private bool _seenWheelLoad;
        private bool _prevAirborne;
        // A grounded wheel carries far more than this; arms the detector.
        private const float GroundedLoadN = 100.0f;
        // All four below this = no tyre touching = airborne.
        private const float AirborneLoadN = 1.0f;

        // Load-weighted scalar slip (issue #30). Below this summed wheelLoad
        // (newtons) no tyre is meaningfully on the ground, so the weighting has
        // nothing to key on and falls back (silent when the load channel is
        // live, legacy max-abs when it never populated). A grounded car carries
        // hundreds-plus N per wheel; airborne is <1 N each, so 4 N cleanly
        // separates them.
        internal const double MinTotalLoadN = 4.0;

        // Frozen-entry guard for the scalar weighting. AC freezes a wheel's
        // wheelSlip[] entry the instant it leaves the ground (it stops solving
        // that contact patch, CM-verified), so an entry bit-identical to the
        // previous physics frame means the wheel is off the road; we zero its
        // load so it can't contribute even on a build where wheelLoad doesn't
        // collapse cleanly on contact loss (belt-and-suspenders for the same
        // case GuardByLoad handles on the quad). The comparison is deliberately
        // exact ==, not an epsilon: it detects "the solver stopped touching this
        // entry", and a grounded wheel (locked included) is still solved every
        // step against a decaying speed and shifting load, so its ndSlip moves
        // in at least the low bits. Speed-gated: at a crawl a grounded wheel's
        // slip legitimately repeats frame to frame, which isn't loss of contact.
        internal const double FrozenGuardSpeedKmh = 10.0;
        private TireQuad _prevSlip;
        private bool     _prevSlipValid;

        public Action<string> Logger { get; set; }

        /// <summary>Opens the AC physics page and starts the polling thread.
        /// Throws if Local\acpmf_physics is unavailable (AC not running)
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
            // New session may be a different car / track; re-arm the airborne
            // detector and the slip load-weighting state from scratch.
            _seenWheelLoad = false;
            _prevAirborne  = false;
            _prevSlipValid = false;
            Interlocked.Exchange(ref _running, 0);
        }

        private void CleanupMmf()
        {
            try { _physicsView?.Dispose(); } catch { }
            try { _physicsMmf?.Dispose();  } catch { }
            _physicsView = null;
            _physicsMmf  = null;
        }

        // Returns true if the physics page is now open and readable. Quiet on
        // failure (the caller is polling so a single missing-MMF means "AC
        // hasn't restarted yet" and shouldn't log).
        private bool TryReopenMmf()
        {
            try
            {
                _physicsMmf  = MemoryMappedFile.OpenExisting(PhysicsName, MemoryMappedFileRights.Read);
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
            // Bump the system timer to 1 ms granularity for the duration of
            // the loop. Mirrors what TrueforceDevice.StreamLoop does, without
            // it, Thread.Sleep(1) below decays to the default ~15 ms tick and
            // our 1 kHz cadence collapses. timeBeginPeriod is reference-
            // counted on Windows, so this nests safely with the stream
            // thread's call.
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
                    if (reopenPending)
                    {
                        // AC restarted (or never started since the failure):
                        // try to reopen the MMF. Success resets us to normal
                        // 1 kHz cadence; failure stays in 200 ms retry mode.
                        if (TryReopenMmf())
                        {
                            consecutiveErrors = 0;
                            reopenPending     = false;
                            _lastPacketId     = -1;   // force re-emit on next observed packet
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
                            // Only emit when AC has actually written new physics data.
                            // AC's solver runs at ~333 Hz; polling at 1 kHz keeps
                            // event detection latency ≤1 ms but produces 2-3 polls
                            // per real frame, emitting on every poll would re-run
                            // every effect for the same inputs and inflate MeasuredHz.
                            int pktId = _physicsView.ReadInt32(OFF_PACKET_ID);
                            if (pktId != _lastPacketId)
                            {
                                _lastPacketId = pktId;
                                EmitFrame(ReadFrame());
                            }
                            consecutiveErrors = 0;
                        }
                        catch (Exception ex)
                        {
                            consecutiveErrors++;
                            // Only log the first error in a streak (and the
                            // moment we cross into reopen mode). Otherwise a
                            // sustained failure spams the log at 1 kHz.
                            if (consecutiveErrors == 1)
                                Log($"AC poll error: {ex.GetType().Name}: {ex.Message}");
                            if (consecutiveErrors >= ReopenAfterConsecutiveErrors)
                            {
                                Log("AC shared memory unresponsive; will attempt reopen.");
                                CleanupMmf();
                                reopenPending = true;
                            }
                        }
                    }

                    // Stopwatch-paced cadence. If we fall behind (GC pause, etc.),
                    // reset the phase so we don't spin trying to catch up.
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

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);

        private TelemetryFrame ReadFrame()
        {
            float gas      = _physicsView.ReadSingle(OFF_GAS);
            int   gear     = _physicsView.ReadInt32 (OFF_GEAR);
            int   rpms     = _physicsView.ReadInt32 (OFF_RPMS);
            float steer    = _physicsView.ReadSingle(OFF_STEER_ANGLE);
            float speedKmh = _physicsView.ReadSingle(OFF_SPEED_KMH);
            float accGX    = _physicsView.ReadSingle(OFF_ACC_G_X);
            float accGY    = _physicsView.ReadSingle(OFF_ACC_G_Y);
            float accGZ    = _physicsView.ReadSingle(OFF_ACC_G_Z);
            float wsFL     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_FL);
            float wsFR     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_FR);
            float wsRL     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_RL);
            float wsRR     = _physicsView.ReadSingle(OFF_WHEEL_SLIP_RR);
            float wlFL     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_FL);
            float wlFR     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_FR);
            float wlRL     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_RL);
            float wlRR     = _physicsView.ReadSingle(OFF_WHEEL_LOAD_RR);
            float waFL     = _physicsView.ReadSingle(OFF_WHEEL_ANG_FL);
            float waFR     = _physicsView.ReadSingle(OFF_WHEEL_ANG_FR);
            float waRL     = _physicsView.ReadSingle(OFF_WHEEL_ANG_RL);
            float waRR     = _physicsView.ReadSingle(OFF_WHEEL_ANG_RR);
            float stFL     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_FL);
            float stFR     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_FR);
            float stRL     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_RL);
            float stRR     = _physicsView.ReadSingle(OFF_SUSP_TRAVEL_RR);
            float tc       = _physicsView.ReadSingle(OFF_TC);
            int   pitLimit = _physicsView.ReadInt32 (OFF_PIT_LIMITER_ON);
            float yawRadS  = _physicsView.ReadSingle(OFF_LOCAL_ANG_VEL_Y);

            const float  G        = 9.80665f;
            const double RadToDeg = 180.0 / Math.PI;

            double throttle01 = gas;
            if (throttle01 < 0) throttle01 = 0;
            else if (throttle01 > 1) throttle01 = 1;

            // Airborne detection. The free-spinning, unloaded tyres of a car
            // in the air make wheelSlip[] spike, which TractionLossEffect would
            // read as a hard slide and buzz. When every wheel's vertical load
            // is ~0 the car is off the ground; we surface that as
            // TelemetryFrame.Airborne and let AirborneEffect duck the configured
            // voices. Gated on having seen a real grounded load first, so a
            // build that leaves wheelLoad at 0 reports airborne=false forever
            // instead of going permanently "airborne".
            float maxLoad = Math.Max(
                Math.Max(Math.Abs(wlFL), Math.Abs(wlFR)),
                Math.Max(Math.Abs(wlRL), Math.Abs(wlRR)));
            // Arm airborne detection only once a real grounded load is seen, so
            // a build that leaves wheelLoad at ~0 reports airborne=false forever
            // instead of going permanently "airborne".
            if (maxLoad > GroundedLoadN && !_seenWheelLoad)
                _seenWheelLoad = true;
            bool airborne = _seenWheelLoad && maxLoad < AirborneLoadN;
            if (airborne != _prevAirborne)
            {
                Log(airborne ? "AC: airborne (wheel loads ~0)." : "AC: grounded.");
                _prevAirborne = airborne;
            }

            // Load-weighted scalar slip (issue #30). Weighting each wheel's slip
            // by the vertical load it carries drops an unloaded wheel out of the
            // reading, so a jump or a one-wheel lift no longer buzzes as a
            // phantom slide (the free-spinning airborne tyre reads high slip but
            // ~0 load). This is the scalar the direct traction path reads;
            // GuardByLoad below handles the quad separately. The frozen-entry
            // guard, fallback selection, and threshold live in pure statics
            // (ZeroFrozenLoads / DirectScalarSlip) so they unit-test like their
            // GuardByLoad / DeriveSlipRatio siblings.
            var slipQuad = TireQuad.Of(wsFL, wsFR, wsRL, wsRR);
            var effLoad  = ZeroFrozenLoads(slipQuad, _prevSlip, _prevSlipValid, speedKmh,
                                           TireQuad.Of(wlFL, wlFR, wlRL, wlRR));
            _prevSlip      = slipQuad;
            _prevSlipValid = true;
            double weightedSlip = DirectScalarSlip(slipQuad, effLoad, _seenWheelLoad);

            // Per-tire quads. AC's wheelSlip[] is best identified as Kunos
            // ndSlip: COMBINED lat+long slip normalized to peak grip, ~1.0 at
            // the limit, exactly the unit TireCombinedSlip declares, so the
            // CTM rollups and AxleSlip light up with zero calibration.
            // Guarded per wheel by vertical load: an unloaded wheel FREEZES
            // its wheelSlip entry at the last value (verified against Content
            // Manager's reader), so a kerb-hopped or airborne corner would
            // otherwise hold a phantom slide. The guard zeroes unloaded
            // wheels' slip entries; it arms only after a real grounded load,
            // so builds that leave wheelLoad at 0 pass values through.
            var load    = TireQuad.Of(wlFL, wlFR, wlRL, wlRR);
            var combined = GuardByLoad(TireQuad.Of(wsFL, wsFR, wsRL, wsRR), load, _seenWheelLoad);
            var wheelRot = TireQuad.Of(waFL, waFR, waRL, waRR);
            // Signed longitudinal slip ratio derived from wheel rev rate vs
            // car speed (AC1 exposes no slip-ratio array): locked wheel gives
            // -1 regardless of tire-radius error, wheelspin runs positive.
            // Silent below walking pace and in reverse (see DeriveSlipRatio).
            var slipRatio = GuardByLoad(
                DeriveSlipRatio(wheelRot, speedKmh, gear == 0), load, _seenWheelLoad);

            return new TelemetryFrame
            {
                Rpms       = rpms,
                Throttle01 = throttle01,

                SpeedKmh          = speedKmh,
                AccelerationSway  = accGX * G,
                AccelerationHeave = accGY * G,
                AccelerationSurge = accGZ * G,
                YawRateDegPerSec  = yawRadS * RadToDeg,
                SteeringAngle     = steer,

                Gear      = GearString(gear),
                WheelSlip = weightedSlip,
                Airborne  = airborne,

                // Quads: combined slip (ndSlip scale), suspension travel in
                // meters (CTM load weighting), wheel rotation, derived slip
                // ratio. Slip ANGLE stays unset: AC1's physics page has no
                // per-wheel slip angle, and inventing one from body motion
                // belongs to a model, not a reader. Surface rumble / strips
                // do not exist in the page; KerbThump stays Forza-only.
                HasTireQuads    = true,
                TireCombinedSlip = combined,
                SuspTravelM      = TireQuad.Of(stFL, stFR, stRL, stRR),
                WheelRotRadS     = wheelRot,
                TireSlipRatio    = slipRatio,

                // TC intervention level (0..1); >0 means the game is actively
                // cutting power, the most direct grip-loss ground truth AC has.
                TcActive = tc > 0.01f ? 1 : 0,

                // pitLimiterOn read directly from AC's physics page so the
                // PitLimiterEffect sees the actual button state instead of
                // the SimHub overlay's pit-lane-geometry mapping.
                PitLimiterActive = pitLimit,
                // MaxRpm and AbsActive are deliberately left at their defaults;
                // TrueforcePlugin.DispatchFrame overlays them from the latest
                // SimHub reading, which is the right authority for both.
            };
        }

        // Fixed rolling radius for the slip-ratio derivation. Precision does
        // not matter much: a locked wheel reads omega ~0 so the ratio goes to
        // -1 whatever the radius, and the wheelspin threshold (+0.5) has wide
        // margin against the ~10% radius spread of racing tires.
        internal const float RollingRadiusM = 0.33f;

        // Below this speed the ratio derivation returns silence: a parked or
        // crawling car has omega ~0 on every LOADED wheel, which a naive
        // (omega*r - v)/v reads as full lockup, and the lockup consumers have
        // no speed gate of their own (LockupJudder would pulse at every stop,
        // grid, and pit box). Lockup/wheelspin detection below ~11 km/h is
        // not meaningful anyway.
        internal const double SlipRatioMinSpeedMs = 3.0;

        /// <summary>Signed longitudinal slip ratio per wheel from rotation
        /// speed vs car speed: (omega*r - v) / v, clamped to +-2. Returns an
        /// all-zero quad (no signal) below SlipRatioMinSpeedMs and in reverse
        /// gear: at standstill every loaded wheel would otherwise read -1
        /// (full lockup), and in reverse the signed wheel rotation runs
        /// opposite the unsigned speed so the ratio pins at the clamp; both
        /// are derivation artifacts, not grip states.</summary>
        internal static TireQuad DeriveSlipRatio(TireQuad wheelRotRadS, double speedKmh, bool reverseGear)
        {
            double v = Math.Abs(speedKmh) / 3.6;
            if (reverseGear || v < SlipRatioMinSpeedMs) return default;
            double R(double omega)
            {
                double r = (omega * RollingRadiusM - v) / v;
                if (r > 2.0) r = 2.0; else if (r < -2.0) r = -2.0;
                return r;
            }
            return TireQuad.Of(R(wheelRotRadS.FL), R(wheelRotRadS.FR),
                               R(wheelRotRadS.RL), R(wheelRotRadS.RR));
        }

        /// <summary>Effective per-wheel loads for the scalar #30 weighting with
        /// frozen (off-ground) wheels zeroed. A wheel whose wheelSlip[] entry is
        /// bit-identical to the previous physics frame has lost contact (AC
        /// stopped solving it), so its load is forced to 0 and it drops out of
        /// the weighted average, even if wheelLoad misbehaves on contact loss.
        /// No-op on the first frame (no previous to compare) and at/below
        /// FrozenGuardSpeedKmh, where a grounded wheel's slip can legitimately
        /// repeat. Exact == on purpose (see the field comment).</summary>
        internal static TireQuad ZeroFrozenLoads(
            TireQuad slip, TireQuad prevSlip, bool prevValid, double speedKmh, TireQuad loadN)
        {
            if (!prevValid || speedKmh <= FrozenGuardSpeedKmh) return loadN;
            return TireQuad.Of(
                slip.FL == prevSlip.FL ? 0.0 : loadN.FL,
                slip.FR == prevSlip.FR ? 0.0 : loadN.FR,
                slip.RL == prevSlip.RL ? 0.0 : loadN.RL,
                slip.RR == prevSlip.RR ? 0.0 : loadN.RR);
        }

        /// <summary>The scalar WheelSlip the direct traction path reads (#30):
        /// load-weighted |slip| across the four tyres. Fallback when no tyre is
        /// loaded: silent when the load channel has proven live (seenLoad, i.e.
        /// genuinely airborne) else the legacy max-abs (this build never
        /// populated wheelLoad, so weighting can't work at all).</summary>
        internal static double DirectScalarSlip(TireQuad slip, TireQuad effLoad, bool seenLoad)
        {
            double fallback = seenLoad ? 0.0 : slip.MaxAbs;
            return SlipWeighting.Weighted(
                slip.FL, slip.FR, slip.RL, slip.RR,
                effLoad.FL, effLoad.FR, effLoad.RL, effLoad.RR,
                MinTotalLoadN, fallback);
        }

        /// <summary>Zero a quad's entries for wheels carrying ~no vertical
        /// load. An unloaded AC wheel freezes its slip readings (and a free
        /// spinner reads as massive wheelspin), so its entries are noise.
        /// No-op until a real grounded load has been seen (armed), matching
        /// the airborne detector's defensive gate.</summary>
        internal static TireQuad GuardByLoad(TireQuad q, TireQuad loadN, bool armed)
        {
            if (!armed) return q;
            return TireQuad.Of(
                Math.Abs(loadN.FL) < AirborneLoadN ? 0.0 : q.FL,
                Math.Abs(loadN.FR) < AirborneLoadN ? 0.0 : q.FR,
                Math.Abs(loadN.RL) < AirborneLoadN ? 0.0 : q.RL,
                Math.Abs(loadN.RR) < AirborneLoadN ? 0.0 : q.RR);
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
