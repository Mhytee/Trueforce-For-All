// Reads the wheel's PHYSICAL steering position straight off its HID
// controller interface, independent of any game.
//
// Why this exists: the stationary spring needs to know where the wheel is to
// add centering weight. Normally we get that from game telemetry, but some
// games stop reporting it when you're not actively driving. Forza Horizon, in
// particular, reports steering as 0 the entire time you're paused or in the
// pre-race countdown (verified from the paused-FFB diagnostic), and its menu
// centering is a DirectInput spring effect we can't capture. So the only way
// to weight the wheel in those states is to read the wheel's own position.
//
// Approach: the wheel exposes its axes on a Generic-Desktop HID interface
// (separate from the MI_02 Trueforce vendor interface). We locate the steering
// axis from the report descriptor (Simulation Steering, or Generic Desktop X /
// Wheel), open that interface read-only, and parse incoming input reports with
// HidSharp's descriptor-driven parser, so we don't hardcode byte offsets per
// wheel. The value is normalized to [-1, 1] (0 = centred) from the axis's own
// logical range. Read failures are non-fatal: the spring just falls back to
// game-reported steering.

using System;
using System.Diagnostics;
using System.Threading;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;

namespace TrueforceForAll.Core
{
    public sealed class WheelSteeringReader : IDisposable
    {
        // Steering axis usages we recognize, in priority order:
        //   Simulation Controls (0x02) / Steering (0xC8)
        //   Generic Desktop (0x01) / X (0x30)
        //   Generic Desktop (0x01) / Wheel (0x38)
        private const uint UsageSteering = 0x000200C8;
        private const uint UsageX        = 0x00010030;
        private const uint UsageWheel    = 0x00010038;

        private readonly ushort _vid;
        private readonly ushort _pid;

        private HidStream _stream;
        private HidDeviceInputReceiver _receiver;
        private DeviceItemInputParser _parser;
        private byte[] _buf;
        private uint _steeringUsage;
        private int _running;

        public Action<string> Logger { get; set; }

        /// <summary>Latest physical steering, normalized to roughly [-1, 1]
        /// (0 = centred). Sign convention is the wheel's; the spring consumer
        /// flips it if needed, same as it does for game-reported steering.</summary>
        public float SteerNorm { get; private set; }

        /// <summary>Stopwatch ticks of the last successful steering read, so the
        /// consumer can treat a stalled reader as stale. 0 until the first read.</summary>
        // Backed by an explicit field with Interlocked: written on the HID receiver
        // thread, read on the FFB stream thread, and a plain 64-bit long read/write
        // isn't atomic on the 32-bit host (matches the codebase convention).
        private long _lastUpdateTicks;
        public long LastUpdateTicks => Interlocked.Read(ref _lastUpdateTicks);

        public bool IsRunning => _running != 0;

        public WheelSteeringReader(ushort vid, ushort pid)
        {
            _vid = vid;
            _pid = pid;
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try
            {
                if (!OpenSteeringInterface())
                {
                    Log("WheelSteeringReader: no steering axis found on the wheel's HID interfaces; physical-position spring disabled (falling back to game steering).");
                    Interlocked.Exchange(ref _running, 0);
                    return;
                }
                _receiver.Received += OnReceived;
                _receiver.Start(_stream);
                Log($"WheelSteeringReader: reading physical steering (usage 0x{_steeringUsage:X8}).");
            }
            catch (Exception ex)
            {
                Log($"WheelSteeringReader: start failed ({ex.GetType().Name}: {ex.Message}); falling back to game steering.");
                Cleanup();
                Interlocked.Exchange(ref _running, 0);
            }
        }

        // Find the HID interface + device-item that carries the steering axis,
        // open it read-only, and build the descriptor parser. Returns false when
        // no steering axis is found (or nothing opens).
        private bool OpenSteeringInterface()
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(_vid, _pid))
            {
                ReportDescriptor rd;
                try { rd = dev.GetReportDescriptor(); }
                catch { continue; }

                foreach (var deviceItem in rd.DeviceItems)
                {
                    uint usage = FindSteeringUsage(deviceItem);
                    if (usage == 0) continue;

                    HidStream stream;
                    try { stream = dev.Open(); }
                    catch { continue; }   // in use / access denied; try the next interface

                    _stream         = stream;
                    _steeringUsage  = usage;
                    _parser         = deviceItem.CreateDeviceItemInputParser();
                    _receiver       = rd.CreateHidDeviceInputReceiver();
                    _buf            = new byte[Math.Max(1, dev.GetMaxInputReportLength())];
                    return true;
                }
            }
            return false;
        }

        // Highest-priority steering usage present in this device item's input
        // data items, or 0 if none.
        private static uint FindSteeringUsage(DeviceItem deviceItem)
        {
            bool hasX = false, hasWheel = false;
            foreach (var report in deviceItem.InputReports)
            {
                foreach (var dataItem in report.DataItems)
                {
                    foreach (uint u in dataItem.Usages.GetAllValues())
                    {
                        if (u == UsageSteering) return UsageSteering;   // best match
                        if (u == UsageX)     hasX = true;
                        if (u == UsageWheel) hasWheel = true;
                    }
                }
            }
            if (hasX)     return UsageX;
            if (hasWheel) return UsageWheel;
            return 0;
        }

        private void OnReceived(object sender, EventArgs e)
        {
            try
            {
                while (_running != 0 && _receiver.TryRead(_buf, 0, out Report report))
                {
                    if (!_parser.TryParseReport(_buf, 0, report)) continue;
                    UpdateSteering();
                }
            }
            catch (Exception ex)
            {
                Log($"WheelSteeringReader: read error ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // Pull the steering axis out of the freshly-parsed report and normalize
        // it from the axis's own logical range to [-1, 1].
        private void UpdateSteering()
        {
            int count = _parser.ValueCount;
            for (int i = 0; i < count; i++)
            {
                var dv = _parser.GetValue(i);
                if (UsageOf(dv) != _steeringUsage) continue;

                var di = dv.DataItem;
                double min = di.LogicalMinimum;
                double max = di.LogicalMaximum;
                if (max <= min) return;   // degenerate range; can't normalize

                double logical = dv.GetLogicalValue();
                double norm = 2.0 * (logical - min) / (max - min) - 1.0;
                if (norm > 1.0) norm = 1.0; else if (norm < -1.0) norm = -1.0;

                SteerNorm = (float)norm;
                Interlocked.Exchange(ref _lastUpdateTicks, Stopwatch.GetTimestamp());
                return;
            }
        }

        // The usage backing a parsed value (the data item can carry several;
        // DataIndex selects which one this value is).
        private static uint UsageOf(DataValue dv)
        {
            int idx = dv.DataIndex;
            int k = 0;
            foreach (uint u in dv.DataItem.Usages.GetAllValues())
            {
                if (k == idx) return u;
                k++;
            }
            return 0;
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _running, 0);
            Cleanup();
        }

        private void Cleanup()
        {
            try { if (_receiver != null) _receiver.Received -= OnReceived; } catch { }
            try { _stream?.Close(); } catch { }
            try { _stream?.Dispose(); } catch { }
            _stream   = null;
            _receiver = null;
            _parser   = null;
            _buf      = null;
        }

        public void Dispose() => Stop();

        private void Log(string msg)
        {
            var l = Logger;
            if (l != null) try { l(msg); } catch { }
        }
    }
}
