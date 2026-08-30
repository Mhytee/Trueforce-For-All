// Reads AC's outgoing HID++ FFB target from the USB bus by spawning USBPcapCMD
// as a child process, parsing its pcap stdout, and latching the most-recent
// FFB target value for the Trueforce stream to inject into ep3 bytes 6-9.
//
// AC sends DirectInput-equivalent FFB to the wheel as HID Set_Output_Reports
// on ep0 (control endpoint). The actual force command is HID++ feature page
// 0x8123 (G-series force feedback) function 2 long-form messages, signed
// 16-bit big-endian at offset 10-11 of the HID++ payload. The feature
// *index* is fixed per wheel model (G PRO 0x0e, RS50 0x10); it is seeded
// per model via SetFfbFeatureIndexSeed (default 0x0e) with the runtime
// resolver as the fallback (see _ffbFeatureIndex). When we
// stream Trueforce on ep3, the wheel uses
// bytes 6-9 of our packet as motor torque, ignoring AC's ep0 commands. By
// mirroring AC's commands into bytes 6-9, FFB and Trueforce coexist.
// (cur-as-torque-target confirmed independently by mescon's Linux kernel
// driver on RS50 hardware, 2026-07.)
//
// USBPcap installs as a kernel-mode USB filter driver. USBPcapCMD.exe streams
// pcap to stdout when invoked with -o -. We don't require admin in our
// process, USBPcap's own access checks happen in its CMD process.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class UsbPcapFfbTap : IDisposable
    {
        // Default install path. We probe Program Files and Program Files (x86)
        // and a couple of common alternates.
        private static readonly string[] CandidatePaths = new[]
        {
            @"C:\Program Files\USBPcap\USBPcapCMD.exe",
            @"C:\Program Files (x86)\USBPcap\USBPcapCMD.exe",
        };

        private const int DLT_USBPCAP = 249;

        // _packed layout: low 16 bits = ffbTarget bit-pattern, high 48 bits =
        // Stopwatch.ElapsedTicks masked to 48 bits. Masking on store + logical
        // (unsigned) right shift on read + modular subtraction on age makes
        // the freshness check wrap-safe, without the mask, a left-shift by
        // 16 lands in the sign bit at ~162 days of QPC uptime and silently
        // breaks FFB pass-through until reboot.
        private const long TimestampMask = 0x0000_FFFF_FFFF_FFFFL;

        // How long the reader loop sleeps between auto-discovery retries when
        // the wheel can't be found. Long enough to not spam logs, short enough
        // that a replug or re-elevation feels responsive.
        private const int RediscoveryRetryMs = 15000;

        // After this many consecutive capture sessions that exit without
        // yielding a single packet, we discard the cached interface/address
        // and re-run full discovery (the wheel was likely replugged and got a
        // new device address). Below the threshold we restart the capture on
        // the SAME cached interface/address. This is the crux of fix 1:
        // re-running the full-bus `-A` discovery scan on every transient drop
        // pegs a CPU core when a separate high-traffic device (e.g. the wheel
        // rim's own DD base) sits on its own USBPcap interface, because that
        // interface gets re-scanned every cycle. We only ever need the
        // wheelbase's interface, and only when it actually changes.
        private const int MaxCaptureFailuresBeforeRediscovery = 3;

        // Capture-restart backoff. A capture that keeps failing fast must not
        // respawn USBPcapCMD every couple of seconds (that also re-prompts UAC
        // in a loop when USBPcap needs elevation). Backoff grows from this
        // floor toward the ceiling and resets to the floor on a healthy
        // session. Doubling: 2s, 4s, 8s, 16s, 30s(cap).
        private const int CaptureBackoffFloorMs = 2000;
        private const int CaptureBackoffCeilMs  = 30000;

        // Backoff applied when USBPcapCMD can't launch without elevation, or
        // the user dismisses the UAC prompt. Long, because retrying sooner
        // only re-prompts; the user has to grant admin (or run SimHub
        // elevated) for the tap to work at all.
        private const int ElevationBackoffMs = 60000;

        // Win32 launch errors that mean "elevation is the blocker, stop
        // hammering the spawn": ERROR_ELEVATION_REQUIRED (manifest needs admin,
        // UseShellExecute=false) and ERROR_CANCELLED (user dismissed the UAC
        // prompt under a runas/AppCompat shim).
        private const int ErrorElevationRequired = 740;
        private const int ErrorCancelled         = 1223;

        /// <summary>Whether the host (SimHub) process is running elevated. USBPcap
        /// capture needs administrator rights, and elevation can't be gained
        /// without restarting SimHub, so when this is false the tap doesn't even
        /// attempt the capture (no failed launch / UAC loop) and just reports
        /// that SimHub must be restarted as admin. The plugin sets this from its
        /// own elevation check; defaults true so the tap behaves normally if a
        /// caller never sets it.</summary>
        public bool HostElevated { get; set; } = true;
        private bool _loggedNotElevated;

        // Resolved interface + device address. Either supplied via the manual
        // override constructor args, or filled in by discovery inside the
        // reader loop. Reset to null/0 by ClearDiscovered() when the user
        // explicitly clears the manual override and wants auto-discovery again.
        private string _usbPcapInterface;
        private int _deviceAddress;

        // Set when the caller explicitly passed an interface+address (manual
        // picker or env-var override). When true, the reader loop skips
        // discovery and never re-runs it. When false, the loop will retry
        // discovery on failure.
        private readonly bool _manualOverride;

        // Optional VID/PID of the wheel that HID enumeration already found.
        // Plumbed to WheelUsbDiscovery so it can log the smoking-gun
        // "HID saw it, USBPcap didn't" line when discovery fails.
        private ushort? _hidFoundVid;
        private ushort? _hidFoundPid;

        private readonly string _usbPcapCmdPath;

        private Process _proc;
        private Thread _readerThread;
        private volatile bool _stopping;

        // Most-recent FFB target (signed int16) and the Stopwatch timestamp at
        // which it was captured. Read from any thread; written only by the
        // reader thread. We use a single int64 field with packed value+timestamp
        // so reads are torn-tear-safe under Volatile semantics.
        // Layout: low 16 bits = signed int16 (cast to ushort for storage),
        //         high 48 bits = stopwatch ticks (truncated, monotonic).
        private long _packed;

        // Stopwatch tick (masked to 48 bits) of the last successfully parsed
        // FFB sample. Used by HasRecentPackets / GetLastSampleAgeMs so the UI
        // can distinguish "process is alive" from "process is alive AND
        // actually receiving FFB data". Read from any thread; written only
        // by the reader thread.
        private long _lastSampleTicks;

        private static readonly Stopwatch _sw = Stopwatch.StartNew();

        // Status surfaced to the UI / logs. Populated by the reader thread.
        public string Status { get; private set; } = "Stopped";
        public bool IsRunning => _proc != null && !_proc.HasExited;
        // Backed by an explicit field with Interlocked so the liveness watchdog
        // (a different thread) can't read a torn 64-bit value on the 32-bit host
        // and mis-fire the capture-kill. Incremented once per parsed packet.
        private long _packetsParsed;
        public long PacketsParsed => Interlocked.Read(ref _packetsParsed);
        public long FfbSamplesCaptured { get; private set; }

        // Packets that passed the device-address filter, i.e. traffic that is
        // really the wheel's. Interlocked like _packetsParsed because in
        // whole-bus capture this is the liveness signal (see LivenessProgress)
        // and the watchdog thread must not read a torn 64-bit value on the
        // 32-bit host.
        private long _packetsForOurDevice;
        public long PacketsForOurDevice => Interlocked.Read(ref _packetsForOurDevice);

        // Diagnostic counters. Written only by the parser thread; read by any
        // thread via property getters. Triage tool for the case where the tap
        // is running but FFB pass-through still feels broken: tells us which
        // endpoint(s) and transfer type(s) the game is actually using.
        // Critical when the resolved 0x8123 feature index is wrong or the
        // game uses an unexpected HID++ shape; the tuple histogram surfaces it.
        public long ControlTransfersOnOurDevice { get; private set; }
        public long Ep0ControlTransfersOnOurDevice { get; private set; }
        public long SetReportsOnOurDevice { get; private set; }

        // OUT-direction counters (host → wheel). Per-transfer-type and
        // per-endpoint breakdown. If the wheel's game protocol uses a
        // non-ep0 / non-control transport, the parser-match counters above
        // stay at zero but these tell us where to look.
        // Indices for transfer types: 0=Iso, 1=Interrupt, 2=Control, 3=Bulk.
        private readonly long[] _outTransferTypeCounts = new long[4];
        private readonly long[] _outEndpointCounts     = new long[16];
        public long IsoOutOnOurDevice       => _outTransferTypeCounts[0];
        public long InterruptOutOnOurDevice => _outTransferTypeCounts[1];
        public long ControlOutOnOurDevice   => _outTransferTypeCounts[2];
        public long BulkOutOnOurDevice      => _outTransferTypeCounts[3];
        public long[] SnapshotOutEndpointCounts()
        {
            var snap = new long[16];
            Array.Copy(_outEndpointCounts, snap, 16);
            return snap;
        }

        // (reportId << 16) | (featIdx << 8) | (funcByte & 0xf0) → count of
        // Set_Reports observed with that triplet. Surfaces the actual HID++
        // protocol the game is using so a divergence from the expected
        // (0x11, 0x0e, 0x20) jumps out in the log dump. Guarded by _tupleLock.
        private readonly Dictionary<int, long> _tupleCounts = new Dictionary<int, long>();
        private readonly object _tupleLock = new object();

        // Resolved HID++ FFB feature index. Logitech wheels deliver native FFB
        // via HID++ feature page 0x8123, but the feature *index* is fixed per
        // wheel model: G PRO = 0x0e, RS50 = 0x10 (confirmed by mescon,
        // 2026-07, who also confirmed the USB product string identifies the
        // model even when a compat-mode RS50 spoofs the G PRO PID). The
        // plugin seeds the per-model value via SetFfbFeatureIndexSeed
        // (default 0x0e), so known wheels are correct from the first packet.
        // The resolver stays as the fallback for unknown models and
        // self-heals a dead-silent wrong seed (see SetFfbFeatureIndexSeed
        // for the confirm-lock limit): MaybeResolveFfbFeatureIndex() promotes
        // the dominant high-rate func-0x20 index, counting BOTH HID++ long
        // (reportId 0x11) and very-long (reportId 0x12) reports toward it.
        //
        // The RS50 (C276) IS in scope here. Windows USBPcap evidence (issue #5
        // woTF capture, BeamNG + G Hub) proves RS50 native FFB on Windows is
        // HID++ feat 0x10 (page 0x8123) fn2, BE16 force at payload offset
        // 10-11: structurally identical to G PRO's 0x0e path, just a different
        // index. mescon's "raw report-0x01 on ep3" is its Linux *driver's* own
        // transport choice, NOT how the Windows runtime drives the wheel.
        // So the resolver auto-promotes 0x10 on RS50 with no special-casing.
        //
        // Issue #8 (Infinitum9, FH6 + RS50, 2026-05-24 capture): this wheel
        // delivers Forza's FFB predominantly on the very-long report 0x12
        // (18 of 26 func-2 SET_REPORTs; the real high-amplitude force, incl.
        // full-scale 32767, was 0x12-only, while 0x11 carried only +/-3). The
        // old 0x11-only extractor dropped every 0x12 packet, so cur was never
        // populated and the device never entered active mode.
        //
        // Both report IDs are extracted and both count toward the resolver:
        // which one carries the stream is decided per session by
        // FfbReportArbiter (the RS50 puts most of it on 0x12, and the owner's
        // G PRO ran two whole sessions on 0x12 only on 2026-08-28). The
        // experimental opt-in that used to gate 0x12 is retired.
        private const byte FfbFeatureIndexSeed = 0x0e;
        // Min cumulative func-0x20 samples at a candidate index before the
        // resolver switches off the seed. 32 so short / menu-heavy sessions
        // still latch the real index (Infinitum9's whole capture had only 26
        // func-2 packets); the 4x-dominance rule + re-entrant re-evaluation +
        // confirm-lock are the real guard against a stray settings-write
        // burst, and known wheels start on a per-model seed anyway.
        private const long FfbIndexMinSamples = 32;

        // Positive RS50 identification from discovery (native RS50 PID, or an
        // RS50 product string on a spoofed G PRO PID; mescon, 2026-07). Kept
        // for the capture fingerprint. volatile: plugin thread writes at
        // wiring time, parser thread reads.
        public volatile bool Rs50Identified;
        // Per-model seed for the feature index (default: G PRO's 0x0e). Set by
        // the plugin before Start() from PID + product string, see
        // SetFfbFeatureIndexSeed below.
        private volatile byte _ffbSeed = FfbFeatureIndexSeed;
        private volatile byte _ffbFeatureIndex = FfbFeatureIndexSeed;

        // Hardware-confirmed FFB feature indices, pinned per wheel so the
        // statistical resolver can never wander onto non-FFB traffic. Field
        // lesson (FM8, 2026-07-03): with the game's real FFB silent, FM8 still
        // streams high-rate func-0x20 writes on feat 0x12 (LED/aux), the
        // resolver latched them, "extracted" their payload bytes as force and
        // CONFIRMED itself — the wheel buzzed on rendered LED data. The
        // "wrong indices never produce extracted samples" assumption in the
        // confirm-gate comment below is simply false for that traffic shape,
        // so for wheels whose index we have proven on hardware we don't vote,
        // we pin. Unlisted wheels keep the auto-resolver unchanged.
        //   C26D/C26E (G923 Xbox/PC): 0x0B — proven 2026-07-02, TFFA driver
        //     harness (FFB_LEAK_PASS at 0x0B + selective-intercept validation).
        //   C276 (RS50): 0x10 — issue #5 woTF Windows capture.
        //   C266/C267 (G923 PS): predicted 0x08 but NOT hardware-confirmed,
        //     deliberately unpinned until someone proves it.
        private volatile bool _ffbIndexPinned;
        public bool IsFfbFeatureIndexPinned => _ffbIndexPinned;

        private static byte? KnownFfbIndexForWheel(ushort vid, ushort pid)
        {
            if (vid != 0x046D) return null;
            switch (pid)
            {
                case 0xC26D:
                case 0xC26E: return 0x0B;   // G923 Xbox/PC
                case 0xC276: return 0x10;   // RS50
                default:     return null;
            }
        }

        private void MaybePinKnownFfbIndex(ushort vid, ushort pid)
        {
            if (_ffbIndexPinned) return;
            byte? known = KnownFfbIndexForWheel(vid, pid);
            if (!known.HasValue) return;
            _ffbFeatureIndex  = known.Value;
            _ffbIndexResolved = true;
            _ffbIndexPinned   = true;
            Log($"FFB feature index PINNED to 0x{known.Value:X2} for wheel {vid:X4}:{pid:X4} " +
                "(hardware-confirmed; statistical resolution disabled for this wheel).");
        }
        private bool _ffbIndexResolved;                 // parser-thread only
        // Set true the first time real FFB is actually extracted on the
        // currently-selected index (i.e. force flowed, not just "an index hit
        // the sample threshold"). This is the *only* state that stops the
        // resolver: a confirmed index can't be a wrong-latch, because wrong
        // indices never produce extracted samples. Until confirmed, the
        // resolver keeps running and may re-switch, so an early commit to a
        // non-FFB func-0x20 tuple (e.g. a menu/settings burst that reaches 200
        // before driving FFB ramps) self-heals to the real FFB index within ~1
        // s of gameplay instead of sticking dead until a SimHub restart.
        private volatile bool _ffbIndexConfirmed;       // any thread reads; parser writes
        public byte ResolvedFfbFeatureIndex => _ffbFeatureIndex;
        public bool IsFfbFeatureIndexResolved => _ffbIndexResolved;

        // Capture fingerprint: recorded once, the first time real FFB is
        // extracted on any path. A compact, human-readable description of the
        // wire shape that worked (transport, report ID, feature index,
        // encoding, which report the arbiter settled on). Surfaced in the
        // Export-logs manifest and the compatibility report. Null until first
        // extraction. volatile: parser writes, UI/plugin threads read.
        private volatile string _captureFingerprint;
        public string CaptureFingerprint => _captureFingerprint;
        // 0x11-vs-0x12 arbitration lives in FfbReportArbiter: exactly one
        // report is the live force channel per session and the other one's
        // same-shaped management writes are ignored, whichever way round the
        // driver has it. Parser thread only.
        private readonly FfbReportArbiter _reportArbiter = new FfbReportArbiter();

        /// <summary>Which HID++ report the game's force is arriving on this
        /// session: 0 until decided, else 0x11 or 0x12. Diagnostics only.</summary>
        public byte LiveForceReport => _reportArbiter.LiveReport;

        // Stopwatch stamp of the newest fn2 write on the FFB feature that the
        // live report (or any report, while undecided) carried, taken BEFORE
        // the arbiter and the NOFFB simulation get a say. Compared against the
        // last decoded sample by ForceTrafficSinceLastSample so the plugin can
        // tell "the game has gone quiet" from "the game is writing force we
        // are not decoding": the quiet-spell hold is only right for the first.
        private long _lastForceTrafficTicks;

        // The rev-light LEVEL the wheel was last told to show, per HID++
        // feature index, from any host writer (the game's own lighting, G HUB,
        // or us): a long-report fn6 on the rev-light feature carries the lit
        // step count in byte 9. When we rewrite a colour slot mid-game the
        // upload has to raise the level to at least one to land, and this is
        // what lets the write settle the strip back to what the game had on it
        // instead of to dark. Parser thread writes, UI thread reads; both are
        // whole-int stores so a torn read is impossible.
        private readonly int[]  _lastLevelByFeature      = new int[256];
        private readonly long[] _lastLevelTicksByFeature = new long[256];

        private void NoteLevelWrite(byte featIdx, int level)
        {
            _lastLevelByFeature[featIdx] = level;
            Interlocked.Exchange(ref _lastLevelTicksByFeature[featIdx], _sw.ElapsedTicks & TimestampMask);
        }

        /// <summary>The strip level most recently written to
        /// <paramref name="featIdx"/> by anyone, if that write is no older than
        /// <paramref name="maxAgeMs"/>.</summary>
        public bool TryGetLastLevelWrite(byte featIdx, int maxAgeMs, out int level)
        {
            level = _lastLevelByFeature[featIdx];
            long t = Interlocked.Read(ref _lastLevelTicksByFeature[featIdx]);
            if (t == 0) return false;
            long now = _sw.ElapsedTicks & TimestampMask;
            long ageTicks = (now - t) & TimestampMask;
            return ageTicks <= (Stopwatch.Frequency / 1000L) * maxAgeMs;
        }
        // Shape of the first extraction, stashed for the fingerprint string.
        private bool   _firstShapeSet;
        private string _firstTransport;
        private int    _firstReportId = -1;
        private int    _firstFeatIdx  = -1;
        private string _firstEncoding;
        // Don't declare a capture confirmed off one stray matching packet:
        // require a sustained run of extracted samples first. ~50 samples is a
        // fraction of a second of real FFB at 250-500 Hz, but far more than a
        // lone misparsed report. The human Yes/No prompt is the final arbiter;
        // this just keeps us from asking off noise.
        private const long CaptureConfirmSamples = 50;

        /// <summary>Set the per-model feature-index seed (from PID + USB
        /// product string, see WheelDiscovery.FfbFeatureIndexSeedFor). Call
        /// before Start(). Seed only: the resolver still runs, and a wrong
        /// seed self-heals while the seeded index stays dead-silent (the
        /// silent-seed escape in MaybeResolveFfbFeatureIndex). The limit: a
        /// func-0x20 packet arriving ON the seeded index extracts and
        /// confirm-locks it, so a wrong seed pointing at an index that still
        /// sees traffic can stick. That exposure is identical to the
        /// universal 0x0e seed this replaces, and per mescon (2026-07) a
        /// compat-mode RS50 keeps feature 0x10 live, so the RS50 seed is
        /// expected correct. Adopts the seed immediately unless extracted
        /// force has already confirmed an index; ResetFeatureIndexResolution
        /// also re-arms to this value, so an experimental-capture toggle keeps
        /// the per-model seed.</summary>
        public void SetFfbFeatureIndexSeed(byte seed)
        {
            _ffbSeed = seed;
            if (!_ffbIndexConfirmed) _ffbFeatureIndex = seed;
        }

        /// <summary>Re-arm the feature-index resolver: drop back to the seed
        /// index and clear the resolved/confirmed latches so the next pass
        /// re-evaluates from scratch, and forget which report carried force.
        /// Keeps the accumulated tuple history, so if 0x12 traffic was
        /// already seen the re-resolve to the real index is immediate.</summary>
        public void ResetFeatureIndexResolution()
        {
            // A hardware-pinned index survives re-arms: the pin exists because
            // the statistical path proved unsafe for this wheel. Confirmation
            // and fingerprint state still reset below so the next capture
            // re-records what worked.
            if (!_ffbIndexPinned)
            {
                _ffbFeatureIndex   = _ffbSeed;
                _ffbIndexResolved  = false;
            }
            _ffbIndexConfirmed     = false;
            _nextFfbResolveTicks   = 0;
            _captureFingerprint     = null;   // let the next capture re-record what worked
            _firstShapeSet          = false;
            _firstReportId          = -1;
            _firstFeatIdx           = -1;
            _reportArbiter.Reset();
        }

        // Note one extracted FFB sample's wire shape. Records which report ID
        // carried real force (for accurate attribution) and stashes the first
        // shape seen for the fingerprint string. transport: "ep0-ctrl" /
        // "interrupt-out". reportId/featIdx: -1 to omit (the DirectInput PID
        // path has no HID++ index). Does NOT confirm yet, see
        // MaybeConfirmCaptureFingerprint.
        private void NoteExtraction(string transport, int reportId, int featIdx, string encoding)
        {
            if (!_firstShapeSet)
            {
                _firstTransport = transport;
                _firstReportId  = reportId;
                _firstFeatIdx   = featIdx;
                _firstEncoding  = encoding;
                _firstShapeSet  = true;
            }
        }

        // Once a sustained run of samples has been extracted, record the
        // capture fingerprint a single time. Cheap to call every parse
        // iteration: a null check and a counter compare until it fires.
        private void MaybeConfirmCaptureFingerprint()
        {
            if (_captureFingerprint != null || !_firstShapeSet) return;
            if (FfbSamplesCaptured < CaptureConfirmSamples) return;

            string ridStr  = _firstReportId >= 0 ? $"reportId=0x{_firstReportId:X2} " : "";
            string featStr = _firstFeatIdx  >= 0 ? $"featIdx=0x{_firstFeatIdx:X2} " : "";
            byte live = _reportArbiter.LiveReport;
            string liveStr = live != 0 ? $" live=0x{live:X2}" : "";

            _captureFingerprint =
                $"transport={_firstTransport} {ridStr}{featStr}encoding={_firstEncoding}" +
                $"{liveStr}{(Rs50Identified ? " rs50Id=ON" : "")}";

            Log($"FFB capture confirmed (sustained, {FfbSamplesCaptured} samples): {_captureFingerprint}");
        }

        /// <summary>True when the wheel has received a force write on the live
        /// report (or any report while none is live yet) more recently than
        /// the last sample this tap decoded: the game is driving the wheel in
        /// a shape we are not turning into a target. The plugin's quiet-spell
        /// hold must not treat that as the game having gone quiet.</summary>
        public bool ForceTrafficSinceLastSample
        {
            get
            {
                long traffic = Interlocked.Read(ref _lastForceTrafficTicks);
                if (traffic == 0) return false;
                long sample = Interlocked.Read(ref _lastSampleTicks);
                if (sample == 0) return true;
                long d = (traffic - sample) & TimestampMask;
                return d > 0 && d < (TimestampMask >> 1);
            }
        }

        // Stamp a force write on the FFB feature before anything can reject
        // it. Only the live report counts once one is chosen: the other
        // report's management writes share the fn2 shape and would otherwise
        // read as "undecoded force" through every parked quiet spell.
        private void NoteForceTraffic(byte reportId)
        {
            byte live = _reportArbiter.LiveReport;
            if (live != 0 && reportId != live) return;
            Interlocked.Exchange(ref _lastForceTrafficTicks, _sw.ElapsedTicks & TimestampMask);
        }

        // Returns a snapshot of the tuple histogram. Safe to call from any
        // thread; the parser thread updates under _tupleLock and this also
        // takes _tupleLock for a consistent read.
        public Dictionary<int, long> SnapshotTupleCounts()
        {
            lock (_tupleLock) return new Dictionary<int, long>(_tupleCounts);
        }

        // Optional file path for raw-packet logging. When non-null, the parser
        // writes a real pcap file (DLT_USBPCAP, magic 0xa1b2c3d4) containing
        // every OUT transfer to the wheel's device address, regardless of
        // endpoint or transfer type. Off by default; toggled via the
        // Diagnostics panel and explicitly opt-in because the file can grow
        // quickly (~2-3 KB/sec of active FFB) and ships USB bus traffic with
        // logs. Wireshark opens the trace directly: install USBPcap and
        // drag-drop the .pcap.
        //
        // Why pcap rather than a custom binary: third-party tools (Wireshark)
        // already decode every USBPcap field for us, so we don't have to ship
        // or document a parser. Recipient can sort packets, filter by
        // endpoint, decode HID++ payloads without writing code.
        //
        // Set once at construction or via SetRawPacketLogPath; the parser
        // re-reads it on each packet so toggle takes effect quickly.
        private string _rawLogPath;
        private FileStream _rawLogStream;
        private long _rawLogBytesWritten;
        private const long RawLogMaxBytes = 50L * 1024 * 1024; // 50 MB safety cap
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void SetRawPacketLogPath(string path)
        {
            // Reader thread sees the new path on its next iteration. We don't
            // open the stream here; the parser handles open/close so the file
            // handle stays on the writer thread.
            _rawLogPath = path;
        }
        public string CurrentRawPacketLogPath => _rawLogPath;
        public long RawLogBytesWritten => Interlocked.Read(ref _rawLogBytesWritten);

        // Wall-clock ticks of the last periodic diagnostics emission. Emitted
        // by the parser thread every ~5 seconds when the tap is active so the
        // exported logs reliably contain at least one snapshot of the
        // counters/histogram during the user's repro session.
        private long _nextDiagEmitTicks;
        private const int DiagEmitIntervalMs = 5000;

        // The FFB feature-index resolver is gated on its own fast cadence,
        // separate from the 5 s diagnostics emit, so first FFB on a non-0x0e
        // wheel (RS50 -> 0x10) latches sub-second instead of waiting up to one
        // diagnostics interval. Runs until _ffbIndexConfirmed (real FFB
        // actually extracted), not merely until a tentative resolve; after
        // confirmation the gate stops calling it entirely (zero steady-state
        // cost). Before confirmation it may re-switch indices, which is what
        // lets a wrong early commit self-heal.
        private long _nextFfbResolveTicks;
        private const int FfbResolveIntervalMs = 250;

        // True when this tap was constructed with an explicit (interface,
        // address) override. The UI uses this to decide whether to show the
        // "clear manual override" affordance.
        public bool IsManualOverride => _manualOverride;
        public string CurrentInterface => _usbPcapInterface;
        public int CurrentDeviceAddress => _deviceAddress;

        // Milliseconds since the last FFB sample was latched, or long.MaxValue
        // if we've never latched one. Used by the UI to detect the "process
        // running but no data flowing" state.
        // Newest packet of any kind parsed for our device. While the plugin
        // streams ep3 at 1 kHz this advances every millisecond, so a stall
        // here means the CAPTURE has died (USBPcap stalled, the wheel moved
        // address), not that the game has gone quiet. The plugin's quiet-spell
        // hold reads it so a dead capture can never keep an active zero on the
        // wheel: with no capture there is nothing to know the game by.
        private long _lastDevicePacketTicks;

        /// <summary>Milliseconds since the capture last delivered a packet for
        /// the wheel, or long.MaxValue if it never has.</summary>
        public long MsSinceDevicePacket
        {
            get
            {
                long last = Interlocked.Read(ref _lastDevicePacketTicks);
                if (last == 0) return long.MaxValue;
                long now = _sw.ElapsedTicks & TimestampMask;
                long ageTicks = (now - last) & TimestampMask;
                return ageTicks * 1000L / Stopwatch.Frequency;
            }
        }

        public long MsSinceLastSample
        {
            get
            {
                long last = Interlocked.Read(ref _lastSampleTicks);
                if (last == 0) return long.MaxValue;
                long now = _sw.ElapsedTicks & TimestampMask;
                long ageTicks = (now - last) & TimestampMask;
                return ageTicks * 1000L / Stopwatch.Frequency;
            }
        }

        // Optional logger (e.g., SimHub.Logging.Current.Info). Avoids a hard
        // dependency on log4net from this library.
        public Action<string> Logger { get; set; }

        // Invoked when the tap heals a drifted device address: the wheel's
        // identity (same VID/PID) was found at a new USBPcap interface/address
        // than the one we were tapping. Args: (newInterface, newAddress). The
        // plugin uses this to update a saved manual override so the corrected
        // location persists across restarts. We never switch to a *different*
        // wheel here, so this only ever reports the same device at a new spot.
        public Action<string, int> OnDeviceRelocated { get; set; }

        // Set by the reader loop after repeated empty capture sessions to force
        // a fresh identity-based device re-resolution on the next iteration,
        // so a replug (same wheel, new address) heals even under a manual
        // override instead of looping forever on a dead address.
        private bool _forceRevalidate;

        // Fired once per driving session when we're tapping the right device,
        // the game is actively driving, yet no force feedback reaches our
        // capture even after trying whole-bus mode. The plugin surfaces it as a
        // user-facing notice (the genuine "USBPcap can't see the wheel on this
        // port" case). Arg: the human-readable message.
        public Action<string> OnNoFfbWarning { get; set; }

        // "Force feedback should be flowing right now" hint, set by the plugin
        // from telemetry (car moving / race-on). The self-heal escalation
        // (whole-bus retry, no-FFB warning) only fires while this is true, so
        // menus / loading / parked states never trip it. The rising edge
        // timestamps when active driving began and re-arms the one-shot warning.
        private volatile bool _gameFfbExpected;
        private int _gameFfbExpectedSinceMs;
        public bool GameFfbExpected
        {
            get => _gameFfbExpected;
            set
            {
                if (value && !_gameFfbExpected)
                {
                    _gameFfbExpectedSinceMs = Environment.TickCount;
                    _noFfbWarned = false;   // re-arm the warning for a new drive
                }
                _gameFfbExpected = value;
            }
        }

        // Self-heal escalation state (parser thread). When we're driving with no
        // FFB captured, first retry capture in whole-bus (-A) mode in case the
        // per-device USBPcap filter is dropping the wheel's FFB endpoint; if
        // that still yields nothing, warn the user once.
        private volatile bool _useBroadCapture;   // -A instead of --devices N
        private volatile bool _noFfbWarned;
        private long _ffbAtCaptureStart;          // FfbSamplesCaptured at capture start
        private int  _nextWatchdogMs;
        private const int WatchdogIntervalMs = 2000;
        private const int BroadCaptureSwitchMs = 8000;   // driving-with-no-FFB before -A retry
        private const int NoFfbWarnMs          = 15000;  // driving-with-no-FFB before warning

        // Dev/test: when true, the matcher still records FFB tuples (FFB looks
        // present on the wire) but never extracts a value or confirms the index,
        // simulating "we can see the wheel and the game's FFB reports but can't
        // get the force out of them." Lets the no-FFB self-heal escalation
        // (-A retry, then the warning) be exercised on a working wheel. Toggled
        // by the NOFFB access code.
        public volatile bool SimulateNoFfbCapture;

        // Liveness watchdog: while we're streaming to the wheel (heartbeat from
        // the send-activity probe climbing), a healthy capture parses packets
        // briskly. If the capture parses NOTHING for LivenessTimeoutMs while the
        // heartbeat clearly advanced, the capture has gone silent and the reader
        // thread is blocked in a read with no way to notice. We kill the child
        // (unblocking it) so ReaderLoop re-validates by identity and restarts.
        // This is a binary alive/dead check, not rate-matching, so dropped
        // packets never trip it; only total silence does.
        private Thread _livenessThread;
        private const int LivenessCheckMs   = 1000;
        private const int LivenessTimeoutMs = 5000;
        private const long LivenessMinSends = 100;   // heartbeat must clearly advance to arm
        private long _liveLastParsed;
        private long _liveLastSends;
        private long _liveLastProgressTicks;

        // The progress signal the watchdog watches. In --devices mode the
        // upstream USBPcapCMD filter restricts the stream to our address, so
        // the raw parse counter IS wheel traffic. In whole-bus (-A) fallback
        // the raw counter climbs from every device on the bus, so a
        // re-enumerated wheel (new address, stale filter) would look alive
        // forever, exactly the G923 fallback case that defeated the self-heal.
        // Watch only device-matched packets there; our own 1 kHz stream to the
        // wheel keeps it climbing while the wheel is genuinely present.
        private long LivenessProgress => _useBroadCapture ? PacketsForOurDevice : PacketsParsed;

        // Pass null/0 (the defaults) to auto-discover via WheelUsbDiscovery on
        // Start(). Pass explicit values only when overriding (env vars,
        // manual picker, tests).
        // usbPcapCmdPathOverride: absolute path to USBPcapCMD.exe; checked
        // first before the env var / default-path probe. Used by the
        // settings-panel Browse action when USBPcap is installed somewhere
        // off the beaten path.
        public UsbPcapFfbTap(string usbPcapInterface = null, int deviceAddress = 0, string usbPcapCmdPathOverride = null)
        {
            _usbPcapInterface = usbPcapInterface;
            _deviceAddress = deviceAddress;
            _manualOverride = !string.IsNullOrEmpty(usbPcapInterface) && deviceAddress > 0;
            _usbPcapCmdPath = LocateUsbPcapCmd(usbPcapCmdPathOverride);
        }

        // Tell discovery the VID/PID the HID stack already enumerated. Surfaces
        // the "HID found it, USBPcap didn't" log line on auto-discovery failure
        // so a bug-report log makes the bisection obvious.
        public void SetHidDiscoveredWheel(ushort vid, ushort pid)
        {
            _hidFoundVid = vid;
            _hidFoundPid = pid;
        }

        // Identity (VID/PID) of the device the user manually pinned. When set on
        // a manual override, the self-heal re-locates THIS device after a USB
        // re-enumeration (same identity, new address) and never switches to a
        // different device. 0 = unknown: the tap then leaves a manual pin
        // untouched rather than guessing.
        private ushort _overrideVid;
        private ushort _overridePid;
        public void SetOverrideIdentity(ushort vid, ushort pid)
        {
            _overrideVid = vid;
            _overridePid = pid;
        }

        // Probe returning our device's monotonic "packets sent to the wheel"
        // count (TrueforceDevice.PacketsSent). The liveness watchdog uses it as
        // a heartbeat: while we're actively streaming this climbs at ~1 kHz, so
        // if it advances meaningfully while our capture parses nothing, the
        // capture has gone silent (stale address / USBPcap stall) and we kick
        // it. Null when not wired (then the watchdog is inert).
        private Func<long> _sendActivityProbe;
        public void SetSendActivityProbe(Func<long> probe) => _sendActivityProbe = probe;

        // Public so the settings UI can validate a user-picked path with the
        // same probe order the constructor uses.
        public static string LocateUsbPcapCmd(string pathOverride = null)
        {
            if (!string.IsNullOrEmpty(pathOverride) && File.Exists(pathOverride)) return pathOverride;
            string fromEnv = Environment.GetEnvironmentVariable("USBPCAPCMD");
            if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            foreach (var p in CandidatePaths)
                if (File.Exists(p)) return p;
            return null;
        }

        public bool Start()
        {
            if (_usbPcapCmdPath == null)
            {
                Status = "USBPcap not installed (FFB pass-through disabled)";
                Log(Status);
                return false;
            }
            if (_readerThread != null) return true;

            _stopping = false;
            _readerThread = new Thread(ReaderLoop)
            {
                IsBackground = true,
                Name = "UsbPcapFfbTap",
                Priority = ThreadPriority.AboveNormal,
            };
            _readerThread.Start();
            _livenessThread = new Thread(LivenessLoop)
            {
                IsBackground = true,
                Name = "UsbPcapFfbTap-liveness",
            };
            _livenessThread.Start();
            return true;
        }

        public void Stop()
        {
            _stopping = true;
            try { _proc?.Kill(); } catch { }
            try { _readerThread?.Join(2000); } catch { }
            try { _livenessThread?.Join(1500); } catch { }
            _readerThread = null;
            _livenessThread = null;
            try { _proc?.Dispose(); } catch { }   // Kill() doesn't release the managed handles
            _proc = null;
            Status = "Stopped";
        }

        public void Dispose() => Stop();

        // Returns the latest FFB target if it's no older than maxAgeMs, else null.
        public short? TryGetFreshFfbTarget(int maxAgeMs)
        {
            long packed = System.Threading.Interlocked.Read(ref _packed);
            if (packed == 0) return null;

            short value     = (short)(packed & 0xffff);
            long  timestamp = (long)((ulong)packed >> 16);

            long now = _sw.ElapsedTicks & TimestampMask;
            long ageTicks = (now - timestamp) & TimestampMask;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if (ageTicks > maxAgeTicks) return null;

            return value;
        }

        /// <summary>Drop the last captured FFB target so TryGetFreshFfbTarget
        /// returns null until a genuinely fresh game packet is captured. The
        /// plugin calls this while it holds the stream stopped for a pause, so a
        /// stream restart mid-transition (FH6 quick travel / teleport) can't
        /// replay a stale pre-pause force and slam the wheel to lock (issue #13).
        /// The reader thread repopulates this the instant real FFB flows again.
        /// Also arms a reset of the classic slot state, so a wheel on that path
        /// cannot have a bare PLAY after the pause republish the pre-pause
        /// force from a slot we still believe is loaded.</summary>
        public void ClearLastFfbTarget()
        {
            _classicResetRequested = true;
            // Drop the spring snapshot NOW, from this thread: the parser only
            // honors the reset request at its next command, and a paused game
            // may send none. A published spring must not keep producing
            // emulated force across the pause (the spring analogue of the
            // stale-force replay this method exists to prevent). Safe here: a
            // volatile reference write; slot state itself stays parser-owned.
            _playingSprings = null;
            System.Threading.Interlocked.Exchange(ref _packed, 0);
        }

        // ---------- reader thread ----------

        // Resolve and validate which device the tap should capture. Returns
        // false only when there is no usable target and the caller should back
        // off and retry. The key behavior: when we know the wheel's identity
        // (VID/PID from HID enumeration) and FFB has not yet been confirmed, we
        // re-locate that SAME identity on the bus and heal the address if it
        // drifted (replug / re-enumeration). This validates a manual override
        // instead of blindly trusting a pinned address, and never switches to a
        // different wheel: if the identity is missing or ambiguous, we leave an
        // existing target untouched. Once FFB is confirmed, we skip the scan and
        // reuse the working target (no churn, no extra USBPcap spawns).
        private bool EnsureDeviceTarget(bool forceRevalidate)
        {
            bool haveTarget = !string.IsNullOrEmpty(_usbPcapInterface) && _deviceAddress > 0;

            // Which identity do we re-locate by? A manual override follows the
            // device the USER pinned (so we never switch them to a different
            // wheel); auto mode follows the HID-detected wheel. A manual pin
            // with an unknown identity (saved before we tracked it) is left
            // untouched, never guessed at.
            ushort? healVid = null, healPid = null;
            if (_manualOverride)
            {
                if (_overrideVid != 0) { healVid = _overrideVid; healPid = _overridePid; }
            }
            else
            {
                healVid = _hidFoundVid; healPid = _hidFoundPid;
            }

            // Pin the FFB feature index as soon as the wheel identity is
            // known — the pin is about the HID++ feature layout, not the bus
            // address, so it doesn't need the scan below to succeed.
            if (healVid.HasValue && healPid.HasValue)
                MaybePinKnownFfbIndex(healVid.Value, healPid.Value);

            if (healVid.HasValue && healPid.HasValue
                && (forceRevalidate || !haveTarget || !_ffbIndexConfirmed))
            {
                var matches = WheelUsbDiscovery.FindAllMatching(
                    _usbPcapCmdPath, healVid.Value, healPid.Value, Logger);

                if (matches != null && matches.Count == 1)
                {
                    var m = matches[0];
                    if (!haveTarget)
                    {
                        _usbPcapInterface = m.Interface;
                        _deviceAddress = m.DeviceAddress;
                        Log($"Auto-discovered: {m}");
                        return true;
                    }
                    if (m.Interface != _usbPcapInterface || m.DeviceAddress != _deviceAddress)
                    {
                        Log($"FFB tap: device {healVid.Value:X4}:{healPid.Value:X4} is now on " +
                            $"{m.Interface} dev {m.DeviceAddress} (was {_usbPcapInterface} dev {_deviceAddress}); " +
                            "retargeting" + (_manualOverride ? " and updating the saved device override." : "."));
                        _usbPcapInterface = m.Interface;
                        _deviceAddress = m.DeviceAddress;
                        try { OnDeviceRelocated?.Invoke(m.Interface, m.DeviceAddress); } catch { }
                    }
                    return true;
                }

                // Scan failed (null), identity not on the bus (0), or ambiguous
                // (>1 identical wheels): never guess. Keep an existing target;
                // only when we have none do we fall through to find-anything.
                if (haveTarget) return true;
            }

            if (haveTarget) return true;

            // No identity, or identity scan inconclusive, and no target yet:
            // fall back to the original "first supported wheel" discovery. Auto
            // mode only; a manual override always carries a target.
            if (!_manualOverride)
            {
                var hit = WheelUsbDiscovery.Find(_usbPcapCmdPath, Logger, hidFoundVid: _hidFoundVid, hidFoundPid: _hidFoundPid);
                if (hit != null)
                {
                    _usbPcapInterface = hit.Interface;
                    _deviceAddress = hit.DeviceAddress;
                    MaybePinKnownFfbIndex(hit.Vid, hit.Pid);
                    Log($"Auto-discovered: {hit}");
                    return true;
                }
            }
            return false;
        }

        // Periodic self-heal escalation, called from the parse loop (tick-gated
        // so it costs ~nothing). Returns true when the capture must be torn down
        // and restarted (to switch into whole-bus mode). Only ever acts while
        // the game is actively driving (GameFfbExpected) and FFB still hasn't
        // been captured, so it cannot misfire in menus or while parked. Disarms
        // permanently once real FFB is confirmed.
        private bool MaybeWatchdog()
        {
            int now = Environment.TickCount;
            if (now < _nextWatchdogMs) return false;
            _nextWatchdogMs = now + WatchdogIntervalMs;

            if (_ffbIndexConfirmed) return false;                 // working; never thrash
            if (FfbSamplesCaptured > _ffbAtCaptureStart) return false; // FFB flowing this capture
            // Spring-parameter captures are FFB flowing too (FS25-class games
            // never produce a force value); without this the escalation chain
            // would tell a user with working spring emulation that no FFB is
            // reaching the plugin.
            if (SpringUpdatesCaptured > _springsAtCaptureStart) return false;
            // The plugin's synthetic spring owns FFB (FS on wheels the game
            // sends only heartbeats to, so not even spring parameters arrive):
            // no game FFB is expected at all, don't escalate or warn.
            if (SyntheticFfbActive) return false;
            if (!_gameFfbExpected) return false;                  // not driving -> no FFB expected

            // If the user pinned a device that isn't a Logitech wheel, there's
            // no FFB to find and "try another USB port" would be wrong advice.
            // Don't escalate here; the plugin surfaces a targeted "that's not a
            // wheel, clear the override" notice instead.
            if (_manualOverride && _overrideVid != 0
                && !WheelDiscovery.IsSupportedWheel(_overrideVid, _overridePid))
                return false;

            int drivingMs = now - _gameFfbExpectedSinceMs;

            // Tier 2: driving a while with nothing -> retry once in whole-bus
            // mode (the per-device filter may be dropping the FFB endpoint).
            if (!_useBroadCapture && drivingMs >= BroadCaptureSwitchMs)
            {
                _useBroadCapture = true;
                Log($"FFB tap: {drivingMs} ms of active driving with no force feedback captured on " +
                    $"{_usbPcapInterface} dev {_deviceAddress}; retrying with whole-bus capture in case the " +
                    "per-device filter is dropping the wheel's FFB endpoint.");
                return true;   // ReaderLoop restarts the capture in -A mode
            }

            // Tier 3: whole-bus already on and still nothing -> warn once.
            if (_useBroadCapture && !_noFfbWarned && drivingMs >= NoFfbWarnMs)
            {
                _noFfbWarned = true;
                string msg = "Driving, but no game force feedback is reaching the plugin, so it has nothing " +
                    "to pass through. Most likely fixes, in order: (1) quit G HUB from the system tray " +
                    "(right-click its tray icon and Quit); it can intercept the wheel's force feedback; " +
                    "(2) make sure force " +
                    "feedback is enabled and this wheel is selected in the game's own settings; (3) run " +
                    "SimHub as administrator; (4) as a last resort try a different USB port, ideally a USB " +
                    "2.0 port on the back of the motherboard (not a hub or front-panel port), in case the " +
                    "capture driver can't see the wheel's traffic there.";
                Log("FFB tap: " + msg);
                try { OnNoFfbWarning?.Invoke(msg); } catch { }
            }
            return false;
        }

        // Runs on its own thread because the reader thread is the one that gets
        // stuck (a blocked, timeout-less read). See LivenessMinSends/Timeout.
        private void LivenessLoop()
        {
            while (!_stopping)
            {
                if (SleepInterruptible(LivenessCheckMs)) return;

                var proc = _proc;
                if (proc == null || proc.HasExited) continue;   // no active capture
                var probe = _sendActivityProbe;
                if (probe == null) continue;                    // not wired -> inert

                long parsed = LivenessProgress;
                long sends;
                try { sends = probe(); } catch { continue; }
                long nowTicks = _sw.ElapsedTicks;

                // Capture is making progress -> healthy, re-baseline.
                if (parsed != Interlocked.Read(ref _liveLastParsed))
                {
                    Interlocked.Exchange(ref _liveLastParsed, parsed);
                    Interlocked.Exchange(ref _liveLastSends, sends);
                    Interlocked.Exchange(ref _liveLastProgressTicks, nowTicks);
                    continue;
                }

                // Capture parsed nothing since last check. Only act if we were
                // clearly streaming to the wheel in the meantime (heartbeat
                // advanced past the floor) and the stall has lasted long enough.
                long stalledMs = (nowTicks - Interlocked.Read(ref _liveLastProgressTicks)) * 1000L / Stopwatch.Frequency;
                long sentSinceProgress = sends - Interlocked.Read(ref _liveLastSends);
                if (stalledMs >= LivenessTimeoutMs && sentSinceProgress >= LivenessMinSends)
                {
                    Log($"FFB tap: capture saw 0 packets from the wheel in {stalledMs} ms while {sentSinceProgress} were sent to it; " +
                        "the capture has stalled (wheel may have re-enumerated). Restarting it.");
                    _forceRevalidate = true;                    // re-locate by identity on restart
                    Interlocked.Exchange(ref _liveLastProgressTicks, nowTicks);  // avoid re-firing before restart settles
                    Interlocked.Exchange(ref _liveLastSends, sends);
                    try { proc.Kill(); } catch { }              // unblocks the reader -> ReaderLoop restarts
                }
            }
        }

        private void ReaderLoop()
        {
            // Register the pcap reader in the MMCSS "Pro Audio" band (NORMAL) so
            // a freshly captured FFB force is latched promptly even under game
            // CPU load; the 1 kHz pump that consumes it is already in this band.
            // Best-effort, reverted when the thread exits.
            using (MmcssScope.Enter("Pro Audio", MmcssScope.PriorityNormal))
                ReaderLoopCore();
        }

        private void ReaderLoopCore()
        {
            // Outer loop owns BOTH discovery (when there's no manual override)
            // and capture. Splitting them here means a stale-cache failure on
            // first try can be recovered by replugging the wheel mid-session
            // without restarting SimHub.
            //
            // Consecutive capture sessions that exited without producing a
            // single packet. Reset to 0 by a healthy session. Drives both the
            // restart backoff (fix 2) and the decision to re-run full
            // discovery (fix 1) instead of re-scanning the whole bus every
            // cycle.
            int consecutiveFailures = 0;

            while (!_stopping)
            {
                // Pre-flight: USBPcap capture needs administrator rights. If
                // SimHub isn't elevated, attempting the launch only fails (and a
                // retry loop just keeps failing / re-prompting), and elevation
                // can't be gained without restarting SimHub. So skip the attempt
                // entirely, show a clear "run as admin and restart" message, and
                // stay dormant. Restarting SimHub as admin reinitializes the tap.
                if (!HostElevated)
                {
                    Status = "SimHub is not running as administrator. Force-feedback pass-through needs it: turn on Run as administrator in SimHub's settings, then restart SimHub.";
                    if (!_loggedNotElevated)
                    {
                        Log("UsbPcapFfbTap: SimHub is not elevated; FFB pass-through is off until SimHub is restarted as administrator (not launching USBPcapCMD).");
                        _loggedNotElevated = true;
                    }
                    if (SleepInterruptible(ElevationBackoffMs)) break;
                    continue;
                }

                bool producedPackets = false;
                try
                {
                    if (!EnsureDeviceTarget(_forceRevalidate))
                    {
                        _forceRevalidate = false;
                        Status = "No supported wheel found on any USBPcap interface (FFB pass-through disabled). Retrying in 15s...";
                        Log(Status);
                        if (SleepInterruptible(RediscoveryRetryMs)) break;
                        continue;
                    }
                    _forceRevalidate = false;

                    long packetsBefore = PacketsParsed;
                    StartUsbPcapCmd();
                    ParseStream();
                    producedPackets = PacketsParsed > packetsBefore;
                }
                catch (System.ComponentModel.Win32Exception w32)
                    when (w32.NativeErrorCode == ErrorElevationRequired || w32.NativeErrorCode == ErrorCancelled)
                {
                    // USBPcapCMD requires admin and SimHub isn't elevated, or
                    // the user dismissed the UAC prompt. Retrying right away
                    // only re-prompts, so back off hard and say why rather than
                    // spinning a UAC loop. This does not touch the cached
                    // interface/address: re-discovery wouldn't fix elevation.
                    Status = "USBPcap needs administrator rights. Run SimHub as administrator to enable FFB pass-through.";
                    Log($"UsbPcapFfbTap: USBPcapCMD requires elevation (Win32 {w32.NativeErrorCode}); backing off {ElevationBackoffMs / 1000}s.");
                    try { _proc?.Kill(); } catch { }
                    try { _proc?.Dispose(); } catch { }
                    _proc = null;
                    if (SleepInterruptible(ElevationBackoffMs)) break;
                    continue;
                }
                catch (Exception ex)
                {
                    Status = $"Error: {ex.Message}";
                    Log($"UsbPcapFfbTap: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try { _proc?.Kill(); } catch { }
                    try { _proc?.Dispose(); } catch { }   // dispose every restart, not just GC-finalize
                    _proc = null;
                }

                if (_stopping) break;

                // Fix 1: reuse the discovered interface/address across restarts.
                // A session that produced packets is healthy. Only after a run
                // of empty/failed sessions do we assume the wheel moved (replug
                // -> new device address) and clear the cache so the next
                // iteration re-runs discovery. This keeps the expensive
                // full-bus `-A` scan off the hot path, which is what pegged a
                // CPU core when the wheel rim's separate DD base sat on its own
                // high-traffic USBPcap interface (re-scanned every cycle).
                if (producedPackets) consecutiveFailures = 0;
                else consecutiveFailures++;

                if (consecutiveFailures >= MaxCaptureFailuresBeforeRediscovery)
                {
                    // Auto mode: drop the cached target so discovery re-runs.
                    // Manual override: keep the pin but force an identity-based
                    // re-validate next iteration, so a replug (same wheel, new
                    // address) heals instead of looping on a dead address.
                    if (!_manualOverride) { _usbPcapInterface = null; _deviceAddress = 0; }
                    _forceRevalidate = true;
                    consecutiveFailures = 0;
                    // On some composite devices (e.g. G923) the per-device
                    // --devices filter crashes USBPcapCMD (0 packets, then
                    // EndOfStreamException) while the whole-bus -A capture
                    // works fine. If device-specific keeps failing, escalate
                    // to -A immediately rather than waiting for the
                    // GameFfbExpected timer (which needs 8 s of active
                    // driving - the crash cycle resets every 5 s so it
                    // never fires on affected systems).
                    if (!_useBroadCapture)
                    {
                        _useBroadCapture = true;
                        Log("FFB tap: device-specific capture failed 3 consecutive sessions with 0 packets; " +
                            "escalating to whole-bus (-A) capture (composite device / --devices filter issue).");
                    }
                }

                // Fix 2: backoff so a fast-failing capture can't respawn
                // USBPcapCMD in a tight loop. Healthy session -> floor; each
                // consecutive failure doubles up to the ceiling.
                int backoff = producedPackets
                    ? CaptureBackoffFloorMs
                    : Math.Min(CaptureBackoffCeilMs, CaptureBackoffFloorMs << Math.Min(consecutiveFailures, 8));
                if (SleepInterruptible(backoff)) break;
            }
        }

        // Sleep that returns true if interrupted by Stop() request.
        private bool SleepInterruptible(int ms)
        {
            int slept = 0;
            while (slept < ms)
            {
                if (_stopping) return true;
                int chunk = Math.Min(200, ms - slept);
                Thread.Sleep(chunk);
                slept += chunk;
            }
            return false;
        }

        private void StartUsbPcapCmd()
        {
            // Normally we filter to the wheel's device at the driver level
            // (--devices) to keep CPU low. After a no-FFB-while-driving escalation
            // we fall back to whole-bus capture (-A) and filter to the wheel in
            // our parser (see the `dev != _deviceAddress` guard below), because
            // on some composite devices --devices appears to drop the FFB
            // endpoint that -A captures fine.
            string args = _useBroadCapture
                ? $"-d {_usbPcapInterface} -A -o -"
                : $"-d {_usbPcapInterface} -o - --devices {_deviceAddress}";
            var psi = new ProcessStartInfo
            {
                FileName = _usbPcapCmdPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi);
            if (_proc == null) throw new InvalidOperationException("Process.Start returned null");
            // Kill-on-close job: the capture child must die with SimHub, or it
            // survives taskkill /F holding the USBPcap device + inherited
            // sockets and poisons the next session (see ChildProcessJob).
            ChildProcessJob.TryAssign(_proc, Log);
            // Baselines for the watchdog: FFB count at the start of this capture
            // session, and the next watchdog tick.
            _ffbAtCaptureStart = FfbSamplesCaptured;
            _springsAtCaptureStart = SpringUpdatesCaptured;
            _nextWatchdogMs = Environment.TickCount + WatchdogIntervalMs;
            // Liveness baseline: fresh capture gets a full grace window before
            // a stall can be declared. Uses the same selector the watchdog
            // reads, so a mode flip between captures cannot fake a stall (a
            // counter jump in the other direction just reads as progress).
            Interlocked.Exchange(ref _liveLastParsed, LivenessProgress);
            try { Interlocked.Exchange(ref _liveLastSends, _sendActivityProbe?.Invoke() ?? 0); } catch { }
            Interlocked.Exchange(ref _liveLastProgressTicks, _sw.ElapsedTicks);
            Status = $"Tapping {_usbPcapInterface} dev {_deviceAddress}{(_useBroadCapture ? " (whole-bus)" : "")}";
            Log($"UsbPcapFfbTap started: {_usbPcapInterface} dev {_deviceAddress}{(_useBroadCapture ? " (whole-bus capture)" : "")}");

            // Drain stderr so it doesn't fill its pipe buffer and stall the child.
            // Snapshot the process into a local: _proc gets reassigned by a later
            // capture restart, and closing over the field would make this thread
            // read the restarted process's stderr (or NRE on the disposed old one)
            // and leak, blocked in ReadLine, one thread per restart. Bound to this
            // process, the thread exits cleanly when this capture is killed.
            var stderrProc = _proc;
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = stderrProc.StandardError.ReadLine()) != null)
                        Log($"[USBPcapCMD] {line}");
                }
                catch { }
            }) { IsBackground = true, Name = "UsbPcapFfbTap-stderr" }.Start();
        }

        // Reusable 16-byte pcap record-header buffer. Reused across the parse
        // loop so the wheel's ~250-1000+ Hz OUT-report stream doesn't allocate a
        // fresh array per packet (steady Gen0 fuel that contributes to the
        // GC-pause-driven ring underruns). Safe to reuse: the header is only read
        // for caplen and handed to MaybeLogPcap, whose first param is discarded.
        private readonly byte[] _recHdr = new byte[16];

        private void ParseStream() => ParseFrom(_proc.StandardOutput.BaseStream);

        // Split from ParseStream so a test can drive the parser with a
        // synthetic capture stream (no child process).
        internal void ParseFrom(Stream s)
        {
            // ---- pcap global header (24 bytes, LE) ----
            byte[] gh = ReadExact(s, 24);
            uint magic    = BitConverter.ToUInt32(gh, 0);
            int  linkType = BitConverter.ToInt32(gh, 20);
            if (magic != 0xa1b2c3d4 || linkType != DLT_USBPCAP)
                throw new InvalidDataException($"Not a USBPcap stream (magic=0x{magic:x8}, linktype={linkType})");

            // Slot state describes the wheel's live FFB slots; a fresh capture
            // (first start, or a watchdog restart) knows nothing about them.
            ResetClassicState();
            _classicResetRequested = false;

            byte[] payload = new byte[1024];

            while (!_stopping)
            {
                ReadExactInto(s, _recHdr, 0, 16);
                int caplen = BitConverter.ToInt32(_recHdr, 8);
                if (caplen <= 0 || caplen > 65535)
                    throw new InvalidDataException($"caplen={caplen}");
                if (payload.Length < caplen) payload = new byte[caplen];
                ReadExactInto(s, payload, 0, caplen);
                Interlocked.Increment(ref _packetsParsed);

                // ---- USBPcap pseudo-header ----
                if (caplen < 27) continue;
                int headerLen = BitConverter.ToUInt16(payload, 0);
                if (headerLen < 27 || headerLen > caplen) continue;

                int dev      = BitConverter.ToUInt16(payload, 19);
                byte ep      = payload[21];
                byte xfer    = payload[22];

                // Byte logging runs ahead of the per-device filter so that a
                // pcap captured in whole-bus mode contains writes to OTHER
                // devices on the same root hub as well, not just our pinned
                // wheel. That's the diagnostic value when we suspect the
                // wheel's FFB is going to a different USB address than the
                // one we pinned (composite-device cases like the G923 Xbox's
                // XInput child). In --devices mode the upstream USBPcapCMD
                // filter already restricts the stream to our address, so
                // running the call early is a no-op there. MaybeLogPcap is
                // itself a cheap null-check when the toggle is off.
                bool isOut = (ep & 0x80) == 0;
                if (isOut) MaybeLogPcap(_recHdr, payload, caplen);

                if (dev != _deviceAddress) continue;
                Interlocked.Increment(ref _packetsForOurDevice);
                Interlocked.Exchange(ref _lastDevicePacketTicks, _sw.ElapsedTicks & TimestampMask);

                // Per-direction / per-transfer-type / per-endpoint breakdown.
                // OUT direction is the host writing to the wheel (FFB and our
                // Trueforce stream live here). The bit 7 of the endpoint byte
                // is direction (0=OUT, 1=IN); low 4 bits are endpoint number.
                int epNum  = ep & 0x0f;
                if (isOut)
                {
                    if (xfer < _outTransferTypeCounts.Length) _outTransferTypeCounts[xfer]++;
                    _outEndpointCounts[epNum]++;
                }

                // Classic Logitech FFB path: a non-Trueforce game writes force
                // as the slot-based classic protocol on an interrupt OUT
                // endpoint (the wheel's normal FFB endpoint). For interrupt OUT
                // the report data starts right after the USBPcap pseudo-header
                // (no setup stage, unlike the ep0 control path below).
                // Independent of the ep0 HID++ path; whichever transport the
                // running game uses latches the freshest value, and
                // TryGetFreshFfbTarget arbitrates by recency. See
                // HandleClassicFfbCommand for the protocol state machine.
                if (isOut && xfer == 0x01 && headerLen < caplen)
                    HandleClassicFfbCommand(payload, headerLen, caplen - headerLen);

                // Interrupt-OUT HID++ FFB path. Some wheels deliver the SAME
                // HID++ 0x8123 FFB long-form report as the ep0 control path
                // below, but as a raw interrupt OUT report instead of an ep0
                // SET_REPORT (no setup stage, report starts at headerLen).
                // Decoded from the Xbox G923 (C26E) byte trace 2026-05-17:
                // report 0x11, devIdx 0xff, feature index 0x0b, func 0x2d
                // (fn&0xf0==0x20), force = signed int16 big-endian at payload
                // offset 10-11 (identical encoding to the ep0 path; only the
                // feature index and transport differ). We feed the tuple to
                // the same feature-index resolver so 0x0b auto-promotes the
                // way RS50's 0x10 does, then extract once it matches. The ep0
                // path's resolver only sees ep0 traffic, so without this an
                // interrupt-only wheel never resolves and never matches.
                if (isOut && xfer == 0x01 && headerLen + 12 <= caplen
                    && payload[headerLen] == 0x11 && payload[headerLen + 1] == 0xff)
                {
                    byte iFeat = payload[headerLen + 2];
                    byte iFunc = payload[headerLen + 3];
                    RecordTupleSeen(0x11, iFeat, iFunc);
                    if ((iFunc & 0xf0) == 0x60) NoteLevelWrite(iFeat, payload[headerLen + 9]);
                    if (iFeat == _ffbFeatureIndex && (iFunc & 0xf0) == 0x20)
                    {
                        NoteForceTraffic(0x11);
                        short ffbTarget = (short)((payload[headerLen + 10] << 8) | payload[headerLen + 11]);
                        bool accept = _reportArbiter.Accept(0x11, ffbTarget, Environment.TickCount);
                        string decision = _reportArbiter.TakeDecision();
                        if (decision != null) Log("FFB tap: " + decision);
                        if (accept && !SimulateNoFfbCapture)
                        {
                            long ts = _sw.ElapsedTicks & TimestampMask;
                            long pk = (ts << 16) | (uint)(ushort)ffbTarget;
                            System.Threading.Interlocked.Exchange(ref _packed, pk);
                            System.Threading.Interlocked.Exchange(ref _lastSampleTicks, ts);
                            FfbSamplesCaptured++;
                            _ffbIndexConfirmed = true;   // real FFB flowed on this index; lock the resolver
                            NoteExtraction("interrupt-out", 0x11, iFeat, "hidpp-int16be@10");
                        }
                    }
                }

                MaybeConfirmCaptureFingerprint();
                MaybeEmitDiagnostics();
                if (MaybeWatchdog()) return;   // restart capture (whole-bus retry)
                if (!_ffbIndexConfirmed)
                {
                    long nowMs = Environment.TickCount;
                    if (nowMs >= _nextFfbResolveTicks)
                    {
                        _nextFfbResolveTicks = nowMs + FfbResolveIntervalMs;
                        MaybeResolveFfbFeatureIndex();
                    }
                }
                if (xfer != 0x02) continue;             // control transfer
                ControlTransfersOnOurDevice++;
                if ((ep & 0x7f) != 0x00) continue;       // ep0
                Ep0ControlTransfersOnOurDevice++;
                if (headerLen < 28) continue;
                byte stage = payload[27];
                if (stage != 0) continue;                // setup stage only

                int setupOffset = headerLen;
                if (setupOffset + 8 > caplen) continue;
                byte bmRequestType = payload[setupOffset + 0];
                byte bRequest      = payload[setupOffset + 1];
                if (bmRequestType != 0x21 || bRequest != 0x09) continue; // HID Set_Report
                SetReportsOnOurDevice++;

                int dataOffset = setupOffset + 8;
                int dataLen = caplen - dataOffset;
                if (dataLen < 12) continue;

                // HID++ payload: [reportID][devIdx][featIdx][funcByte][params...]
                byte reportId = payload[dataOffset + 0];
                byte featIdx  = payload[dataOffset + 2];
                byte funcByte = payload[dataOffset + 3];
                RecordTupleSeen(reportId, featIdx, funcByte);
                // A rev-light level write (long report, fn6): remember what the
                // strip was told to show so a slot rewrite can put it back.
                if (reportId == 0x11 && (funcByte & 0xf0) == 0x60)
                    NoteLevelWrite(featIdx, payload[dataOffset + 9]);

                // G-series FFB: HID++ page 0x8123 long (0x11) or very-long
                // (0x12) form, function 2 (high nibble of funcByte), at the
                // per-wheel-resolved feature index. Both report IDs share the
                // same header+payload layout (force = signed int16, big-endian,
                // at offset 10-11). Which one carries the game's stream is a
                // per-session driver fact (RS50: mostly 0x12; the owner's G PRO:
                // 0x11 for months, then two sessions of 0x12 only on
                // 2026-08-28), so FfbReportArbiter picks the live report from
                // the traffic and the other one's same-shaped management
                // writes are ignored.
                if ((reportId == 0x11 || reportId == 0x12)
                    && featIdx == _ffbFeatureIndex && (funcByte & 0xf0) == 0x20)
                {
                    NoteForceTraffic(reportId);
                    short ffbTarget = (short)((payload[dataOffset + 10] << 8) | payload[dataOffset + 11]);
                    bool accept = _reportArbiter.Accept(reportId, ffbTarget, Environment.TickCount);
                    string decision = _reportArbiter.TakeDecision();
                    if (decision != null) Log("FFB tap: " + decision);

                    if (accept && !SimulateNoFfbCapture)
                    {
                        long timestamp = _sw.ElapsedTicks & TimestampMask;
                        long packed = (timestamp << 16) | (uint)(ushort)ffbTarget;
                        System.Threading.Interlocked.Exchange(ref _packed, packed);
                        System.Threading.Interlocked.Exchange(ref _lastSampleTicks, timestamp);
                        FfbSamplesCaptured++;
                        _ffbIndexConfirmed = true;   // real FFB flowed on this index; lock the resolver
                        NoteExtraction("ep0-ctrl", reportId, featIdx, "hidpp-int16be@10");
                    }
                }
            }
            CloseRawLog();
        }

        // ---------- classic Logitech FFB protocol (slot state machine) -------
        //
        // The G923 PS/PC (C266) is the only supported wheel that never speaks
        // HID++: its own captures (FH5 and ACC, 2026-05-17) contain ZERO ep0
        // SET_REPORT and zero "11 ff" interrupt HID++ writes, so every guard on
        // the HID++ paths (feature-index pinning, the 0x11/0x12 report
        // arbiter) is dead code on it. Its force arrives as
        // the classic slot protocol on ep01, which is STATEFUL: a force is
        // downloaded into one of four slots and plays until it is stopped.
        //
        // The old handler matched one shape ("11 08", slot 1 download-and-play,
        // variable force) and treated every other command as if it did not
        // exist. That included STOP (109 of them in a 5 minute FH5 capture): on
        // a stop the wheel goes quiet but the tap kept replaying the last
        // downloaded force for up to FfbTargetMaxAgeMs (10 s), so our ep3
        // stream held a force the game had already cancelled. That is the same
        // failure ClearLastFfbTarget was written for (issue #13's full-lock),
        // one layer down, and the reason a strong force can be held long after
        // the game stopped commanding it.
        //
        // Byte 0 is (slotMask << 4) | command; byte 1 is the force type on the
        // commands that carry one. 0xF8 is the whole-byte extended-command
        // prefix (rev LEDs on this wheel), not a slot/command pair.
        private const int  ClassicSlotCount   = 4;
        private const byte ClassicTypeVariable = 0x08;   // params: force X at byte 2, offset-binary
        private const byte ClassicTypeHiResSpring = 0x0b; // params: dead band + slopes + clip (see ParseHiResSpring)
        private const byte ClassicMaxForceType = 0x0e;   // highest defined type (high-res auto-center)
        private const byte ClassicCmdDownload        = 0x0;
        private const byte ClassicCmdDownloadAndPlay = 0x1;
        private const byte ClassicCmdPlay            = 0x2;
        private const byte ClassicCmdStop            = 0x3;
        private const byte ClassicCmdRefreshForce    = 0xc;
        private const byte ClassicExtendedCommand    = 0xf8;

        // Parser-thread only. Per slot: the last decoded force, whether we
        // could decode it at all, and whether the slot is currently playing.
        private readonly short[] _classicSlotForce   = new short[ClassicSlotCount];
        private readonly bool[]  _classicSlotDecoded = new bool[ClassicSlotCount];
        private readonly bool[]  _classicSlotPlaying = new bool[ClassicSlotCount];
        private short _classicLastPublished;
        private bool  _classicHavePublished;
        // Set by ClearLastFfbTarget (plugin thread) and honored by the parser,
        // so the slot state is only ever mutated on the parser thread. Without
        // it a bare PLAY after a pause could republish the pre-pause force.
        private volatile bool _classicResetRequested;

        private void ResetClassicState()
        {
            Array.Clear(_classicSlotForce, 0, ClassicSlotCount);
            Array.Clear(_classicSlotDecoded, 0, ClassicSlotCount);
            Array.Clear(_classicSlotPlaying, 0, ClassicSlotCount);
            Array.Clear(_classicSlotSpring, 0, ClassicSlotCount);
            _playingSprings = null;
            _classicLastPublished = 0;
            _classicHavePublished = false;
        }

        // ---------- classic spring emulation (FS25-class games) --------------
        //
        // Some classic-protocol games never stream a force value at all: they
        // command force as a parametric HIGH-RESOLUTION SPRING (type 0x0b) and
        // let the wheel's firmware compute torque from its own position.
        // Farming Simulator 25 drives its entire FFB this way (a servo spring
        // whose dead band tracks where the game wants the wheel; reporter
        // capture 2026-08-06), and the owner's FH5 capture shows the same type
        // holding an auto-center at 0x80. In Trueforce mode the firmware does
        // not run that math, which reads as "FFB dead" even though the game is
        // commanding centering the whole time. The parser stores the playing
        // springs' parameters here; the plugin evaluates them against the
        // wheel's PHYSICAL position (WheelSteeringReader) at the 1 kHz pump
        // via TryEvaluateClassicSprings and streams the result as cur on ep3.
        //
        // Wire layout (classic 7-byte report, after the slot/cmd + type pair):
        //   byte2 = D1 upper 8 bits, byte3 = D2 upper 8 bits (11-bit dead band
        //   edges over the full lock range), byte4 = K2<<4 | K1 (per-side
        //   slope nibbles), byte5 = [D2 low 3][S2][D1 low 3][S1] (dead band
        //   LSBs + per-side invert bits), byte6 = CLIP (torque saturation).
        // Validated against the FS25 corpus (dead band tracks steering, edges
        // ordered, S bits zero); the torque model below (fraction = deviation
        // * 2^K, capped at CLIP) is the plausible reading of the slope nibble
        // and is the one thing that still needs an on-wheel confirmation.
        private sealed class ClassicSpring
        {
            public float D1, D2;     // dead band edges, 0..1 of full lock range
            public int   K1, K2;     // per-side slope exponents (0..15)
            public bool  S1, S2;     // per-side invert (push away, not toward)
            public float Clip;       // torque saturation, 0..1
        }

        // Slot springs are parser-thread state, like the force arrays above.
        // _playingSprings is the cross-thread snapshot: rebuilt (fresh array,
        // reference-swapped) whenever a change touches a spring, read by the
        // pump thread. Elements are never mutated after publish.
        private readonly ClassicSpring[] _classicSlotSpring = new ClassicSpring[ClassicSlotCount];
        private volatile ClassicSpring[] _playingSprings;

        /// <summary>Spring parameter writes captured into a playing slot. The
        /// spring analogue of FfbSamplesCaptured: proof the game is commanding
        /// FFB even though no force value ever appears on the wire.</summary>
        public long SpringUpdatesCaptured { get; private set; }
        private long _springsAtCaptureStart;

        /// <summary>Set by the plugin while its synthetic spring owns the
        /// wheel's FFB (FS spring mode): no game FFB is expected on the bus,
        /// so the no-FFB watchdog must not escalate capture modes or warn.
        /// Volatile-free by design: written from the plugin tick, read from
        /// the parse loop, and a stale read for one watchdog interval is
        /// harmless.</summary>
        public bool SyntheticFfbActive { get; set; }

        /// <summary>True while any captured classic spring is playing. LED /
        /// OLED writes gate on "game FFB is quiet"; a playing spring is game
        /// FFB even though TryGetFreshFfbTarget stays null, so the quiet
        /// probe must consult this too.</summary>
        public bool AnyClassicSpringPlaying => !SimulateNoFfbCapture && _playingSprings != null;

        private static ClassicSpring ParseHiResSpring(byte[] p, int off, int len)
        {
            if (len < 7) return null;
            byte lo = p[off + 5];
            int d1 = (p[off + 2] << 3) | ((lo >> 1) & 0x07);
            int d2 = (p[off + 3] << 3) | ((lo >> 5) & 0x07);
            return new ClassicSpring
            {
                // Guard an inverted band; the force math assumes D1 <= D2.
                D1   = Math.Min(d1, d2) / 2047f,
                D2   = Math.Max(d1, d2) / 2047f,
                K1   = p[off + 4] & 0x0f,
                K2   = (p[off + 4] >> 4) & 0x0f,
                S1   = (lo & 0x01) != 0,
                S2   = (lo & 0x10) != 0,
                Clip = p[off + 6] / 255f,
            };
        }

        private void PublishSpringSnapshot()
        {
            int n = 0;
            for (int i = 0; i < ClassicSlotCount; i++)
                if (_classicSlotPlaying[i] && _classicSlotSpring[i] != null) n++;
            if (n == 0) { _playingSprings = null; return; }
            var arr = new ClassicSpring[n];
            int j = 0;
            for (int i = 0; i < ClassicSlotCount; i++)
                if (_classicSlotPlaying[i] && _classicSlotSpring[i] != null) arr[j++] = _classicSlotSpring[i];
            _playingSprings = arr;
        }

        /// <summary>Evaluate the playing classic springs at the wheel's
        /// physical position. steerNorm is -1..1 (full left..full right).
        /// Returns null when no spring is playing; 0 when the wheel sits
        /// inside every dead band (the game commands a spring, the spring
        /// commands nothing right now). Sign space matches the variable-force
        /// decode and the stationary spring: positive pulls toward lower
        /// steer, so a wheel right of the band gets a positive (leftward)
        /// pull. Allocation-free; safe at 1 kHz.</summary>
        public short? TryEvaluateClassicSprings(float steerNorm)
        {
            if (SimulateNoFfbCapture) return null;
            var springs = _playingSprings;
            if (springs == null) return null;
            if (steerNorm < -1f) steerNorm = -1f; else if (steerNorm > 1f) steerNorm = 1f;
            float p = (steerNorm + 1f) * 0.5f;
            float sum = 0f;
            for (int i = 0; i < springs.Length; i++)
            {
                var s = springs[i];
                if (p > s.D2)
                {
                    float f = (p - s.D2) * (1 << s.K2);
                    if (f > s.Clip) f = s.Clip;
                    sum += s.S2 ? -f : f;
                }
                else if (p < s.D1)
                {
                    float f = (s.D1 - p) * (1 << s.K1);
                    if (f > s.Clip) f = s.Clip;
                    sum += s.S1 ? f : -f;
                }
            }
            int v = (int)(sum * 32767f);
            if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
            return (short)v;
        }

        private void HandleClassicFfbCommand(byte[] payload, int off, int len)
        {
            if (_classicResetRequested)
            {
                _classicResetRequested = false;
                ResetClassicState();
            }
            if (len < 2) return;

            byte b0 = payload[off];
            if (b0 == ClassicExtendedCommand) return;   // rev LEDs and friends

            int slots = (b0 >> 4) & 0x0f;
            int cmd   = b0 & 0x0f;
            // Slot mask 0 is not a classic command. This is what keeps our OWN
            // ep3 Trueforce stream (report 0x01) out of the state machine.
            if (slots == 0) return;

            // HID++ firewall. Report IDs 0x10 / 0x11 / 0x12 collide with slot-1
            // download / download-and-play / play, so a HID++ wheel's FFB would
            // otherwise drive this state machine and clobber _packed. Byte 1 is
            // the HID++ device index (0xff on a directly attached wheel) and a
            // classic force type is never above 0x0e, so one range check
            // separates them. Every command we act on carries either a force
            // type or an unused zero here.
            byte typeOrPad = payload[off + 1];
            if (typeOrPad > ClassicMaxForceType) return;

            bool decodedForce = false;
            bool springsTouched = false;
            bool springIntoPlayingSlot = false;
            switch (cmd)
            {
                case ClassicCmdDownload:          // load a slot, do NOT play it
                case ClassicCmdDownloadAndPlay:
                case ClassicCmdRefreshForce:      // retune a force already playing
                {
                    // VARIABLE decodes to a scalar force; HI-RES SPRING decodes
                    // to parameters the plugin evaluates against the wheel's
                    // physical position. Any other type still marks the slot
                    // playing so a later STOP is accounted for, but contributes
                    // nothing: guessing at an undecoded payload is how you
                    // invent a force the game never asked for.
                    bool isVariable = typeOrPad == ClassicTypeVariable && len >= 3;
                    short f = isVariable ? (short)((payload[off + 2] - 0x80) << 8) : (short)0;
                    ClassicSpring spring = typeOrPad == ClassicTypeHiResSpring
                        ? ParseHiResSpring(payload, off, len) : null;
                    for (int i = 0; i < ClassicSlotCount; i++)
                    {
                        if ((slots & (1 << i)) == 0) continue;
                        if (cmd == ClassicCmdRefreshForce && !_classicSlotPlaying[i]) continue;
                        // Whatever this download is, it overwrites the slot:
                        // a spring replaced by a variable force (or any other
                        // type) must stop contributing spring torque.
                        if (_classicSlotSpring[i] != null) { _classicSlotSpring[i] = null; springsTouched = true; }
                        if (isVariable)
                        {
                            _classicSlotForce[i]   = f;
                            _classicSlotDecoded[i] = true;
                            decodedForce = true;
                        }
                        else if (spring != null)
                        {
                            _classicSlotSpring[i]  = spring;
                            _classicSlotDecoded[i] = false;   // no scalar; the spring path owns this slot
                            springsTouched = true;
                        }
                        else
                        {
                            _classicSlotDecoded[i] = false;
                        }
                        if (cmd == ClassicCmdDownloadAndPlay) _classicSlotPlaying[i] = true;
                        if (spring != null && _classicSlotPlaying[i]) springIntoPlayingSlot = true;
                    }
                    break;
                }
                case ClassicCmdPlay:
                    for (int i = 0; i < ClassicSlotCount; i++)
                        if ((slots & (1 << i)) != 0)
                        {
                            _classicSlotPlaying[i] = true;
                            if (_classicSlotSpring[i] != null) springsTouched = true;
                        }
                    break;
                case ClassicCmdStop:
                    for (int i = 0; i < ClassicSlotCount; i++)
                        if ((slots & (1 << i)) != 0)
                        {
                            _classicSlotPlaying[i] = false;
                            if (_classicSlotSpring[i] != null) springsTouched = true;
                        }
                    break;
                default:
                    // Default spring on/off, set default spring, dead band and
                    // the rest do not change the commanded force.
                    return;
            }

            if (springsTouched) PublishSpringSnapshot();
            // A spring landing in a playing slot is captured game FFB, even
            // though no force value exists to publish: count it so the no-FFB
            // escalation chain (whole-bus retry + user warning) stands down.
            // A bare download into a stopped slot does NOT count, same
            // reasoning as the never-played zero below.
            if (springIntoPlayingSlot && !SimulateNoFfbCapture)
            {
                SpringUpdatesCaptured++;
                NoteExtraction("interrupt-out", -1, -1, "classic-spring 0x0b (hi-res spring params)");
            }

            int sum = 0;
            for (int i = 0; i < ClassicSlotCount; i++)
                if (_classicSlotPlaying[i] && _classicSlotDecoded[i]) sum += _classicSlotForce[i];
            if (sum > short.MaxValue) sum = short.MaxValue;
            else if (sum < short.MinValue) sum = short.MinValue;
            short ffbTarget = (short)sum;

            // Publish when we learned something: a fresh force, or a state
            // change that moved the effective force (a STOP publishing 0 is the
            // whole point). Commands that taught us nothing must NOT refresh
            // the timestamp, or an undecodable slot's traffic would keep a
            // stale value looking fresh.
            if (!decodedForce && _classicHavePublished && ffbTarget == _classicLastPublished) return;
            // Never publish a zero out of nowhere. Until a force has actually
            // played, a bare download or a stop leaves the wheel commanding
            // nothing, and counting that as a captured sample would mark a dead
            // capture as working (and make "the game is writing FFB" true on a
            // game that never played any).
            if (!_classicHavePublished && ffbTarget == 0) return;
            _classicLastPublished = ffbTarget;
            _classicHavePublished = true;
            if (SimulateNoFfbCapture) return;

            long ts = _sw.ElapsedTicks & TimestampMask;
            long pk = (ts << 16) | (uint)(ushort)ffbTarget;
            System.Threading.Interlocked.Exchange(ref _packed, pk);
            System.Threading.Interlocked.Exchange(ref _lastSampleTicks, ts);
            FfbSamplesCaptured++;
            NoteExtraction("interrupt-out", -1, -1, "classic-slot int8@2 (Logitech slot protocol)");
        }

        private void RecordTupleSeen(byte reportId, byte featIdx, byte funcByte)
        {
            int key = (reportId << 16) | (featIdx << 8) | (funcByte & 0xf0);
            lock (_tupleLock)
            {
                _tupleCounts.TryGetValue(key, out long count);
                _tupleCounts[key] = count + 1;
            }
        }

        // Promote the dominant HID++ FFB feature index for any wheel whose
        // firmware places page 0x8123 at an index other than the 0x0e seed
        // (RS50 -> 0x10, G PRO stays 0x0e). The FFB feature streams at
        // ~250-500 Hz during play; HID++ settings features are occasional, so
        // the dominant func&0xf0==0x20 index by count (summing reportId 0x11
        // and 0x12) is the FFB feature. Parser-thread only; called from the parse loop on the
        // FfbResolveIntervalMs cadence until _ffbIndexConfirmed, so first FFB
        // on a non-0x0e wheel latches within a few hundred ms of gameplay (the
        // seed means G PRO is never delayed).
        //
        // This is re-entrant and self-correcting, NOT a one-shot latch. The
        // old one-shot version had a hole: with a single func-0x20 tuple seen
        // so far, secondCount==0 makes the "4x runner-up" dominance test
        // trivially true (bestCount>=0), so the FIRST index to reach the sample
        // floor won permanently. If a non-FFB func-0x20 burst (a menu/settings
        // write before driving) reached the floor first, the wrong index
        // latched for the whole session and FFB never flowed until a SimHub
        // restart. Now we keep re-evaluating until real FFB is actually
        // extracted (_ffbIndexConfirmed): cumulative counts mean the true,
        // high-rate FFB index overtakes a stale wrong winner within ~1 s of
        // driving, we switch to it, force flows, and only then do we lock.
        private void MaybeResolveFfbFeatureIndex()
        {
            if (_ffbIndexPinned) return;      // hardware-confirmed; never vote
            if (_ffbIndexConfirmed) return;
            // No per-wheel gate: RS50 (C276) resolves to feat 0x10 by the same
            // dominant-tuple rule that resolves G PRO to 0x0e (issue #5 woTF
            // Windows capture confirmed RS50 native FFB is HID++ 0x8123 fn2).

            // Sum func-0x20 counts per feature index across BOTH HID++ long
            // (0x11) and very-long (0x12) reports. A wheel that splits its FFB
            // across both report IDs (RS50: 18 on 0x12, 8 on 0x11, same index
            // 0x10) must have those reinforce one index, not compete as two
            // separate keys, or the 4x-dominance test below sees 18 vs 8 for
            // the SAME index and never switches.
            var perIndex = new Dictionary<byte, long>();
            lock (_tupleLock)
            {
                foreach (var kv in _tupleCounts)
                {
                    // Key = (reportId<<16)|(featIdx<<8)|(funcByte&0xf0).
                    byte rid = (byte)(kv.Key >> 16);
                    if (rid != 0x11 && rid != 0x12) continue;      // long / very-long form
                    if ((byte)kv.Key != 0x20) continue;            // function 2
                    byte f = (byte)(kv.Key >> 8);
                    perIndex.TryGetValue(f, out long acc);
                    perIndex[f] = acc + kv.Value;
                }
            }

            byte bestIdx = 0;
            long bestCount = 0, secondCount = 0;
            foreach (var kv in perIndex)
            {
                if (kv.Value > bestCount) { secondCount = bestCount; bestCount = kv.Value; bestIdx = kv.Key; }
                else if (kv.Value > secondCount) { secondCount = kv.Value; }
            }

            if (bestCount < FfbIndexMinSamples) return;   // not enough data yet

            // Winner already matches the index we extract on: nothing to switch.
            // Mark resolved for the UI; real confirmation happens when force is
            // actually extracted (sets _ffbIndexConfirmed, which stops us).
            if (bestIdx == _ffbFeatureIndex)
            {
                _ffbIndexResolved = true;
                return;
            }

            // Winner differs from the current index. Only switch when it's
            // clearly dominant (>=4x the runner-up) so a stray settings write
            // can't pull us off a good index. With a stale wrong winner sitting
            // in second place, the real FFB index has to out-count it 4:1,
            // which a high-rate FFB stream does within ~1 s of driving; that is
            // the self-heal. (When the winner is the only func-0x20 tuple,
            // secondCount==0 and this still admits the seed->real first switch,
            // exactly as the clean RS50 path needs; the re-entrancy is what
            // makes a subsequent wrong-first-winner recoverable.)
            //
            // Exception: if the current seed index has ZERO func-0x20 samples
            // it is provably wrong (no FFB has ever arrived on it). In that
            // case the 4x gate is too strict: the runner-up count comes from
            // unrelated HID++ writes (e.g. LED feature 0x12 keepalives) that
            // will never be outraced 4:1 because the seed never moves at all.
            // Dropping to the sample-floor check alone is safe here because
            // "pull us off a good index" requires the current index to actually
            // have samples; a silent seed cannot be good.
            perIndex.TryGetValue(_ffbFeatureIndex, out long seedSamples);
            bool seedIsDeadSilent = seedSamples == 0;
            if (bestCount >= secondCount * 4 || seedIsDeadSilent)
            {
                byte old = _ffbFeatureIndex;
                _ffbFeatureIndex = bestIdx;
                _ffbIndexResolved = true;
                Log($"FFB tap: selected HID++ 0x8123 feature index 0x{bestIdx:X2} " +
                    $"(was 0x{old:X2}); {bestCount} samples, runner-up {secondCount}. " +
                    "Will confirm once force is extracted.");
            }
        }

        private void MaybeEmitDiagnostics()
        {
            long now = Environment.TickCount;
            if (now < _nextDiagEmitTicks) return;
            _nextDiagEmitTicks = now + DiagEmitIntervalMs;

            // Build a short top-N tuple histogram. With AC + G PRO we expect
            // a single dominant tuple (0x11, 0x0e, 0x20). Multiple tuples is
            // the smoking-gun signal that we should investigate.
            string tuples;
            lock (_tupleLock)
            {
                if (_tupleCounts.Count == 0) tuples = "(none)";
                else
                {
                    var parts = new List<string>(_tupleCounts.Count);
                    foreach (var kv in _tupleCounts)
                    {
                        byte r = (byte)(kv.Key >> 16);
                        byte f = (byte)(kv.Key >> 8);
                        byte u = (byte)(kv.Key);
                        parts.Add($"({r:X2},{f:X2},{u:X2})={kv.Value}");
                    }
                    tuples = string.Join(" ", parts);
                }
            }
            // Build the OUT-endpoint histogram (only emit non-zero slots so
            // the line stays readable when one endpoint dominates).
            var epOut = new List<string>();
            for (int i = 0; i < _outEndpointCounts.Length; i++)
                if (_outEndpointCounts[i] > 0) epOut.Add($"ep{i}={_outEndpointCounts[i]}");

            Log($"FFB tap diag: packets={PacketsForOurDevice} " +
                $"out_ctrl={ControlOutOnOurDevice} out_int={InterruptOutOnOurDevice} " +
                $"out_bulk={BulkOutOnOurDevice} out_iso={IsoOutOnOurDevice} " +
                $"out_by_ep=[{string.Join(" ", epOut)}] " +
                $"ep0ctrl={Ep0ControlTransfersOnOurDevice} setrep={SetReportsOnOurDevice} " +
                $"ffbIdx=0x{_ffbFeatureIndex:X2}{(_ffbIndexConfirmed ? "**" : _ffbIndexResolved ? "*" : "")} " +
                $"live={(_reportArbiter.LiveReport != 0 ? $"0x{_reportArbiter.LiveReport:X2}" : "none")} " +
                $"matched={FfbSamplesCaptured} tuples=[{tuples}]" +
                (SpringUpdatesCaptured > 0
                    ? $" springs={SpringUpdatesCaptured}{(_playingSprings != null ? " (playing)" : "")}"
                    : "") +
                (_rawLogStream != null ? $" trace={RawLogBytesWritten}b" : ""));
        }

        // Append one packet to the pcap trace. Wireshark (with USBPcap)
        // opens the file directly because we write a real DLT_USBPCAP pcap
        // stream: 24-byte global header on first packet, then per-packet
        // 16-byte record headers + the full pseudo-header + payload that
        // USBPcap originally emitted. Wall-clock timestamps so the recipient
        // sees real times in Wireshark instead of stopwatch ticks. Bounded by
        // RawLogMaxBytes; once hit, we close and warn one time.
        private void MaybeLogPcap(byte[] _, byte[] payload, int caplen)
        {
            string path = _rawLogPath;
            if (path == null)
            {
                CloseRawLog();
                return;
            }
            if (_rawLogStream == null)
            {
                try
                {
                    // Create (truncate). Each enable starts a fresh trace,
                    // and the global header below assumes byte 0 of the
                    // file is the magic.
                    _rawLogStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    WritePcapGlobalHeader(_rawLogStream);
                    _rawLogBytesWritten = 24;
                    Log($"FFB tap: pcap trace opened at {path} (Wireshark + USBPcap dissector).");
                }
                catch (Exception ex)
                {
                    Log($"FFB tap: failed to open pcap trace {path}: {ex.Message}");
                    _rawLogPath = null;
                    return;
                }
            }
            if (_rawLogBytesWritten >= RawLogMaxBytes)
            {
                Log($"FFB tap: pcap trace hit {RawLogMaxBytes / (1024 * 1024)} MB cap; disabling. " +
                    "Toggle off and on in Diagnostics to reset.");
                CloseRawLog();
                _rawLogPath = null;
                return;
            }
            try
            {
                DateTime now = DateTime.UtcNow;
                long delta = (now - UnixEpoch).Ticks;
                uint secs  = (uint)(delta / TimeSpan.TicksPerSecond);
                uint usecs = (uint)((delta % TimeSpan.TicksPerSecond) / 10); // 1 tick = 100 ns
                byte[] rec = new byte[16];
                WriteUint32Le(rec, 0,  secs);
                WriteUint32Le(rec, 4,  usecs);
                WriteUint32Le(rec, 8,  (uint)caplen);  // captured length
                WriteUint32Le(rec, 12, (uint)caplen);  // original length (same; we don't truncate)
                _rawLogStream.Write(rec, 0, rec.Length);
                _rawLogStream.Write(payload, 0, caplen);
                Interlocked.Add(ref _rawLogBytesWritten, rec.Length + caplen);
            }
            catch (Exception ex)
            {
                Log($"FFB tap: pcap trace write failed: {ex.Message}");
                CloseRawLog();
            }
        }

        private static void WritePcapGlobalHeader(Stream s)
        {
            // pcap "classic" global header. Wireshark recognizes DLT_USBPCAP
            // (linktype 249) so the USBPcap dissector kicks in automatically.
            byte[] gh = new byte[24];
            WriteUint32Le(gh, 0,  0xa1b2c3d4); // magic_number
            WriteUint16Le(gh, 4,  2);          // version_major
            WriteUint16Le(gh, 6,  4);          // version_minor
            WriteInt32Le (gh, 8,  0);          // thiszone (UTC)
            WriteUint32Le(gh, 12, 0);          // sigfigs
            WriteUint32Le(gh, 16, 65535);      // snaplen
            WriteUint32Le(gh, 20, 249);        // network = DLT_USBPCAP
            s.Write(gh, 0, gh.Length);
        }

        private static void WriteUint32Le(byte[] buf, int offset, uint v)
        {
            buf[offset + 0] = (byte)(v);
            buf[offset + 1] = (byte)(v >> 8);
            buf[offset + 2] = (byte)(v >> 16);
            buf[offset + 3] = (byte)(v >> 24);
        }
        private static void WriteInt32Le(byte[] buf, int offset, int v) => WriteUint32Le(buf, offset, (uint)v);
        private static void WriteUint16Le(byte[] buf, int offset, ushort v)
        {
            buf[offset + 0] = (byte)(v);
            buf[offset + 1] = (byte)(v >> 8);
        }

        private void CloseRawLog()
        {
            var s = _rawLogStream;
            if (s == null) return;
            try { s.Flush(); } catch { }
            try { s.Dispose(); } catch { }
            _rawLogStream = null;
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            byte[] buf = new byte[n];
            ReadExactInto(s, buf, 0, n);
            return buf;
        }

        private static void ReadExactInto(Stream s, byte[] buf, int offset, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, offset + got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
        }

        private void Log(string msg)
        {
            var l = Logger;
            if (l != null) try { l(msg); } catch { }
        }
    }
}
