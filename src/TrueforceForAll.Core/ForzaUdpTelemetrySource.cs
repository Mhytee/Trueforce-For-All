// Forza UDP "Data Out" telemetry source. Listens on a configurable port for
// the binary packets Forza Horizon 5 / Forza Motorsport (2023) emit when the
// user enables UDP RACE TELEMETRY in Settings → HUD and Gameplay.
//
// Forza emits ~60 Hz, so the rate is the same as the SimHub fallback — the
// reason this source is "enhanced" is the per-tire data Forza exposes that
// SimHub's StatusDataBase doesn't surface: SurfaceRumble[4] (the same channel
// Turn 10's own Trueforce path consumes), WheelOnRumbleStrip[4] for kerb
// pulses, TireCombinedSlip[4] for direct traction-loss detection, and the
// per-car NumCylinders (lets EnginePulse auto-tune its firing frequency).
//
// Packet layout (little-endian, FM7-compatible "Sled" + Horizon-Dash for
// FH4/5; offsets verified against the Forza Motorsport Data Out docs):
//
//   Sled (0..231) — present in every Forza title:
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
//     228 NumCylinders       int32
//
//   Horizon insert (232..243) — 12 bytes Microsoft hasn't documented; skipped.
//
//   Dash (244..end on Horizon, 232..end on Motorsport — we only handle the
//   Horizon-shape here since our target is FH5/FH6):
//     256 Speed              float32  (m/s)
//     315 Accel              uint8    (throttle, 0..255)
//     316 Brake              uint8    (0..255)
//     319 Gear               uint8    (0=R, 1=N, 2..n=fwd)
//
// IsRaceOn handling: in FH4/FH5 this flag is 1 during freeroam too — it's
// just "is the player driving" despite the name. We emit on every received
// packet regardless of the flag, but zero engine/slip/grip channels when
// IsRaceOn=0 so effects decay to silence during pause/menu instead of
// holding their last-known values.
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

        // Sled offsets (relative to packet start).
        private const int OFF_IS_RACE_ON         = 0;
        private const int OFF_ENGINE_MAX_RPM     = 8;
        private const int OFF_CURRENT_RPM        = 16;
        private const int OFF_ACCEL_X            = 20;
        private const int OFF_ACCEL_Y            = 24;
        private const int OFF_ANG_VEL_Y          = 48;
        private const int OFF_NORM_SUSP_FL       = 68;
        private const int OFF_TIRE_SLIP_RATIO_FL = 84;
        private const int OFF_ON_RUMBLE_STRIP_FL = 116;
        private const int OFF_SURFACE_RUMBLE_FL  = 148;
        private const int OFF_TIRE_COMBINED_FL   = 180;
        private const int OFF_NUM_CYLINDERS      = 228;
        // Horizon Dash offsets (Sled + 12 bytes of unknowns at 232..243).
        private const int OFF_SPEED_HORIZON      = 256;
        private const int OFF_ACCEL_PEDAL        = 315;
        private const int OFF_BRAKE_PEDAL        = 316;
        private const int OFF_GEAR_HORIZON       = 319;

        // Smallest Sled-only payload. Anything shorter we discard.
        private const int MinSledLength    = 232;
        // Full Horizon-Dash size used by FH4/FH5 (and likely FH6).
        private const int HorizonDashLength = 324;

        private readonly int _port;
        private readonly IPAddress _bindAddress;
        private readonly IPEndPoint _forwardTo;   // null = no forwarding
        private UdpClient _udp;
        private Socket _forwardSocket;            // separate socket so a forward send error can't kill the receive socket
        private Thread _thread;
        private volatile bool _stopping;
        private int _running;

        public Action<string> Logger { get; set; }

        /// <summary>Most recent IsRaceOn flag, exposed so the UI can show
        /// "active / paused" state independent of MeasuredHz.</summary>
        public bool LastIsRaceOn { get; private set; }

        /// <summary>Number of packets received since Start(). Useful for the
        /// settings panel to confirm the user has the port wired up correctly.</summary>
        public long PacketsReceived => _packetsReceived;
        private long _packetsReceived;

        /// <summary>Number of packets successfully forwarded to the secondary
        /// destination since Start(). Stays at 0 when no forward target was
        /// configured. Lets the settings panel show "forwarding to SimHub:
        /// N packets relayed" so the user can confirm coexistence works.</summary>
        public long PacketsForwarded => _packetsForwarded;
        private long _packetsForwarded;

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
                    // Unexpected — back off briefly so we don't busy-loop on
                    // a permanent error (e.g. socket torn down externally).
                    Thread.Sleep(50);
                    continue;
                }

                if (len < MinSledLength) continue;

                // Forward FIRST so a parse error in our pipeline can't strand
                // SimHub without telemetry. Forward is fire-and-forget UDP —
                // we don't care if the target listener exists.
                if (_forwardSocket != null && _forwardTo != null)
                {
                    try
                    {
                        _forwardSocket.SendTo(scratch, 0, len, SocketFlags.None, _forwardTo);
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
                    EmitFrame(ParsePacket(scratch, len));
                }
                catch (Exception ex)
                {
                    Log($"Forza parse error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private TelemetryFrame ParsePacket(byte[] buf, int len)
        {
            int isRaceOn   = ReadInt32(buf,  OFF_IS_RACE_ON);
            float maxRpm   = ReadFloat(buf,  OFF_ENGINE_MAX_RPM);
            float curRpm   = ReadFloat(buf,  OFF_CURRENT_RPM);
            float accelY   = ReadFloat(buf,  OFF_ACCEL_Y);
            float accelX   = ReadFloat(buf,  OFF_ACCEL_X);
            float yawRad   = ReadFloat(buf,  OFF_ANG_VEL_Y);

            float susFL = ReadFloat(buf, OFF_NORM_SUSP_FL + 0);
            float susFR = ReadFloat(buf, OFF_NORM_SUSP_FL + 4);
            float susRL = ReadFloat(buf, OFF_NORM_SUSP_FL + 8);
            float susRR = ReadFloat(buf, OFF_NORM_SUSP_FL + 12);

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

            int numCyl = ReadInt32(buf, OFF_NUM_CYLINDERS);

            // Horizon-Dash fields. Tolerate Motorsport-Sled-only packets by
            // checking length; we just leave speed/throttle/gear at zero in
            // that case (the Sled doesn't carry them). For FH5/FH6 (>=324)
            // these are populated.
            float speedMs   = 0;
            byte  accelByte = 0;
            byte  brakeByte = 0;
            byte  gearByte  = 1;   // 1 = N
            if (len >= HorizonDashLength)
            {
                speedMs   = ReadFloat(buf, OFF_SPEED_HORIZON);
                accelByte = buf[OFF_ACCEL_PEDAL];
                brakeByte = buf[OFF_BRAKE_PEDAL];
                gearByte  = buf[OFF_GEAR_HORIZON];
            }

            bool raceOn = isRaceOn != 0;
            LastIsRaceOn = raceOn;

            // Forza's accel fields are already m/s² (no g→m/s² conversion
            // needed here, unlike AC's wheelSlip path which gets cars in g).
            const double RadToDeg = 180.0 / Math.PI;

            // When paused / in menu (IsRaceOn=0), Forza often keeps the last
            // captured frame's payload frozen. Returning those values would
            // hold engine pulse + slip effects at their last-known intensity
            // until the user resumed. Zero the volatile channels instead so
            // effects decay cleanly. Static-ish fields (NumCylinders,
            // EngineMaxRpm) remain so we don't lose the auto-detect on pause.
            if (!raceOn)
            {
                return new TelemetryFrame
                {
                    Rpms       = 0,
                    MaxRpm     = maxRpm,
                    Throttle01 = 0,
                    SpeedKmh   = 0,
                    Gear       = "N",
                    NumCylinders = numCyl > 0 ? numCyl : (int?)null,
                    // Surface signals zeroed; effects silence.
                };
            }

            double surfaceMax = Math.Max(
                Math.Max(Math.Abs(surFL), Math.Abs(surFR)),
                Math.Max(Math.Abs(surRL), Math.Abs(surRR)));

            double combinedMax = Math.Max(
                Math.Max(Math.Abs(cmbFL), Math.Abs(cmbFR)),
                Math.Max(Math.Abs(cmbRL), Math.Abs(cmbRR)));

            bool anyRumbleStrip = rsFL != 0 || rsFR != 0 || rsRL != 0 || rsRR != 0;

            return new TelemetryFrame
            {
                Rpms       = curRpm,
                MaxRpm     = maxRpm,
                Throttle01 = accelByte / 255.0,
                SpeedKmh   = speedMs * 3.6,

                AccelerationHeave = accelY,                  // m/s², up
                AccelerationSway  = accelX,                  // m/s², right
                YawRateDegPerSec  = yawRad * RadToDeg,

                Gear       = GearString(gearByte),
                WheelSlip  = combinedMax,

                SurfaceRumble = surfaceMax,
                OnRumbleStrip = anyRumbleStrip,
                NumCylinders  = numCyl > 0 ? numCyl : (int?)null,

                // AbsActive is left default: Forza's Data Out doesn't expose
                // ABS pump activity; SimHub's reader can't either. Effect
                // stays silent for ABS, but everything else is full-fat.
            };
        }

        // Forza convention: 0=R, 1=N, 2=1st, 3=2nd, ... Matches AC's gear
        // string convention so GearShiftEffect compares unchanged.
        private static string GearString(int gear)
        {
            if (gear == 0) return "R";
            if (gear == 1) return "N";
            return (gear - 1).ToString();
        }

        // Forza Data Out is little-endian. Windows hosts are little-endian
        // too so BitConverter is direct; we don't bother with byte-swap
        // fallbacks for big-endian platforms (Forza doesn't ship there).
        private static int ReadInt32(byte[] b, int off)
        {
            return b[off] | (b[off+1] << 8) | (b[off+2] << 16) | (b[off+3] << 24);
        }

        private static float ReadFloat(byte[] b, int off)
        {
            return BitConverter.ToSingle(b, off);
        }

        private void Log(string msg)
        {
            var l = Logger;
            if (l != null) try { l(msg); } catch { }
        }
    }
}
