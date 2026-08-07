using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    /// <summary>Enhanced telemetry source for Farming Simulator, fed by the
    /// TF4ALL Telemetry game mod over a named pipe this side hosts
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
        public override bool IsRunning => _thread != null && !_stop;

        public Action<string> Logger { get; set; }

        private Thread _thread;
        private volatile bool _stop;
        private NamedPipeServerStream _server;
        private volatile bool _inVehicle;
        private volatile float _motorLoad01 = -1f;
        private bool _connectedLogged;

        /// <summary>Engine load 0..1 from the mod, or negative when the game
        /// did not report one. Not a TelemetryFrame field yet; the
        /// implement-drag layer is its intended consumer.</summary>
        public float MotorLoad01 => _motorLoad01;

        // In-vehicle is the game's own word for "the player is driving", so
        // this source is authoritative the way Forza's IsRaceOn is: on foot,
        // in menus, or with the mod silent there is no force to render.
        public override bool HasAuthoritativeSessionState => true;
        public override bool IsSessionActive => MeasuredHz > 0 && _inVehicle;

        public override void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(ServerLoop) { IsBackground = true, Name = "TF4ALL-FsTelemetry" };
            _thread.Start();
        }

        public override void Stop()
        {
            _stop = true;
            try { _server?.Dispose(); } catch { }   // unblocks WaitForConnection / ReadLine
            try { _thread?.Join(1500); } catch { }
            _thread = null;
        }

        private void ServerLoop()
        {
            while (!_stop)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(
                        "TF4ALLTelemetry", PipeDirection.In, 1, PipeTransmissionMode.Byte))
                    {
                        _server = server;
                        server.WaitForConnection();
                        if (_stop) return;
                        if (!_connectedLogged)
                        {
                            _connectedLogged = true;
                            Logger?.Invoke("Farming Simulator connected (TF4ALL Telemetry game mod).");
                        }
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string line;
                            while (!_stop && (line = reader.ReadLine()) != null)
                            {
                                try { ParseLine(line); }
                                catch { /* one bad line must not drop the connection */ }
                            }
                        }
                    }
                }
                catch
                {
                    // Disposed mid-wait or the game closed; either way a fresh
                    // server instance goes back to listening.
                }
                _server = null;
                if (!_stop) Thread.Sleep(500);
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
                EmitFrame(new TelemetryFrame());
                return;
            }

            double rpm    = o.Value<double?>("rpm") ?? 0.0;
            double maxRpm = o.Value<double?>("maxRpm") ?? 0.0;
            double speed  = o.Value<double?>("speedKmh") ?? 0.0;

            var load = o.Value<double?>("motorLoad");
            _motorLoad01 = load.HasValue && !double.IsNaN(load.Value)
                ? (float)Math.Min(Math.Max(load.Value, 0.0), 1.0) : -1f;

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
                Rpms       = rpm,
                MaxRpm     = maxRpm,
                SpeedKmh   = speed,
                Gear       = o.Value<string>("gearName") ?? (o.Value<int?>("gear")?.ToString() ?? ""),
                RpmPercent = revPct,
                RedlineReached = maxRpm > 0 && rpm >= maxRpm,
                RedlineRpm = 0,   // FS exposes no real shift RPM; never faked from MaxRpm
            };

            // Suspension quad from the wheels array. The steered pair is the
            // front axle (steered wheels carry the rack); left/right by the
            // wheel's lateral position when the mod could read one, else by
            // wheel index. Whether "left minus right" pulls the correct way
            // is validated on hardware, RoadKickModel's standing contract.
            if (o["wheels"] is JArray wheels)
            {
                JObject fa = null, fb = null, ra = null, rb = null;
                double fax = double.MinValue, fbx = double.MinValue;
                foreach (var t in wheels)
                {
                    if (!(t is JObject w) || w["y"] == null) continue;
                    if (w["steer"] != null)
                    {
                        double x = w.Value<double?>("x") ?? double.MinValue;
                        if (fa == null) { fa = w; fax = x; }
                        else if (fb == null) { fb = w; fbx = x; }
                    }
                    else if (ra == null) ra = w;
                    else if (rb == null) rb = w;
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
            }

            EmitFrame(frame);
        }
    }
}
