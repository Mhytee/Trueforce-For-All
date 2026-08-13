// Drives the Logitech G PRO (and RS50, and G923 Xbox) wheel rim's rev/shift
// LEDs over HID++. The G923 Xbox uses the SAME 0x807A feature and level pair;
// it just exposes no 7-byte SHORT collection, so its SHORT-form commands ride
// the 20-byte LONG collection padded to 20B with report id 0x11 (see the
// _cmdShort fields). The G923 PlayStation variant is different: it uses the
// legacy F8-12 report, handled by LegacyLedF8Channel, not this class.
//
// Protocol decoded 2026-05-16 from a USBPcap capture of the rev lights being
// driven in a game on a G PRO (captures/gpro_leds.pcap; replies decoded
// 2026-08-11 by fn2_effect_probe.py). G HUB was CLOSED for that capture: the
// writer is Logitech's driver stack on its own, so do not describe this as
// "G HUB's sequence". mescon's RS50 RGB-zone model (page 0x807B, per-LED RGB,
// 6-step apply) does NOT apply to the G PRO. The G PRO is LEVEL-based, and
// mescon's PROTOCOL_SPECIFICATION.md section 9.3.2 names the functions:
//
//   * Feature page 0x807A (resolved via HID++ root getFeature; this wheel =
//     index 0x09, but it varies per wheel/firmware so always resolve it).
//   * Function-byte software-ID nibble = 0x0d (what the captured host uses).
//   * fn0 = GET_INFO. Reply params: [version, STRIP LENGTH, length again, ..].
//     The strip length is 0x0a on the G PRO/RS50 and 0x05 on a G923 (mescon,
//     issue #20, 2026-08-11): a G923 has ten LEDs but lights them outside-in
//     in fixed symmetric pairs, so only five steps are addressable. The level
//     is a fraction of THIS length, so we read it instead of assuming 10.
//   * fn1 = GET_CAPS (the supported-effect list; we send it for burst parity
//     and ignore the reply).
//   * fn2 = GET_STATE. Reply param 0 = the live effect. 0 = displaying
//     nothing, and in that state the wheel REFUSES every fn6 level with
//     HID++ error 5 (LogitechInternal) - dark lights that look like success
//     to a writer that never reads replies, which we were until 2026-08-11.
//   * fn3 = SET_EFFECT. The captured host sends `fn3 param 0x02` only after
//     fn2 answered 0 (our capture); with the wheel already displaying, no
//     fn3 is sent (PeposCJ's first-party captures, spec 12.4). mescon's RS50
//     result says an unconditional fn3 stomps the user's chosen effect, so
//     we now query first and skip it when the wheel is already lit.
//   * Per update + keepalive: SHORT fn2 `10 ff IDX 2d 00 00 00` then LONG
//     fn6 `11 ff IDX 6d 00 01 00 <len> 00 LL 00..` where byte 7 = strip
//     length and byte 9 (LL) = rev level 0..len = how many steps light. The
//     captured host resends this pair continuously even when LL is
//     unchanged; the wheel's onboard profile owns the colors / direction /
//     scaling, so there is NO RGB or per-LED control here, only the level.
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
        private const byte SwId            = 0x0D;   // fn-byte sw-id nibble of the captured host

        /// <summary>Default / maximum strip length. The real per-wheel length
        /// comes from fn0 at resolve time (10 on G PRO/RS50, 5 on a G923);
        /// use <see cref="StripLength"/> once the channel is ready.</summary>
        public const int LedCount = 10;

        // A game's HID++ FFB and these LEDs share ONE control pipe into a
        // single command processor on the wheel. The captured Logitech host
        // resends the level pair ~every 156 ms, but it isn't fighting a sim's
        // ~250-500 Hz FFB stream for that pipe; we are. Resending that fast
        // starved FFB (it cut in/out and the soft endstop snapped). The wheel
        // holds the level fine for far longer, so we keep alive only ~1 Hz,
        // and skip even that whenever a real change-write already refreshed
        // it within the interval.
        private const int KeepAliveMs = 1000;
        private const int ArmGapMs    = 4;          // pace the one-time arm burst
        // Minimum gap between level-pair writes. The captured host sends the
        // pair at a STEADY ~156 ms regardless of how fast revs change; it
        // never bursts. Sending immediately on every level change (which
        // happens rapidly near the shift point) delayed the game's FFB
        // packets on the shared HID++ pipe enough that the wheel decayed
        // force -> "FFB goes limp when the lights come on". One fixed-cadence
        // sender, no bursts, matches that proven-safe footprint.
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
        private HidStream _replyStream;     // the collection that carried the getFeature reply
        private volatile int _stripLen = LedCount;  // fn0's strip length (10 G PRO/RS50, 5 G923)

        // Effect selection. The wheel has ONE pattern selector (the same one
        // its base menu shows); we read it (fn2) and set it (fn3 + commit +
        // fn7) deliberately, and NEVER revert it: a pick is exactly as real
        // as using the base's own menu. _knownSelection is the last state we
        // read or wrote (0 = not seen yet / displaying nothing);
        // _pendingSelect is a pick made while the pipe wasn't writable,
        // applied at the next arm.
        private volatile int _knownSelection;
        private volatile int _pendingSelect;
        private bool _ready;
        private volatile bool _armed;
        private volatile int  _level;       // target rev level 0..StripLength (set by SetLevel)
        private int  _sentLevel = -1;       // last level actually written to the wire
        private long _lastWriteMs;          // last time the level pair hit the wire
        private Thread _hbThread;
        private volatile bool _hbStop;

        public bool IsReady => _ready;

        /// <summary>Addressable rev steps on this wheel, from fn0 (GET_INFO):
        /// 10 on the G PRO/RS50, 5 on a G923. <see cref="LedCount"/> until the
        /// wheel has answered. The fn6 level is a fraction of THIS.</summary>
        public int StripLength => _stripLen;

        public string ResolvedInfo =>
            _ready ? $"revFeat=0x{_idxRev:X2} strip={_stripLen} via {_devName}" : "(not resolved)";

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

                // One HID++ probe on the wire at a time (see HidppProbeGate):
                // racing the OLED probe at startup cost whichever lost.
                lock (WheelDiscovery.HidppProbeGate)
                {
                    foreach (var kv in groups)
                        if (TryGroup(kv.Key, kv.Value)) return true;
                }

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

                // fn0 (GET_INFO) param 1 is the strip length: 10 on the
                // G PRO/RS50, 5 on a G923 (mescon, 2026-08-11). The fn6 level
                // is a fraction of it, so a G923 sent our old hardcoded
                // 0x0a/0..10 was out of range above level 5. Read it once
                // here (probe gate still held, reply stream warm). On any
                // failure keep the 10 default, which is the old behaviour.
                var info = new byte[LenVeryLong];
                if (TryQuery(Fn(0), 0x00, info))
                {
                    int sl = StripLengthFromInfo(info, info.Length);
                    if (sl > 0) _stripLen = sl;
                    else _log($"[RPM-LED] fn0 strip length out of range "
                              + $"({info[5]}); assuming {LedCount}");
                }
                else _log($"[RPM-LED] fn0 gave no reply; assuming a {LedCount}-step strip");

                // Seed the known selection (fn2 = GET_STATE) so the settings
                // picker can show where the wheel actually is from the first
                // open, not only after an arm has read it.
                if (TryQuery(Fn(2), 0x00, info) && info[4] > 0)
                    _knownSelection = info[4];

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

        private void ClearStreams()
        {
            _short = _long = _veryLong = _cmdShort = _replyStream = null;
            _stripLen = LedCount;
            _ready = false;
        }

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
                catch (Exception ex) { _log($"[RPM-LED] getFeature read failed: {ex.Message}"); return 0; }
                if (n < 5) continue;
                if (resp[1] != DevWired) continue;
                // HID++ error: 0xFF in the feature slot, our request echoed after.
                if (resp[2] == 0xFF && n >= 7 && resp[4] == RootIndex && resp[5] == RootGetFn)
                {
                    _log($"[RPM-LED] HID++ error 0x{resp[6]:X2} for 0x{pageId:X4}");
                    return 0xFF;
                }
                // Match the FULL function byte (sw-id included). A getFeature
                // reply does not echo the page it answers, so this is the only
                // thing tying a reply to OUR request: without it, the OLED
                // probe's reply on this shared pipe latched the display's
                // feature index as revFeat (seen 2026-08-08).
                if (resp[2] != RootIndex || resp[3] != RootGetFn) continue;
                byte idx = resp[4];
                if (idx != 0 && idx < 0x80)
                {
                    _replyStream = s;   // feature replies land here too
                    return idx;
                }
            }
            return 0;
        }

        // ---- Feature-level replies ------------------------------------------

        /// <summary>What one frame off the reply stream is, relative to a
        /// feature-level request we just made.</summary>
        internal enum FnReply
        {
            Skip,   // someone else's traffic (broadcast, another sw-id, root)
            Match,  // the reply to OUR request (device + feature + full fn byte)
            Error,  // HID++ error frame echoing our feature index
        }

        /// <summary>Classify a frame against a feature-level request. Matching
        /// needs the FULL function byte (sw-id included): replies carry no
        /// page, broadcasts carry sw-id 0, and the OLED channel's traffic on
        /// this same pipe carries sw-id 0x0A. The error shape is the one both
        /// channels have field-validated: 0xFF in the feature slot, our
        /// request echoed at [4..5], the code at [6] (5 = LogitechInternal =
        /// "no active effect, level refused"; 7 = INVALID_FUNCTION_ID).</summary>
        internal static FnReply ClassifyFnReply(byte[] resp, int n, byte featIdx, byte fnByte)
        {
            if (resp == null || n < 7) return FnReply.Skip;
            if (resp[1] != DevWired) return FnReply.Skip;
            if (resp[2] == 0xFF && resp[4] == featIdx) return FnReply.Error;
            if (resp[2] == featIdx && resp[3] == fnByte) return FnReply.Match;
            return FnReply.Skip;
        }

        /// <summary>fn0 (GET_INFO) reply params are [version, strip length,
        /// length again, ..]; params start at byte 4. Returns the strip
        /// length, or -1 when the frame is short or the value is not
        /// plausible (observed lengths: 10 = G PRO/RS50, 5 = G923).</summary>
        internal static int StripLengthFromInfo(byte[] resp, int n)
        {
            if (resp == null || n < 6) return -1;
            int len = resp[5];
            return (len >= 1 && len <= 20) ? len : -1;
        }

        /// <summary>What the arm burst does about the effect selection, given
        /// fn2's answer and any pending pick.</summary>
        internal struct ArmEffectPlan
        {
            public bool SendFn3;
            public byte Fn3Param;
            public int  WheelOwnEffect;   // fn2's answer, normalized (0 = none/unreadable)
        }

        /// <summary>Decide the arm burst's effect handling. Pure so the matrix
        /// is testable. currentEffect: fn2's answer, -1 = unreadable.
        /// pendingSelect: a pick waiting to land, 0 = none. Rules:
        ///  - pending pick not already active: select it (this also starts
        ///    a display showing nothing, since 0 != pick);
        ///  - no pick, wheel displaying nothing or unreadable: the captured
        ///    legacy fn3 param 0x02, without which a non-displaying wheel
        ///    refuses every level with HID++ error 5;
        ///  - no pick, wheel displaying: leave the selection alone.</summary>
        internal static ArmEffectPlan PlanArmEffect(int currentEffect, int pendingSelect)
        {
            var plan = new ArmEffectPlan
            { WheelOwnEffect = currentEffect > 0 ? currentEffect : 0 };
            if (pendingSelect > 0 && pendingSelect != currentEffect)
            { plan.SendFn3 = true; plan.Fn3Param = (byte)pendingSelect; }
            else if (currentEffect <= 0)
            { plan.SendFn3 = true; plan.Fn3Param = 0x02; }
            return plan;
        }

        // Drop everything queued on the reply stream (stale broadcasts, the
        // replies to writes we never read) so the next match-read starts
        // clean. Passive: nothing is written.
        private void DrainReplies(HidStream s)
        {
            if (s == null) return;
            int old = s.ReadTimeout;
            try
            {
                s.ReadTimeout = 1;
                var buf = new byte[LenVeryLong];
                for (int i = 0; i < 64; i++)
                {
                    try { if (s.Read(buf, 0, buf.Length) <= 0) break; }
                    catch { break; }
                }
            }
            finally { try { s.ReadTimeout = old; } catch { } }
        }

        // Every observed reply arrives in ~3 ms (mescon measured a ~2.5 ms
        // median ack). Arm-time reads can run on SimHub's telemetry thread,
        // so they get a bound sized to the protocol, not the probe's 250 ms.
        private const int ReplyReadMs = 80;

        /// <summary>Drain, write one SHORT-form feature request, and read its
        /// reply into <paramref name="respOut"/>. False on write failure, no
        /// usable reply, or an error frame echoing this request.</summary>
        private bool TryQuery(byte fnByte, byte param0, byte[] respOut)
        {
            var s = _replyStream;
            if (s == null) return false;
            DrainReplies(s);
            try { WriteShort(new byte[] { RepShort, DevWired, _idxRev, fnByte, param0, 0x00, 0x00 }); }
            catch (Exception ex) { _log($"[RPM-LED] query write failed: {ex.Message}"); return false; }
            return TryMatchReply(s, fnByte, respOut, maxTimeouts: 2);
        }

        /// <summary>Read frames off <paramref name="s"/> until one matches our
        /// feature + full fn byte, skipping foreign traffic. Error frames for
        /// our feature are always logged; one echoing THIS request fails it.</summary>
        private bool TryMatchReply(HidStream s, byte fnByte, byte[] respOut, int maxTimeouts)
        {
            if (s == null) return false;
            int oldTimeout = 250;
            try { oldTimeout = s.ReadTimeout; s.ReadTimeout = ReplyReadMs; } catch { }
            try
            {
                // Deadline, not an iteration count: every open handle on the
                // collection gets a copy of every input report, so foreign
                // frames (the OLED channel's replies, the wheel's broadcasts)
                // each consume a read WITHOUT a TimeoutException. An
                // iteration bound alone would let that chatter stretch a
                // telemetry-thread arm read far past its budget.
                long deadline = NowMs() + (long)ReplyReadMs * maxTimeouts;
                int timeouts = 0;
                while (NowMs() < deadline)
                {
                    var resp = new byte[LenVeryLong];
                    int n;
                    try { n = s.Read(resp, 0, resp.Length); }
                    catch (TimeoutException)
                    {
                        if (++timeouts >= maxTimeouts) return false;
                        continue;
                    }
                    catch (Exception ex) { _log($"[RPM-LED] reply read failed: {ex.Message}"); return false; }

                    switch (ClassifyFnReply(resp, n, _idxRev, fnByte))
                    {
                        case FnReply.Match:
                            Array.Copy(resp, respOut, Math.Min(respOut.Length, resp.Length));
                            return true;
                        case FnReply.Error:
                            byte echoedFn = resp[5], code = resp[6];
                            _log($"[RPM-LED] HID++ error 0x{code:X2}"
                                 + (code == 0x05 ? " (LogitechInternal: no active effect, level refused)" : "")
                                 + $" echoing fn{echoedFn >> 4:X}");
                            if (echoedFn == fnByte) return false;
                            break;
                    }
                }
                return false;
            }
            finally { try { s.ReadTimeout = oldTimeout; } catch { } }
        }

        // ---- Rev-level protocol --------------------------------------------

        private byte Fn(int fn) => (byte)((fn << 4) | SwId);

        private static void ArmGap() { try { Thread.Sleep(ArmGapMs); } catch { } }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>Run the captured Logitech arm sequence once, then start
        /// the ~1 Hz keepalive that re-sends the current level (the wheel
        /// holds it for a good while but reverts eventually if never
        /// refreshed). fn3 is CONDITIONAL: the wheel is queried first (fn2 =
        /// GET_STATE) and SET_EFFECT goes out only when it reports 0 =
        /// displaying nothing, which is the state that refuses level writes
        /// with HID++ error 5. A wheel already displaying keeps whatever
        /// effect its user chose; an unconditional fn3 would force effect 2
        /// over it (mescon's RS50 result). If the state cannot be read we
        /// fall back to SENDING fn3: that is the pre-2026-08 behaviour, and
        /// the failure mode of the opposite choice is dark lights.</summary>
        private void Arm()
        {
            if (_armed) return;
            // SHORT fn0, fn1, fn2, [fn3 param 0x02], fn0 - the captured
            // burst, byte for byte, with the fn2 reply actually read now.
            // Space the writes a few ms apart so the one-time burst doesn't
            // monopolise the shared HID++ pipe and hitch FFB at session start.
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(0), 0x00, 0x00, 0x00 });
            ArmGap();
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(1), 0x00, 0x00, 0x00 });
            ArmGap();

            int effect = -1;
            var resp = new byte[LenVeryLong];
            if (TryQuery(Fn(2), 0x00, resp)) effect = resp[4];
            ArmGap();

            var plan = PlanArmEffect(effect, _pendingSelect);
            if (effect > 0) _knownSelection = effect;
            bool sentSetEffect = false;
            if (plan.SendFn3)
            {
                if (plan.WheelOwnEffect > 0)
                {
                    // Switching a wheel that is already displaying: a bare
                    // fn3 only stages, so run the full switch (commit +
                    // refresh) or the strip keeps rendering the old pattern.
                    // Level 0: arming always starts from a dark bar.
                    ApplyEffectSwitch(plan.Fn3Param, 0);
                }
                else
                {
                    // From a dark panel the captured burst suffices; keep it
                    // byte-exact with the capture.
                    WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(3), plan.Fn3Param, 0x00, 0x00 });
                }
                ArmGap();
                sentSetEffect = true;
                _knownSelection = plan.Fn3Param;
                if (_pendingSelect > 0)
                    _log($"[RPM-LED] applying pending pattern pick: effect {plan.Fn3Param}");
                else if (effect < 0)
                    _log("[RPM-LED] fn2 state unreadable; sent SET_EFFECT unconditionally (legacy behaviour)");
            }
            else
            {
                _log($"[RPM-LED] wheel displaying effect {effect}; selection left alone");
            }
            _pendingSelect = 0;

            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(0), 0x00, 0x00, 0x00 });
            ArmGap();

            // Best-effort check on the pair's own fn2 reply: a wheel still
            // reporting 0 after the burst will refuse every level with error
            // 5 and the lights stay dark while everything here logs success.
            // TryMatchReply also surfaces any error frame (the fn6 refusal
            // included), so this is where a dead strip becomes visible.
            // Only when the state was actually READ as 0: a wheel that
            // ignored the arm-time fn2 will ignore the pair's too, and its
            // late reply (queued from before fn3) would make this check lie
            // in either direction. Drain first so the match can only be the
            // pair's own reply, not the fn3 echo / broadcast / fn0 leftovers.
            bool verify = sentSetEffect && effect == 0;
            if (verify) DrainReplies(_replyStream);
            SendPair(0);
            if (verify
                && TryMatchReply(_replyStream, Fn(2), resp, maxTimeouts: 1)
                && resp[4] == 0)
            {
                _log("[RPM-LED] wheel still reports no active effect after arming; "
                     + "level writes will be refused and the lights may stay dark");
            }

            _sentLevel = 0;
            _armed = true;

            _hbStop = false;
            _hbThread = new Thread(SenderLoop)
            { IsBackground = true, Name = "RpmLedSender" };
            _hbThread.Start();
        }

        // SHORT fn2 then LONG fn6 with byte 7 = strip length and byte 9 =
        // level. Exactly the pair in the capture; sending fn2 first matches
        // the captured ordering.
        private void SendPair(int level)
        {
            WriteShort(new byte[] { RepShort, DevWired, _idxRev, Fn(2), 0x00, 0x00, 0x00 });
            SendFn6(level);
            _lastWriteMs = NowMs();
        }

        private void SendFn6(int level)
        {
            int len = _stripLen;
            byte lvl = (byte)(level < 0 ? 0 : level > len ? len : level);
            var f6 = new byte[LenLong];
            f6[0] = RepLong; f6[1] = DevWired; f6[2] = _idxRev; f6[3] = Fn(6);
            f6[4] = 0x00; f6[5] = 0x01; f6[6] = 0x00; f6[7] = (byte)len; f6[8] = 0x00;
            f6[9] = lvl;   // 0..len = steps lit
            WriteLong(f6);
        }

        // Switch a DISPLAYING wheel to another effect. A bare fn3 only STAGES
        // the selection: observed on the G PRO 2026-08-12, fn2 starts
        // reporting the new effect while the strip keeps rendering the old
        // pattern, and the staged value evaporated once our streams closed.
        // mescon's 9.12 sequence is what actually repaints: fn3 select, fn6
        // pre-config (level 0), fn6 commit (level = strip length), fn7
        // enable/refresh. The from-nothing ARM never needed this because a
        // dark panel has no old pattern to keep (the May capture shows fn3 +
        // a level write sufficing from effect 0). Replies are read so a
        // refusal (a custom slot fn3, or fn7 missing on some firmware) is a
        // log line instead of a silent no-op. Caller holds _io.
        // The 9.12 bracket (fn6 level 0 pre-config, fn6 FULL BAR commit)
        // frames an RGB rewrite; transcribed literally for a pure pattern
        // switch it flashed every LED at each switch and again at restore
        // (owner, on-wheel, 2026-08-12). So commit at the REAL target level:
        // 0 for a preview start or a restore (dark, no flash), the current
        // revs mid-session (seamless). If a retest shows the pattern no
        // longer repaints, the full-bar commit was load-bearing after all;
        // reinstate SendFn6(0) + SendFn6(_stripLen) here before fn7.
        private void ApplyEffectSwitch(byte effect, int level)
        {
            var resp = new byte[LenVeryLong];
            if (!TryQuery(Fn(3), effect, resp))
                _log($"[RPM-LED] SET_EFFECT {effect} drew no reply; the switch may not take");
            SendFn6(level);             // commit at the level we actually want lit
            if (!TryQuery(Fn(7), 0x00, resp))
                _log("[RPM-LED] fn7 refresh drew no reply; the pattern may not repaint until re-arm");
            _lastWriteMs = NowMs();
        }

        // The ONLY thing that writes the level pair after arming. Fixed
        // cadence, never bursts: at most one pair per ChangeMinMs when the
        // target moved, plus a ~1 Hz keepalive when it's steady. This bounds
        // our HID++ pipe usage to the captured host's footprint so it doesn't
        // starve the sim's FFB.
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

        /// <summary>Set the target rev level 0..<see cref="StripLength"/>.
        /// Arms on first call. Does NOT write here, only updates the target;
        /// SenderLoop writes it at a fixed cadence so we never burst the
        /// shared HID++ pipe and starve FFB. Worst-case LED latency ~
        /// ChangeMinMs, the captured host's own cadence.</summary>
        public void SetLevel(int level)
        {
            if (!_ready) return;
            int max = _stripLen;
            if (level < 0) level = 0; else if (level > max) level = max;
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

        /// <summary>Map a 0..1 rev fill (or redline) to the wheel's level
        /// range. The wheel's onboard profile owns colors / direction; we
        /// only choose how many steps.</summary>
        public void ApplyRevBar(double pct, bool redline)
        {
            if (pct < 0) pct = 0; else if (pct > 1) pct = 1;
            int max = _stripLen;
            int lvl = redline ? max : (int)Math.Floor(pct * max + 0.5);
            SetLevel(lvl);
        }

        /// <summary>The wheel's pattern selection as last read (fn2 at arm /
        /// resolve) or written by us. 0 = not seen yet, or the wheel was
        /// displaying nothing. The base's own menu shows the same state.</summary>
        public int KnownSelection => _knownSelection;

        /// <summary>Remember a pick for the next arm, for when the pipe is
        /// not writable right now (a game running its own FFB). A later
        /// <see cref="SelectPatternNow"/> supersedes it.</summary>
        public void StagePattern(int effect)
        {
            if (effect >= 1 && effect <= 9) _pendingSelect = effect;
        }

        /// <summary>Set the wheel's pattern selection NOW, exactly like using
        /// the base's own menu: nothing ever reverts it. No-ops when the
        /// wheel is already there. Works armed (mid-session, keeps the
        /// current level lit) or merely ready (idle). Caller decides the
        /// pipe is safe to write; caller opens the channel first.</summary>
        public bool SelectPatternNow(int effect)
        {
            if (effect < 1) return false;
            if (effect > 9) effect = 9;
            if (!_ready) return false;
            lock (_io)
            {
                if (!_ready) return false;
                _pendingSelect = 0;   // this write supersedes any staged pick
                if (_knownSelection == effect) return true;
                try
                {
                    ApplyEffectSwitch((byte)effect, _armed ? _level : 0);
                    if (_armed) _sentLevel = _level;
                    _knownSelection = effect;
                    _log($"[RPM-LED] wheel selection set to effect {effect}");
                    return true;
                }
                catch (Exception ex)
                {
                    _log($"[RPM-LED] pattern select failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>Rev level to 0 (LEDs off) and stop the keepalive so the
        /// rim returns to its profile idle state.</summary>
        public void TurnOff()
        {
            // Stop/capture UNDER the lock: Arm() writes _hbStop/_hbThread
            // under _io too, so doing this outside raced a concurrent
            // first-call Arm (ForceOff on the UI thread vs SetLevel on the
            // telemetry thread) and leaked the freshly started sender thread.
            Thread t;
            lock (_io)
            {
                _hbStop = true;
                t = _hbThread; _hbThread = null;
                if (_ready)
                {
                    // Level to 0 only. The pattern SELECTION is deliberately
                    // left exactly where it is: a pick is as real as using
                    // the base's own menu, and nothing here reverts it.
                    try { if (_armed) SendPair(0); }
                    catch (Exception ex) { _log($"[RPM-LED] TurnOff failed: {ex.Message}"); }
                }
                _level = 0;
                _sentLevel = -1;
                _armed = false;
            }
            // Join OUTSIDE _io: the sender's loop body takes _io, so joining
            // while holding it would deadlock against a tick already blocked
            // on the lock.
            try { t?.Join(300); } catch { }
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
