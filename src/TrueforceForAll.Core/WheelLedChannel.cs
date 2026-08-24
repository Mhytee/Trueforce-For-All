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
//   * fn1 = GET_CAPS (the supported-effect list). No longer sent: it existed
//     only for parity with the old arm burst and its reply was ignored.
//   * fn2 = GET_STATE. Reply param 0 = the live effect. 0 = displaying
//     nothing, and in that state the wheel REFUSES every fn6 level with
//     HID++ error 5 (LogitechInternal) - dark lights that look like success
//     to a writer that never reads replies, which we were until 2026-08-11.
//   * fn3 = SET_EFFECT. The captured host sends `fn3 param 0x02` only after
//     fn2 answered 0 (our capture); with the wheel already displaying, no
//     fn3 is sent (PeposCJ's first-party captures, spec 12.4). mescon's RS50
//     result says an unconditional fn3 stomps the user's chosen effect, so
//     we query first and skip it when the wheel is already lit.
//
// WHEN, not just what. mescon's two live G HUB captures (issue #20,
// 2026-08-14: a full AC EVO session and an iRacing LED bring-up) show the
// fn0/fn1/fn2 reads happening ONCE at wheel attach and then nothing but the
// bare fn2 + fn6 level pair, with no fn3/fn4/fn7 in any session; an arm
// sequence fired mid-session killed the wheel's input path on his hardware,
// and fired at SDK session init left the session open but never streaming.
// That squares with the load-bearing fn3 above if the effect selection is
// persistent firmware state which his wheels already had set. So ALL
// discovery and any fn3 happen once in TryOpenGroup, at attach, before a
// session exists; after that this class only ever sends the fn2 + fn6 pair.
// We used to re-run the whole burst on the first rev level, which is in
// game, mid-session, the exact case he measured.
//
// THIS IS ALIGNMENT AND DEAD-TRAFFIC REMOVAL, NOT A FIX FOR A BUG WE HAVE.
// The discriminating experiment already ran here (owner rig, FH6 on a G PRO,
// 2026-08-14): with Mode B on and our self-armed ep3 stream carrying force,
// mid-drive pattern switches (fn3 + fn6 + fn7, plus a preview sweep) produced
// NO force blip. Our own ep3 stream is immune to a mid-session fn3 on
// G PRO/Windows. mescon's kill was against a NATIVE, driver-armed stream on
// his Linux stack: same endpoint and wire format, different arming path, so
// his result is not refuted by ours and ours is not explained by his. What
// the re-burst cost us was three reads whose answers TryOpenGroup already
// had, on a pipe we know is sensitive, at the worst available moment. That
// is reason enough to stop sending it, and it keeps us honest against a
// second implementation's captures, but do not write this up as a fix. The
// G923 Xbox 0x807A path is still unvalidated on hardware, and that is where
// an untested divergence would have bitten.
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
        // Per-slot RGB config (Logitech: RPM_LED_PATTERN). Where a custom
        // LIGHTSYNC slot's colours and animation direction live. Documented from
        // mescon's Linux driver, which verified it on an RS50; whether a G PRO
        // implements it is exactly what ProbeSlotFeature exists to find out.
        private const ushort PageSlotConfig = 0x807B;
        // Standard HID++ brightness control. A DEVICE-level setting, not part of a
        // slot: mescon's driver stomped users' stored profile brightness by
        // writing it during slot applies (his issue #29), which is why our slot
        // writes never touch it and this lives on its own.
        private const ushort PageBrightness = 0x8040;
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
        // Minimum gap between level-pair writes, matching the captured host,
        // which sends the pair at a STEADY ~156 ms regardless of how fast revs
        // change and never bursts.
        //
        // THE OLD REASON GIVEN HERE WAS WRONG and is recorded because it was
        // load-bearing for years. It claimed bursting LED writes starved the
        // game's FFB on the shared pipe and caused "FFB goes limp when the
        // lights come on". That is not what did it: the limp came from sending
        // FFB on the LED endpoint at all while Trueforce was driving force over
        // ep3, and it was fixed by zeroing FFB on that endpoint (owner,
        // 2026-08-24). Write RATE was never shown to be the cause.
        //
        // So this floor is now only "match what G HUB does", which is a
        // reasonable default and not a proven constraint. It costs up to ~160 ms
        // of latency on a bar that moves in ten steps, which is visible near the
        // shift point. LEDRATE<ms> changes it live so the real limit can be
        // found on hardware instead of guessed at; the default stays here until
        // someone has driven a faster one and watched the force.
        private static volatile int _changeMinMs = 160;
        public static int ChangeMinMsValue
        {
            get { return _changeMinMs; }
            set { _changeMinMs = value < 10 ? 10 : (value > 1000 ? 1000 : value); }
        }
        private static int ChangeMinMs { get { return _changeMinMs; } }

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
        // True when the open path found a dark panel (fn2 = 0) and sent the
        // fn3 that wakes it. Only then is the post-pair fn2 check meaningful.
        private bool _wokeDarkPanel;
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
                int openEffect = -1;
                if (TryQuery(Fn(2), 0x00, info)) openEffect = info[4];
                if (openEffect > 0) _knownSelection = openEffect;

                // Wake a dark panel HERE, at attach, rather than on the first
                // rev level. A wheel whose fn2 answers 0 is displaying nothing
                // and refuses every fn6 level with HID++ error 5, so the fn3
                // that starts it is load-bearing (mescon, 2026-08-11). Yet
                // mescon's two live G HUB captures (2026-08-14) contain no
                // fn3 in any session, and an arm sequence fired mid-session
                // killed the wheel's input path. Both hold if the effect
                // selection is persistent firmware state that G HUB's wheels
                // already had set, which is our reading of it. So the one fn3
                // we may still need goes out at attach, before a session
                // exists and before there is any force on the wheel to cut.
                var openPlan = PlanArmEffect(openEffect, _pendingSelect);
                if (openPlan.SendFn3)
                {
                    if (openPlan.WheelOwnEffect > 0)
                        ApplyEffectSwitch(openPlan.Fn3Param, 0);
                    else
                        WriteShort(new byte[] { RepShort, DevWired, _idxRev,
                                                Fn(3), openPlan.Fn3Param, 0x00, 0x00 });
                    _wokeDarkPanel = openEffect <= 0;
                    _knownSelection = openPlan.Fn3Param;
                    _pendingSelect = 0;
                    if (openEffect < 0)
                        _log("[RPM-LED] fn2 state unreadable at open; sent SET_EFFECT "
                             + $"{openPlan.Fn3Param} unconditionally (legacy behaviour)");
                    else if (openEffect == 0)
                        _log($"[RPM-LED] wheel displaying nothing; sent SET_EFFECT "
                             + $"{openPlan.Fn3Param} at open so level writes are accepted");
                    else
                        _log($"[RPM-LED] applying staged pattern pick at open: effect {openPlan.Fn3Param}");
                }
                else _log($"[RPM-LED] wheel displaying effect {openEffect}; selection left alone");

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
            _wokeDarkPanel = false;
            _ready = false;
            // Feature indices are per-connection. _idxRev is re-resolved on every
            // open, but these short-circuit on a cached non-zero value, so a
            // reconnect (or a different base) would otherwise send a persistent
            // 32-byte slot write to whatever feature now sits at the old index.
            _idxSlot = 0;
            _idxBrightness = 0;
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
                    int n;
                    try { n = s.Read(buf, 0, buf.Length); if (n <= 0) break; }
                    catch { break; }
                    // Frames we were going to discard anyway. Reading the wheel's
                    // own change notifications out of them costs nothing and adds
                    // no traffic; see NoteEffectBroadcast.
                    NoteEffectBroadcast(buf, n);
                }
            }
            finally { try { s.ReadTimeout = old; } catch { } }
        }

        /// <summary>Learn the wheel's selection from an UNSOLICITED effect-change
        /// notification, which is how a change made on the base's own menu reaches
        /// us. Without this our idea of the selection only ever reflects what WE
        /// last wrote, so a user turning the knob themselves is invisible.
        ///
        /// Deliberately read-only and free: these frames already arrive on the
        /// input endpoint and were being thrown away. Polling the wheel instead
        /// would put real traffic on the pipe it shares with the game's force,
        /// which is not worth it for this.
        ///
        /// Strict about what it accepts. A broadcast is our feature index with a
        /// ZERO function byte (no function nibble, no software id, which is what
        /// makes it a notification rather than a reply to something we asked), and
        /// an effect in the range the device actually uses. Anything else is left
        /// alone, because guessing here would move our idea of the selection to a
        /// slot the user is not on.</summary>
        private void NoteEffectBroadcast(byte[] frame, int n)
        {
            if (frame == null || n < 5) return;
            if (_idxRev == 0 || frame[2] != _idxRev) return;
            if (frame[3] != 0x00) return;               // a reply, not a notification
            int effect = frame[4];
            if (effect < 1 || effect > 9) return;
            if (_knownSelection == effect) return;

            _knownSelection = effect;
            _log($"[RPM-LED] wheel reports its selection changed to effect {effect}");
        }

        // Every observed reply arrives in ~3 ms (mescon measured a ~2.5 ms
        // median ack). Arm-time reads can run on SimHub's telemetry thread,
        // so they get a bound sized to the protocol, not the probe's 250 ms.
        private const int ReplyReadMs = 80;

        /// <summary>Drain, write one SHORT-form feature request, and read its
        /// reply into <paramref name="respOut"/>. False on write failure, no
        /// usable reply, or an error frame echoing this request.</summary>
        private bool TryQuery(byte fnByte, byte param0, byte[] respOut)
            => TryQueryOn(_idxRev, fnByte, param0, respOut);

        /// <summary>Same, against an arbitrary resolved feature index. Split out
        /// so a second feature page can be questioned without the rev-light
        /// index being assumed.</summary>
        private bool TryQueryOn(byte featureIdx, byte fnByte, byte param0, byte[] respOut)
        {
            var s = _replyStream;
            if (s == null || featureIdx == 0) return false;
            DrainReplies(s);
            try { WriteShort(new byte[] { RepShort, DevWired, featureIdx, fnByte, param0, 0x00, 0x00 }); }
            catch (Exception ex) { _log($"[RPM-LED] query write failed: {ex.Message}"); return false; }
            return TryMatchReply(s, featureIdx, fnByte, respOut, maxTimeouts: 2);
        }

        /// <summary>Number of custom LIGHTSYNC slots. Effects 5..9 ARE these
        /// slots (5 = CUSTOM 1), which is how a slot is selected.</summary>
        public const int CustomSlotCount = 5;

        /// <summary>One stored LIGHTSYNC slot: which way it fills and what colour
        /// each LED is.
        ///
        /// <see cref="Rgb"/> is 30 bytes in PHYSICAL LED order, LED 1 first. The
        /// wire carries it reversed (LED 10 first); that is handled inside the
        /// channel so nothing above it has to remember.</summary>
        public sealed class WheelLedSlot
        {
            public byte Slot;

            /// <summary>Device direction value, 1..4: 1 inside-out, 2 outside-in,
            /// 3 left-to-right, 4 right-to-left. The firmware rejects anything
            /// else, so callers must map their own enum onto these.</summary>
            public byte DirectionWire;

            /// <summary>3 bytes per LED, LED 1 first.</summary>
            public byte[] Rgb;

            public WheelLedSlot Clone() => new WheelLedSlot
            {
                Slot = Slot,
                DirectionWire = DirectionWire,
                Rgb = Rgb == null ? null : (byte[])Rgb.Clone(),
            };
        }

        /// <summary>Resolve the per-slot RGB config page, or 0 if this wheel does
        /// not implement it. Cached after the first successful resolve: feature
        /// indices are fixed for the life of a connection.</summary>
        private byte SlotFeatureIndex()
        {
            if (_idxSlot != 0) return _idxSlot;
            _idxSlot = TryGetFeature(PageSlotConfig);
            return _idxSlot;
        }
        private byte _idxSlot;

        /// <summary>Read a stored slot's direction and colours.
        ///
        /// Colours come back in PHYSICAL LED order (LED 1 first). The wire order
        /// is reversed (LED 10 first), and that reversal is handled here so no
        /// caller has to think about it.</summary>
        public bool TryReadSlot(int slot, out WheelLedSlot config)
        {
            config = null;
            if (slot < 0 || slot >= CustomSlotCount) return false;

            lock (_io)
            {
                if (!IsReady) return false;
                if (_stripLen != LedCount) return false;   // see TryWriteSlot
                byte idx = SlotFeatureIndex();
                if (idx == 0) return false;

                var resp = new byte[LenVeryLong];
                _lastReplyLength = 0;
                if (!TryQueryOn(idx, (byte)(0x10 | SwId), (byte)slot, resp)) return false;
                if (resp[4] != slot) { _log($"[RPM-LED] slot {slot} read: echo mismatch"); return false; }

                // REFUSE a reply too short to hold all thirty colour bytes.
                //
                // The reply buffer is 64 zeroed bytes, so a shorter frame (this
                // page's wiring is not byte-verified on every wheel, and replies
                // can land on the 20-byte LONG collection) leaves a tail of zeros
                // that looks exactly like black LEDs. Accepting that would save a
                // half-black pattern as the user's ORIGINAL, and the restore would
                // later paint their slot black. Refusing is safe: the caller
                // declines to write a slot it could not read.
                const int needed = 6 + LedCount * 3;
                if (_lastReplyLength < needed)
                {
                    _log($"[RPM-LED] slot {slot + 1} read: reply was {_lastReplyLength} bytes, "
                         + $"needs {needed}; refusing rather than reading zeros as black LEDs");
                    return false;
                }

                var rgb = new byte[LedCount * 3];
                for (int i = 0; i < LedCount; i++)
                {
                    int src = 6 + (LedCount - 1 - i) * 3;   // 4 = slot, 5 = direction, then colours
                    if (src + 2 >= resp.Length) return false;
                    rgb[i * 3 + 0] = resp[src + 0];
                    rgb[i * 3 + 1] = resp[src + 1];
                    rgb[i * 3 + 2] = resp[src + 2];
                }
                config = new WheelLedSlot { Slot = (byte)slot, DirectionWire = resp[5], Rgb = rgb };
                return true;
            }
        }

        /// <summary>Write a slot's direction and colours, then select and repaint
        /// it. Colours are given in PHYSICAL LED order and reversed onto the wire
        /// here.
        ///
        /// THIS PERSISTS ON THE WHEEL. A slot is a saved preset, so this
        /// overwrites whatever the user had stored there until something writes it
        /// back. Callers must have taken a backup first.
        ///
        /// The sequence follows mescon's driver, which drives this page on both an
        /// RS50 and a G PRO: select the slot's effect on 0x807A, a pre-config
        /// level, the colour upload on 0x807B, a commit level, then enable. The
        /// level is deliberately the CURRENT rev level rather than a full bar:
        /// committing at full length flashes every LED (observed on this wheel
        /// 2026-08-12), and our own testing showed fn7 promotes the staged effect
        /// at any level. It is floored at 1 because a level of 0 blanks the strip
        /// before the colours land, which is the bug that made mescon's uploads
        /// look like they had done nothing.</summary>
        /// <param name="displayLevel">How many steps to light once the colours
        /// land. -1 (the default) keeps the current rev level, which is what
        /// automatic use wants. Pass the strip length to show the whole pattern,
        /// which is what a human verifying a write wants.</param>
        public bool TryWriteSlot(WheelLedSlot config, int displayLevel = -1)
        {
            if (config == null || config.Rgb == null || config.Rgb.Length < LedCount * 3) return false;
            if (config.Slot >= CustomSlotCount) return false;
            byte dir = config.DirectionWire;
            if (dir < 1 || dir > 4) dir = 3;    // firmware rejects anything else

            lock (_io)
            {
                if (!IsReady) return false;
                // The payload is a fixed ten-LED, thirty-byte block. A strip that
                // is not ten steps (a G923 addresses five) would be sent a
                // wrong-sized persistent write, so refuse rather than guess.
                if (_stripLen != LedCount)
                {
                    _log($"[RPM-LED] slot write: this wheel has a {_stripLen}-step strip, "
                         + $"not {LedCount}; refusing rather than writing a wrong-sized payload");
                    return false;
                }
                if (_veryLong == null)
                {
                    // The colour upload needs the 64-byte collection. Finding out
                    // AFTER the effect switch has gone out would leave the wheel
                    // on the target slot showing its old colours.
                    _log("[RPM-LED] slot write: no 64-byte collection on this wheel");
                    return false;
                }
                byte idx = SlotFeatureIndex();
                if (idx == 0) { _log("[RPM-LED] slot write: 0x807B not present"); return false; }

                var resp = new byte[LenVeryLong];
                try
                {
                    // 1. Select this slot's effect (5 + slot) on the rev page.
                    if (!TryQuery(Fn(3), (byte)(0x05 + config.Slot), resp))
                        _log($"[RPM-LED] slot write: SET_EFFECT {5 + config.Slot} not acknowledged, continuing");

                    // 2. Pre-config level. Never 0 during the sequence itself: a
                    // level of 0 switches the strip off before the colours land,
                    // so they never appear. The level is put BACK afterwards.
                    int wanted = displayLevel >= 0 ? displayLevel : _level;
                    int lvl = Math.Max(1, Math.Min(_stripLen, wanted));
                    SendFn6(lvl);

                    // 3. The colour upload itself: [slot][direction][30 RGB, LED10 first].
                    var pkt = new byte[LenVeryLong];
                    pkt[0] = RepVeryLong; pkt[1] = DevWired; pkt[2] = idx; pkt[3] = (byte)(0x20 | SwId);
                    pkt[4] = config.Slot;
                    pkt[5] = dir;
                    for (int i = 0; i < LedCount; i++)
                    {
                        int src = (LedCount - 1 - i) * 3;
                        pkt[6 + i * 3 + 0] = config.Rgb[src + 0];
                        pkt[6 + i * 3 + 1] = config.Rgb[src + 1];
                        pkt[6 + i * 3 + 2] = config.Rgb[src + 2];
                    }
                    DrainReplies(_replyStream);
                    WriteVeryLong(pkt);
                    // Report failure rather than optimism. A caller that treats an
                    // unacknowledged write as success can mark a backup settled
                    // while the wheel still holds our colours, and the next borrow
                    // then captures those as "the original".
                    if (!TryMatchReply(_replyStream, idx, (byte)(0x20 | SwId), resp, maxTimeouts: 2))
                    {
                        _log("[RPM-LED] slot write: SET_CONFIG was not acknowledged");
                        return false;
                    }

                    // 4. Commit + enable so the strip repaints with the new colours.
                    SendFn6(lvl);
                    TryQuery(Fn(7), 0x00, resp);

                    // Step 1 changed which effect the wheel is on, so record it.
                    // Leaving it stale made the settings picker keep naming the
                    // OLD pattern, and worse, SelectPatternNow early-returns true
                    // without writing when the cache already names the effect
                    // asked for, so picking the one it wrongly named did nothing
                    // at all and there was no way back through the UI.
                    _knownSelection = 5 + config.Slot;

                    // 5. Put the level back where it belongs. The floor above is a
                    // requirement of the UPLOAD, not a state to leave behind: a
                    // caller that asked for no particular level (the restore on
                    // exit, an apply while parked) wants the strip as it was,
                    // which is usually dark. Skipping this left exactly one LED
                    // lit after SimHub closed.
                    int settle = Math.Max(0, Math.Min(_stripLen, wanted));
                    if (settle != lvl) SendFn6(settle);

                    _log($"[RPM-LED] slot {config.Slot + 1} written (direction {dir})");
                    return true;
                }
                catch (Exception ex)
                {
                    _log($"[RPM-LED] slot write failed: {ex.Message}");
                    return false;
                }
            }
        }

        private byte _idxBrightness;

        /// <summary>Bytes in the last matched reply. Only meaningful immediately
        /// after a successful query, under the same lock.</summary>
        private int _lastReplyLength;

        /// <summary>Read the wheel's overall LED brightness as a percentage, or -1
        /// if this wheel does not expose it.
        ///
        /// fn0 reports the maximum the device uses and fn1 the current value; the
        /// two are combined so a device counting to 100 and one counting to 255
        /// both come back as a percentage.</summary>
        public int TryReadBrightnessPercent()
        {
            lock (_io)
            {
                if (!IsReady) return -1;
                if (_idxBrightness == 0) _idxBrightness = TryGetFeature(PageBrightness);
                if (_idxBrightness == 0) return -1;

                var resp = new byte[LenVeryLong];
                int max = 100;
                if (TryQueryOn(_idxBrightness, (byte)(0x00 | SwId), 0x00, resp))
                {
                    int reported = (resp[4] << 8) | resp[5];
                    if (reported > 0) max = reported;
                }
                if (!TryQueryOn(_idxBrightness, (byte)(0x10 | SwId), 0x00, resp)) return -1;

                int current = (resp[4] << 8) | resp[5];
                if (max <= 0) return -1;
                int pct = (int)Math.Round(100.0 * current / max);
                return Math.Max(0, Math.Min(100, pct));
            }
        }

        /// <summary>Set the wheel's overall LED brightness, as a percentage.
        ///
        /// ONLY ever called because the user moved the control. Nothing writes
        /// this on load, on shutdown, or as part of showing a pattern: it is a
        /// setting of theirs that happens to live on the wheel, and quietly
        /// rewriting it would be taking something that was not offered.</summary>
        public bool TryWriteBrightnessPercent(int percent)
        {
            if (percent < 0) percent = 0; else if (percent > 100) percent = 100;
            lock (_io)
            {
                if (!IsReady) return false;
                if (_idxBrightness == 0) _idxBrightness = TryGetFeature(PageBrightness);
                if (_idxBrightness == 0) { _log("[RPM-LED] brightness feature (0x8040) not present"); return false; }

                var resp = new byte[LenVeryLong];
                int max = 100;
                if (TryQueryOn(_idxBrightness, (byte)(0x00 | SwId), 0x00, resp))
                {
                    int reported = (resp[4] << 8) | resp[5];
                    if (reported > 0) max = reported;
                }

                int value = (int)Math.Round(max * percent / 100.0);
                var pkt = new byte[LenLong];
                pkt[0] = RepLong; pkt[1] = DevWired; pkt[2] = _idxBrightness; pkt[3] = (byte)(0x20 | SwId);
                pkt[4] = (byte)((value >> 8) & 0xFF);
                pkt[5] = (byte)(value & 0xFF);

                try
                {
                    DrainReplies(_replyStream);
                    WriteLong(pkt);
                    if (!TryMatchReply(_replyStream, _idxBrightness, (byte)(0x20 | SwId), resp, maxTimeouts: 2))
                    {
                        _log("[RPM-LED] brightness write was not acknowledged");
                        return false;
                    }
                    _log($"[RPM-LED] brightness set to {percent}% ({value}/{max})");
                    return true;
                }
                catch (Exception ex) { _log($"[RPM-LED] brightness write failed: {ex.Message}"); return false; }
            }
        }

        /// <summary>Longest slot name the wire carries. The wheel's own menu shows
        /// these, so a name set here is visible on the base itself.</summary>
        public const int SlotNameMaxLength = 8;

        /// <summary>Read a slot's stored name, or null. fn3 on the slot page is
        /// GET_NAME (it is a read despite the number sitting where a "set" would
        /// on the other page).</summary>
        public string TryReadSlotName(int slot)
        {
            if (slot < 0 || slot >= CustomSlotCount) return null;
            lock (_io)
            {
                if (!IsReady) return null;
                byte idx = SlotFeatureIndex();
                if (idx == 0) return null;

                var resp = new byte[LenVeryLong];
                if (!TryQueryOn(idx, (byte)(0x30 | SwId), (byte)slot, resp)) return null;

                // Printable ASCII only, stopping at the first terminator: the
                // buffer is fixed width and the tail is whatever was there before.
                var sb = new System.Text.StringBuilder();
                for (int i = 5; i < Math.Min(resp.Length, 5 + SlotNameMaxLength + 2); i++)
                {
                    byte c = resp[i];
                    if (c == 0x00) break;
                    if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
                }
                return sb.ToString().Trim();
            }
        }

        /// <summary>Name a slot, as shown in the wheel's own LIGHTSYNC menu.
        /// Truncated to <see cref="SlotNameMaxLength"/> and to plain ASCII, which
        /// is all the wire carries.</summary>
        /// <summary>What a name becomes once the wheel has it: trimmed, plain
        /// ASCII, and no longer than the field. Exposed so a caller can tell
        /// whether a slot ALREADY carries a name without writing it to find out,
        /// and so that comparison uses the same rules as the write rather than a
        /// second copy of them that could drift.</summary>
        public static string SlotNameFor(string name)
        {
            var ascii = new System.Text.StringBuilder();
            foreach (char c in (name ?? string.Empty).Trim())
            {
                if (ascii.Length >= SlotNameMaxLength) break;
                ascii.Append(c >= 0x20 && c < 0x7F ? c : '?');
            }
            return ascii.ToString();
        }

        public bool TryWriteSlotName(int slot, string name)
        {
            if (slot < 0 || slot >= CustomSlotCount) return false;
            lock (_io)
            {
                if (!IsReady) return false;
                byte idx = SlotFeatureIndex();
                if (idx == 0) return false;

                string ascii = SlotNameFor(name);

                var pkt = new byte[LenVeryLong];
                pkt[0] = RepVeryLong; pkt[1] = DevWired; pkt[2] = idx; pkt[3] = (byte)(0x40 | SwId);
                pkt[4] = (byte)slot;
                pkt[5] = (byte)ascii.Length;
                for (int i = 0; i < ascii.Length; i++) pkt[6 + i] = (byte)ascii[i];

                try
                {
                    DrainReplies(_replyStream);
                    WriteVeryLong(pkt);
                    var resp = new byte[LenVeryLong];
                    if (!TryMatchReply(_replyStream, idx, (byte)(0x40 | SwId), resp, maxTimeouts: 2))
                    {
                        _log($"[RPM-LED] slot {slot + 1} name was not acknowledged");
                        return false;
                    }
                    _log($"[RPM-LED] slot {slot + 1} named '{ascii}'");
                    return true;
                }
                catch (Exception ex) { _log($"[RPM-LED] slot name write failed: {ex.Message}"); return false; }
            }
        }

        /// <summary>READ-ONLY probe of the per-slot RGB config page (0x807B).
        ///
        /// Everything known about slot programming comes from mescon's Linux
        /// driver, which verified it on an RS50; his own notes say the G PRO's
        /// wiring is not byte-verified. This asks the wheel directly and writes
        /// what it says to the log. It only ever READS: it resolves the feature
        /// and requests each slot's stored config. Nothing is written to a slot,
        /// no effect is selected, and the rev-light state is untouched, so it is
        /// safe to run at any time, including with a game running.
        ///
        /// Returns a human-readable report; the caller logs and/or shows it.</summary>
        public string ProbeSlotFeature()
        {
            // Open on demand so the probe works with no game running, the same
            // way the LED test button does. Otherwise this would only be usable
            // mid-session, which is exactly when poking the wheel is least wise.
            if (!IsReady)
            {
                try { OpenAndResolve(); } catch (Exception ex) { _log($"[RPM-LED] probe open threw: {ex.Message}"); }
            }

            var sb = new System.Text.StringBuilder();
            lock (_io)
            {
                if (!IsReady)
                {
                    return "Could not open the wheel's HID++ channel, so there was nothing to ask. "
                         + "Check the wheel is connected and powered on, then try again.";
                }

                sb.AppendLine("Wheel: " + (_devName ?? "(unnamed)"));
                sb.AppendLine($"Rev page 0x807A resolved at index 0x{_idxRev:X2}, strip length {_stripLen}.");

                byte idxSlot = TryGetFeature(PageSlotConfig);
                if (idxSlot == 0)
                {
                    sb.AppendLine("Slot page 0x807B: NOT PRESENT on this wheel.");
                    sb.AppendLine("This wheel does not expose the per-slot colour feature over HID++, "
                                + "so custom colours cannot be written by us the way they can on an RS50.");
                    string none = sb.ToString();
                    _log("[RPM-LED] slot probe:\n" + none);
                    return none;
                }

                sb.AppendLine($"Slot page 0x807B: PRESENT at index 0x{idxSlot:X2}.");
                sb.AppendLine("Stored slot configs (fn1 GET_CONFIG), raw reply bytes:");

                for (byte slot = 0; slot < 5; slot++)
                {
                    var resp = new byte[LenVeryLong];
                    // fn1 with the same sw-id nibble the rest of this channel uses.
                    bool ok = TryQueryOn(idxSlot, (byte)(0x10 | SwId), slot, resp);
                    if (!ok)
                    {
                        sb.AppendLine($"  CUSTOM {slot + 1}: no reply (feature present but this call refused).");
                        continue;
                    }

                    var hex = new System.Text.StringBuilder();
                    for (int i = 4; i < Math.Min(resp.Length, 36); i++) hex.Append(resp[i].ToString("X2")).Append(' ');
                    sb.AppendLine($"  CUSTOM {slot + 1}: {hex.ToString().TrimEnd()}");
                    // Expected shape per mescon: [slot echo][direction 1..4][30 bytes RGB, LED10 first].
                    if (resp[4] == slot)
                        sb.AppendLine($"      slot echo OK, direction byte = {resp[5]} "
                                    + "(1=inside-out 2=outside-in 3=left-to-right 4=right-to-left)");
                    else
                        sb.AppendLine($"      slot echo MISMATCH (got {resp[4]}), so the reply shape differs from the RS50's.");
                }

                string report = sb.ToString();
                _log("[RPM-LED] slot probe:\n" + report);
                return report;
            }
        }

        /// <summary>Read frames off <paramref name="s"/> until one matches our
        /// feature + full fn byte, skipping foreign traffic. Error frames for
        /// our feature are always logged; one echoing THIS request fails it.</summary>
        private bool TryMatchReply(HidStream s, byte fnByte, byte[] respOut, int maxTimeouts)
            => TryMatchReply(s, _idxRev, fnByte, respOut, maxTimeouts);

        private bool TryMatchReply(HidStream s, byte featureIdx, byte fnByte, byte[] respOut, int maxTimeouts)
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

                    // Foreign frames are skipped below; a notification among them
                    // still tells us what the wheel is showing.
                    NoteEffectBroadcast(resp, n);

                    switch (ClassifyFnReply(resp, n, featureIdx, fnByte))
                    {
                        case FnReply.Match:
                            // Record how many bytes ACTUALLY arrived. The buffer is
                            // 64 bytes of zeros, so a shorter frame leaves a tail of
                            // zeros that reads as real data. For a slot config that
                            // means black LEDs saved as the user's "original", so
                            // callers that index deep into a reply must check this.
                            _lastReplyLength = n;
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

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>Start the level pair and its ~1 Hz keepalive. There is NO
        /// arm burst any more. mescon's two live G HUB captures (2026-08-14, a
        /// full AC EVO session and an iRacing LED bring-up) show fn0/fn1/fn2
        /// discovery reads ONCE at wheel attach and then the bare fn2 + fn6
        /// level pair forever, with no fn3/fn4/fn7 in any session. Our old
        /// burst re-read fn0/fn1/fn2, which TryOpenGroup had already read, and
        /// it fired on the FIRST REV LEVEL: in game, mid-session. That is
        /// precisely the shape and the moment mescon measured killing a
        /// wheel's input path, so the re-burst is gone and the effect decision
        /// moved to TryOpenGroup. What is left here is a pattern pick the user
        /// staged AFTER the channel opened, which is a deliberate action
        /// rather than a sequence we invent. See the file header for why this
        /// is protocol alignment rather than a fix: our ep3 stream was tested
        /// immune to mid-session fn3 on this hardware.</summary>
        private void Arm()
        {
            if (_armed) return;

            if (_pendingSelect > 0 && _pendingSelect != _knownSelection)
            {
                // Staged while the pipe was busy (a game running its own FFB).
                // Level 0: arming always starts from a dark bar.
                ApplyEffectSwitch((byte)_pendingSelect, 0);
                _knownSelection = _pendingSelect;
                _log($"[RPM-LED] applying pending pattern pick: effect {_pendingSelect}");
            }
            _pendingSelect = 0;

            // Best-effort check on the pair's own fn2 reply: a wheel still
            // reporting 0 after the open-time wake will refuse every level
            // with error 5 and the lights stay dark while everything here
            // logs success. TryMatchReply also surfaces any error frame (the
            // fn6 refusal included), so this is where a dead strip becomes
            // visible. Drain first so the match can only be the pair's own
            // reply, not a queued echo or broadcast from open.
            var resp = new byte[LenVeryLong];
            bool verify = _wokeDarkPanel;
            if (verify) DrainReplies(_replyStream);
            SendPair(0);
            if (verify
                && TryMatchReply(_replyStream, Fn(2), resp, maxTimeouts: 1)
                && resp[4] == 0)
            {
                _log("[RPM-LED] wheel still reports no active effect after the open-time "
                     + "wake; level writes will be refused and the lights may stay dark");
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

        // The 0x807B slot config needs 32 parameter bytes, which only the 64-byte
        // VERY_LONG report can carry. Falls back to the LONG collection if the
        // wheel exposes no VERY_LONG one, where it will simply fail rather than
        // silently truncate the colours.
        private void WriteVeryLong(byte[] r)
        {
            if (_veryLong != null) _veryLong.Write(r);
            else if (_long != null) _long.Write(r);
            else _cmdShort.Write(r);
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
