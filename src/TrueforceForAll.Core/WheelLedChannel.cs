// Drives the Logitech wheel rim's 10 RGB rev/shift LEDs over HID++.
//
// Separate channel from the Trueforce audio-haptic stream (interface 2,
// vendor usage 0xFFFD). mescon's RS50 spec documents the LED protocol as
// fully independent of FFB, so writing here can't disturb FFB or the ep3
// haptic stream.
//
// WINDOWS COLLECTION SPLIT (confirmed on the G PRO, pid 0xC272, from a Test
// run): the wheel's HID++ interface (MI_01) is exposed by Windows as THREE
// separate HID collections, one per HID++ report ID / size:
//     col with maxOut  7  -> SHORT     reports (ID 0x10)
//     col with maxOut 20  -> LONG      reports (ID 0x11)
//     col with maxOut 64  -> VERY_LONG reports (ID 0x12)
// A given report ID is only valid on its own collection; writing it to the
// wrong one fails with "Incorrect function", and the device's reply to a
// request appears on whichever collection owns the reply's report ID (a
// SHORT request gets a LONG/VERY_LONG answer on a *different* handle). So we
// open all three sibling collections and route every read/write by report ID.
//
// Protocol (mescon, logitech-rs50-linux-driver RS50_PROTOCOL_SPECIFICATION):
//   - HID++ root feature 0x0000, getFeature(pageId) resolves a runtime
//     feature index for LIGHTSYNC effect page 0x807A and RGB-zone page
//     0x807B. Indices differ per wheel/firmware so they MUST be queried.
//   - Applying a rev-bar state is a fixed 6-step sequence (see ApplyRgb).
//   - The RGB payload sends LED10 first and LED1 last.
//
// Verbose by design: the settings "Test" button + SimHub log are the
// hardware-verification tool. Nothing here can brick the wheel.

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
        private const byte RootGetFn  = 0x0B;  // function 0 | software-id 0x0B (mescon)

        private const ushort PageLightsyncEffect = 0x807A; // effect mode + enable
        private const ushort PageRgbZone         = 0x807B; // per-LED RGB config

        public const int LedCount = 10;

        private readonly Action<string> _log;
        private readonly object _io = new object();

        // One open stream per HID++ report size. A wheel that exposes all
        // three on a single collection (some firmwares / non-Windows) would
        // have the same stream referenced by all three; we only require that
        // SHORT is writable plus at least one of LONG/VERY_LONG for the reply.
        private HidStream _short, _long, _veryLong;
        private string _devName;

        private byte _idxEffect;   // resolved index of page 0x807A
        private byte _idxRgb;      // resolved index of page 0x807B
        private bool _ready;

        public bool IsReady => _ready;
        public string ResolvedInfo =>
            _ready ? $"effect=0x{_idxEffect:X2} rgb=0x{_idxRgb:X2} via {_devName}"
                   : "(not resolved)";

        public WheelLedChannel(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        // ---- Discovery + feature resolution ---------------------------------

        /// <summary>Find the wheel, group its HID++ sibling collections by
        /// report size, open them, and resolve the 0x807A / 0x807B feature
        /// indices. Idempotent; returns true once a channel is live.</summary>
        public bool OpenAndResolve()
        {
            lock (_io)
            {
                if (_ready) return true;

                // Gather every non-Trueforce HID collection of the wheel,
                // grouped by device-instance stem (the path up to "&col..").
                // Each group is one physical interface split into per-report
                // collections by Windows.
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
                {
                    if (TryGroup(kv.Key, kv.Value)) return true;
                }

                _log("[RPM-LED] probed all interface groups; none answered HID++ getFeature.");
                return false;
            }
        }

        // Strip the per-report collection suffix so the SHORT/LONG/VERY_LONG
        // siblings of one interface share a key. Path looks like
        //   \\?\hid#vid_046d&pid_c272&mi_01&col03#a&32897e1&0&0002#{guid}
        // We key on everything before "&col"; interfaces without "&col"
        // (single-collection) key on the full path.
        private static string GroupStem(string path)
        {
            int i = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? path.Substring(0, i) : path;
        }

        private bool TryGroup(string stem, List<HidDevice> collections)
        {
            HidStream shortS = null, longS = null, veryS = null;
            var opened = new List<HidStream>();
            try
            {
                // Classify each collection by its max output report length.
                // 7 -> SHORT, 20 -> LONG, 64 -> VERY_LONG. Open lazily; skip
                // any that refuse to open (often the input-only gamepad).
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
                    DisposeAll(opened);
                    return false;
                }
                if (longS == null && veryS == null)
                {
                    _log($"[RPM-LED] group has no LONG/VERY_LONG reply collection: {stem}");
                    DisposeAll(opened);
                    return false;
                }

                _short = shortS; _long = longS; _veryLong = veryS;

                byte effIdx = TryGetFeature(PageLightsyncEffect);
                if (effIdx == 0)
                {
                    _log($"[RPM-LED] no HID++ reply for 0x807A in group {stem}");
                    DisposeAll(opened); ClearStreams();
                    return false;
                }
                byte rgbIdx = TryGetFeature(PageRgbZone);
                if (rgbIdx == 0)
                {
                    _log($"[RPM-LED] 0x807A ok (idx 0x{effIdx:X2}) but 0x807B missing in group {stem}");
                    DisposeAll(opened); ClearStreams();
                    return false;
                }

                _idxEffect = effIdx;
                _idxRgb = rgbIdx;
                _ready = true;
                _log($"[RPM-LED] resolved {ResolvedInfo}  (short/long/vlong = "
                     + $"{(_short != null)}/{(_long != null)}/{(_veryLong != null)})");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[RPM-LED] group probe error ({stem}): {ex.Message}");
                DisposeAll(opened); ClearStreams();
                return false;
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
        /// reads the reply off whichever collection carries it (LONG then
        /// VERY_LONG then SHORT). Returns the resolved feature index, or 0 if
        /// the device gave no usable reply (0 is never a valid index).</summary>
        private byte TryGetFeature(ushort pageId)
        {
            var req = new byte[LenShort];
            req[0] = RepShort;
            req[1] = DevWired;
            req[2] = RootIndex;
            req[3] = RootGetFn;
            req[4] = (byte)(pageId >> 8);
            req[5] = (byte)(pageId & 0xFF);
            req[6] = 0x00;

            try { _short.Write(req); }
            catch (Exception ex) { _log($"[RPM-LED] getFeature write failed: {ex.Message}"); return 0; }

            // The reply is one report on the collection that owns its ID.
            // HID++ 2.0 answers root.getFeature with a LONG report; some
            // firmwares use VERY_LONG (mescon). Try LONG, then VERY_LONG,
            // then SHORT, accepting the first that echoes root + our request.
            foreach (var s in new[] { _long, _veryLong, _short })
            {
                byte idx = ReadFeatureReply(s, pageId);
                if (idx == 0xFF) return 0;     // explicit HID++ error
                if (idx != 0) return idx;
            }
            return 0;
        }

        // Returns: resolved index (1..0x7F) on success, 0 on timeout/no-match,
        // 0xFF if the device answered with a HID++ error report.
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
                if (resp[3] == 0xFF)
                {
                    _log($"[RPM-LED] HID++ error report for page 0x{pageId:X4}");
                    return 0xFF;
                }
                byte idx = resp[4];
                if (idx != 0 && idx < 0x80) return idx;
            }
            return 0;
        }

        // ---- Rev-bar rendering ---------------------------------------------

        /// <summary>Map a 0..1 rev fill into the LED bar and push it.
        /// <paramref name="redline"/> forces a full red bar (caller blinks it
        /// by alternating redline true/false).</summary>
        public void ApplyRevBar(double pct, bool redline)
        {
            if (!_ready) return;
            if (pct < 0) pct = 0; else if (pct > 1) pct = 1;

            int lit = redline ? LedCount : (int)Math.Floor(pct * LedCount + 0.5);
            if (lit > LedCount) lit = LedCount;

            var rgb = new byte[LedCount * 3];
            for (int i = 0; i < LedCount; i++)
            {
                if (i >= lit) continue;            // unlit -> 0,0,0
                byte r, g, b;
                if (redline)        { r = 255; g = 0;   b = 0; }
                else if (i < 6)     { r = 0;   g = 255; b = 0; }   // LED 1-6 green
                else if (i < 8)     { r = 255; g = 120; b = 0; }   // LED 7-8 amber
                else                { r = 255; g = 0;   b = 0; }   // LED 9-10 red
                rgb[i * 3 + 0] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }
            ApplyRgb(rgb);
        }

        /// <summary>mescon's 6-step LIGHTSYNC apply, each report routed to the
        /// collection that owns its report ID. <paramref name="rgbLed1to10"/>
        /// is 30 bytes in physical order (LED1 first); protocol wants LED10
        /// first so we reverse per-LED triplets into the wire payload.</summary>
        public void ApplyRgb(byte[] rgbLed1to10)
        {
            if (!_ready || rgbLed1to10 == null || rgbLed1to10.Length < LedCount * 3) return;
            const byte slot = 0x00, dir = 0x00;

            lock (_io)
            {
                try
                {
                    // 1: SET_EFFECT mode 5  (SHORT, page 0x807A fn3)
                    WriteShort(new byte[] { RepShort, DevWired, _idxEffect, 0x3C, 0x05, 0x00, 0x00 });

                    // 2: PRE_CONFIG  (LONG, page 0x807A fn6)
                    var pre = new byte[LenLong];
                    pre[0] = RepLong; pre[1] = DevWired; pre[2] = _idxEffect; pre[3] = 0x6C;
                    pre[4] = 0x00; pre[5] = 0x01; pre[6] = 0x00; pre[7] = 0x0A;
                    WriteLong(pre);

                    // 3: RGB zone config  (VERY_LONG, page 0x807B fn2)
                    var z = new byte[LenVeryLong];
                    z[0] = RepVeryLong; z[1] = DevWired; z[2] = _idxRgb; z[3] = 0x2C;
                    z[4] = slot; z[5] = dir;
                    for (int led = 0; led < LedCount; led++)
                    {
                        int srcLed = LedCount - 1 - led;   // wire LED10 first
                        z[6 + led * 3 + 0] = rgbLed1to10[srcLed * 3 + 0];
                        z[6 + led * 3 + 1] = rgbLed1to10[srcLed * 3 + 1];
                        z[6 + led * 3 + 2] = rgbLed1to10[srcLed * 3 + 2];
                    }
                    WriteVeryLong(z);

                    // 4: activate slot  (SHORT, page 0x807B fn3)
                    WriteShort(new byte[] { RepShort, DevWired, _idxRgb, 0x3C, slot, 0x00, 0x00 });

                    // 5: COMMIT  (LONG, page 0x807A fn6; differs from step 2 at byte 8)
                    var commit = new byte[LenLong];
                    commit[0] = RepLong; commit[1] = DevWired; commit[2] = _idxEffect; commit[3] = 0x6C;
                    commit[4] = 0x00; commit[5] = 0x01; commit[6] = 0x00; commit[7] = 0x0A;
                    commit[8] = 0x0A;
                    WriteLong(commit);

                    // 6: ENABLE / REFRESH  (SHORT, page 0x807A fn7)
                    WriteShort(new byte[] { RepShort, DevWired, _idxEffect, 0x7C, 0x00, 0x00, 0x00 });
                }
                catch (Exception ex)
                {
                    _log($"[RPM-LED] apply failed: {ex.Message}");
                    _ready = false;   // force a re-probe on next OpenAndResolve()
                }
            }
        }

        private void WriteShort(byte[] r)    => _short.Write(r);
        private void WriteLong(byte[] r)
        {
            // If the wheel has no separate LONG collection it may accept LONG
            // on the SHORT handle (single-collection firmwares); fall back.
            if (_long != null) _long.Write(r); else _short.Write(r);
        }
        private void WriteVeryLong(byte[] r)
        {
            if (_veryLong != null) _veryLong.Write(r);
            else if (_long != null) _long.Write(r);
            else _short.Write(r);
        }

        /// <summary>Turn every LED off (on disable / shutdown).</summary>
        public void Clear()
        {
            if (_ready) ApplyRgb(new byte[LedCount * 3]);
        }

        public void Dispose()
        {
            lock (_io)
            {
                try { if (_ready) ApplyRgb(new byte[LedCount * 3]); } catch { }
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
