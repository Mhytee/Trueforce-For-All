// Reads AC's outgoing HID++ FFB target from the USB bus by spawning USBPcapCMD
// as a child process, parsing its pcap stdout, and latching the most-recent
// FFB target value for the Trueforce stream to inject into ep3 bytes 6-9.
//
// AC sends DirectInput-equivalent FFB to the wheel as HID Set_Output_Reports
// on ep0 (control endpoint). The actual force command is HID++ feature 0x0e
// function 2 long-form messages, signed 16-bit big-endian at offset 10-11
// of the HID++ payload. When we stream Trueforce on ep3, the wheel uses
// bytes 6-9 of our packet as motor torque, ignoring AC's ep0 commands. By
// mirroring AC's commands into bytes 6-9, FFB and Trueforce coexist.
//
// USBPcap installs as a kernel-mode USB filter driver. USBPcapCMD.exe streams
// pcap to stdout when invoked with -o -. We don't require admin in our
// process — USBPcap's own access checks happen in its CMD process.

using System;
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

        // Not readonly: when constructed in auto-discover mode (null/0), Start()
        // populates these from WheelUsbDiscovery before the reader loop spins up.
        private string _usbPcapInterface;
        private int _deviceAddress;
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

        private static readonly Stopwatch _sw = Stopwatch.StartNew();

        // Status surfaced to the UI / logs. Populated by the reader thread.
        public string Status { get; private set; } = "Stopped";
        public bool IsRunning => _proc != null && !_proc.HasExited;
        public long PacketsParsed { get; private set; }
        public long FfbSamplesCaptured { get; private set; }

        // Optional logger (e.g., SimHub.Logging.Current.Info). Avoids a hard
        // dependency on log4net from this library.
        public Action<string> Logger { get; set; }

        // Pass null/0 (the defaults) to auto-discover via WheelUsbDiscovery on
        // Start(). Pass explicit values only when overriding (env vars, tests).
        public UsbPcapFfbTap(string usbPcapInterface = null, int deviceAddress = 0)
        {
            _usbPcapInterface = usbPcapInterface;
            _deviceAddress = deviceAddress;
            _usbPcapCmdPath = LocateUsbPcapCmd();
        }

        private static string LocateUsbPcapCmd()
        {
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

            // Auto-discover the wheel's USBPcap interface and device address
            // unless the caller provided overrides. This avoids hardcoding
            // values that vary per-machine and per-replug.
            if (string.IsNullOrEmpty(_usbPcapInterface) || _deviceAddress <= 0)
            {
                Status = "Discovering wheel on USB bus...";
                Log(Status);
                var hit = WheelUsbDiscovery.Find(_usbPcapCmdPath, Logger);
                if (hit == null)
                {
                    Status = "No supported wheel found on any USBPcap interface (FFB pass-through disabled)";
                    Log(Status);
                    return false;
                }
                _usbPcapInterface = hit.Interface;
                _deviceAddress = hit.DeviceAddress;
                Log($"Auto-discovered: {hit}");
            }

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
            long  timestamp = packed >> 16;

            long now = _sw.ElapsedTicks;
            long ageTicks = now - timestamp;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if (ageTicks > maxAgeTicks) return null;

            return value;
        }

        // ---------- reader thread ----------

        private void ReaderLoop()
        {
            // Outer loop auto-restarts USBPcapCMD if it crashes (rare but
            // possible — we don't want a single failure to permanently disable
            // FFB pass-through).
            while (!_stopping)
            {
                try
                {
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
                Thread.Sleep(2000);
            }
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
                if (xfer != 0x02) continue;             // control transfer
                if ((ep & 0x7f) != 0x00) continue;       // ep0
                if (headerLen < 28) continue;
                byte stage = payload[27];
                if (stage != 0) continue;                // setup stage only

                int setupOffset = headerLen;
                if (setupOffset + 8 > caplen) continue;
                byte bmRequestType = payload[setupOffset + 0];
                byte bRequest      = payload[setupOffset + 1];
                if (bmRequestType != 0x21 || bRequest != 0x09) continue; // HID Set_Report

                int dataOffset = setupOffset + 8;
                int dataLen = caplen - dataOffset;
                if (dataLen < 12) continue;

                // HID++ payload: [reportID][devIdx][featIdx][funcByte][params...]
                byte reportId = payload[dataOffset + 0];
                byte featIdx  = payload[dataOffset + 2];
                byte funcByte = payload[dataOffset + 3];

                // AC's FFB: feature 0x0e long form, function 2 (high nibble of funcByte).
                // FFB target = signed int16, big-endian, at offset 10-11 of the HID++ payload.
                if (reportId == 0x11 && featIdx == 0x0e && (funcByte & 0xf0) == 0x20)
                {
                    short ffbTarget = (short)((payload[dataOffset + 10] << 8) | payload[dataOffset + 11]);
                    long timestamp = _sw.ElapsedTicks;
                    long packed = ((long)timestamp << 16) | (uint)(ushort)ffbTarget;
                    System.Threading.Interlocked.Exchange(ref _packed, packed);
                    FfbSamplesCaptured++;
                }
            }
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
