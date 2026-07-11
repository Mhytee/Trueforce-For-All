// Forza UDP "Data Out" telemetry source. Listens on a configurable port for
// the binary packets Forza Horizon 5 / Forza Motorsport (2023) emit when the
// user enables UDP RACE TELEMETRY in Settings → HUD and Gameplay.
//
// Rate: Forza Motorsport (2023) emits a fixed ~60 Hz, but the Horizon titles
// emit one packet PER RENDERED FRAME, so their telemetry rate tracks the game's
// FPS (commonly 100-160+ Hz, not 60). MeasuredHz therefore reads above 60 on
// Horizon and rises/falls with framerate; that is correct, not an estimator
// artifact, and the higher rate is finer telemetry resolution for the effects.
// "Enhanced" is justified on two counts: that (often) higher-than-SimHub rate
// AND the per-tire data Forza exposes that
// SimHub's StatusDataBase doesn't surface: SurfaceRumble[4] (the same channel
// Turn 10's own Trueforce path consumes), WheelOnRumbleStrip[4] for kerb
// pulses, TireCombinedSlip[4] for direct traction-loss detection, and the
// per-car NumCylinders (lets EnginePulse auto-tune its firing frequency).
//
// Packet layout (little-endian, FM7-compatible "Sled" + Horizon-Dash for
// FH4/5; offsets verified against the Forza Motorsport Data Out docs):
//
//   Sled (0..231), present in every Forza title:
//     0   IsRaceOn           int32  (0 = paused / menu / cutscene)
//     8   EngineMaxRpm       float32
//     16  CurrentEngineRpm   float32
//     20  AccelerationX      float32  (right, m/s²)
//     24  AccelerationY      float32  (up,    m/s²)   ← heave for road bumps
//     48  AngularVelocityY   float32  (yaw,   rad/s)
//     68  NormSuspTravel[4]  float32×4 (FL/FR/RL/RR, 0..1)
//     84  TireSlipRatio[4]   float32×4
//     116 OnRumbleStrip[4]   int32×4
//     148 SurfaceRumble[4]   float32×4 (vibration signal scaled by surface)
//     180 TireCombinedSlip[4] float32×4
//     212 CarOrdinal         int32   (Forza's unique car ID; 0 = no car loaded)
//     228 NumCylinders       int32
//
//   Horizon insert (232..243), 12 bytes Microsoft hasn't documented; skipped.
//
//   Dash block, identical field order everywhere, but its BASE offset moves:
//   Horizon (324-byte packet) puts it at 244 (after the 12-byte insert, plus
//   one unknown trailing byte at 323); Motorsport (311 = FM7, 331 = FM2023)
//   puts it directly at 232. FM2023 then appends TireWear[4] + TrackOrdinal
//   at 311..330. Dash-relative offsets (verified against the geeooff/
//   forza-data-web decoder structs, 2026-07-03):
//     +12 Speed              float32  (m/s)          → 256 Horizon, 244 FM8
//     +71 Accel              uint8    (throttle)     → 315 Horizon, 303 FM8
//     +72 Brake              uint8                   → 316 Horizon, 304 FM8
//     +75 Gear               uint8    (0=R, 1=N, 2..n=fwd)
//     +76 Steer              int8     (-127 left .. +127 right)
//   The base is resolved at runtime by plausibility check (ResolveDashBase),
//   not trusted blindly, so a doc drift or a future layout shift degrades to
//   a loud log line instead of garbage speed feeding the force path.
//
// IsRaceOn handling: in FH4/FH5 this flag is 1 during freeroam too, it's
// just "is the player driving" despite the name. We emit on every received
// packet regardless of the flag, but zero engine/slip/grip channels when
// IsRaceOn=0 so effects decay to silence during pause/menu instead of
// holding their last-known values.
//
// Spawn / un-pause settle window: the moment IsRaceOn flips 0->1 (load
// into a session, rewind, fast-travel, un-pause) Forza places the car on
// the track and its first frames carry the suspension-drop / tyre-touchdown
// transient: a big AccelerationHeave spike and a combined-slip spike. Fed
// straight through, that lands as a wheel jolt on every spawn. For a short
// window after the rising edge we keep the impact / grip channels zeroed
// (engine, throttle, speed, gear still flow so the engine note and
// auto-detect resume normally), so effects ramp in from rest instead of
// being kicked by the placement transient.
//
// Length tolerance: parse is gated on a minimum packet length so a future
// title that appends fields (likely FH6) just works without code changes.
// We require >= 232 (full Sled) for any useful effect; fields beyond that
// are optional and zeroed if the packet is shorter.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class ForzaUdpTelemetrySource : TelemetrySourceBase
    {
        public override string Name => "Forza (UDP)";
        public override bool   IsEnhanced => true;
        public override bool   IsRunning  => _running != 0;
        // Authoritative session signal: Forza's IsRaceOn is 0 during pause /
        // menu / cutscene / replay and 1 while actually on track (including
        // stationary), which is exactly when force feedback should be flowing.
        public override bool   IsSessionActive => IsRunning && LastIsRaceOn;
        // IsRaceOn is an authoritative pause flag, so the FFB pass-through can
        // trust !IsSessionActive to mean "paused / menu" and release the wheel.
        public override bool   HasAuthoritativeSessionState => true;
        // Forza's UDP packet at offset 228 carries NumCylinders, populated
        // every frame. Lets the plugin label AutoCylinderSource as
        // "telemetry" on car change without waiting for the first frame.
        public override bool   ProvidesNumCylinders => true;

        // Sled offsets (relative to packet start).
        private const int OFF_IS_RACE_ON         = 0;
        private const int OFF_ENGINE_MAX_RPM     = 8;
        private const int OFF_CURRENT_RPM        = 16;
        private const int OFF_ACCEL_X            = 20;
        private const int OFF_ACCEL_Y            = 24;
        private const int OFF_ACCEL_Z            = 28;   // longitudinal (forward/backward)
        private const int OFF_ANG_VEL_Y          = 48;
        private const int OFF_NORM_SUSP_FL       = 68;
        private const int OFF_TIRE_SLIP_RATIO_FL = 84;   // TireSlipRatio[4], signed (+spin/-lock)
        private const int OFF_WHEEL_ROT_FL       = 100;  // WheelRotationSpeed[4], rad/s
        private const int OFF_ON_RUMBLE_STRIP_FL = 116;
        private const int OFF_SURFACE_RUMBLE_FL  = 148;
        private const int OFF_TIRE_SLIP_ANGLE_FL = 164;  // TireSlipAngle[4], radians, signed
        private const int OFF_TIRE_COMBINED_FL   = 180;
        private const int OFF_SUSP_TRAVEL_M_FL   = 196;  // SuspensionTravelMeters[4]
        private const int OFF_CAR_ORDINAL        = 212;
        private const int OFF_NUM_CYLINDERS      = 228;

        // Dash offsets, relative to the dash BASE (see header). The base is
        // 244 on Horizon packets and 232 on Motorsport packets, resolved by
        // ResolveDashBase at runtime.
        private const int DASH_SPEED  = 12;
        private const int DASH_ACCEL  = 71;
        private const int DASH_BRAKE  = 72;
        private const int DASH_GEAR   = 75;
        // Steer: signed int8, offset-from-center, -127 (full left) .. +127
        // (full right). Feeds the stationary-spring FFB floor so it works in
        // Forza, not just AC. Low resolution (254 steps lock-to-lock) so the
        // consumer smooths it.
        private const int DASH_STEER  = 76;
        // Bytes of dash a parser must be able to address (through Steer).
        private const int DashSpanNeeded = DASH_STEER + 1;

        private const int DashBaseMotorsport = 232;   // FM7 (311) and FM2023 (331)
        private const int DashBaseHorizon    = 244;   // FH4/FH5/FH6 (324)

        // Smallest Sled-only payload. Anything shorter we discard.
        private const int MinSledLength    = 232;
        // Known full-packet sizes.
        private const int Fm7DashLength     = 311;
        private const int HorizonDashLength = 324;
        private const int Fm8DashLength     = 331;   // FM2023: dash@232 + TireWear[4] + TrackOrdinal

        private readonly int _port;
        private readonly IPAddress _bindAddress;
        private readonly IPEndPoint _forwardTo;   // null = no forwarding
        private UdpClient _udp;
        private Socket _forwardSocket;            // separate socket so a forward send error can't kill the receive socket
        private Thread _thread;
        private volatile bool _stopping;
        private int _running;

        public Action<string> Logger { get; set; }

        /// <summary>Most recent IsRaceOn flag FROM A REAL FRAME (all-zero
        /// keepalives don't stamp it: their flag byte is zeroed payload, not
        /// state). Exposed so the UI can show "active / paused" state
        /// independent of MeasuredHz.</summary>
        public bool LastIsRaceOn { get; private set; }

        // FH6 interleaves all-zero keepalives between real frames several
        // times a second while driving (see the keepalive gate in
        // ParsePacket). A lone keepalive adjacent to fresh real frames must
        // not reach consumers: its zeroed scalars (speed / rpm / gear "N")
        // wipe their caches mid-race (force notches through low-speed gates,
        // spurious neutral-gear decays in the effects). Only persistent
        // emptiness (a real pause / menu: no real frame for
        // KeepaliveSilenceAfterMs) passes the silencing zero frame through.
        // Receive-thread only.
        internal double KeepaliveSilenceAfterMs = 250;
        private long _lastRealFrameTicks;
        internal bool ShouldEmit(TelemetryFrame frame, long nowTicks)
        {
            if (frame.MaxRpm > 0)
            {
                _lastRealFrameTicks = nowTicks;
                return true;
            }
            if (_lastRealFrameTicks != 0
                && (nowTicks - _lastRealFrameTicks) * 1000.0
                   / System.Diagnostics.Stopwatch.Frequency < KeepaliveSilenceAfterMs)
                return false;
            // Persistent emptiness IS session state, even though one zeroed
            // payload is not: no real frame for KeepaliveSilenceAfterMs means
            // pause / menu, so the session flag drops HERE (the zeroed packets
            // themselves no longer stamp it in ParsePacket). This keeps the
            // FFB provider's pause-release and the stop-stream-on-pause gate
            // engaging for Horizon pauses that stream only keepalives (issue
            // #13's full-lock protection), just one silence window later than
            // the per-packet stamping it replaces. Dropping _prevRaceOn too
            // re-arms the raceOn 0->1 settle edge for the restart-from-menu
            // placement jolt, matching the pre-swallow behavior.
            LastIsRaceOn = false;
            _prevRaceOn  = false;
            return true;
        }

        /// <summary>Forza's CarOrdinal for the currently-loaded car (unique
        /// per car model across the entire Forza catalog). Null until we've
        /// seen a non-zero ordinal. Plugin uses this as a CarId fallback when
        /// SimHub's data feed has nothing (Forza game-profile not selected
        /// in SimHub, UDP forwarding misconfigured, etc.) so the per-car
        /// override logic still has something to key on.</summary>
        public int? CurrentCarOrdinal { get; private set; }

        // Spawn / un-pause settle window (see header comment). _prevRaceOn
        // tracks the IsRaceOn level so we can detect the 0->1 edge; on that
        // edge we stamp _raceOnEdgeTicks and suppress the impact/grip
        // channels until SettleTicks have elapsed. Single-threaded: only the
        // receive thread's ParsePacket touches these.
        private bool _prevRaceOn;
        private long _raceOnEdgeTicks;
        private static readonly long SettleTicks =
            System.Diagnostics.Stopwatch.Frequency * 400 / 1000;   // 400 ms

        // Airborne edge tracking (see the airborne detector in ParsePacket) so
        // we log the transition once instead of every frame while in the air.
        private bool _prevAirborne;

        // Load-channel-live latch for the load-weighted slip (issue #30). Armed
        // the first time any wheel shows real suspension compression, so a
        // genuinely airborne frame (all four drooped, summed load ~0) reads as
        // "every wheel unloaded => silent" rather than "channel dead => fall
        // back to the loud unweighted value". Normalized suspension travel is a
        // core Sled field so this arms on the first grounded frame in practice;
        // the latch only guards a hypothetical title that leaves it at 0.
        private bool _seenSuspLoad;
        // A grounded wheel sits well above full droop; this proves the channel
        // is live (above DroopThreshold, below any loaded value).
        private const float SuspLoadArmThreshold = 0.05f;
        // Summed normalized suspension travel below which every wheel is drooped
        // (airborne). Four wheels each under DroopThreshold (0.02) sum under 0.08.
        private const double MinTotalSuspLoad = 0.08;

        // Suspension-droop threshold for the airborne detector. Forza's
        // NormalizedSuspensionTravel runs 0.0 (fully extended / wheel hanging)
        // to 1.0 (fully compressed). A grounded car carries its weight on the
        // springs so every wheel sits well off zero; only an unloaded wheel
        // collapses toward full droop. 0.02 is a tight floor that a loaded
        // tyre never reaches.
        private const float DroopThreshold = 0.02f;

        /// <summary>Number of packets received since Start(). Useful for the
        /// settings panel to confirm the user has the port wired up correctly.</summary>
        public long PacketsReceived => _packetsReceived;
        private long _packetsReceived;
        private long _lastParseDiagTicks;   // throttles the [Forza-parse] raw dump

        /// <summary>Number of packets successfully forwarded to the secondary
        /// destination since Start(). Stays at 0 when no forward target was
        /// configured. Lets the settings panel show "forwarding to SimHub:
        /// N packets relayed" so the user can confirm coexistence works.</summary>
        public long PacketsForwarded => _packetsForwarded;
        private long _packetsForwarded;

        /// <summary>Masks short raceOn=0 gaps (replay loops, rewinds) on the
        /// FORWARDED copy so SimHub never sees the disconnect. Our own parse
        /// always reads the original packet. Configure via Enabled /
        /// MaxGapSeconds; safe to leave at defaults.</summary>
        public ForzaGapBridge GapBridge { get; } = new ForzaGapBridge();

        public ForzaUdpTelemetrySource(int port, IPAddress bindAddress = null, IPEndPoint forwardTo = null)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _port        = port;
            _bindAddress = bindAddress ?? IPAddress.Any;
            _forwardTo   = forwardTo;
        }

        public override void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

            try
            {
                // ExclusiveAddressUse=false so a SimHub install also bound to
                // the same port doesn't fail us at startup. With both bound,
                // exactly one of the two receives each datagram (Windows
                // doesn't multicast UDP to multiple SO_REUSEADDR listeners),
                // but at least we don't crash and the user can fix the
                // collision by changing the port.
                _udp = new UdpClient();
                _udp.ExclusiveAddressUse = false;
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(_bindAddress, _port));
                _udp.Client.ReceiveTimeout = 1000;  // ms; gives the loop a chance to notice _stopping

                // Separate send-only socket for the forwarder so a transient
                // send error (DestinationUnreachable when the target listener
                // isn't running) can't disrupt the receive socket. SOCK_DGRAM
                // doesn't actually surface unreachable errors on Windows
                // unless connected, so this is mostly defensive.
                if (_forwardTo != null)
                {
                    _forwardSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                }
            }
            catch
            {
                CleanupSocket();
                Interlocked.Exchange(ref _running, 0);
                throw;
            }

            _stopping = false;
            _thread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name         = "ForzaUdpTelemetrySource",
            };
            _thread.Start();
            if (_forwardTo != null)
                Log($"Forza UDP source listening on {_bindAddress}:{_port}, forwarding to {_forwardTo}.");
            else
                Log($"Forza UDP source listening on {_bindAddress}:{_port}.");
        }

        public override void Stop()
        {
            _stopping = true;
            // Closing the socket unblocks ReceiveFrom with a SocketException
            // we swallow inside the loop.
            try { _udp?.Close(); } catch { }
            try { _thread?.Join(2000); } catch { }
            _thread = null;
            CleanupSocket();
            Interlocked.Exchange(ref _running, 0);
        }

        private void CleanupSocket()
        {
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            try { _forwardSocket?.Close(); } catch { }
            try { _forwardSocket?.Dispose(); } catch { }
            _forwardSocket = null;
        }

        private void ReceiveLoop()
        {
            var remoteEp = new IPEndPoint(IPAddress.Any, 0);
            byte[] scratch = new byte[1024];   // FH packets are 324; 1024 is generous.
            byte[] fwdBuf  = new byte[1024];   // forward copy: the gap bridge may patch it

            while (!_stopping)
            {
                int len;
                try
                {
                    EndPoint ep = remoteEp;
                    len = _udp.Client.ReceiveFrom(scratch, 0, scratch.Length, SocketFlags.None, ref ep);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (Exception)
                {
                    if (_stopping) return;
                    // Unexpected, back off briefly so we don't busy-loop on
                    // a permanent error (e.g. socket torn down externally).
                    Thread.Sleep(50);
                    continue;
                }

                if (len < MinSledLength) continue;

                // Forward FIRST so a parse error in our pipeline can't strand
                // SimHub without telemetry. Forward is fire-and-forget UDP
                // we don't care if the target listener exists. The forwarded
                // bytes go through the gap bridge on a COPY: short raceOn=0
                // gaps get masked for SimHub while our own parse (below)
                // always sees the game's real session state.
                if (_forwardSocket != null && _forwardTo != null)
                {
                    try
                    {
                        Buffer.BlockCopy(scratch, 0, fwdBuf, 0, len);
                        GapBridge.Process(fwdBuf, len,
                            System.Diagnostics.Stopwatch.GetTimestamp(),
                            System.Diagnostics.Stopwatch.Frequency);
                        _forwardSocket.SendTo(fwdBuf, 0, len, SocketFlags.None, _forwardTo);
                        Interlocked.Increment(ref _packetsForwarded);
                    }
                    catch (Exception)
                    {
                        // Swallow: target may be down, network may be flapping;
                        // either way, we don't want forward errors to spam the
                        // log on every packet. The "0 forwarded" counter in
                        // the UI is the user-facing signal.
                    }
                }

                try
                {
                    Interlocked.Increment(ref _packetsReceived);
                    var frame = ParsePacket(scratch, len);
                    if (ShouldEmit(frame, System.Diagnostics.Stopwatch.GetTimestamp()))
                        EmitFrame(frame);
                }
                catch (Exception ex)
                {
                    Log($"Forza parse error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // internal (not private) so the unit tests can feed synthetic packets
        // straight in without standing up a UDP socket. No behavior change.
        internal TelemetryFrame ParsePacket(byte[] buf, int len)
        {
            int isRaceOn   = ReadInt32(buf,  OFF_IS_RACE_ON);
            float maxRpm   = ReadFloat(buf,  OFF_ENGINE_MAX_RPM);
            float curRpm   = ReadFloat(buf,  OFF_CURRENT_RPM);

            // DIAGNOSTIC: throttled raw dump to crack the FH6 packet layout.
            // If curRpm/maxRpm look sane while driving, the sled offsets are
            // right and raceOn@0 is a real game state (FH6 may zero it in
            // free-roam); if they're garbage, the whole layout shifted.
            {
                long pdnow = System.Diagnostics.Stopwatch.GetTimestamp();
                if (pdnow - _lastParseDiagTicks > System.Diagnostics.Stopwatch.Frequency * 2)
                {
                    _lastParseDiagTicks = pdnow;
                    float spd256 = len >= 260 ? ReadFloat(buf, 256) : -1f;
                    float spd244 = len >= 248 ? ReadFloat(buf, 244) : -1f;
                    var sb = new System.Text.StringBuilder();
                    for (int k = 0; k < Math.Min(len, 24); k++) sb.Append(buf[k].ToString("X2"));
                    Log($"[Forza-parse] len={len} raceOn@0={isRaceOn} maxRpm@8={maxRpm:F0} curRpm@16={curRpm:F0} spd@256={spd256:F1} spd@244={spd244:F1} head24={sb}");
                }
            }

            float accelY   = ReadFloat(buf,  OFF_ACCEL_Y);
            float accelX   = ReadFloat(buf,  OFF_ACCEL_X);
            float accelZ   = ReadFloat(buf,  OFF_ACCEL_Z);
            float yawRad   = ReadFloat(buf,  OFF_ANG_VEL_Y);

            float susFL = ReadFloat(buf, OFF_NORM_SUSP_FL + 0);
            float susFR = ReadFloat(buf, OFF_NORM_SUSP_FL + 4);
            float susRL = ReadFloat(buf, OFF_NORM_SUSP_FL + 8);
            float susRR = ReadFloat(buf, OFF_NORM_SUSP_FL + 12);

            // Arm the load-channel-live latch (issue #30) the moment any wheel
            // shows real compression. Done here, ahead of the settle / keepalive
            // gates, so a car placed and then jumped still proves the channel
            // live before the airborne frame needs the "silent, not loud"
            // fallback.
            if (!_seenSuspLoad &&
                (susFL > SuspLoadArmThreshold || susFR > SuspLoadArmThreshold ||
                 susRL > SuspLoadArmThreshold || susRR > SuspLoadArmThreshold))
                _seenSuspLoad = true;

            int rsFL = ReadInt32(buf, OFF_ON_RUMBLE_STRIP_FL + 0);
            int rsFR = ReadInt32(buf, OFF_ON_RUMBLE_STRIP_FL + 4);
            int rsRL = ReadInt32(buf, OFF_ON_RUMBLE_STRIP_FL + 8);
            int rsRR = ReadInt32(buf, OFF_ON_RUMBLE_STRIP_FL + 12);

            float surFL = ReadFloat(buf, OFF_SURFACE_RUMBLE_FL + 0);
            float surFR = ReadFloat(buf, OFF_SURFACE_RUMBLE_FL + 4);
            float surRL = ReadFloat(buf, OFF_SURFACE_RUMBLE_FL + 8);
            float surRR = ReadFloat(buf, OFF_SURFACE_RUMBLE_FL + 12);

            float cmbFL = ReadFloat(buf, OFF_TIRE_COMBINED_FL + 0);
            float cmbFR = ReadFloat(buf, OFF_TIRE_COMBINED_FL + 4);
            float cmbRL = ReadFloat(buf, OFF_TIRE_COMBINED_FL + 8);
            float cmbRR = ReadFloat(buf, OFF_TIRE_COMBINED_FL + 12);

            // Per-tire channels for the CTM quads. All live inside the Sled
            // (offsets < 232), so they're present in EVERY Forza packet,
            // Sled-only included. Slip angle is signed radians; suspension
            // travel is meters (a vertical-load proxy); slip ratio is signed
            // (+ = wheelspin, - = lockup); wheel rotation is rad/s.
            float saFL = ReadFloat(buf, OFF_TIRE_SLIP_ANGLE_FL + 0);
            float saFR = ReadFloat(buf, OFF_TIRE_SLIP_ANGLE_FL + 4);
            float saRL = ReadFloat(buf, OFF_TIRE_SLIP_ANGLE_FL + 8);
            float saRR = ReadFloat(buf, OFF_TIRE_SLIP_ANGLE_FL + 12);

            float stmFL = ReadFloat(buf, OFF_SUSP_TRAVEL_M_FL + 0);
            float stmFR = ReadFloat(buf, OFF_SUSP_TRAVEL_M_FL + 4);
            float stmRL = ReadFloat(buf, OFF_SUSP_TRAVEL_M_FL + 8);
            float stmRR = ReadFloat(buf, OFF_SUSP_TRAVEL_M_FL + 12);

            float srFL = ReadFloat(buf, OFF_TIRE_SLIP_RATIO_FL + 0);
            float srFR = ReadFloat(buf, OFF_TIRE_SLIP_RATIO_FL + 4);
            float srRL = ReadFloat(buf, OFF_TIRE_SLIP_RATIO_FL + 8);
            float srRR = ReadFloat(buf, OFF_TIRE_SLIP_RATIO_FL + 12);

            float wrFL = ReadFloat(buf, OFF_WHEEL_ROT_FL + 0);
            float wrFR = ReadFloat(buf, OFF_WHEEL_ROT_FL + 4);
            float wrRL = ReadFloat(buf, OFF_WHEEL_ROT_FL + 8);
            float wrRR = ReadFloat(buf, OFF_WHEEL_ROT_FL + 12);

            double frontSlipAngle   = 0.5 * (saFL + saFR);   // signed, radians
            double frontSuspTravelM = 0.5 * (stmFL + stmFR);

            int numCyl  = ReadInt32(buf, OFF_NUM_CYLINDERS);
            int carOrd  = ReadInt32(buf, OFF_CAR_ORDINAL);
            CurrentCarOrdinal = carOrd > 0 ? carOrd : (int?)null;

            // Dash fields. The dash block's base moves per title (232 on
            // Motorsport, 244 on Horizon); ResolveDashBase picks the base by
            // plausibility check instead of trusting the length→layout table
            // blindly. Sled-only packets (232) carry no dash at all — speed /
            // throttle / gear stay at their idle defaults.
            float speedMs   = 0;
            byte  accelByte = 0;
            byte  brakeByte = 0;
            byte  gearByte  = 1;   // 1 = N
            double? steerNorm = null;
            int dashBase = ResolveDashBase(buf, len);
            if (dashBase > 0)
            {
                speedMs   = ReadFloat(buf, dashBase + DASH_SPEED);
                accelByte = buf[dashBase + DASH_ACCEL];
                brakeByte = buf[dashBase + DASH_BRAKE];
                gearByte  = buf[dashBase + DASH_GEAR];
                // Steer is a signed byte (read the byte, reinterpret as sbyte)
                // normalized to ~[-1, 1]. Sign matches Forza's convention
                // (+ = right); the spring's downstream invert was tuned on
                // AC, so the Forza direction is a hardware-verify item.
                steerNorm = (double)unchecked((sbyte)buf[dashBase + DASH_STEER]) / 127.0;
            }

            // Forza's accel fields are already m/s² (no g→m/s² conversion
            // needed here, unlike AC's wheelSlip path which gets cars in g).
            const double RadToDeg = 180.0 / Math.PI;

            // FH6 interleaves all-zero "keepalive" packets between real frames:
            // IsRaceOn=0 with the ENTIRE payload zeroed (EngineMaxRpm@8 == 0).
            // Earlier builds treated ANY IsRaceOn=0 as "silence everything", so
            // those empties wiped the live telemetry several times a second
            // while driving and the effects never sustained (a real frame one
            // tick, a zeroed one the next). So gate silence on a genuinely-empty
            // packet (maxRpm==0),
            // NOT on IsRaceOn. Any packet carrying real physics flows through
            // the full data path below regardless of the flag. (For the
            // record: IsRaceOn is 1 during free-roam driving on FH4/FH5/FH6,
            // "is the player driving" despite the name; replays and cutscenes
            // report 0 with live physics, and the flag-based force gating
            // keeps the wheel quiet there on purpose.) A true pause /
            // menu either streams these empties or a frozen stationary frame
            // whose surface/slip channels are ~0, so nothing buzzes when you're
            // not driving. LONE empties between fresh real frames never reach
            // consumers at all (ShouldEmit swallows them in the receive loop);
            // only persistent emptiness flows through as this silencing frame.
            //
            // This check sits ABOVE the raceOn / settle / airborne stamping
            // below on purpose: an all-zero payload's IsRaceOn byte is zeroed
            // payload, not state. Stamping it flapped LastIsRaceOn (which
            // notched the FFB provider's pause-release several times a second
            // mid-race) and recorded a raceOn 0->1 edge on every
            // keepalive->real transition, re-opening the 400 ms settle window
            // so the grip / impact channels never escaped suppression during
            // FH6 races. Persistent emptiness DOES drop the flag, in
            // ShouldEmit, once the silence window expires: one zeroed payload
            // is noise, a quarter second of nothing but them is a pause.
            bool emptyKeepalive = maxRpm <= 0f;
            if (emptyKeepalive)
            {
                return new TelemetryFrame
                {
                    Rpms       = 0,
                    MaxRpm     = maxRpm,
                    Throttle01 = 0,
                    SpeedKmh   = 0,
                    Gear       = "N",
                    NumCylinders = numCyl > 0 ? numCyl : (int?)null,
                    // Steering intentionally not reported: an empty packet zeros
                    // it anyway and the stationary spring falls back to the
                    // wheel's physical position, so a null avoids feeding the
                    // spring a stray 0.
                };
            }

            bool raceOn = isRaceOn != 0;
            LastIsRaceOn = raceOn;

            // Detect the 0->1 (and first-packet) edge and open the settle
            // window. While settling, the car is being placed on the track;
            // its heave / slip / surface channels carry a placement transient
            // we must not feed to the effects.
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (raceOn && !_prevRaceOn) _raceOnEdgeTicks = nowTicks;
            _prevRaceOn = raceOn;
            bool settling = raceOn
                         && _raceOnEdgeTicks != 0
                         && (nowTicks - _raceOnEdgeTicks) < SettleTicks;

            // Airborne detection. When the car leaves the ground (FH jumps,
            // crest pops) every wheel unloads and its suspension travel
            // collapses toward full droop. The free-spinning, unloaded tyres
            // make TireCombinedSlip and SurfaceRumble spike, which the traction
            // loss and road effects read as a phantom slide / rumble (reported
            // in FH6 on jumps). We surface it as TelemetryFrame.Airborne and
            // let AirborneEffect duck the configured voices; the detection
            // lives here (where the suspension data is), the policy lives in
            // the effect (where the user can tune it).
            bool airborne = raceOn
                         && susFL < DroopThreshold && susFR < DroopThreshold
                         && susRL < DroopThreshold && susRR < DroopThreshold;
            if (airborne != _prevAirborne)
            {
                Log(airborne
                    ? $"Forza: airborne (susp droop FL={susFL:F3} FR={susFR:F3} RL={susRL:F3} RR={susRR:F3})."
                    : "Forza: grounded.");
                _prevAirborne = airborne;
            }

            double surfaceMax = Math.Max(
                Math.Max(Math.Abs(surFL), Math.Abs(surFR)),
                Math.Max(Math.Abs(surRL), Math.Abs(surRR)));

            double combinedMax = Math.Max(
                Math.Max(Math.Abs(cmbFL), Math.Abs(cmbFR)),
                Math.Max(Math.Abs(cmbRL), Math.Abs(cmbRR)));

            // Load-weighted whole-car slip (issue #30). Forza's normalized
            // suspension travel (0 = wheel hanging, 1 = fully compressed) is the
            // per-wheel load proxy, so an airborne or kerb-lifted wheel's slip
            // spike drops out of the reading instead of buzzing as a phantom
            // slide. Fallback when every wheel is drooped: silent if the channel
            // has proven live (airborne), else the legacy max-abs.
            double slipFallback = _seenSuspLoad ? 0.0 : combinedMax;
            double weightedSlip = LoadWeighting.Weighted(
                cmbFL, cmbFR, cmbRL, cmbRR,
                susFL, susFR, susRL, susRR,
                MinTotalSuspLoad, slipFallback);

            // Load-weighted surface rumble (issue #35). Same load weighting as
            // the slip channel above, applied to Forza's per-tyre SurfaceRumble[]
            // instead of the load-blind max: weight each wheel's rumble by its
            // suspension compression so a free-spinning airborne wheel or one
            // wheel brushing gravel no longer reads as the whole car on that
            // surface. (LoadWeighting is the channel-agnostic sum(|v|*load)/
            // sum(load); the loads cancel to a ratio, so it serves rumble as
            // well as slip.) Fallback when every wheel is drooped mirrors the
            // slip channel: silent once the suspension channel has proven live,
            // else the legacy max.
            double surfaceFallback = _seenSuspLoad ? 0.0 : surfaceMax;
            double weightedSurface = LoadWeighting.Weighted(
                surFL, surFR, surRL, surRR,
                susFL, susFR, susRL, susRR,
                MinTotalSuspLoad, surfaceFallback);

            bool anyRumbleStrip = rsFL != 0 || rsFR != 0 || rsRL != 0 || rsRR != 0;

            // During the spawn / un-pause settle window, suppress the
            // jolt-prone channels (impact, grip, surface). Engine, throttle,
            // speed and gear pass through so the engine note and
            // auto-detection resume immediately; the surface/impact effects
            // ramp in cleanly once the car has settled.
            return new TelemetryFrame
            {
                Rpms       = curRpm,
                MaxRpm     = maxRpm,
                Throttle01 = accelByte / 255.0,
                SpeedKmh   = speedMs * 3.6,

                AccelerationHeave = settling ? 0.0 : accelY, // m/s², up
                AccelerationSway  = settling ? 0.0 : accelX, // m/s², right
                AccelerationSurge = settling ? 0.0 : accelZ, // m/s², forward
                YawRateDegPerSec  = settling ? 0.0 : yawRad * RadToDeg,

                Gear       = GearString(gearByte),
                WheelSlip  = settling ? 0.0 : weightedSlip,
                // SAT inputs, suppressed during the settle window like the other
                // grip channels so the spawn transient can't drive a future SAT
                // model. Front-pair averages computed above.
                FrontSlipAngleRad     = settling ? 0.0 : frontSlipAngle,
                FrontSuspTravelMeters = settling ? 0.0 : frontSuspTravelM,
                Airborne   = airborne,

                // Wheel position for the stationary-spring floor. Flows even
                // during the settle window (it's position, not an impact
                // channel). Null on Sled-only packets that lack the dash.
                SteeringAngle = steerNorm,

                SurfaceRumble = settling ? 0.0 : weightedSurface,
                OnRumbleStrip = !settling && anyRumbleStrip,
                NumCylinders  = numCyl > 0 ? numCyl : (int?)null,

                // Per-tire quads for the CTM. Suppressed to zero during the
                // settle window like the scalar grip channels (data present,
                // placement transient must not reach the effects). Wheel
                // rotation flows through the settle window — it's kinematics,
                // not an impact channel, and the rear rotation-pulse texture
                // wants it continuous.
                HasTireQuads    = true,
                TireSlipRatio   = settling ? default : TireQuad.Of(srFL, srFR, srRL, srRR),
                TireSlipAngleRad= settling ? default : TireQuad.Of(saFL, saFR, saRL, saRR),
                TireCombinedSlip= settling ? default : TireQuad.Of(cmbFL, cmbFR, cmbRL, cmbRR),
                SuspTravelM     = settling ? default : TireQuad.Of(stmFL, stmFR, stmRL, stmRR),
                SurfaceRumbleQ  = settling ? default : TireQuad.Of(surFL, surFR, surRL, surRR),
                WheelRotRadS    = TireQuad.Of(wrFL, wrFR, wrRL, wrRR),

                // AbsActive is left default: Forza's Data Out doesn't expose
                // ABS pump activity; SimHub's reader can't either. Effect
                // stays silent for ABS, but everything else is full-fat.
            };
        }

        // Dash-base resolution cache. Keyed by packet length; a session
        // streams a single length so this is effectively resolve-once. Only
        // committed from a live (non-keepalive) packet, because an all-zero
        // payload passes plausibility at ANY offset and would lock in
        // whatever base we tried first.
        private int _dashBaseCachedLen = -1;
        private int _dashBaseCached    = -1;

        /// <summary>Pick the dash block's base offset for this packet length
        /// by plausibility check rather than trusting the length→layout table
        /// blindly. Returns -1 when the packet has no usable dash (Sled-only,
        /// or nothing passed the checks — logged loudly, sled channels still
        /// flow). internal for unit tests.</summary>
        internal int ResolveDashBase(byte[] buf, int len)
        {
            if (len == _dashBaseCachedLen) return _dashBaseCached;

            int primary, alternate;
            if (len == HorizonDashLength)
            {
                primary = DashBaseHorizon;  alternate = DashBaseMotorsport;
            }
            else if (len >= Fm7DashLength)
            {
                // FM7 (311), FM2023 (331), and any future Motorsport-family
                // length: dash directly after the sled.
                primary = DashBaseMotorsport;  alternate = DashBaseHorizon;
            }
            else
            {
                _dashBaseCachedLen = len;
                _dashBaseCached    = -1;
                return -1;   // Sled-only: no dash to find.
            }

            int chosen;
            if (DashPlausible(buf, len, primary))
            {
                chosen = primary;
            }
            else if (DashPlausible(buf, len, alternate))
            {
                chosen = alternate;
                Log($"[Forza-dash] len={len}: documented dash base {primary} failed plausibility, using {alternate}. " +
                    "Layout may have shifted — verify against current Data Out docs.");
            }
            else
            {
                chosen = -1;
                Log($"[Forza-dash] len={len}: NO dash base passed plausibility (tried {primary}, {alternate}). " +
                    "Speed/throttle/gear/steer unavailable; sled channels still flow.");
            }

            bool liveFrame = ReadFloat(buf, OFF_ENGINE_MAX_RPM) > 0f;
            if (liveFrame)
            {
                _dashBaseCachedLen = len;
                _dashBaseCached    = chosen;
                if (chosen > 0)
                    Log($"[Forza-dash] len={len}: dash base {chosen} " +
                        $"(speed {ReadFloat(buf, chosen + DASH_SPEED):F1} m/s, gear byte {buf[chosen + DASH_GEAR]}).");
            }
            return chosen;
        }

        private static bool DashPlausible(byte[] buf, int len, int dashBase)
        {
            if (dashBase + DashSpanNeeded > len) return false;
            float v = ReadFloat(buf, dashBase + DASH_SPEED);
            // >150 m/s (540 km/h) is not a car speed; NaN/Inf is not a float
            // that was ever a speed.
            if (float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 150f) return false;
            byte gear = buf[dashBase + DASH_GEAR];
            return gear <= 11;
        }

        // Forza convention: 0=R, 1=N, 2=1st, 3=2nd, ... Matches AC's gear
        // string convention so GearShiftEffect compares unchanged.
        private static string GearString(int gear)
        {
            if (gear == 0) return "R";
            if (gear == 1) return "N";
            return (gear - 1).ToString();
        }

        /// <summary>Cheap shape check used by UdpPortScanner: packet length
        /// matches one of the known Forza Data Out sizes. Forza ships four
        /// sizes: Sled-only (232), FM7 Dash (311), Horizon Dash (FH4/FH5/FH6,
        /// 324), and FM2023 Dash (331 = FM7 dash + TireWear[4] + TrackOrdinal).
        /// The length match is a strong enough signal for discovery; random
        /// UDP traffic won't be exactly one of these sizes by chance.</summary>
        public static bool IsValidPacketCandidate(byte[] buf, int len)
        {
            if (buf == null) return false;
            return len == 232 || len == Fm7DashLength || len == HorizonDashLength || len == Fm8DashLength;
        }

        /// <summary>Default candidate ports the discovery flow tries when
        /// the user's configured port shows zero packets after startup.
        /// Forza default 5300 plus the SimHub Forza default (4123) and a
        /// few common alternates.</summary>
        public static readonly int[] DiscoveryCandidatePorts =
            { 5300, 5301, 5302, 4123, 9999 };

        // Forza Data Out is little-endian. Windows hosts are little-endian
        // too so BitConverter is direct; we don't bother with byte-swap
        // fallbacks for big-endian platforms (Forza doesn't ship there).
        private static int ReadInt32(byte[] b, int off)
        {
            return b[off] | (b[off+1] << 8) | (b[off+2] << 16) | (b[off+3] << 24);
        }

        private static float ReadFloat(byte[] b, int off)
        {
            // Boundary sanitizer (2026-07-05 review finding): a single
            // corrupt UDP frame carrying NaN/Infinity would otherwise poison
            // every downstream exponential moving average PERMANENTLY
            // (ema += (NaN - ema) * a never recovers), silencing Mode B — the
            // only FM8 force source — for the rest of the session. Non-finite
            // floats read as 0, the same neutral value the settle window uses.
            float v = BitConverter.ToSingle(b, off);
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }

        private void Log(string msg)
        {
            var l = Logger;
            if (l != null) try { l(msg); } catch { }
        }
    }
}
