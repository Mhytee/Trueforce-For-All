// Drives the Logitech G PRO (and RS50, and G923 Xbox) wheel rim's rev/shift
// LEDs over HID++. The G923 Xbox uses the SAME 0x807A feature and level pair;
// it just exposes no 7-byte SHORT collection, so its SHORT-form commands ride
// the 20-byte LONG collection padded to 20B with report id 0x11 (see the
// _cmdShort fields). The G923 PlayStation variant is different: it uses the
// legacy F8-12 report, handled by LegacyLedF8Channel, not this class.
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

        // iRacing/MAIRA FFB and these LEDs share ONE HID++ control pipe into a
        // single command processor on the wheel. G HUB resends the level pair
        // ~every 156 ms, but it isn't fighting a sim's ~250-500 Hz FFB stream
        // for that pipe; we are. Resending that fast starved FFB (it cut in/out
        // and the soft endstop snapped). The wheel holds the level fine for far
        // longer, so we keep alive only ~1 Hz, and skip even that whenever a
        // real change-write already refreshed it within the interval.
        private const int KeepAliveMs = 1000;
        private const int ArmGapMs    = 4;          // pace the one-time arm burst
        // Minimum gap between level-pair writes. G HUB sends the pair at a
        // STEADY ~156 ms regardless of how fast revs change; it never bursts.
        // Sending immediately on every level change (which happens rapidly
        // near the shift point) delayed MAIRA's FFB packets on the shared
        // HID++ pipe enough that the wheel decayed force -> "FFB goes limp
        // when the lights come on". One fixed-cadence sender, no bursts,
        // matches G HUB's proven-safe footprint.
        private const int ChangeMinMs = 160;

        private readonly Action<string> _log;
        private readonly object _io = new object();

        // One open stream per HID++ report size (see file header).
        private HidStream _short, _long, _veryLong;
        private string _devName;

        // Where SHORT-form HID++ commands (getFeature, ARM, the fn2 of the level
        // pair) are written. The G PRO has a real 7-byte SHORT collection and
        // uses report id 0x10. The G923 Xbox exposes NO 7-byte collection, so its
        // SHORT commands ride the 20-byte LONG collection instead, padded to 20B
        // and sent with report id 0x11 (its FFB uses 0x11 the same way).
        private HidStream _cmdShort;
        private int  _cmdShortLen;      // pad SHORT commands to this length (7 or 20)
        private byte _cmdShortRepId;    // report id for SHORT commands (0x10 or 0x11)

        private byte _idxRev;       // resolved feature index of page 0x807A
        private bool _ready;
        private volatile bool _armed;
        private volatile int  _level;       // target rev level 0..10 (set by SetLevel)
        private int  _sentLevel = -1;       // last level actually written to the wire
        private long _lastWriteMs;          // last time the level pair hit the wire
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
                        // On the G923 Xbox the Trueforce interface is mi_01 (it is
                        // mi_02 on the G PRO). Opening it here would collide with our
                        // ep3 Trueforce stream, so skip it for those PIDs; the HID++
                        // rev-light collections live on mi_00 (col02 = command,
                        // col03 = reply).
                        bool skipMi01 = pid == 0xC26D || pid == 0xC26E;
                        foreach (var dev in list.GetHidDevices(WheelDiscovery.LogitechVid, pid))
                        {
                            string path = dev.DevicePath ?? string.Empty;
                            if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;   // Trueforce audio interface, never HID++
                            if (skipMi01 && path.IndexOf("mi_01", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;   // G923 Xbox Trueforce interface
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
                    else if (outLen == LenLong && longS == null) { longS = s; if (_devName == null) _devName = dev.GetFriendlyName(); }
                    else if (outLen == LenVeryLong && veryS == null) veryS = s;
                }

                // The G923 Xbox exposes no 7-byte SHORT collection: its SHORT-form
                // HID++ commands ride the 20-byte LONG collection instead. The G PRO
                // has a real SHORT collection. Require at least one writable command
                // collection of either kind.
                if (shortS == null && longS == null)
                {
                    _log($"[RPM-LED] group has no SHORT or LONG command collection: {stem}");
                    DisposeAll(opened); return false;
                }
                if (longS == null && veryS == null)
                {
                    _log($"[RPM-LED] group has no LONG/VERY_LONG reply collection: {stem}");
                    DisposeAll(opened); return false;
                }

                _short = shortS; _long = longS; _veryLong = veryS;
                if (shortS != null) { _cmdShort = shortS; _cmdShortLen = LenShort; _cmdShortRepId = RepShort; }
                else                { _cmdShort = longS;  _cmdShortLen = LenLong;  _cmdShortRepId = RepLong;  }

                byte idx = TryGetFeature(PageRevLights);
                if (idx == 0)
                {
                    _log($"[RPM-LED] no HID++ reply for 0x807A in group {stem}");
                    DisposeAll(opened); ClearStreams(); return false;
                }

                _idxRev = idx;
                _ready = true;
                // Dispose any opened-but-not-kept duplicate streams (a non-standard
                // topology exposing two collections of the same report size opens a
                // second stream assigned to no slot). The failure paths DisposeAll;
                // the success path must dispose the orphans or they leak till exit.
                foreach (var s in opened)
                    if (s != _short && s != _long && s != _veryLong)
                        try { s.Dispose(); } catch { }
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

        private void ClearStreams() { _short = _long = _veryLong = _cmdShort = null; _ready = false; }

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
            req[0] = _cmdShortRepId; req[1] = DevWired; req[2] = RootIndex; req[3] = RootGetFn;
            req[4] = (byte)(pageId >> 8); req[5] = (byte)(pageId & 0xFF); req[6] = 0x00;

            try { _cmdShort.Write(PadShort(req)); }
            catch (Exception ex) { _log($"[RPM-LED] getFeature write failed: {ex.Message}"); return 0; }

            // Replies land on the largest collection (the 64-byte VERY_LONG /
            // col03 on the G923 Xbox); read it first when there is no dedicated
            // SHORT collection so we don't burn read timeouts on the command one.
            var replyOrder = _short != null
                ? new[] { _long, _veryLong, _short }
                : new[] { _veryLong, _long };
            foreach (var s in replyOrder)
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

        private static void ArmGap() { try { Thread.Sleep(ArmGapMs); } catch { } }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>Run G HUB's arm sequence once, then start the ~1 Hz
        /// keepalive that re-sends the current level (the wheel holds it for a
        /// good while but reverts eventually if never refreshed).</summary>
        private void Arm()
        {
            if (_armed) return;
            // SHORT fn0, fn1, fn2, fn3(param 0x02), fn0. This is a one-time
            // 7-transfer burst; space the writes a few ms apart so it doesn't
            // monopolise the shared HID++ pipe and hitch FFB at session start.
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(0), 0x00, 0x00, 0x00 });
            ArmGap();
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(1), 0x00, 0x00, 0x00 });
            ArmGap();
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(2), 0x00, 0x00, 0x00 });
            ArmGap();
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(3), 0x02, 0x00, 0x00 });
            ArmGap();
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(0), 0x00, 0x00, 0x00 });
            ArmGap();
            SendPair(0);
            _sentLevel = 0;
            _armed = true;

            _hbStop = false;
            _hbThread = new Thread(SenderLoop)
            { IsBackground = true, Name = "RpmLedSender" };
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
            _lastWriteMs = NowMs();
        }

        // The ONLY thing that writes the level pair after arming. Fixed
        // cadence, never bursts: at most one pair per ChangeMinMs when the
        // target moved, plus a ~1 Hz keepalive when it's steady. This bounds
        // our HID++ pipe usage to G HUB's footprint so it doesn't starve the
        // sim's FFB.
        private void SenderLoop()
        {
            const int tickMs = 30;
            while (!_hbStop)
            {
                Thread.Sleep(tickMs);
                if (_hbStop || !_ready) continue;

                long now = NowMs();
                bool changed   = _level != _sentLevel;
                bool dueChange = changed && (now - _lastWriteMs) >= ChangeMinMs;
                bool dueKeep   = !changed && (now - _lastWriteMs) >= KeepAliveMs;
                if (!dueChange && !dueKeep) continue;

                lock (_io)
                {
                    if (!_ready || !_armed) continue;
                    int target = _level;
                    if (target == _sentLevel && (NowMs() - _lastWriteMs) < KeepAliveMs)
                        continue;
                    try
                    {
                        SendPair(target);
                        _sentLevel = target;
                    }
                    catch (Exception ex)
                    {
                        _log($"[RPM-LED] sender failed: {ex.Message}");
                        _ready = false;   // force re-probe next OpenAndResolve()
                    }
                }
            }
        }

        /// <summary>Set the target rev level 0..10. Arms on first call. Does
        /// NOT write here, only updates the target; SenderLoop writes it at a
        /// fixed cadence so we never burst the shared HID++ pipe and starve
        /// FFB. Worst-case LED latency ~ ChangeMinMs, same as G HUB.</summary>
        public void SetLevel(int level)
        {
            if (!_ready) return;
            if (level < 0) level = 0; else if (level > LedCount) level = LedCount;
            if (!_armed)
            {
                lock (_io)
                {
                    if (!_ready) return;
                    try { if (!_armed) Arm(); }
                    catch (Exception ex)
                    {
                        _log($"[RPM-LED] arm failed: {ex.Message}");
                        _ready = false;
                        return;
                    }
                }
            }
            _level = level;   // volatile; SenderLoop picks it up
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
                _sentLevel = -1;
                _armed = false;
            }
        }

        public void Clear() => TurnOff();

        // Callers build a 7-byte SHORT payload with r[0] as a placeholder report
        // id. Route it to the command stream: the G PRO uses its real 7-byte SHORT
        // collection (report 0x10); the G923 Xbox has none, so SHORT rides the
        // 20-byte LONG collection padded to 20B with report id 0x11.
        private void WriteShort(byte[] r)
        {
            r[0] = _cmdShortRepId;
            _cmdShort.Write(PadShort(r));
        }
        private void WriteLong(byte[] r)
        {
            if (_long != null) _long.Write(r); else _cmdShort.Write(r);
        }

        // Pad a SHORT (7-byte) payload up to the command stream's output length.
        // A no-op for the G PRO (LenShort == 7); pads to 20B for the G923 Xbox.
        private byte[] PadShort(byte[] r)
        {
            if (_cmdShortLen <= r.Length) return r;
            var padded = new byte[_cmdShortLen];
            Array.Copy(r, padded, r.Length);
            return padded;
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
