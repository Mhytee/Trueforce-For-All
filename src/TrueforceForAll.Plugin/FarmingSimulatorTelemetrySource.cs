using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    /// <summary>Enhanced telemetry source for Farming Simulator, fed by the
    /// TF4ALL Enhanced Telemetry game mod over a named pipe this side hosts
    /// (\\.\pipe\TF4ALLTelemetry, newline-delimited JSON at up to ~100 Hz).
    ///
    /// Same contract and station as the other enhanced sources
    /// (AcSharedMemoryTelemetrySource, ForzaUdpTelemetrySource): it owns the
    /// WHOLE TelemetryFrame while alive, and SimHub is the fallback when the
    /// mod is not installed (EvaluateFsTelemetryFallback mirrors the Forza
    /// demote/upgrade). Lives in the Plugin, not Core, only because the JSON
    /// parsing rides SimHub's shipped Newtonsoft, the same reason
    /// SimHubTelemetrySource lives here; the contract it implements is
    /// Core's.</summary>
    public sealed class FarmingSimulatorTelemetrySource : TelemetrySourceBase
    {
        public override string Name => "FarmingSimulator (TF4ALL mod)";
        public override bool IsEnhanced => true;
        public override bool IsRunning => _threads[0] != null && !_stop;

        public Action<string> Logger { get; set; }

        // Two accept threads, four allowed pipe instances: with a spare
        // listener always armed, the mod's periodic reconnect (its only
        // defense against a SimHub restart leaving it a dead handle, which
        // GIANTS' Lua cannot detect) lands on a fresh instance instantly
        // instead of racing a single instance's teardown. SimHub's own GSI
        // server uses the same shape (five listener threads in its dump).
        private const int ServerThreads = 2;
        private const int MaxPipeInstances = 4;
        private readonly Thread[] _threads = new Thread[ServerThreads];
        private volatile bool _stop;
        private readonly NamedPipeServerStream[] _servers = new NamedPipeServerStream[ServerThreads];
        private readonly object _parseLock = new object();
        private volatile bool _inVehicle;
        // Vehicle identity from the mod (>= 0.2.5). Latched per session.
        private volatile string _stableCarId;
        private volatile string _carDisplayName;
        private string _lastCarCfg;   // parse-thread only (under _parseLock)

        /// <summary>Stable per-vehicle-type car id derived from the game's
        /// configFileName, or null until the mod reports one. Identical
        /// across sessions, saves and machines (mods included) — unlike
        /// SimHub's FS placeholder id, which is a per-session timestamp.</summary>
        public string StableCarId => _stableCarId;

        /// <summary>The vehicle's official in-game display name
        /// (getFullName, e.g. "Fendt Favorit 515"), or null.</summary>
        public string CarDisplayName => _carDisplayName;

        // Tri-state: null until the mod (>= 0.2.6) has reported the field
        // this session, so an older mod cleanly falls back to ungated
        // engine-load behavior instead of silently killing the drag layer.
        private volatile object _implementActive;   // boxed bool or null
        // Same tri-state shape for hydraulic motion (mod >= 0.2.8).
        private volatile object _implementMoving;   // boxed bool or null
        // Travel rate, fraction of full travel per second (mod >= 0.2.14).
        private volatile object _implementSpeed;    // boxed float or null
        // Discrete-event counter (mod >= 0.2.16).
        private volatile object _implementEvent;    // boxed int or null
        // Manual-only motion flag (mod >= 0.2.19).
        private volatile object _implementManual;   // boxed bool or null
        // Stage-end counter + the stopping part's speed (mod >= 0.2.22).
        private volatile object _implementPhase;      // boxed int or null
        private volatile object _implementPhaseSpeed; // boxed float or null

        private volatile string _reportedModVersion;
        /// <summary>The mod version the pipe is ACTUALLY streaming from
        /// (mod >= 0.2.11 self-reports), or null. The game only reads its
        /// mods folder at launch, so this can lag the zip the plugin just
        /// refreshed; the tab banner tells the user to restart when it
        /// does.</summary>
        public string ReportedModVersion => _reportedModVersion;
        // Surge derivation state (parse thread only, under _parseLock).
        private double _prevSpeedMps = double.NaN;
        private long _prevSpeedTicks;
        // Slip diagnostic cadence (parse thread only).
        private long _lastSlipDiagTicks;
        private bool _slipDiagDue;

        // Per-wheel slip magnitude 0..1, or null when the wheel can't say:
        // no ground contact, or neither a surface speed (mod 0.2.12 "ws")
        // nor a game-reported "slip" magnitude (FS22 lastSlip) is present.
        // The 1.4 m/s (~5 km/h) denominator floor makes stationary
        // wheelspin read full instead of dividing by zero.
        // Signed slip ratio in the travel direction (+ = wheelspin, - =
        // lockup), clamped to ±2. An airborne wheel reads neutral 0; a wheel
        // without a surface speed reads null (the FS22 "slip" fallback is
        // unsigned and cannot participate in signed quads).
        private static double? SignedRatio(Newtonsoft.Json.Linq.JObject w, double vMps, double dirSign)
        {
            if (w == null) return null;
            if (!(w.Value<bool?>("contact") ?? true)) return 0.0;
            var wsv = w.Value<double?>("ws");
            if (!wsv.HasValue || double.IsNaN(wsv.Value)) return null;
            double vSigned = vMps * dirSign;
            double r = (wsv.Value - vSigned) * dirSign / Math.Max(vMps, 1.4);
            return Math.Min(Math.Max(r, -2.0), 2.0);
        }

        // Utilization scale for the axle voices (grip limit ~35% ratio on
        // soil; slight >1 headroom for "past the limit").
        private static double Uti(double signedRatio)
            => Math.Min(Math.Abs(signedRatio) / 0.35, 1.2);

        private static double? WheelSlip01(Newtonsoft.Json.Linq.JObject w, double vMps, double dirSign)
        {
            if (!(w.Value<bool?>("contact") ?? true)) return null;
            var wsv = w.Value<double?>("ws");
            if (wsv.HasValue && !double.IsNaN(wsv.Value))
            {
                double vSigned = vMps * dirSign;
                double r = Math.Abs(wsv.Value - vSigned) / Math.Max(vMps, 1.4);
                return Math.Min(r, 1.0);
            }
            var sl = w.Value<double?>("slip");
            if (sl.HasValue && !double.IsNaN(sl.Value))
                return Math.Min(Math.Max(sl.Value, 0.0), 1.0);
            return null;
        }

        /// <summary>True while the vehicle or an attached implement is
        /// actually working (lowered / powered on); false while everything
        /// is raised; null when the mod doesn't report it (pre-0.2.6).</summary>
        public bool? ImplementActive => (bool?)_implementActive;

        private volatile float _attachedFill01;
        private volatile float _towedMassKg;

        // Vehicle propellant tank (mod >= 0.2.21). Level/capacity in the
        // tank's native unit (diesel litres, electric kWh, methane kg);
        // -1 = not reported this frame (older mod, on foot, no consumer).
        private volatile float _fuelLevel = -1f;
        private volatile float _fuelCapacity = -1f;
        // Burn rate for the time-left readout, units/hour, derived from the
        // TANK's own drain: one level sample every ~5 s, rate = level lost
        // between the oldest and newest sample over their real-time span
        // (~150 s sliding window, valid from ~30 s of data). The game's
        // spec_motorized.lastFuelUsage is deliberately NOT the source: its
        // FS25 unit convention is unverified and it tracks throttle closely
        // enough that even smoothed it seesawed an hours-scale readout by
        // hours (owner on-wheel report 2026-08-10). What the tank actually
        // loses per hour is unit-correct by construction, and a 5 s update
        // cadence is what makes the readout sit still. The game's value is
        // kept only for the periodic diag line. Ring is parse-thread only
        // (under _parseLock); consumers read the volatile rate.
        private volatile float _fuelDrainPerHour = -1f;
        private volatile float _fuelUseGameRaw = -1f;
        private readonly List<KeyValuePair<long, float>> _fuelRing
            = new List<KeyValuePair<long, float>>();
        private long _lastFuelDiagTicks;   // parse thread only
        private long _fuelIdleSinceTicks;  // parse thread only
        private volatile string _fuelType;

        /// <summary>Tank level in its native unit, or -1 when the mod does
        /// not report fuel (pre-0.2.21, on foot, or no consumer).</summary>
        public float FuelLevel => _fuelLevel;

        /// <summary>Tank capacity in the same unit, or -1.</summary>
        public float FuelCapacity => _fuelCapacity;

        /// <summary>Tank fraction as 0..100, or -1 when unknown.</summary>
        public float FuelPercent
        {
            get
            {
                float l = _fuelLevel, c = _fuelCapacity;
                if (l < 0f || c <= 0f) return -1f;
                return Math.Min(l / c * 100f, 100f);
            }
        }

        /// <summary>Display unit for the tank ("L", "kWh" or "kg").</summary>
        public string FuelUnit
        {
            get
            {
                string t = _fuelType;
                if (t != null && t.IndexOf("electric", StringComparison.OrdinalIgnoreCase) >= 0) return "kWh";
                if (t != null && t.IndexOf("methane", StringComparison.OrdinalIgnoreCase) >= 0) return "kg";
                return "L";
            }
        }

        /// <summary>Minutes of fuel left at the tank's observed drain rate,
        /// capped at 5999 (the dash renders 99h 59m as the ceiling), or -1
        /// while unknown: under ~30 s of level samples yet, or a near-zero
        /// drain (engine off), where any number would be a bogus multi-week
        /// figure.</summary>
        public float FuelMinutesLeft
        {
            get
            {
                float l = _fuelLevel, u = _fuelDrainPerHour;
                if (l < 0f || u < 0.05f) return -1f;
                return Math.Min(l / u * 60f, 5999f);
            }
        }

        /// <summary>Highest fill fraction (0..1) across attached units
        /// (mod >= 0.2.7): a loaded trailer reads high, empty gear reads 0.
        /// Excludes the tractor's own tanks by construction.</summary>
        public float AttachedFill01 => _attachedFill01;

        /// <summary>Summed live mass (kg, cargo included) of attached units
        /// (mod >= 0.2.7). Informational; no consumer yet.</summary>
        public float TowedMassKg => _towedMassKg;

        /// <summary>Normalize a GIANTS vehicle configFileName into a stable,
        /// machine-independent, filesystem-safe car id (the preset library
        /// uses car ids as folder names). Base game: "data/vehicles/x/y/y.xml"
        /// -> "x_y_y"; mods keep their mod-folder prefix so two mods' "truck"
        /// can't collide. Lowercased; non-alphanumerics collapse to '_';
        /// tail-capped at 64 chars (the tail is the most specific part).</summary>
        internal static string NormalizeCarId(string configFileName)
        {
            if (string.IsNullOrWhiteSpace(configFileName)) return null;
            string p = configFileName.Replace('\\', '/').ToLowerInvariant();
            foreach (var marker in new[] { "/mods/", "/pdlc/", "/dlc/", "data/vehicles/", "data/" })
            {
                int i = p.LastIndexOf(marker, StringComparison.Ordinal);
                if (i >= 0) { p = p.Substring(i + marker.Length); break; }
            }
            if (p.EndsWith(".xml", StringComparison.Ordinal))
                p = p.Substring(0, p.Length - 4);
            var sb = new System.Text.StringBuilder(p.Length);
            bool underscore = false;
            foreach (char c in p)
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); underscore = false; }
                else if (!underscore) { sb.Append('_'); underscore = true; }
            }
            string id = sb.ToString().Trim('_');
            if (id.Length > 64) id = id.Substring(id.Length - 64).Trim('_');
            return id.Length > 0 ? id : null;
        }

        private volatile float _motorLoad01 = -1f;
        // Stamped with every parsed in-vehicle load (Interlocked: written on
        // the pipe threads, read on the FFB pump, 64-bit on a 32-bit host).
        private long _motorLoadStampTicks;
        private bool _connectedLogged;
        private string _lastLoopError;

        /// <summary>Engine load 0..1 from the mod, or negative when the game
        /// did not report one OR the last report is stale (>400 ms: an abrupt
        /// pipe death, game closing, must not latch a working-depth load into
        /// the drag multiplier while the spring keeps rendering on fallback
        /// telemetry; 2026-08-08 review). Not a TelemetryFrame field yet; the
        /// implement-drag layer is its intended consumer.</summary>
        public float MotorLoad01
        {
            get
            {
                long stamp = System.Threading.Interlocked.Read(ref _motorLoadStampTicks);
                if (stamp == 0
                    || System.Diagnostics.Stopwatch.GetTimestamp() - stamp
                       > System.Diagnostics.Stopwatch.Frequency * 2 / 5)
                    return -1f;
                return _motorLoad01;
            }
        }

        // Suspension-motion state for the bump synthesis (reader thread
        // only): previous per-wheel positions + the previous frame's stamp.
        // Always on (owner call 2026-08-08): hard suspension transients pulse
        // OnRumbleStrip so the Kerb thump effect renders FS impacts; the
        // effect's own Enabled toggle is the user control. The SUSTAINED
        // SurfaceRumble channel is deliberately NOT synthesized: a tractor's
        // suspension jiggles continuously, and rendering that as constant
        // texture felt bad on the wheel (owner test); discrete hits only.
        private readonly double[] _prevWheelY = new double[8];
        private bool _prevWheelYValid;
        private long _prevFrameTicks;

        // In-vehicle is the game's own word for "the player is driving", so
        // this source is authoritative the way Forza's IsRaceOn is: on foot,
        // in menus, or with the mod silent there is no force to render.
        public override bool HasAuthoritativeSessionState => true;
        public override bool IsSessionActive => MeasuredHz > 0 && _inVehicle;

        public override void Start()
        {
            if (_threads[0] != null) return;
            _stop = false;
            for (int i = 0; i < ServerThreads; i++)
            {
                int slot = i;
                _threads[i] = new Thread(() => ServerLoop(slot))
                {
                    IsBackground = true,
                    Name = "TF4ALL-FsTelemetry-" + slot,
                };
                _threads[i].Start();
            }
        }

        public override void Stop()
        {
            _stop = true;
            for (int i = 0; i < ServerThreads; i++)
            {
                try { _servers[i]?.Dispose(); } catch { }   // unblocks WaitForConnection / ReadLine
            }
            for (int i = 0; i < ServerThreads; i++)
            {
                try { _threads[i]?.Join(1500); } catch { }
                _threads[i] = null;
            }
        }

        private void ServerLoop(int slot)
        {
            while (!_stop)
            {
                try
                {
                    // Explicit ACL, not the default: SimHub commonly runs
                    // ELEVATED (the plugin itself recommends it for USBPcap),
                    // and an elevated process's default pipe security denies
                    // write connections from the non-elevated game. The
                    // game's io.open then fails silently forever (hit live,
                    // 2026-08-07). Authenticated Users get ReadWrite, us
                    // FullControl, the same shape SimHub's own GSI pipe uses.
                    var sec = new PipeSecurity();
                    sec.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                        PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    var me = WindowsIdentity.GetCurrent().User;
                    if (me != null)
                        sec.AddAccessRule(new PipeAccessRule(me,
                            PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var server = new NamedPipeServerStream(
                        "TF4ALLTelemetry", PipeDirection.In, MaxPipeInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec))
                    {
                        _servers[slot] = server;
                        server.WaitForConnection();
                        if (_stop) return;
                        if (!_connectedLogged)
                        {
                            _connectedLogged = true;
                            Logger?.Invoke("Farming Simulator connected (TF4ALL Enhanced Telemetry game mod).");
                        }
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string line;
                            while (!_stop && (line = reader.ReadLine()) != null)
                            {
                                // Two accept threads can overlap briefly
                                // during the mod's reconnect handover; the
                                // lock keeps frame parsing serialized.
                                try { lock (_parseLock) ParseLine(line); }
                                catch { /* one bad line must not drop the connection */ }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Disposed mid-wait or the game closed are normal; a
                    // repeating create failure is not, and silence here once
                    // hid a dead pipe behind an invisible crash-loop. Log
                    // each distinct failure once.
                    if (!_stop && ex.Message != _lastLoopError)
                    {
                        _lastLoopError = ex.Message;
                        Logger?.Invoke("Farming Simulator pipe server error (will keep retrying): " + ex.Message);
                    }
                }
                _servers[slot] = null;
                if (!_stop) Thread.Sleep(250);
            }
        }

        // Drain-rate sampling (parse thread, under _parseLock): one
        // (ticks, level) sample every ~5 s, rate = level lost between the
        // oldest and newest sample over their real-time span, valid from
        // ~30 s of data, sliding over ~150 s. A level RISE restarts the
        // window (a refuel must not read as negative burn) but KEEPS the
        // learned rate: refueling changes the tank, not the burn.
        // Recomputing only when a sample lands is also what keeps the dash
        // readout still between updates.
        private void UpdateFuelDrain(double speedKmh)
        {
            float lvl = _fuelLevel;
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            var ring = _fuelRing;
            if (lvl < 0f)
            {
                if (ring.Count > 0) ring.Clear();
                _fuelDrainPerHour = -1f;
                _fuelIdleSinceTicks = 0;
                return;
            }
            if (ring.Count > 0 && lvl > ring[ring.Count - 1].Value + 0.15f)
                ring.Clear();

            // Only WORK teaches the rate: sitting still with the engine
            // idling must FREEZE the readout, not slowly re-learn the idle
            // burn (owner on-wheel 2026-08-10: time left climbed whenever
            // the machine stopped, because at idle sips the tank "lasts"
            // days). Working = rolling, or under real load, so a stationary
            // PTO machine still counts as work.
            bool working = speedKmh >= 1.0 || _motorLoad01 >= 0.35f;
            if (!working)
            {
                if (_fuelIdleSinceTicks == 0) _fuelIdleSinceTicks = now;
                return;   // rate stays frozen at the last working figure
            }
            if (_fuelIdleSinceTicks != 0)
            {
                // A long stop restarts the window: its idle burn would
                // otherwise land on the next 150 s of work as a rate spike.
                // A short pause stays in-window; mere dilution.
                if (now - _fuelIdleSinceTicks > freq * 20) ring.Clear();
                _fuelIdleSinceTicks = 0;
            }
            if (ring.Count == 0 || now - ring[ring.Count - 1].Key >= freq * 5)
            {
                ring.Add(new KeyValuePair<long, float>(now, lvl));
                while (ring.Count > 1 && now - ring[0].Key > freq * 150)
                    ring.RemoveAt(0);
                double spanS = (now - ring[0].Key) / (double)freq;
                if (spanS >= 30.0)
                {
                    double perHour = (ring[0].Value - lvl) / spanS * 3600.0;
                    // A flat window keeps the previous figure: that is what
                    // a paused game reads, not a real zero burn.
                    if (perHour > 0.0) _fuelDrainPerHour = (float)perHour;
                }
            }
            // Periodic diag (every 30 s while feeding): raw game usage next
            // to the derived drain, so FS25's lastFuelUsage unit convention
            // is provable from a log file if it is ever wanted again.
            if (now - _lastFuelDiagTicks > freq * 30)
            {
                _lastFuelDiagTicks = now;
                SimHub.Logging.Current.Info(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[TF4ALL] FS fuel diag: lvl={0:F2} cap={1:F0} gameUse={2:F2} derived={3:F2}/h leftMin={4:F0}",
                    lvl, _fuelCapacity, _fuelUseGameRaw, _fuelDrainPerHour, FuelMinutesLeft));
            }
        }

        private void ParseLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != '{') return;
            var o = JObject.Parse(line);

            bool inVehicle = o.Value<bool?>("inVehicle") ?? false;
            _inVehicle = inVehicle;
            if (!inVehicle)
            {
                // Emit the empty frame anyway: MeasuredHz keeps tracking (the
                // mod is alive) while IsSessionActive correctly reads false.
                _motorLoad01 = -1f;
                _fuelLevel = -1f; _fuelCapacity = -1f;
                _fuelUseGameRaw = -1f;
                _fuelRing.Clear(); _fuelDrainPerHour = -1f;
                _fuelIdleSinceTicks = 0;
                EmitFrame(new TelemetryFrame());
                return;
            }

            // Vehicle identity (mod >= 0.2.5). configFileName is the stable
            // cross-machine id; getFullName the official display name. Kept
            // latched for the session (like Forza's null-gap car latch):
            // walking away from the vehicle must not un-car the settings UI.
            var carCfg = o.Value<string>("carCfg");
            if (!string.IsNullOrWhiteSpace(carCfg) && carCfg != _lastCarCfg)
            {
                _lastCarCfg = carCfg;
                _stableCarId = NormalizeCarId(carCfg);
                // New vehicle, new tank: the old vehicle's level samples
                // would read the level jump as an absurd drain rate.
                _fuelRing.Clear(); _fuelDrainPerHour = -1f;
            }
            var carName = o.Value<string>("carName");
            if (!string.IsNullOrWhiteSpace(carName)) _carDisplayName = carName.Trim();

            double rpm    = o.Value<double?>("rpm") ?? 0.0;
            double maxRpm = o.Value<double?>("maxRpm") ?? 0.0;
            double speed  = o.Value<double?>("speedKmh") ?? 0.0;

            var load = o.Value<double?>("motorLoad");
            _motorLoad01 = load.HasValue && !double.IsNaN(load.Value)
                ? (float)Math.Min(Math.Max(load.Value, 0.0), 1.0) : -1f;
            System.Threading.Interlocked.Exchange(ref _motorLoadStampTicks,
                System.Diagnostics.Stopwatch.GetTimestamp());

            var impl = o.Value<bool?>("impl");
            if (impl.HasValue) _implementActive = impl.Value;
            var implMove = o.Value<bool?>("implMove");
            if (implMove.HasValue) _implementMoving = implMove.Value;
            var implSpd = o.Value<double?>("implSpd");
            if (implSpd.HasValue && !double.IsNaN(implSpd.Value))
                _implementSpeed = (float)Math.Min(Math.Max(implSpd.Value, 0.0), 5.0);
            else if (implMove.HasValue)
                // implMove without implSpd is definitionally a pre-0.2.14
                // mod (0.2.14+ emits both together): drop any speed latched
                // from an earlier game this SimHub session, even after a
                // game crash that never sent a farewell frame, so the hum
                // degrades to neutral pitch on older mods.
                _implementSpeed = null;
            var implEvt = o.Value<int?>("implEvt");
            if (implEvt.HasValue)
                _implementEvent = implEvt.Value;
            else if (implMove.HasValue)
                // Same stale-latch rule as implSpd, for pre-0.2.16 mods.
                _implementEvent = null;
            var implMan = o.Value<bool?>("implMan");
            if (implMan.HasValue)
                _implementManual = implMan.Value;
            else if (implMove.HasValue)
                // Same stale-latch rule, for pre-0.2.19 mods.
                _implementManual = null;
            var implPhase = o.Value<int?>("implPhase");
            if (implPhase.HasValue)
            {
                _implementPhase = implPhase.Value;
                var phSpd = o.Value<double?>("implPhSpd");
                _implementPhaseSpeed = phSpd.HasValue && !double.IsNaN(phSpd.Value)
                    ? (object)(float)Math.Min(Math.Max(phSpd.Value, 0.0), 5.0)
                    : null;
            }
            else if (implMove.HasValue)
            {
                // Same stale-latch rule, for pre-0.2.22 mods.
                _implementPhase = null;
                _implementPhaseSpeed = null;
            }
            var modVer = o.Value<string>("modVer");
            if (!string.IsNullOrWhiteSpace(modVer)) _reportedModVersion = modVer.Trim();
            var fill = o.Value<double?>("fill");
            if (fill.HasValue && !double.IsNaN(fill.Value))
                _attachedFill01 = (float)Math.Min(Math.Max(fill.Value, 0.0), 1.0);
            var towKg = o.Value<double?>("towKg");
            if (towKg.HasValue && !double.IsNaN(towKg.Value) && towKg.Value >= 0)
                _towedMassKg = (float)towKg.Value;

            // Fuel (mod >= 0.2.21). Emitted every in-vehicle frame while a
            // tank is readable, so plain per-frame assignment is the honest
            // shape: absence means "not reported" and reads -1 at once (no
            // tri-state latch; an older mod simply never sets these).
            var fuelL = o.Value<double?>("fuelL");
            _fuelLevel = fuelL.HasValue && !double.IsNaN(fuelL.Value) && fuelL.Value >= 0
                ? (float)fuelL.Value : -1f;
            var fuelCap = o.Value<double?>("fuelCap");
            _fuelCapacity = fuelCap.HasValue && !double.IsNaN(fuelCap.Value) && fuelCap.Value > 0
                ? (float)fuelCap.Value : -1f;
            var fuelT = o.Value<string>("fuelT");
            _fuelType = string.IsNullOrWhiteSpace(fuelT) ? null : fuelT.Trim();
            var fuelUse = o.Value<double?>("fuelUse");
            _fuelUseGameRaw = fuelUse.HasValue && !double.IsNaN(fuelUse.Value) && fuelUse.Value >= 0
                ? (float)fuelUse.Value : -1f;
            UpdateFuelDrain(speed);

            // Rev bar: FS reports no redline, so the band tops out at MaxRpm,
            // same fallback shape as the SimHub source uses without one.
            const double RevBandStart = 0.87;
            double revPct = 0.0;
            if (maxRpm > 0)
            {
                double lo = maxRpm * RevBandStart;
                revPct = maxRpm > lo ? (rpm - lo) / (maxRpm - lo) : 0.0;
                if (revPct < 0) revPct = 0; else if (revPct > 1) revPct = 1;
            }

            var frame = new TelemetryFrame
            {
                // Tri-state by construction: null until a 0.2.6+ mod reports
                // the flag, so the thud effect stays seeded-empty on old mods.
                ImplementWorking = (bool?)_implementActive,
                ImplementMoving  = (bool?)_implementMoving,
                ImplementSpeed   = (float?)_implementSpeed,
                ImplementEvent   = (int?)_implementEvent,
                ImplementManual  = (bool?)_implementManual,
                ImplementPhase      = (int?)_implementPhase,
                ImplementPhaseSpeed = (float?)_implementPhaseSpeed,
                Rpms       = rpm,
                MaxRpm     = maxRpm,
                SpeedKmh   = speed,
                Gear       = o.Value<string>("gearName") ?? (o.Value<int?>("gear")?.ToString() ?? ""),
                RpmPercent = revPct,
                RedlineReached = maxRpm > 0 && rpm >= maxRpm,
                RedlineRpm = 0,   // FS exposes no real shift RPM; never faked from MaxRpm
            };

            // Chassis dynamics (mod v0.2.3+): world-frame angular velocity.
            // For an upright vehicle world Y IS body yaw, so no quaternion
            // math is needed for the two consumers: yaw rate itself, and
            // lateral acceleration approximated as v * yawRate (the MAIRA
            // no-tire-data lesson), which feeds the cornering-weight scale.
            var avy = o.Value<double?>("avy");
            if (avy.HasValue && !double.IsNaN(avy.Value))
            {
                frame.YawRateDegPerSec = avy.Value * (180.0 / Math.PI);
                frame.AccelerationSway = (speed / 3.6) * avy.Value;   // m/s^2, signed
            }

            // Surge from speed deltas (owner design 2026-08-08): frames flow
            // at up to 100 Hz, so forward deceleration is one subtraction,
            // and FrameEnricher's universal accel-spike derivation turns a
            // >5g spike into CollisionMagnitude for the collision thud.
            // Guards: implausible dt (frame gap, vehicle entry) or a >30 m/s
            // single-frame jump (teleport/reset, not physics) emits nothing.
            // getLastSpeed is unsigned, so a reverse-direction crash still
            // spikes |dv| correctly.
            {
                double vMps = speed / 3.6;
                long nowS = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!double.IsNaN(_prevSpeedMps) && _prevSpeedTicks != 0)
                {
                    double dtS = (nowS - _prevSpeedTicks)
                        / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (dtS > 0.005 && dtS < 0.15)
                    {
                        double dv = vMps - _prevSpeedMps;
                        if (Math.Abs(dv) < 30.0)
                            frame.AccelerationSurge = dv / dtS;
                    }
                }
                _prevSpeedMps = vMps;
                _prevSpeedTicks = nowS;
            }

            // Suspension quad from the wheels array. The steered pair is the
            // front axle (steered wheels carry the rack); left/right by the
            // wheel's lateral position when the mod could read one, else by
            // wheel index. Whether "left minus right" pulls the correct way
            // is validated on hardware, RoadKickModel's standing contract.
            if (o["wheels"] is JArray wheels)
            {
                JObject fa = null, fb = null, ra = null, rb = null;
                double fax = double.MinValue, fbx = double.MinValue;
                var curY = new double[8];
                var curYSet = new bool[8];
                bool anyContactField = false, anyContactTrue = false;
                foreach (var t in wheels)
                {
                    if (!(t is JObject w) || w["y"] == null) continue;
                    int wi = (w.Value<int?>("i") ?? 0) - 1;
                    if (wi >= 0 && wi < 8)
                    {
                        double wy = w.Value<double?>("y") ?? double.NaN;
                        if (!double.IsNaN(wy)) { curY[wi] = wy; curYSet[wi] = true; }
                    }
                    // Per-wheel slip (mod 0.2.9+, GIANTS lastSlip 0..1),
                    // contact-weighted AVERAGE per the frame doc's contract:
                    // an airborne or lifted wheel contributes nothing, an
                    // all-wheel slide reads full strength, and one spinning
                    // wheel of eight reads as the partial event it is (the
                    // Forza/AC load-weighting convention with binary loads).
                    var contact = w.Value<bool?>("contact");
                    if (contact.HasValue)
                    {
                        anyContactField = true;
                        if (contact.Value) anyContactTrue = true;
                    }
                    if (w["steer"] != null)
                    {
                        double x = w.Value<double?>("x") ?? double.MinValue;
                        if (fa == null) { fa = w; fax = x; }
                        else if (fb == null) { fb = w; fbx = x; }
                    }
                    else if (ra == null) ra = w;
                    else if (rb == null) rb = w;
                }
                // Airborne: every reported wheel off the ground. Drives the
                // airborne ducker (vibrations cut mid-air, thump on landing
                // via the suspension kick). Null when the mod reports no
                // contact flags, so the effect stays inert on old mods.
                if (anyContactField) frame.Airborne = !anyContactTrue;

                // Bump synthesis, always on: a hard suspension transient
                // pulses OnRumbleStrip, whose rising edge fires the Kerb
                // thump effect's percussive hit. Discrete hits only; the
                // sustained SurfaceRumble channel stays unset on purpose
                // (constant tractor jiggle rendered as texture felt bad).
                {
                    long nowT = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (_prevWheelYValid && _prevFrameTicks != 0)
                    {
                        double dt = (nowT - _prevFrameTicks)
                            / (double)System.Diagnostics.Stopwatch.Frequency;
                        if (dt > 0.001 && dt < 0.5)
                        {
                            double sum = 0; int n = 0;
                            for (int i = 0; i < 8; i++)
                            {
                                if (!curYSet[i]) continue;
                                sum += Math.Abs(curY[i] - _prevWheelY[i]) / dt;
                                n++;
                            }
                            if (n > 0)
                            {
                                double meanRate = sum / n;
                                if (meanRate > 3.0)
                                {
                                    // Physically absurd (a real suspension
                                    // never averages 3 m/s): this is the
                                    // game tearing the vehicle down on exit.
                                    // Scrub the quad so the kick model never
                                    // sees the death spike (it pulled the
                                    // wheel hard for seconds on game close).
                                    frame.SuspTravelM = default;
                                    frame.FrontSuspTravelMeters = null;
                                }
                                else
                                {
                                    // 0.35 m/s of mean suspension motion =
                                    // full scale; past 55% of it is a hit.
                                    frame.OnRumbleStrip = meanRate / 0.35 > 0.55;
                                }
                            }
                        }
                    }
                    Array.Copy(curY, _prevWheelY, 8);
                    _prevWheelYValid = true;
                    _prevFrameTicks = nowT;
                }
                if (fa != null && fb != null)
                {
                    if (fax != double.MinValue && fbx != double.MinValue && fbx > fax)
                    { var tw = fa; fa = fb; fb = tw; }
                    double fl = fa.Value<double?>("y") ?? 0.0;
                    double fr = fb.Value<double?>("y") ?? 0.0;
                    frame.SuspTravelM = new TireQuad
                    {
                        FL = fl,
                        FR = fr,
                        RL = ra?.Value<double?>("y") ?? 0.0,
                        RR = rb?.Value<double?>("y") ?? 0.0,
                    };
                    frame.FrontSuspTravelMeters = (fl + fr) * 0.5;
                }

                // Periodic slip diagnostic (every 5 s while feeding): per-
                // wheel surface speed + load and the computed rollups, so a
                // "no slip feel" report is diagnosable from the log alone.
                {
                    long nowD = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (nowD - _lastSlipDiagTicks > System.Diagnostics.Stopwatch.Frequency * 5)
                    {
                        _lastSlipDiagTicks = nowD;
                        _slipDiagDue = true;
                    }
                }

                // Slip, second pass (mod 0.2.12): FS25 exposes no slip value
                // anywhere (field + method dumps verified 2026-08-08), so
                // the mod streams each wheel's SURFACE speed (rotation-angle
                // delta x radius, signed) and slip is derived here: ratio =
                // (surface - ground) / ground. Ground speed is unsigned, so
                // travel direction comes from the wheels' consensus first,
                // which is why this cannot fold into the loop above. Tire
                // load (getTireLoad) weights the overall average, the real
                // Forza/AC convention; a wheel with no load data weighs 1.
                // Per-axle rollups feed the Axle slip voices. Falls back to
                // a game-reported "slip" magnitude (FS22 lastSlip).
                {
                    double vMps = speed / 3.6;
                    double wsSum = 0; int wsN = 0;
                    foreach (var t in wheels)
                    {
                        if (!(t is JObject w) || !(w.Value<bool?>("contact") ?? true)) continue;
                        var wsv = w.Value<double?>("ws");
                        if (wsv.HasValue && !double.IsNaN(wsv.Value)) { wsSum += wsv.Value; wsN++; }
                    }
                    // Direction consensus: only meaningful when the wheels
                    // agree on average motion above walking pace.
                    double dirSign = (wsN > 0 && Math.Abs(wsSum / wsN) > 0.6) ? Math.Sign(wsSum) : 1.0;

                    double num = 0, den = 0, fSum = 0, rSum = 0;
                    int fN = 0, rN = 0;
                    var diagWs = _slipDiagDue ? new System.Text.StringBuilder() : null;
                    foreach (var t in wheels)
                    {
                        if (!(t is JObject w)) continue;
                        double? s01 = WheelSlip01(w, vMps, dirSign);
                        if (diagWs != null)
                        {
                            if (diagWs.Length > 0) diagWs.Append(' ');
                            var wsRaw = w.Value<double?>("ws");
                            diagWs.Append(wsRaw.HasValue ? wsRaw.Value.ToString("F1") : "-");
                            diagWs.Append('/');
                            var ldRaw = w.Value<double?>("load");
                            diagWs.Append(ldRaw.HasValue ? ldRaw.Value.ToString("F0") : "-");
                        }
                        if (!s01.HasValue) continue;
                        double wt = Math.Max(w.Value<double?>("load") ?? 1.0, 0.05);
                        num += s01.Value * wt; den += wt;
                        // Grip UTILIZATION for the axle voices: the effect's
                        // onset gate (default 0.85) speaks near the LIMIT,
                        // and on soil the grip limit sits near ~35% raw slip
                        // ratio, not 100% (raw-as-utilization kept the
                        // voices silent through visible wheelspin,
                        // 2026-08-08). Slight >1 headroom keeps "past the
                        // limit" distinguishable for the onset curve.
                        // Front = the SAME first-two-steered pair the
                        // suspension quad uses: FS reports a steer field on
                        // EVERY wheel (rears hold 0), so "has steer" put all
                        // four wheels on the front axle and the effect
                        // hard-zeroed on the missing rear (diag: "R=-").
                        double uti = Math.Min(s01.Value / 0.35, 1.2);
                        bool front = ReferenceEquals(w, fa) || ReferenceEquals(w, fb);
                        if (front) { fSum += uti; fN++; }
                        else { rSum += uti; rN++; }
                    }
                    if (den > 0) frame.WheelSlip = num / den;
                    if (fN > 0) frame.FrontGrip01 = fSum / fN;
                    if (rN > 0) frame.RearGrip01  = rSum / rN;

                    // Signed quads (mod 0.2.15, owner-approved lockup feel):
                    // the derived ratio is SIGNED (+ wheelspin, - lockup),
                    // the one thing GIANTS never offered. Filling the quads
                    // lights up the deriver's Lockup events, the Lockup
                    // judder voice, Axle slip's braking gate, and the
                    // rev-locked rear pulse (real wheel rad/s via "wr").
                    // An airborne corner reads neutral 0 (a lifted wheel is
                    // not a lockup); a corner with no surface speed at all
                    // (old mod) keeps the quads unset. The composer's
                    // fill-only guard keeps our utilization rollups
                    // authoritative over quad recomputation.
                    if (fa != null && fb != null && ra != null && rb != null)
                    {
                        double? sFL = SignedRatio(fa, vMps, dirSign);
                        double? sFR = SignedRatio(fb, vMps, dirSign);
                        double? sRL = SignedRatio(ra, vMps, dirSign);
                        double? sRR = SignedRatio(rb, vMps, dirSign);
                        if (sFL.HasValue && sFR.HasValue && sRL.HasValue && sRR.HasValue)
                        {
                            frame.TireSlipRatio = TireQuad.Of(sFL.Value, sFR.Value, sRL.Value, sRR.Value);
                            frame.TireCombinedSlip = TireQuad.Of(
                                Uti(sFL.Value), Uti(sFR.Value), Uti(sRL.Value), Uti(sRR.Value));
                            frame.WheelRotRadS = TireQuad.Of(
                                fa.Value<double?>("wr") ?? 0.0, fb.Value<double?>("wr") ?? 0.0,
                                ra.Value<double?>("wr") ?? 0.0, rb.Value<double?>("wr") ?? 0.0);
                            frame.HasTireQuads = true;
                        }
                    }

                    if (diagWs != null)
                    {
                        _slipDiagDue = false;
                        // Signed ratio range: negative = toward lockup. The
                        // interesting question for FS is whether the physics
                        // ever lets a braking wheel rotate SLOWER than the
                        // ground at all.
                        string ratioTxt = "-";
                        if (frame.HasTireQuads)
                        {
                            var q = frame.TireSlipRatio;
                            double mn = Math.Min(Math.Min(q.FL, q.FR), Math.Min(q.RL, q.RR));
                            double mx = Math.Max(Math.Max(q.FL, q.FR), Math.Max(q.RL, q.RR));
                            ratioTxt = $"{mn:F2}..{mx:F2}";
                        }
                        SimHub.Logging.Current.Info(
                            $"[TF4ALL] FS slip diag: v={speed:F1}km/h dir={dirSign:+0;-0} "
                            + $"ws/load=[{diagWs}] slip={(frame.WheelSlip.HasValue ? frame.WheelSlip.Value.ToString("F2") : "-")} "
                            + $"F={(frame.FrontGrip01.HasValue ? frame.FrontGrip01.Value.ToString("F2") : "-")} "
                            + $"R={(frame.RearGrip01.HasValue ? frame.RearGrip01.Value.ToString("F2") : "-")} "
                            + $"ratio=[{ratioTxt}]");
                    }
                }
            }

            EmitFrame(frame);
        }
    }
}
