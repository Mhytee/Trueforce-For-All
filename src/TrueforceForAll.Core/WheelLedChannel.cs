// Drives the Logitech G PRO wheel rim's rev/shift LEDs over HID++.
//
// Protocol decoded 2026-05-16 from a USBPcap capture of G HUB driving the
// rev lights in a game (captures/gpro_leds.pcap, analyzer gpro_leds.py).
// mescon's RS50 RGB-zone model (page 0x807B, per-LED RGB, 6-step apply) does
// NOT apply to the G PRO. The G PRO is LEVEL-based:
//
//   * Feature page 0x807A (resolved via HID++ root getFeature; this wheel =
//     index 0x09, but it varies per wheel/firmware so always resolve it).
//   * Function-byte software-ID nibble = 0x0d (what G HUB uses).
//   * Arm once:  fn0 1d? -> SHORT fn0,fn1,fn2, SHORT fn3 param 0x02, SHORT fn0
//   * Per update + ~6 Hz keepalive: SHORT fn2 `10 ff IDX 2d 00 00 00`
//     then LONG fn6 `11 ff IDX 6d 00 01 00 0a 00 LL 00..` where byte 9
//     (LL) = rev level 0..10 = how many LEDs light. G HUB resends this
//     pair continuously even when LL is unchanged; the wheel's onboard
//     profile owns the colours / direction / scaling, so there is NO RGB
//     or per-LED control here, only the 0..10 level.
//
// Transport detail (Windows): the HID++ interface is split into three HID
// collections by report size, maxOut 7=SHORT(0x10) / 20=LONG(0x11) /
// 64=VERY_LONG(0x12). A report ID is only valid on its own collection and a
// request's reply comes back on a different handle, so we open all three and
// route by report ID. Independent of FFB / the Trueforce ep3 stream.

using System;
using System.Collections.Generic;
using System.Threading;
using HidSharp;

namespace TrueforceForAll.Core
{
    public sealed class WheelLedChannel : IDisposable
    {
        // HID++ report IDs and their on-wire total lengths (incl. report ID).
        private const byte RepShort    = 0x10; private const int LenShort    = 7;
        private const byte RepLong     = 0x11; private const int LenLong     = 20;
        private const byte RepVeryLong = 0x12; private const int LenVeryLong = 64;

        private const byte DevWired   = 0xFF;  // HID++ device index for a wired device
        private const byte RootIndex  = 0x00;  // HID++ IRoot feature is always index 0
        private const byte RootGetFn  = 0x0B;  // root getFeature: fn0 | sw-id 0x0B

        private const ushort PageRevLights = 0x807A; // LIGHTSYNC effect / rev level
        private const byte SwId            = 0x0D;   // fn-byte sw-id nibble G HUB uses

        public const int LedCount = 10;             // rev level range is 0..10
        private const int HeartbeatMs = 150;        // G HUB resends ~every 156 ms

        private readonly Action<string> _log;
        private readonly object _io = new object();

        // One open stream per HID++ report size (see file header).
        private HidStream _short, _long, _veryLong;
        private string _devName;

        private byte _idxRev;       // resolved feature index of page 0x807A
        private bool _ready;
        private volatile bool _armed;
        private volatile int  _level;       // current rev level 0..10
        private Thread _hbThread;
        private volatile bool _hbStop;

        public bool IsReady => _ready;
        public string ResolvedInfo =>
            _ready ? $"revFeat=0x{_idxRev:X2} via {_devName}" : "(not resolved)";

        public WheelLedChannel(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        // ---- Discovery + feature resolution ---------------------------------

        /// <summary>Find the wheel, group its HID++ sibling collections by
        /// report size, open them, and resolve the page 0x807A feature index.
        /// Idempotent; returns true once a channel is live.</summary>
        public bool OpenAndResolve()
        {
            lock (_io)
            {
                if (_ready) return true;

                var groups = new Dictionary<string, List<HidDevice>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var list = DeviceList.Local;
                    foreach (var (pid, model) in WheelDiscovery.SupportedPids)
                    {
                        foreach (var dev in list.GetHidDevices(WheelDiscovery.LogitechVid, pid))
                        {
                            string path = dev.DevicePath ?? string.Empty;
                            if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;   // Trueforce audio interface, never HID++
                            string stem = GroupStem(path);
                            if (!groups.TryGetValue(stem, out var g))
                                groups[stem] = g = new List<HidDevice>();
                            g.Add(dev);
                            _log($"[RPM-LED] candidate: {model} maxOut={SafeOutLen(dev)} path={path}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[RPM-LED] enumeration failed: {ex.Message}");
                    return false;
                }

                if (groups.Count == 0)
                {
                    _log("[RPM-LED] no non-Trueforce HID interfaces found for the wheel.");
                    return false;
                }

                foreach (var kv in groups)
                    if (TryGroup(kv.Key, kv.Value)) return true;

                _log("[RPM-LED] probed all interface groups; none answered HID++ getFeature.");
                return false;
            }
        }

        // Strip the per-report collection suffix so the SHORT/LONG/VERY_LONG
        // siblings of one interface share a key.
        private static string GroupStem(string path)
        {
            int i = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? path.Substring(0, i) : path;
        }

        private bool TryGroup(string stem, List<HidDevice> collections)
        {
            var opened = new List<HidStream>();
            try
            {
                HidStream shortS = null, longS = null, veryS = null;
                foreach (var dev in collections)
                {
                    int outLen = SafeOutLen(dev);
                    if (outLen != LenShort && outLen != LenLong && outLen != LenVeryLong)
                        continue;

                    HidStream s;
                    try { s = dev.Open(new OpenConfiguration()); }
                    catch (Exception ex)
                    {
                        _log($"[RPM-LED] open refused ({ex.Message}): {dev.DevicePath}");
                        continue;
                    }
                    s.ReadTimeout = 250;
                    s.WriteTimeout = 250;
                    opened.Add(s);

                    if (outLen == LenShort && shortS == null) { shortS = s; _devName = dev.GetFriendlyName(); }
                    else if (outLen == LenLong && longS == null) longS = s;
                    else if (outLen == LenVeryLong && veryS == null) veryS = s;
                }

                if (shortS == null)
                {
                    _log($"[RPM-LED] group has no SHORT (7-byte) collection: {stem}");
                    DisposeAll(opened); return false;
                }
                if (longS == null && veryS == null)
                {
                    _log($"[RPM-LED] group has no LONG/VERY_LONG reply collection: {stem}");
                    DisposeAll(opened); return false;
                }

                _short = shortS; _long = longS; _veryLong = veryS;

                byte idx = TryGetFeature(PageRevLights);
                if (idx == 0)
                {
                    _log($"[RPM-LED] no HID++ reply for 0x807A in group {stem}");
                    DisposeAll(opened); ClearStreams(); return false;
                }

                _idxRev = idx;
                _ready = true;
                _log($"[RPM-LED] resolved {ResolvedInfo}  (short/long/vlong = "
                     + $"{_short != null}/{_long != null}/{_veryLong != null})");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[RPM-LED] group probe error ({stem}): {ex.Message}");
                DisposeAll(opened); ClearStreams(); return false;
            }
        }

        private void ClearStreams() { _short = _long = _veryLong = null; _ready = false; }

        private static void DisposeAll(List<HidStream> streams)
        {
            foreach (var s in streams) { try { s.Dispose(); } catch { } }
        }

        private static int SafeOutLen(HidDevice d)
        {
            try { return d.GetMaxOutputReportLength(); } catch { return -1; }
        }

        /// <summary>HID++ root getFeature(pageId). Writes the SHORT request and
        /// reads the reply off whichever collection carries it. Returns the
        /// resolved feature index, or 0 if no usable reply.</summary>
        private byte TryGetFeature(ushort pageId)
        {
            var req = new byte[LenShort];
            req[0] = RepShort; req[1] = DevWired; req[2] = RootIndex; req[3] = RootGetFn;
            req[4] = (byte)(pageId >> 8); req[5] = (byte)(pageId & 0xFF); req[6] = 0x00;

            try { _short.Write(req); }
            catch (Exception ex) { _log($"[RPM-LED] getFeature write failed: {ex.Message}"); return 0; }

            foreach (var s in new[] { _long, _veryLong, _short })
            {
                byte idx = ReadFeatureReply(s, pageId);
                if (idx == 0xFF) return 0;
                if (idx != 0) return idx;
            }
            return 0;
        }

        private byte ReadFeatureReply(HidStream s, ushort pageId)
        {
            if (s == null) return 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] resp = new byte[LenVeryLong];
                int n;
                try { n = s.Read(resp, 0, resp.Length); }
                catch (TimeoutException) { return 0; }
                catch (Exception ex) { _log($"[RPM-LED] getFeature read failed: {ex.Message}"); return 0; }
                if (n < 5) continue;
                if (resp[1] != DevWired || resp[2] != RootIndex) continue;
                if (resp[3] == 0xFF) { _log($"[RPM-LED] HID++ error for 0x{pageId:X4}"); return 0xFF; }
                byte idx = resp[4];
                if (idx != 0 && idx < 0x80) return idx;
            }
            return 0;
        }

        // ---- Rev-level protocol --------------------------------------------

        private byte Fn(int fn) => (byte)((fn << 4) | SwId);

        /// <summary>Run G HUB's arm sequence once, then start the ~6 Hz
        /// keepalive that re-sends the current level (the wheel reverts if it
        /// stops being refreshed).</summary>
        private void Arm()
        {
            if (_armed) return;
            // SHORT fn0, fn1, fn2, fn3(param 0x02), fn0
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(0), 0x00, 0x00, 0x00 });
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(1), 0x00, 0x00, 0x00 });
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(2), 0x00, 0x00, 0x00 });
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(3), 0x02, 0x00, 0x00 });
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(0), 0x00, 0x00, 0x00 });
            SendPair(0);
            _armed = true;

            _hbStop = false;
            _hbThread = new Thread(HeartbeatLoop)
            { IsBackground = true, Name = "RpmLedHeartbeat" };
            _hbThread.Start();
        }

        // SHORT fn2 then LONG fn6 with byte 9 = level. Exactly the pair G HUB
        // repeats; sending fn2 first matches the captured ordering.
        private void SendPair(int level)
        {
            byte lvl = (byte)(level < 0 ? 0 : level > LedCount ? LedCount : level);
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(2), 0x00, 0x00, 0x00 });
            var f6 = new byte[LenLong];
            f6[0] = RepLong; f6[1] = DevWired; f6[2] = _idxRev; f6[3] = Fn(6);
            f6[4] = 0x00; f6[5] = 0x01; f6[6] = 0x00; f6[7] = 0x0A; f6[8] = 0x00;
            f6[9] = lvl;   // 0..10 = LEDs lit
            WriteLong(f6);
        }

        private void HeartbeatLoop()
        {
            while (!_hbStop)
            {
                Thread.Sleep(HeartbeatMs);
                if (_hbStop || !_ready) continue;
                lock (_io)
                {
                    if (!_ready || !_armed) continue;
                    try { SendPair(_level); }
                    catch (Exception ex)
                    {
                        _log($"[RPM-LED] heartbeat failed: {ex.Message}");
                        _ready = false;   // force re-probe next OpenAndResolve()
                    }
                }
            }
        }

        /// <summary>Set the rev level 0..10 (count of LEDs lit). Arms on first
        /// call. Pushes immediately for low latency; the heartbeat keeps it
        /// alive thereafter.</summary>
        public void SetLevel(int level)
        {
            if (!_ready) return;
            if (level < 0) level = 0; else if (level > LedCount) level = LedCount;
            lock (_io)
            {
                try
                {
                    if (!_armed) Arm();
                    if (level != _level)
                    {
                        _level = level;
                        SendPair(level);
                    }
                }
                catch (Exception ex)
                {
                    _log($"[RPM-LED] SetLevel failed: {ex.Message}");
                    _ready = false;
                }
            }
        }

        /// <summary>Map a 0..1 rev fill (or redline) to the 0..10 level. The
        /// wheel's onboard profile owns colours / direction; we only choose
        /// how many LEDs.</summary>
        public void ApplyRevBar(double pct, bool redline)
        {
            if (pct < 0) pct = 0; else if (pct > 1) pct = 1;
            int lvl = redline ? LedCount : (int)Math.Floor(pct * LedCount + 0.5);
            SetLevel(lvl);
        }

        /// <summary>Rev level to 0 (LEDs off) and stop the keepalive so the
        /// rim returns to its profile idle state.</summary>
        public void TurnOff()
        {
            _hbStop = true;
            var t = _hbThread; _hbThread = null;
            try { t?.Join(300); } catch { }

            if (!_ready) return;
            lock (_io)
            {
                try { if (_armed) SendPair(0); }
                catch (Exception ex) { _log($"[RPM-LED] TurnOff failed: {ex.Message}"); }
                _level = 0;
                _armed = false;
            }
        }

        public void Clear() => TurnOff();

        private void WriteShort(byte[] r) => _short.Write(r);
        private void WriteLong(byte[] r)
        {
            if (_long != null) _long.Write(r); else _short.Write(r);
        }

        public void Dispose()
        {
            try { TurnOff(); } catch { }
            lock (_io)
            {
                foreach (var s in new[] { _short, _long, _veryLong })
                {
                    if (s == null) continue;
                    try { s.Dispose(); } catch { }
                }
                ClearStreams();
                _ready = false;
            }
        }
    }
}
