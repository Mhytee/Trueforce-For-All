// Drives the Logitech wheel rim's 10 RGB rev/shift LEDs over HID++.
//
// This is a SEPARATE channel from the Trueforce audio-haptic stream
// (WheelDiscovery / TrueforceDevice, interface 2, vendor usage 0xFFFD).
// LEDs travel over the HID++ control interface (report IDs 0x10/0x11/0x12),
// which mescon's RS50 reverse-engineering documents as fully independent of
// FFB ("FFB uses dedicated endpoint 0x03 OUT, NOT the HID++ protocol"), so
// writing here cannot disturb FFB or the ep3 haptic stream.
//
// Protocol (mescon, logitech-rs50-linux-driver RS50_PROTOCOL_SPECIFICATION):
//   - HID++ root feature 0x0000, getFeature(pageId) resolves a runtime
//     feature index. We resolve LIGHTSYNC effect page 0x807A and RGB-zone
//     page 0x807B; indices differ per wheel/firmware so they MUST be queried,
//     never hard-coded.
//   - Applying a rev-bar state is a fixed 6-step sequence (see ApplyRgb).
//   - The RGB payload sends LED10 first and LED1 last (reversed vs the
//     physical left-to-right order the driver thinks in).
//
// Because the exact Windows HID collection that carries HID++ for the G PRO
// is not known a priori, OpenAndResolve() PROBES every non-Trueforce HID
// interface of the wheel: it opens each, sends a getFeature, and keeps the
// first that returns a sane HID++ reply. Every step logs via the Log action
// so the settings "Test" button + SimHub log are the hardware-verification
// tool. Nothing here can brick the wheel: a malformed HID++ request just
// yields an error report, and LED writes are non-destructive (reset on
// replug).

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

        private HidDevice _dev;
        private HidStream _stream;
        private byte _idxEffect;   // resolved index of page 0x807A
        private byte _idxRgb;      // resolved index of page 0x807B
        private bool _ready;

        public bool IsReady => _ready;
        public string ResolvedInfo =>
            _ready ? $"effect=0x{_idxEffect:X2} rgb=0x{_idxRgb:X2} via {_dev?.GetFriendlyName()}"
                   : "(not resolved)";

        public WheelLedChannel(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        // ---- Discovery + feature resolution ---------------------------------

        /// <summary>Find the wheel, probe its HID interfaces for the HID++
        /// control channel, and resolve the 0x807A / 0x807B feature indices.
        /// Idempotent; returns true once a channel is live. Verbose by design.</summary>
        public bool OpenAndResolve()
        {
            lock (_io)
            {
                if (_ready) return true;

                var candidates = new List<HidDevice>();
                try
                {
                    var list = DeviceList.Local;
                    foreach (var (pid, model) in WheelDiscovery.SupportedPids)
                    {
                        foreach (var dev in list.GetHidDevices(WheelDiscovery.LogitechVid, pid))
                        {
                            string path = dev.DevicePath ?? string.Empty;
                            // The Trueforce audio interface (MI_02, usage
                            // 0xFFFD) is opened exclusively by TrueforceDevice
                            // and never carries HID++ — skip it outright.
                            if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;
                            candidates.Add(dev);
                            _log($"[RPM-LED] candidate: {model} maxOut={SafeOutLen(dev)} path={path}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[RPM-LED] enumeration failed: {ex.Message}");
                    return false;
                }

                if (candidates.Count == 0)
                {
                    _log("[RPM-LED] no non-Trueforce HID interfaces found for the wheel.");
                    return false;
                }

                foreach (var dev in candidates)
                {
                    HidStream s = null;
                    try
                    {
                        var cfg = new OpenConfiguration();
                        try { s = dev.Open(cfg); }
                        catch (Exception ex)
                        {
                            _log($"[RPM-LED] open refused ({ex.Message}): {dev.DevicePath}");
                            continue;
                        }
                        s.ReadTimeout  = 250;
                        s.WriteTimeout = 250;

                        byte effIdx = TryGetFeature(s, dev, PageLightsyncEffect);
                        if (effIdx == 0)
                        {
                            _log($"[RPM-LED] no HID++ reply for 0x807A on {dev.DevicePath}");
                            s.Dispose();
                            continue;
                        }
                        byte rgbIdx = TryGetFeature(s, dev, PageRgbZone);
                        if (rgbIdx == 0)
                        {
                            _log($"[RPM-LED] 0x807A ok (idx 0x{effIdx:X2}) but 0x807B missing on {dev.DevicePath}");
                            s.Dispose();
                            continue;
                        }

                        _dev = dev;
                        _stream = s;
                        _idxEffect = effIdx;
                        _idxRgb = rgbIdx;
                        _ready = true;
                        _log($"[RPM-LED] resolved {ResolvedInfo}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _log($"[RPM-LED] probe error on {dev.DevicePath}: {ex.Message}");
                        try { s?.Dispose(); } catch { }
                    }
                }

                _log("[RPM-LED] probed all interfaces; none answered HID++ getFeature.");
                return false;
            }
        }

        private static int SafeOutLen(HidDevice d)
        {
            try { return d.GetMaxOutputReportLength(); } catch { return -1; }
        }

        /// <summary>HID++ root getFeature(pageId). Returns the resolved feature
        /// index, or 0 if the device gave no usable reply (0 is never a valid
        /// non-root index, so it doubles as "not found").</summary>
        private byte TryGetFeature(HidStream s, HidDevice dev, ushort pageId)
        {
            var req = new byte[LenShort];
            req[0] = RepShort;
            req[1] = DevWired;
            req[2] = RootIndex;
            req[3] = RootGetFn;
            req[4] = (byte)(pageId >> 8);
            req[5] = (byte)(pageId & 0xFF);
            req[6] = 0x00;

            try { s.Write(req); }
            catch (Exception ex) { _log($"[RPM-LED] getFeature write failed: {ex.Message}"); return 0; }

            // The wheel may interleave unrelated input reports; read a few
            // times and match the one echoing root + our function byte.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                byte[] resp;
                try
                {
                    int inLen;
                    try { inLen = dev.GetMaxInputReportLength(); } catch { inLen = LenVeryLong; }
                    resp = new byte[Math.Max(LenVeryLong, inLen)];
                    int n = s.Read(resp, 0, resp.Length);
                    if (n < 5) continue;
                }
                catch (TimeoutException) { return 0; }
                catch (Exception ex) { _log($"[RPM-LED] getFeature read failed: {ex.Message}"); return 0; }

                // Expect: [rep] FF 00 <fn echo> <featureIndex> ...
                if (resp[1] == DevWired && resp[2] == RootIndex)
                {
                    byte idx = resp[4];
                    if (resp[3] == 0xFF)            // HID++ error report
                    {
                        _log($"[RPM-LED] HID++ error for page 0x{pageId:X4}");
                        return 0;
                    }
                    if (idx != 0 && idx < 0x80) return idx;
                }
            }
            return 0;
        }

        // ---- Rev-bar rendering ---------------------------------------------

        /// <summary>Map a 0..1 rev fill into a 30-byte LED10→LED1 RGB payload
        /// and push it. <paramref name="redline"/> overrides to a full red
        /// bar (the caller blinks it by alternating redline true/false).</summary>
        public void ApplyRevBar(double pct, bool redline)
        {
            if (!_ready) return;
            if (pct < 0) pct = 0; else if (pct > 1) pct = 1;

            // round-half-up so the last LED lights right at the shift point.
            int lit = redline ? LedCount : (int)Math.Floor(pct * LedCount + 0.5);
            if (lit > LedCount) lit = LedCount;

            // Physical LED 1..10 left-to-right. Classic rev bar: green block,
            // amber, then red near the limit. Full brightness for v1; tuning
            // (brightness, partial-LED dimming) comes after hardware-confirm.
            var rgb = new byte[LedCount * 3];
            for (int i = 0; i < LedCount; i++)
            {
                if (i >= lit) continue;            // unlit -> 0,0,0
                byte r, g, b;
                if (redline)            { r = 255; g = 0;   b = 0; }
                else if (i < 6)         { r = 0;   g = 255; b = 0; }   // LED 1-6 green
                else if (i < 8)         { r = 255; g = 120; b = 0; }   // LED 7-8 amber
                else                    { r = 255; g = 0;   b = 0; }   // LED 9-10 red
                rgb[i * 3 + 0] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }
            ApplyRgb(rgb);
        }

        /// <summary>Run mescon's exact 6-step LIGHTSYNC apply sequence with the
        /// resolved feature indices. <paramref name="rgbLed1to10"/> is 30 bytes
        /// in physical order (LED1 first); the protocol wants LED10 first, so
        /// we reverse per-LED triplets into the wire payload here.</summary>
        public void ApplyRgb(byte[] rgbLed1to10)
        {
            if (!_ready || rgbLed1to10 == null || rgbLed1to10.Length < LedCount * 3) return;
            const byte slot = 0x00, dir = 0x00;

            lock (_io)
            {
                try
                {
                    // 1: SET_EFFECT mode 5  (SHORT, page 0x807A fn3)
                    Write(new byte[] { RepShort, DevWired, _idxEffect, 0x3C, 0x05, 0x00, 0x00 });

                    // 2: PRE_CONFIG  (LONG, page 0x807A fn6)
                    var pre = new byte[LenLong];
                    pre[0] = RepLong; pre[1] = DevWired; pre[2] = _idxEffect; pre[3] = 0x6C;
                    pre[4] = 0x00; pre[5] = 0x01; pre[6] = 0x00; pre[7] = 0x0A;
                    Write(pre);

                    // 3: RGB zone config  (VERY_LONG, page 0x807B fn2)
                    var z = new byte[LenVeryLong];
                    z[0] = RepVeryLong; z[1] = DevWired; z[2] = _idxRgb; z[3] = 0x2C;
                    z[4] = slot; z[5] = dir;
                    for (int led = 0; led < LedCount; led++)
                    {
                        // wire byte 6 = LED10, byte 33 = LED1
                        int srcLed = LedCount - 1 - led;
                        z[6 + led * 3 + 0] = rgbLed1to10[srcLed * 3 + 0];
                        z[6 + led * 3 + 1] = rgbLed1to10[srcLed * 3 + 1];
                        z[6 + led * 3 + 2] = rgbLed1to10[srcLed * 3 + 2];
                    }
                    Write(z);

                    // 4: activate slot  (SHORT, page 0x807B fn3)
                    Write(new byte[] { RepShort, DevWired, _idxRgb, 0x3C, slot, 0x00, 0x00 });

                    // 5: COMMIT  (LONG, page 0x807A fn6; differs from step 2 at byte 8)
                    var commit = new byte[LenLong];
                    commit[0] = RepLong; commit[1] = DevWired; commit[2] = _idxEffect; commit[3] = 0x6C;
                    commit[4] = 0x00; commit[5] = 0x01; commit[6] = 0x00; commit[7] = 0x0A;
                    commit[8] = 0x0A;
                    Write(commit);

                    // 6: ENABLE / REFRESH  (SHORT, page 0x807A fn7)
                    Write(new byte[] { RepShort, DevWired, _idxEffect, 0x7C, 0x00, 0x00, 0x00 });
                }
                catch (Exception ex)
                {
                    _log($"[RPM-LED] apply failed: {ex.Message}");
                    _ready = false;   // force a re-probe on next OpenAndResolve()
                }
            }
        }

        private void Write(byte[] report)
        {
            // Some HID stacks demand the report be padded to the interface's
            // max output length; others want the exact HID++ length. Try exact
            // first, fall back to padded on the first failure.
            try { _stream.Write(report); return; }
            catch { }
            int max;
            try { max = _dev.GetMaxOutputReportLength(); }
            catch { throw; }
            if (max <= report.Length) throw new InvalidOperationException("HID write rejected");
            var padded = new byte[max];
            Buffer.BlockCopy(report, 0, padded, 0, report.Length);
            _stream.Write(padded);
        }

        /// <summary>Turn every LED off (used on disable / shutdown so the rim
        /// doesn't keep a stale rev pattern when the feature is switched off).</summary>
        public void Clear()
        {
            if (_ready) ApplyRgb(new byte[LedCount * 3]);
        }

        public void Dispose()
        {
            lock (_io)
            {
                try { if (_ready) ApplyRgb(new byte[LedCount * 3]); } catch { }
                try { _stream?.Dispose(); } catch { }
                _stream = null;
                _dev = null;
                _ready = false;
            }
        }
    }
}
