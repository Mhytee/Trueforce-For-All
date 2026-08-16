// Drives the "Dynamic" OLED screen on the base of a Logitech G PRO Racing
// Wheel / RS50 over HID++ feature 0x8130 (DisplayGameData).
//
// Protocol documented by PeposCJ (LogiDynamicDash, issue #20 on mescon's
// logitech-trueforce-linux-driver) and recorded in that driver's
// PROTOCOL_SPECIFICATION.md section 12.3. Hardware-confirmed on PeposCJ's RS50
// and on a G PRO here, in game under Mode B, whose own layout table was read
// back through fn1 (the capacities below). A wheel whose firmware does not
// answer 0x8130 simply never resolves and the panel is left alone.
//
//   * Feature page 0x8130, ALWAYS resolved via HID++ root getFeature: the
//     index is a per-device feature-table position, not a property of the
//     page. Observed 0x12 on PeposCJ's RS50 and 0x10 on a G PRO, which is
//     ordinary HID++ behaviour rather than a difference worth reading into.
//   * Function-byte software-ID nibble = 0x0a.
//   * fn0 = layout count, fn1 = layout descriptor, fn2 = clear pending data,
//     fn3 = set layout + data. THAT IS ALL OF THEM: fn4 through fn15 were
//     probed on a G PRO (2026-08-06) and every one answered HID++ error 0x07,
//     INVALID_FUNCTION_ID. So there is no "claim the display" call hiding on
//     this feature, which matters because the panel reverts to the wheel's own
//     view between our writes and an arm sequence was the obvious suspect.
//   * Setters ride the 64-byte VERY_LONG collection (report id 0x12):
//       12 ff IDX 3a LL <layout-specific payload, zero padded to 64>
//     where LL is the layout index 0..9 (A..J).
//
// It is a TYPED RENDERER, NOT A FRAMEBUFFER. The firmware holds a 128x64
// monochrome buffer but no 0x8130 command accepts pixels, coordinates, fonts
// or regions. Each layout is a fixed firmware-drawn composition with bounded
// ASCII fields and 0..255 normalized gauge values; the font size and alignment
// are chosen by WHICH layout you pick, not by the host. Observed on hardware
// (PeposCJ's Build K layout gallery, 2026-07-28):
//
//   A  blank (renders a black frame, accepts no data)
//   B  the firmware's own four-bar Test composition, no data
//   C  one horizontal gauge                       (1 value)
//   D  gauge + thin indicator + one 11-char label (2 values, 1 text)
//   E  gauge + thin indicator + 3-char + 7-char   (2 values, 2 texts)
//   F  medium 1-char, separator, VERY LARGE 3-char
//   G  VERY LARGE 1-char, separator, medium 3-char
//   H  small 21-char row over a larger 10-char row
//   I  four rows, 19/10/19/10, rows 2+4 larger and RIGHT-ALIGNED
//   J  four rows, 19/10/19/10, rows 2+4 larger, all CENTERED
//
// The unfilled part of a gauge is a diagonal-stripe background that the value
// replaces left-to-right with a solid fill.
//
// FIELD CAPACITIES, read from the wheel itself (fn1 per layout) on a G PRO,
// 2026-08-06. Descriptor form: 12 FF <feat> 1A <index> <index+1> <caps...>.
// These are the firmware's own numbers, not anyone's reading of a capture:
//
//   A 0  (none)          F 5  1, 3
//   B 1  (none)          G 6  1, 3
//   C 2  (none)          H 7  21, 10
//   D 3  11              I 8  19, 10, 19, 10
//   E 4  7, 3            J 9  19, 10, 19, 10
//
// All ten agree with PeposCJ's documentation, and E shows what ORDER the
// descriptor uses. E's descriptor reads 7 then 3, while LogiDynamicDash writes
// a 3-char field at payload offset 7 and a 7-char field at offset 10. That
// looks like a contradiction and is not: his hardware gallery recorded E as
// "LAYOUTE" on the LEFT (the 7-char field, from offset 10) and "E1" on the
// RIGHT (the 3-char field, from offset 7). So the descriptor lists fields
// LEFT TO RIGHT AS DRAWN, not in payload order, and E is the only layout where
// those two differ, so it is the only one that could have shown it. Do not
// use descriptor order to infer payload offsets.
//
// The descriptors describe PAYLOAD capacity, not how a field is drawn. Layout
// 7's second field is one 10-character field here and still draws in two
// zones: first character left, the rest right-aligned. See
// OledDashController's header; that is a renderer rule, not a payload one.
//
// SAFETY: this rides the SAME HID++ pipe as the rim rev lights, and the same
// contention rule applies (section 12.5 of that spec, our own finding): while
// any force is present on the HID++ endpoint, a non-force write to it cuts the
// force, regardless of sender count or pacing.
//
// TESTED FOR THIS FEATURE, NOT ASSUMED (2026-08-06, G PRO). Driving under
// Telemetry Based FFB with the screen live showed no force degradation. With
// the gate deliberately lifted (the OLEDANY code) in a game running its own
// force feedback, THE FORCE CUT OUT. So the restriction is real and permanent,
// not inherited caution: OLED writes are only safe when the wheel's force is
// carried on the Trueforce ep3 stream and HID++ is kept force-free, i.e. Mode B
// with the game's own FFB PROVEN quiet by the tap. Writing with no game running
// is also safe, because there is no force anywhere to cut.
//
// The caller owns that gate; this class just refuses to write when nothing has
// been handed to it.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using HidSharp;

namespace TrueforceForAll.Core
{
    /// <summary>The firmware's fixed layout vocabulary, A..J = index 0..9.</summary>
    public enum OledLayout
    {
        Blank = 0,   // A
        Test = 1,   // B
        Gauge = 2,   // C
        GaugeLabel = 3,   // D
        GaugeTwoText = 4,   // E
        SmallBig = 5,   // F
        BigSmall = 6,   // G
        TwoRow = 7,   // H
        FourRowRight = 8,   // I
        FourRowCenter = 9,   // J
    }

    public sealed class WheelOledChannel : IDisposable
    {
        // HID++ report IDs and their on-wire total lengths (incl. report ID).
        private const byte RepShort = 0x10; private const int LenShort = 7;
        private const byte RepLong = 0x11; private const int LenLong = 20;
        private const byte RepVeryLong = 0x12; private const int LenVeryLong = 64;

        private const byte DevWired = 0xFF;   // HID++ device index for a wired device
        private const byte RootIndex = 0x00;   // HID++ IRoot feature is always index 0

        private const ushort PageDisplay = 0x8130; // DisplayGameData
        private const byte SwId = 0x0A;   // fn-byte sw-id nibble (PeposCJ's captures)

        private const byte FnLayoutCount = 0x00;
        private const byte FnLayoutDescriptor = 0x01;
        private const byte FnClearPending = 0x02;
        private const byte FnSetLayout = 0x03;

        // Only these chassis have the Dynamic OLED. The G923 has no screen, so
        // don't even enumerate it: a probe there is pure HID++ noise on a pipe
        // we are trying to keep quiet.
        private static readonly ushort[] OledPids =
        {
            0xC272,   // G PRO Racing Wheel (Xbox/PC)
            0xC268,   // G PRO Racing Wheel (PS/PC)
            0xC276,   // RS50
        };

        // Pacing. The rev lights' 160 ms floor exists to avoid starving a
        // GAME's force on the shared HID++ pipe; this channel only ever runs
        // when that pipe is force-free (Mode B) or nothing is running at all,
        // so that floor is not automatically ours. The 200 ms we started with
        // was caution, not measurement, and it looks like low frame rate on a
        // scrolling row. Tunable so the real ceiling can be found on hardware
        // rather than guessed: SenderLoop ticks fine enough to honor it.
        //
        // Every write draws an acknowledgement (~2.4-3.1 ms observed), so the
        // bus cost is roughly 3 ms per frame: about 3% duty at 10 Hz, 9% at
        // 30 Hz. Note that RATE was never what starved force in the rev-light
        // case: LED traffic existing AT ALL alongside a game's own FFB was.
        // With the pipe force-free, rate buys smoothness and costs only duty.
        //
        // This interval is also the resend period, not merely a change floor:
        // see SenderLoop for why a still frame still has to be sent.
        private const int ChangeMinFloorMs = 20;
        private const int ChangeMinCeilMs = 1000;
        private const int SenderTickMs = 5;

        private volatile int _changeMinMs = 100;

        /// <summary>Minimum gap between writes, in milliseconds. Lower is
        /// smoother and costs more of the shared HID++ pipe.</summary>
        public int WriteIntervalMs
        {
            get { return _changeMinMs; }
            set
            {
                int v = value < ChangeMinFloorMs ? ChangeMinFloorMs
                      : value > ChangeMinCeilMs ? ChangeMinCeilMs : value;
                _changeMinMs = v;
            }
        }

        private readonly Action<string> _log;
        private readonly object _io = new object();

        // One open stream per HID++ report size (see WheelLedChannel's header
        // for why a report ID is only valid on its own collection).
        private HidStream _short, _long, _veryLong;
        private string _devName;

        // Where SHORT-form HID++ commands (getFeature, fn0, fn2) are written.
        private HidStream _cmdShort;
        private int _cmdShortLen;
        private byte _cmdShortRepId;

        private byte _idxDisplay;      // resolved feature index of page 0x8130
        private int _layoutCount = -1; // what fn0 reported, -1 = never answered
        private bool _ready;

        // The 64-byte setter the sender thread should be pushing, or null for
        // "nothing to show". Swapped as a whole immutable array, never mutated
        // in place, so the volatile read on the sender thread is safe.
        private volatile byte[] _pending;
        private byte[] _sent;
        private long _lastWriteMs;
        private Thread _txThread;
        private volatile bool _txStop;

        public bool IsReady => _ready;
        /// <summary>Still on the pipe: either holding a frame up, or running
        /// the sender thread that would. Lets a caller that has already let the
        /// screen go with Release() still know there is a thread to tear down.</summary>
        public bool IsHolding => _txThread != null || _pending != null;
        public int LayoutCount => _layoutCount;
        public string ResolvedInfo =>
            _ready
                ? $"dispFeat=0x{_idxDisplay:X2}"
                  + (_layoutCount >= 0 ? $" layouts={_layoutCount}" : "")
                  + $" via {_devName}"
                : "(not resolved)";

        public WheelOledChannel(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        // ---- Discovery + feature resolution ---------------------------------

        /// <summary>Find a wheel that has an OLED, group its HID++ sibling
        /// collections by report size, open them, and resolve the page 0x8130
        /// feature index. Idempotent; returns true once a channel is live.</summary>
        public bool OpenAndResolve()
        {
            lock (_io)
            {
                if (_ready) return true;

                var groups = new Dictionary<string, List<HidDevice>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var list = DeviceList.Local;
                    foreach (ushort pid in OledPids)
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
                            _log($"[OLED] candidate: pid=0x{pid:X4} maxOut={SafeOutLen(dev)} path={path}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[OLED] enumeration failed: {ex.Message}");
                    return false;
                }

                if (groups.Count == 0)
                {
                    _log("[OLED] no wheel with a Dynamic OLED found (G PRO / RS50 only).");
                    return false;
                }

                // One HID++ probe on the wire at a time (see HidppProbeGate):
                // racing the rev-light probe at startup cost whichever lost.
                lock (WheelDiscovery.HidppProbeGate)
                {
                    foreach (var kv in groups)
                        if (TryGroup(kv.Key, kv.Value)) return true;
                }

                _log("[OLED] probed all interface groups; none answered HID++ getFeature for 0x8130.");
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
                        _log($"[OLED] open refused ({ex.Message}): {dev.DevicePath}");
                        continue;
                    }
                    s.ReadTimeout = 250;
                    s.WriteTimeout = 250;
                    opened.Add(s);

                    if (outLen == LenShort && shortS == null) { shortS = s; _devName = dev.GetFriendlyName(); }
                    else if (outLen == LenLong && longS == null) { longS = s; if (_devName == null) _devName = dev.GetFriendlyName(); }
                    else if (outLen == LenVeryLong && veryS == null) { veryS = s; if (_devName == null) _devName = dev.GetFriendlyName(); }
                }

                // The setter is a 64-byte report, so unlike the rev lights this
                // channel is useless without a VERY_LONG collection.
                if (veryS == null)
                {
                    _log($"[OLED] group has no 64-byte VERY_LONG collection: {stem}");
                    DisposeAll(opened); return false;
                }
                if (shortS == null && longS == null)
                {
                    _log($"[OLED] group has no SHORT or LONG command collection: {stem}");
                    DisposeAll(opened); return false;
                }

                _short = shortS; _long = longS; _veryLong = veryS;
                if (shortS != null) { _cmdShort = shortS; _cmdShortLen = LenShort; _cmdShortRepId = RepShort; }
                else { _cmdShort = longS; _cmdShortLen = LenLong; _cmdShortRepId = RepLong; }

                byte idx = TryGetFeature(PageDisplay);
                if (idx == 0)
                {
                    _log($"[OLED] no HID++ reply for 0x8130 in group {stem}");
                    DisposeAll(opened); ClearStreams(); return false;
                }

                _idxDisplay = idx;
                _ready = true;

                // Dispose opened-but-unassigned duplicates so they don't leak
                // (mirrors WheelLedChannel; a non-standard topology can expose
                // two collections of the same report size).
                foreach (var s in opened)
                    if (s != _short && s != _long && s != _veryLong)
                        try { s.Dispose(); } catch { }

                // fn0 = layout count. Purely informational: it confirms the
                // feature is the display we think it is and tells the log which
                // layouts this firmware admits to. A timeout is not fatal.
                _layoutCount = TryReadLayoutCount();
                _log($"[OLED] resolved {ResolvedInfo}  (short/long/vlong = "
                     + $"{_short != null}/{_long != null}/{_veryLong != null})");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[OLED] group probe error ({stem}): {ex.Message}");
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

        private byte Fn(int fn) => (byte)((fn << 4) | SwId);

        /// <summary>HID++ root getFeature(pageId). Returns the resolved feature
        /// index, or 0 if no usable reply.</summary>
        private byte TryGetFeature(ushort pageId)
        {
            byte fn = Fn(0);   // root getFeature, our sw-id
            var req = new byte[LenShort];
            req[0] = _cmdShortRepId; req[1] = DevWired; req[2] = RootIndex; req[3] = fn;
            req[4] = (byte)(pageId >> 8); req[5] = (byte)(pageId & 0xFF); req[6] = 0x00;

            try { _cmdShort.Write(PadShort(req)); }
            catch (Exception ex) { _log($"[OLED] getFeature write failed: {ex.Message}"); return 0; }

            // Replies land on a bigger collection than the request. Read the
            // 64-byte one first: this feature's own traffic lives there, and on
            // a wheel with no dedicated SHORT collection it is the only reply
            // path. We match on the FULL function byte (sw-id included) so a
            // rev-light reply in flight on the shared pipe can never be
            // mistaken for ours.
            var replyOrder = _short != null
                ? new[] { _veryLong, _long, _short }
                : new[] { _veryLong, _long };
            foreach (var s in replyOrder)
            {
                byte idx = ReadFeatureReply(s, fn, pageId);
                if (idx == 0xFF) return 0;
                if (idx != 0) return idx;
            }
            return 0;
        }

        private byte ReadFeatureReply(HidStream s, byte fn, ushort pageId)
        {
            if (s == null) return 0;
            bool timedOut = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] resp = new byte[LenVeryLong];
                int n;
                try { n = s.Read(resp, 0, resp.Length); }
                catch (TimeoutException)
                {
                    // A single late reply (another HID++ talker such as G HUB
                    // mid-transaction) must not read as "feature absent".
                    if (timedOut) return 0;
                    timedOut = true; continue;
                }
                catch (Exception ex) { _log($"[OLED] getFeature read failed: {ex.Message}"); return 0; }
                if (n < 5) continue;
                if (resp[1] != DevWired) continue;
                // HID++ error: 0xFF in the feature slot, our request echoed after.
                if (resp[2] == 0xFF && n >= 7 && resp[4] == RootIndex && resp[5] == fn)
                {
                    _log($"[OLED] HID++ error 0x{resp[6]:X2} for 0x{pageId:X4}");
                    return 0xFF;
                }
                if (resp[2] != RootIndex || resp[3] != fn) continue;
                byte idx = resp[4];
                if (idx != 0 && idx < 0xFF) return idx;
                // Feature index 0 means "this firmware does not have the page".
                if (idx == 0) return 0xFF;
            }
            return 0;
        }

        /// <summary>fn0 = layout count. Informational; -1 when it never answered.</summary>
        private int TryReadLayoutCount()
        {
            byte fn = Fn(FnLayoutCount);
            try
            {
                var req = new byte[LenShort];
                req[0] = _cmdShortRepId; req[1] = DevWired; req[2] = _idxDisplay; req[3] = fn;
                _cmdShort.Write(PadShort(req));
            }
            catch (Exception ex) { _log($"[OLED] layout-count write failed: {ex.Message}"); return -1; }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] resp = new byte[LenVeryLong];
                int n;
                try { n = _veryLong.Read(resp, 0, resp.Length); }
                catch (TimeoutException) { return -1; }
                catch (Exception) { return -1; }
                if (n < 5) continue;
                if (resp[1] != DevWired || resp[2] != _idxDisplay || resp[3] != fn) continue;
                return resp[4];
            }
            return -1;
        }

        // ---- Layout table (diagnostic) ---------------------------------------

        /// <summary>Ask the wheel what layouts it actually has: fn0 for the
        /// count, then fn1 for each index. This is the same pair Logitech's own
        /// driver issues before it will answer a layout-support query, and fn1
        /// returns a six-byte descriptor per layout.
        ///
        /// The point is evidence. Everything known about these layouts comes
        /// from captures of ONE RS50, and a G PRO has already been seen drawing
        /// index 7 differently. Whether that is a different table, a different
        /// geometry, or a misread of the original, the wheel itself can say.
        /// Raw bytes are logged undecoded on purpose: guessing at the
        /// descriptor's meaning is how the current confusion started.</summary>
        public string ReadLayoutTable()
        {
            if (!_ready) return "(channel not open)";
            lock (_io)
            {
                int count = _layoutCount >= 0 ? _layoutCount : TryReadLayoutCount();
                _log($"[OLED] layout count = {(count < 0 ? "(no reply)" : count.ToString())}");
                if (count <= 0 || count > 32) return "layout count = " + count;

                for (int i = 0; i < count; i++)
                {
                    // fn1 echoes the layout index, so insist on it: see Request.
                    byte[] reply = Request(FnLayoutDescriptor, (byte)i, expectEcho: true);
                    // ONE log call per layout. A single multi-line string loses
                    // everything after the first newline in SimHub's logger,
                    // which silently swallowed this whole table once already.
                    if (reply == null)
                    {
                        _log($"[OLED] layout {i} ({(char)('A' + i)}): (no reply)");
                        continue;
                    }
                    // Whole reply, undecoded: bytes 0-3 are the HID++ header and
                    // the descriptor follows, but guessing at its meaning is how
                    // the confusion this exists to settle got started.
                    var sb = new StringBuilder();
                    sb.Append($"[OLED] layout {i} ({(char)('A' + i)}): ");
                    for (int b = 0; b < 20 && b < reply.Length; b++)
                        sb.Append(reply[b].ToString("X2")).Append(' ');
                    _log(sb.ToString());
                }
                return $"layout count = {count} (per-layout descriptors logged)";
            }
        }

        /// <summary>Ask this feature about functions nobody has documented.
        /// Only fn0-fn3 are known (count, descriptor, clear, set), and the
        /// panel keeps reverting to the wheel's own view between our writes,
        /// which is what a display we FEED but never CLAIM would do. The rev
        /// lights needed an arm sequence before their stream would hold; if
        /// there is an equivalent here it is behind one of these.
        ///
        /// Writes each function with zero parameters and dumps every reply raw,
        /// errors included: an HID++ error is itself an answer, since it says
        /// the function exists but wanted something else. Diagnostic only.</summary>
        public void ProbeFunctions(int firstFn, int lastFn)
        {
            if (!_ready) { _log("[OLED] probe: channel not open"); return; }
            lock (_io)
            {
                for (int fn = firstFn; fn <= lastFn; fn++)
                {
                    byte fnByte = Fn(fn);
                    try
                    {
                        var req = new byte[LenShort];
                        req[0] = _cmdShortRepId; req[1] = DevWired;
                        req[2] = _idxDisplay; req[3] = fnByte;
                        _cmdShort.Write(PadShort(req));
                    }
                    catch (Exception ex)
                    {
                        _log($"[OLED] probe fn{fn}: write failed ({ex.Message})");
                        continue;
                    }

                    bool any = false;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        var resp = new byte[LenVeryLong];
                        int n;
                        try { n = _veryLong.Read(resp, 0, resp.Length); }
                        catch (TimeoutException) { break; }
                        catch (Exception) { break; }
                        if (n < 4) continue;
                        var sb = new StringBuilder($"[OLED] probe fn{fn}: ");
                        for (int b = 0; b < 16 && b < n; b++)
                            sb.Append(resp[b].ToString("X2")).Append(' ');
                        // 0xFF in the feature slot is HID++'s "that request was
                        // wrong", with the offending feature/function echoed.
                        if (resp[2] == 0xFF) sb.Append(" <- HID++ error");
                        _log(sb.ToString());
                        any = true;
                        break;
                    }
                    if (!any) _log($"[OLED] probe fn{fn}: no reply");
                }
            }
        }

        /// <summary>Send a SHORT request on this feature and return the first
        /// matching VERY_LONG reply, or null.</summary>
        private byte[] Request(byte fnId, byte param0, bool expectEcho = false)
        {
            byte fn = Fn(fnId);
            try
            {
                var req = new byte[LenShort];
                req[0] = _cmdShortRepId; req[1] = DevWired; req[2] = _idxDisplay;
                req[3] = fn; req[4] = param0;
                _cmdShort.Write(PadShort(req));
            }
            catch (Exception ex) { _log($"[OLED] fn{fnId} write failed: {ex.Message}"); return null; }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                var resp = new byte[LenVeryLong];
                int n;
                try { n = _veryLong.Read(resp, 0, resp.Length); }
                catch (TimeoutException) { return null; }
                catch (Exception) { return null; }
                if (n < 5) continue;
                if (resp[1] != DevWired || resp[2] != _idxDisplay || resp[3] != fn) continue;
                // Match the ECHOED parameter too. Matching only the header let a
                // late reply from the previous request satisfy this one, so a
                // table read after two timeouts came back shifted: request 3
                // returned layout 0's descriptor, request 4 returned layout 2's.
                // The descriptors were sound, they were just answering the
                // wrong question.
                if (expectEcho && resp[4] != param0) continue;
                return resp;
            }
            return null;
        }

        /// <summary>A setter whose every writable byte is a different printable
        /// character, so whatever the panel draws reads back as the payload
        /// OFFSET each field starts at. This measures a layout's field
        /// boundaries directly instead of inferring them: if a field shows
        /// "VWX", it begins at the offset those letters sit at below.
        ///
        /// Offsets 5..63 map to: 5='A' 6='B' ... 30='Z' 31='0' ... 40='9'
        /// 41='a' 42='b' ... 63='w'.</summary>
        public byte[] BuildRuler(OledLayout layout)
        {
            const string alphabet =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvw";
            var r = NewSetter(layout);
            for (int i = 5; i < LenVeryLong && (i - 5) < alphabet.Length; i++)
                r[i] = (byte)alphabet[i - 5];
            return r;
        }

        /// <summary>Which payload offset a ruler character came from, so a
        /// reported string can be turned back into offsets without counting on
        /// your fingers.</summary>
        public static int RulerOffsetOf(char c)
        {
            const string alphabet =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvw";
            int i = alphabet.IndexOf(c);
            return i < 0 ? -1 : i + 5;
        }

        // ---- Frame building --------------------------------------------------

        // Every setter is the same 5-byte header plus a layout-specific,
        // zero-padded payload in a 64-byte VERY_LONG report.
        private byte[] NewSetter(OledLayout layout)
        {
            var r = new byte[LenVeryLong];
            r[0] = RepVeryLong;
            r[1] = DevWired;
            r[2] = _idxDisplay;
            r[3] = Fn(FnSetLayout);
            r[4] = (byte)layout;
            return r;
        }

        // Left-align into a fixed-width field, zero-padded. Characters outside
        // the firmware's ASCII range become spaces rather than failing the
        // whole frame: a stray degree sign in a car name should not blank the
        // screen. Over-long text is truncated, not rejected.
        private static void WriteField(byte[] dst, int offset, int width, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int n = Math.Min(width, text.Length);
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                dst[offset + i] = (byte)(c >= 0x20 && c <= 0x7F ? c : ' ');
            }
        }

        private static byte Gauge01(double v)
        {
            if (double.IsNaN(v) || v <= 0) return 0;
            if (v >= 1) return 255;
            return (byte)(v * 255.0 + 0.5);
        }

        /// <summary>C: one horizontal gauge, 0..1.</summary>
        public byte[] BuildGauge(double fill)
        {
            var r = NewSetter(OledLayout.Gauge);
            r[5] = Gauge01(fill);
            return r;
        }

        /// <summary>D: gauge + thin indicator + one 11-char label.</summary>
        public byte[] BuildGaugeLabel(double fill, double thin, string label)
        {
            var r = NewSetter(OledLayout.GaugeLabel);
            r[5] = Gauge01(fill);
            r[6] = Gauge01(thin);
            WriteField(r, 7, 11, label);
            return r;
        }

        /// <summary>E: gauge + thin indicator + a 3-char and a 7-char field.</summary>
        public byte[] BuildGaugeTwoText(double fill, double thin, string right3, string left7)
        {
            var r = NewSetter(OledLayout.GaugeTwoText);
            r[5] = Gauge01(fill);
            r[6] = Gauge01(thin);
            WriteField(r, 7, 3, right3);
            WriteField(r, 10, 7, left7);
            return r;
        }

        /// <summary>F: medium 1-char then VERY LARGE 3-char.</summary>
        public byte[] BuildSmallBig(string one, string three) =>
            BuildTwoText(OledLayout.SmallBig, one, 1, three, 3);

        /// <summary>G: VERY LARGE 1-char then medium 3-char. The classic
        /// gear-and-speed driving screen.</summary>
        public byte[] BuildBigSmall(string one, string three) =>
            BuildTwoText(OledLayout.BigSmall, one, 1, three, 3);

        /// <summary>H: small 21-char row over a larger 10-char row.</summary>
        public byte[] BuildTwoRow(string top21, string bottom10) =>
            BuildTwoText(OledLayout.TwoRow, top21, 21, bottom10, 10);

        private byte[] BuildTwoText(OledLayout layout, string a, int aw, string b, int bw)
        {
            var r = NewSetter(layout);
            WriteField(r, 5, aw, a);
            WriteField(r, 5 + aw, bw, b);
            return r;
        }

        /// <summary>I: four rows 19/10/19/10, rows 2+4 larger, right-aligned.</summary>
        public byte[] BuildFourRowRight(string a19, string b10, string c19, string d10) =>
            BuildFourText(OledLayout.FourRowRight, a19, b10, c19, d10);

        /// <summary>J: four rows 19/10/19/10, rows 2+4 larger, centered.</summary>
        public byte[] BuildFourRowCenter(string a19, string b10, string c19, string d10) =>
            BuildFourText(OledLayout.FourRowCenter, a19, b10, c19, d10);

        private byte[] BuildFourText(OledLayout layout, string a, string b, string c, string d)
        {
            var r = NewSetter(layout);
            WriteField(r, 5, 19, a);
            WriteField(r, 24, 10, b);
            WriteField(r, 34, 19, c);
            WriteField(r, 53, 10, d);
            return r;
        }

        // ---- Send ------------------------------------------------------------

        /// <summary>Hand the channel the frame it should be holding on screen.
        /// Does NOT write here: the sender thread does, at a fixed cadence, so
        /// a fast-changing speed readout can never burst the shared HID++ pipe.
        /// Build the payload with one of the Build* helpers.</summary>
        public void Show(byte[] setter)
        {
            if (!_ready || setter == null || setter.Length != LenVeryLong) return;
            _pending = setter;
            EnsureSender();
        }

        private void EnsureSender()
        {
            if (_txThread != null) return;
            lock (_io)
            {
                if (_txThread != null || !_ready) return;
                _txStop = false;
                _txThread = new Thread(SenderLoop)
                { IsBackground = true, Name = "OledSender" };
                _txThread.Start();
            }
        }

        // The ONLY thing that writes setters. Fixed cadence, never bursts: one
        // write per interval for as long as there is anything to show.
        private void SenderLoop()
        {
            while (!_txStop)
            {
                Thread.Sleep(SenderTickMs);
                if (_txStop || !_ready) continue;

                var want = _pending;
                if (want == null) continue;

                // Resend UNCONDITIONALLY at the write interval, changed or not.
                // The firmware drops the Dynamic surface back to the wheel's
                // own view when the host goes quiet, so a frame that holds
                // still is not a frame we can stop sending: an earlier build
                // only refreshed a static frame every two seconds and the panel
                // visibly flickered back to the menu between refreshes, which
                // also cut the four-second lap card short. Whatever the
                // firmware's timeout is, it is shorter than that, so the cheap
                // fix is to never test it.
                long now = NowMs();
                if ((now - _lastWriteMs) < _changeMinMs) continue;

                lock (_io)
                {
                    if (!_ready || _txStop) continue;
                    try
                    {
                        _veryLong.Write(want);
                        _sent = want;
                        _lastWriteMs = NowMs();
                    }
                    catch (Exception ex)
                    {
                        _log($"[OLED] sender failed: {ex.Message}");
                        _ready = false;   // force a re-probe on the next OpenAndResolve()
                    }
                }
            }
        }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>Hand the panel back to the wheel's own view while KEEPING
        /// the sender thread, so taking the screen again costs a field write
        /// rather than a thread start. A caller that shows only occasional
        /// takeovers gives the screen back many times a lap, and Clear()'s
        /// teardown at that rate is thread churn buying nothing.
        ///
        /// The fn2 is what makes the handback immediate. Going quiet alone
        /// would also do it, but only after the firmware's own timeout, which
        /// is unknown beyond being under two seconds (see SenderLoop), and a
        /// takeover that hangs on for a second after its moment is worse than
        /// one that never happened.
        ///
        /// Idempotent: _sent records whether anything is actually on the panel,
        /// so a second call puts nothing on the wire.</summary>
        public void Release()
        {
            _pending = null;
            if (!_ready) return;
            lock (_io)
            {
                try
                {
                    if (_sent != null)
                    {
                        var req = new byte[LenShort];
                        req[0] = _cmdShortRepId; req[1] = DevWired;
                        req[2] = _idxDisplay; req[3] = Fn(FnClearPending);
                        _cmdShort.Write(PadShort(req));
                    }
                }
                catch (Exception ex) { _log($"[OLED] clear failed: {ex.Message}"); }
                _sent = null;
            }
        }

        /// <summary>Release the panel AND stop the sender, so we are off the
        /// HID++ pipe entirely. Called whenever the Mode B gate closes, so the
        /// screen never freezes on a stale frame and nothing of ours is left
        /// running against a pipe a game may be about to use.</summary>
        public void Clear()
        {
            _txStop = true;
            var t = _txThread; _txThread = null;
            try { t?.Join(300); } catch { }
            Release();
        }

        // Pad a SHORT (7-byte) payload up to the command stream's output length.
        private byte[] PadShort(byte[] r)
        {
            if (_cmdShortLen <= r.Length) return r;
            var padded = new byte[_cmdShortLen];
            Array.Copy(r, padded, r.Length);
            return padded;
        }

        public void Dispose()
        {
            try { Clear(); } catch { }
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
