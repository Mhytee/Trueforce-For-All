// Reads AC's outgoing HID++ FFB target from the USB bus by spawning USBPcapCMD
// as a child process, parsing its pcap stdout, and latching the most-recent
// FFB target value for the Trueforce stream to inject into ep3 bytes 6-9.
//
// AC sends DirectInput-equivalent FFB to the wheel as HID Set_Output_Reports
// on ep0 (control endpoint). The actual force command is HID++ feature page
// 0x8123 (G-series force feedback) function 2 long-form messages, signed
// 16-bit big-endian at offset 10-11 of the HID++ payload. The firmware-
// assigned feature *index* varies per wheel (0x0e on G PRO); it is seeded
// to 0x0e and auto-resolved per wheel (see _ffbFeatureIndex). When we
// stream Trueforce on ep3, the wheel uses
// bytes 6-9 of our packet as motor torque, ignoring AC's ep0 commands. By
// mirroring AC's commands into bytes 6-9, FFB and Trueforce coexist.
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
        public long PacketsParsed { get; private set; }
        public long FfbSamplesCaptured { get; private set; }

        // Diagnostic counters. Written only by the parser thread; read by any
        // thread via property getters. Triage tool for the case where the tap
        // is running but FFB pass-through still feels broken: tells us which
        // endpoint(s) and transfer type(s) the game is actually using.
        // Critical when the resolved 0x8123 feature index is wrong or the
        // game uses an unexpected HID++ shape; the tuple histogram surfaces it.
        public long PacketsForOurDevice { get; private set; }
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
        // via HID++ feature page 0x8123, but the firmware-assigned feature
        // *index* varies per model/firmware. G PRO places it at 0x0e, so we
        // seed with that and behaviour on G PRO is unchanged from the first
        // packet. For any other index, MaybeResolveFfbFeatureIndex() promotes
        // the dominant high-rate (reportId 0x11, func 0x20) tuple's index.
        //
        // The RS50 (C276) IS in scope here. Windows USBPcap evidence (issue #5
        // woTF capture, BeamNG + G Hub) proves RS50 native FFB on Windows is
        // HID++ feat 0x10 (page 0x8123) fn2, BE16 force at payload offset
        // 10-11: structurally identical to G PRO's 0x0e path, just a different
        // index. mescon's "raw report-0x01 on ep3" is its Linux *driver's* own
        // transport choice, NOT how the Windows runtime drives the wheel.
        // So the resolver auto-promotes 0x10 on RS50 with no special-casing.
        private const byte FfbFeatureIndexSeed = 0x0e;
        private const long FfbIndexMinSamples = 200;   // min count before switching
        private volatile byte _ffbFeatureIndex = FfbFeatureIndexSeed;
        private bool _ffbIndexResolved;                 // parser-thread only
        public byte ResolvedFfbFeatureIndex => _ffbFeatureIndex;
        public bool IsFfbFeatureIndexResolved => _ffbIndexResolved;

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
        // diagnostics interval. Only runs until _ffbIndexResolved; after that
        // the gate stops calling it entirely (zero steady-state cost).
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
            return true;
        }

        public void Stop()
        {
            _stopping = true;
            try { _proc?.Kill(); } catch { }
            try { _readerThread?.Join(2000); } catch { }
            _readerThread = null;
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

        // ---------- reader thread ----------

        private void ReaderLoop()
        {
            // Outer loop owns BOTH discovery (when there's no manual override)
            // and capture. Splitting them here means a stale-cache failure on
            // first try can be recovered by replugging the wheel mid-session
            // without restarting SimHub.
            while (!_stopping)
            {
                try
                {
                    if (!_manualOverride && (string.IsNullOrEmpty(_usbPcapInterface) || _deviceAddress <= 0))
                    {
                        Status = "Discovering wheel on USB bus...";
                        Log(Status);
                        var hit = WheelUsbDiscovery.Find(_usbPcapCmdPath, Logger, hidFoundVid: _hidFoundVid, hidFoundPid: _hidFoundPid);
                        if (hit == null)
                        {
                            Status = "No supported wheel found on any USBPcap interface (FFB pass-through disabled). Retrying in 15s...";
                            Log(Status);
                            if (SleepInterruptible(RediscoveryRetryMs)) break;
                            continue;
                        }
                        _usbPcapInterface = hit.Interface;
                        _deviceAddress = hit.DeviceAddress;
                        Log($"Auto-discovered: {hit}");
                    }

                    StartUsbPcapCmd();
                    ParseStream();
                }
                catch (Exception ex)
                {
                    Status = $"Error: {ex.Message}";
                    Log($"UsbPcapFfbTap: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try { _proc?.Kill(); } catch { }
                    _proc = null;
                }

                if (_stopping) break;
                // Brief cooldown so we don't spin if the install is broken.
                // After a process exit we also clear the discovered interface
                // so the next iteration re-runs discovery (catches replugs).
                if (!_manualOverride)
                {
                    _usbPcapInterface = null;
                    _deviceAddress = 0;
                }
                Thread.Sleep(2000);
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
            var psi = new ProcessStartInfo
            {
                FileName = _usbPcapCmdPath,
                Arguments = $"-d {_usbPcapInterface} -o - --devices {_deviceAddress}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi);
            if (_proc == null) throw new InvalidOperationException("Process.Start returned null");
            Status = $"Tapping {_usbPcapInterface} dev {_deviceAddress}";
            Log($"UsbPcapFfbTap started: {_usbPcapInterface} dev {_deviceAddress}");

            // Drain stderr so it doesn't fill its pipe buffer and stall the child.
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = _proc.StandardError.ReadLine()) != null)
                        Log($"[USBPcapCMD] {line}");
                }
                catch { }
            }) { IsBackground = true, Name = "UsbPcapFfbTap-stderr" }.Start();
        }

        private void ParseStream()
        {
            var s = _proc.StandardOutput.BaseStream;

            // ---- pcap global header (24 bytes, LE) ----
            byte[] gh = ReadExact(s, 24);
            uint magic    = BitConverter.ToUInt32(gh, 0);
            int  linkType = BitConverter.ToInt32(gh, 20);
            if (magic != 0xa1b2c3d4 || linkType != DLT_USBPCAP)
                throw new InvalidDataException($"Not a USBPcap stream (magic=0x{magic:x8}, linktype={linkType})");

            byte[] payload = new byte[1024];

            while (!_stopping)
            {
                byte[] rh = ReadExact(s, 16);
                int caplen = BitConverter.ToInt32(rh, 8);
                if (caplen <= 0 || caplen > 65535)
                    throw new InvalidDataException($"caplen={caplen}");
                if (payload.Length < caplen) payload = new byte[caplen];
                ReadExactInto(s, payload, 0, caplen);
                PacketsParsed++;

                // ---- USBPcap pseudo-header ----
                if (caplen < 27) continue;
                int headerLen = BitConverter.ToUInt16(payload, 0);
                if (headerLen < 27 || headerLen > caplen) continue;

                int dev      = BitConverter.ToUInt16(payload, 19);
                byte ep      = payload[21];
                byte xfer    = payload[22];
                if (dev != _deviceAddress) continue;
                PacketsForOurDevice++;

                // Per-direction / per-transfer-type / per-endpoint breakdown.
                // OUT direction is the host writing to the wheel (FFB and our
                // Trueforce stream live here). The bit 7 of the endpoint byte
                // is direction (0=OUT, 1=IN); low 4 bits are endpoint number.
                bool isOut = (ep & 0x80) == 0;
                int epNum  = ep & 0x0f;
                if (isOut)
                {
                    if (xfer < _outTransferTypeCounts.Length) _outTransferTypeCounts[xfer]++;
                    _outEndpointCounts[epNum]++;
                    MaybeLogPcap(rh, payload, caplen);
                }

                // DirectInput-style FFB path: a non-Trueforce game writes the
                // FFB target as report 0x11 / cmd 0x08 on an interrupt OUT
                // endpoint (the wheel's normal FFB endpoint), force = int8
                // offset-binary centered at 0x80 at report offset 2. Decoded
                // from the FH5 G923 capture 2026-05-17. For interrupt OUT the
                // report data starts right after the USBPcap pseudo-header
                // (no setup stage, unlike the ep0 control path below).
                // Independent of the ep0 HID++ path; whichever transport the
                // running game uses latches the freshest value, and
                // TryGetFreshFfbTarget arbitrates by recency. Normalized to
                // the int16 scale the HID++ path uses (<<8) so FfbScale tuning
                // behaves the same regardless of which path fed the value.
                if (isOut && xfer == 0x01 && headerLen + 3 <= caplen
                    && payload[headerLen] == 0x11 && payload[headerLen + 1] == 0x08)
                {
                    int force8 = payload[headerLen + 2] - 0x80;   // -128..+127
                    short ffbTarget = (short)(force8 << 8);        // -> int16 scale
                    long ts = _sw.ElapsedTicks & TimestampMask;
                    long pk = (ts << 16) | (uint)(ushort)ffbTarget;
                    System.Threading.Interlocked.Exchange(ref _packed, pk);
                    System.Threading.Interlocked.Exchange(ref _lastSampleTicks, ts);
                    FfbSamplesCaptured++;
                }

                MaybeEmitDiagnostics();
                if (!_ffbIndexResolved)
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

                // G-series FFB: HID++ page 0x8123 long form, function 2 (high
                // nibble of funcByte), at the per-wheel-resolved feature index.
                // FFB target = signed int16, big-endian, at offset 10-11 of the HID++ payload.
                if (reportId == 0x11 && featIdx == _ffbFeatureIndex && (funcByte & 0xf0) == 0x20)
                {
                    short ffbTarget = (short)((payload[dataOffset + 10] << 8) | payload[dataOffset + 11]);
                    long timestamp = _sw.ElapsedTicks & TimestampMask;
                    long packed = (timestamp << 16) | (uint)(ushort)ffbTarget;
                    System.Threading.Interlocked.Exchange(ref _packed, packed);
                    System.Threading.Interlocked.Exchange(ref _lastSampleTicks, timestamp);
                    FfbSamplesCaptured++;
                }
            }
            CloseRawLog();
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
        // the dominant (reportId 0x11, func&0xf0==0x20) tuple by count is
        // unambiguously the FFB feature. Switch once, then latch; parser-thread
        // only. Called from the parse loop on the FfbResolveIntervalMs cadence
        // until resolved, so first FFB on a non-0x0e wheel latches within a
        // few hundred ms of gameplay (the seed means G PRO is never delayed).
        private void MaybeResolveFfbFeatureIndex()
        {
            if (_ffbIndexResolved) return;
            // No per-wheel gate: RS50 (C276) resolves to feat 0x10 by the same
            // dominant-tuple rule that resolves G PRO to 0x0e (issue #5 woTF
            // Windows capture confirmed RS50 native FFB is HID++ 0x8123 fn2).

            byte bestIdx = 0;
            long bestCount = 0, secondCount = 0;
            lock (_tupleLock)
            {
                foreach (var kv in _tupleCounts)
                {
                    // Key = (reportId<<16)|(featIdx<<8)|(funcByte&0xf0).
                    if ((byte)(kv.Key >> 16) != 0x11) continue;   // long form
                    if ((byte)kv.Key != 0x20) continue;            // function 2
                    byte f = (byte)(kv.Key >> 8);
                    if (kv.Value > bestCount) { secondCount = bestCount; bestCount = kv.Value; bestIdx = f; }
                    else if (kv.Value > secondCount) { secondCount = kv.Value; }
                }
            }
            // Require a well-sampled, clearly dominant winner (>=4x runner-up)
            // so a stray HID++ settings write can't hijack the FFB index.
            if (bestCount >= FfbIndexMinSamples && bestCount >= secondCount * 4)
            {
                _ffbIndexResolved = true;
                if (bestIdx != _ffbFeatureIndex)
                {
                    byte old = _ffbFeatureIndex;
                    _ffbFeatureIndex = bestIdx;
                    Log($"FFB tap: resolved HID++ 0x8123 feature index 0x{bestIdx:X2} " +
                        $"(was seed 0x{old:X2}); {bestCount} samples, runner-up {secondCount}.");
                }
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
                $"ffbIdx=0x{_ffbFeatureIndex:X2}{(_ffbIndexResolved ? "*" : "")} " +
                $"matched={FfbSamplesCaptured} tuples=[{tuples}]" +
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
