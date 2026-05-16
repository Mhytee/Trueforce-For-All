// Discover which USBPcap interface and USB device address a supported Trueforce
// wheel is on, so the FFB tap doesn't depend on hardcoded values that vary
// per-machine and per-replug.
//
// Approach: USBPcap's --inject-descriptors flag synthesizes the cached device
// descriptors of all already-connected devices into the start of every capture
// stream. We open a brief capture on each USBPcap interface, scan packets for
// a USB device descriptor (bLength=18, bDescriptorType=1) whose VID/PID matches
// our supported list, and return the (interface, deviceAddress) pair from the
// first hit. The pseudo-header on the synthesized packets carries the address.
//
// Why not text enumeration? USBPcapCMD's interactive listing only prints when
// stdin is a TTY (zero output from a piped child), and --extcap-config errors
// without admin rights. Descriptor injection works on a normal capture, which
// runs without elevation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class WheelDiscoveryResult
    {
        public string Interface;       // e.g. \\.\USBPcap2
        public int    DeviceAddress;   // 1..127 from the USBPcap pseudo-header
        public ushort Vid;
        public ushort Pid;
        public string Model;
        public override string ToString() => $"{Model} on {Interface} addr {DeviceAddress}";
    }

    // A single device descriptor we observed during a scan. Used both by Find()
    // (filters to supported Trueforce wheels) and by the manual-picker UI
    // (shows every device the scan saw so the user can override when auto-
    // discovery missed the wheel).
    public sealed class UsbDeviceCandidate
    {
        public string Interface;
        public int    DeviceAddress;
        public ushort Vid;
        public ushort Pid;
        public string Model;                 // null when we don't know it
        public bool   IsSupportedWheel;      // VID=Logitech AND PID in SupportedPids
        public override string ToString()
        {
            string label = !string.IsNullOrEmpty(Model)
                ? $"{Model} ({Vid:X4}:{Pid:X4})"
                : $"USB device {Vid:X4}:{Pid:X4}";
            return $"{Interface} addr {DeviceAddress} – {label}";
        }
    }

    // Per-scan counters surfaced to the log so we can tell auto-discovery
    // failures apart: "stream never produced a packet" vs "stream had packets
    // but no descriptor matched" vs "stream had descriptors but none Logitech"
    // are three very different bugs and used to look identical in logs.
    public sealed class ScanStats
    {
        public string Interface;
        public bool   StreamOpened;
        public int    PacketsScanned;
        public int    ControlTransfers;
        public int    DescriptorPacketsSeen;
        public bool   SawPermissionError;
        public bool   TimedOut;
        public List<UsbDeviceCandidate> Candidates = new List<UsbDeviceCandidate>();
    }

    public static class WheelUsbDiscovery
    {
        private const int DLT_USBPCAP = 249;

        // Per-interface budget. Descriptor injection happens within the first
        // few packets, so 1.5 s is generous; total worst case is ~N × 1.5 s
        // across N interfaces (typically 2-3 on a desktop).
        private const int DefaultPerInterfaceTimeoutMs = 1500;

        public static WheelDiscoveryResult Find(
            string usbPcapCmdPath,
            Action<string> log = null,
            int perInterfaceTimeoutMs = DefaultPerInterfaceTimeoutMs,
            ushort? hidFoundVid = null,
            ushort? hidFoundPid = null)
        {
            var scans = ScanAllInterfaces(usbPcapCmdPath, log, perInterfaceTimeoutMs);
            if (scans == null) return null;

            WheelDiscoveryResult firstSupported = null;
            int totalCandidates = 0;
            foreach (var stat in scans)
            {
                totalCandidates += stat.Candidates.Count;
                foreach (var c in stat.Candidates)
                {
                    if (firstSupported == null && c.IsSupportedWheel)
                    {
                        firstSupported = new WheelDiscoveryResult
                        {
                            Interface     = c.Interface,
                            DeviceAddress = c.DeviceAddress,
                            Vid           = c.Vid,
                            Pid           = c.Pid,
                            Model         = c.Model,
                        };
                    }
                }
            }

            if (firstSupported != null)
            {
                log?.Invoke($"WheelUsbDiscovery: found {firstSupported}");
                return firstSupported;
            }

            // Diagnostics: explain WHY we didn't find anything so a log
            // diff in a bug report tells us where to look.
            int ifaces = scans.Count;
            if (ifaces == 0)
            {
                log?.Invoke("WheelUsbDiscovery: no USBPcap interfaces reported. USBPcap's capture " +
                            "driver is not loaded. This is expected right after installing it: restart " +
                            "the computer so the driver attaches at boot, then relaunch SimHub. If it " +
                            "persists after a reboot, USBPcap is missing or blocked (antivirus, or " +
                            "Windows 11 Memory integrity / Smart App Control).");
            }
            else if (totalCandidates == 0)
            {
                log?.Invoke($"WheelUsbDiscovery: scanned {ifaces} interface(s), saw zero device descriptors. " +
                            "Most likely cause: USBPcap descriptor cache is stale (replug the wheel) or USBPcap can't access the bus (try running SimHub as administrator).");
            }
            else
            {
                var seen = string.Join(", ", scans.SelectMany(s => s.Candidates)
                    .Select(c => $"{c.Vid:X4}:{c.Pid:X4}@{c.Interface.Replace(@"\\.\", "")}/{c.DeviceAddress}")
                    .Take(20));
                log?.Invoke($"WheelUsbDiscovery: scanned {ifaces} interface(s), saw {totalCandidates} device(s) but none match supported wheels. Devices: {seen}");
            }

            // HID-vs-USBPcap divergence: if HID already enumerated a supported
            // wheel but USBPcap didn't see it, that's the smoking-gun pattern
            // for a stale descriptor cache or an inaccessible root hub.
            if (hidFoundVid.HasValue && hidFoundPid.HasValue)
            {
                log?.Invoke(
                    $"WheelUsbDiscovery: HID enumerated wheel {hidFoundVid.Value:X4}:{hidFoundPid.Value:X4} " +
                    "but USBPcap discovery did NOT see it. The wheel is plugged in and the HID stack found it, " +
                    "USBPcap just can't see it on the bus. Try, in order: (1) restart the computer " +
                    "(USBPcap's capture driver only attaches at boot, especially right after it was " +
                    "installed), (2) replug the wheel so USBPcap re-caches its descriptor, (3) run " +
                    "SimHub as administrator, or (4) pick the device manually from the diagnostics panel.");
            }
            return null;
        }

        // Full scan of all USBPcap interfaces, returning every device descriptor
        // seen. Used by the manual-picker UI and by Find() above. Returns null
        // when USBPcapCMD.exe isn't locatable, or an empty list when no
        // interfaces are reported.
        public static List<ScanStats> ScanAllInterfaces(
            string usbPcapCmdPath,
            Action<string> log = null,
            int perInterfaceTimeoutMs = DefaultPerInterfaceTimeoutMs)
        {
            if (string.IsNullOrEmpty(usbPcapCmdPath) || !File.Exists(usbPcapCmdPath))
            {
                log?.Invoke("WheelUsbDiscovery: USBPcapCMD.exe not found, skipping discovery");
                return null;
            }

            List<string> ifaces = EnumerateInterfaces(usbPcapCmdPath, log);
            var results = new List<ScanStats>(ifaces.Count);
            foreach (string iface in ifaces)
            {
                var stat = ScanInterface(usbPcapCmdPath, iface, perInterfaceTimeoutMs, log);
                results.Add(stat);
                LogScanStats(stat, log);
            }
            return results;
        }

        private static void LogScanStats(ScanStats s, Action<string> log)
        {
            if (log == null) return;
            string verdict = s.Candidates.Count == 0 ? "no descriptors"
                          : $"{s.Candidates.Count} device descriptor(s)";
            string perm = s.SawPermissionError ? " [access denied, try running SimHub as administrator]" : "";
            string opened = s.StreamOpened ? "" : " [stream never produced a valid pcap header]";
            string timedOut = s.TimedOut ? " [hit timeout]" : "";
            log.Invoke($"WheelUsbDiscovery: {s.Interface} scanned {s.PacketsScanned} pkts " +
                       $"({s.ControlTransfers} control), {s.DescriptorPacketsSeen} descriptor packet(s), {verdict}{perm}{opened}{timedOut}");
        }

        // ---------- interface enumeration ----------

        private static List<string> EnumerateInterfaces(string usbPcapCmdPath, Action<string> log)
        {
            var result = new List<string>();
            var psi = new ProcessStartInfo
            {
                FileName = usbPcapCmdPath,
                Arguments = "--extcap-interfaces",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return result;
                    string stdout = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);

                    // Lines look like:  interface {value=\\.\USBPcap1}{display=USBPcap1}
                    foreach (string raw in stdout.Split('\n'))
                    {
                        string line = raw.Trim();
                        const string token = "{value=";
                        int v = line.IndexOf(token, StringComparison.Ordinal);
                        if (v < 0) continue;
                        int end = line.IndexOf('}', v);
                        if (end < 0) continue;
                        string val = line.Substring(v + token.Length, end - v - token.Length);
                        if (val.StartsWith(@"\\.\USBPcap", StringComparison.OrdinalIgnoreCase))
                            result.Add(val);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"WheelUsbDiscovery: extcap-interfaces failed: {ex.Message}");
            }

            return result;
        }

        // ---------- per-interface descriptor scan ----------

        private static ScanStats ScanInterface(
            string usbPcapCmdPath, string iface, int timeoutMs, Action<string> log)
        {
            var stat = new ScanStats { Interface = iface };

            var psi = new ProcessStartInfo
            {
                FileName = usbPcapCmdPath,
                // -A captures all devices on the root hub; --inject-descriptors
                // emits the cached device descriptors at stream start.
                Arguments = $"-d {iface} -A --inject-descriptors -o -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Process proc = null;
            Thread parser = null;

            try
            {
                proc = Process.Start(psi);
                if (proc == null) return stat;
                Process p = proc;
                ScanStats statCapture = stat;

                // Drain stderr so its pipe doesn't fill and stall the child;
                // also watch for the well-known permission-denied message so we
                // can surface a useful hint.
                var stderrThread = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardError.ReadLine()) != null)
                        {
                            if (line.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                line.IndexOf("Couldn't open device", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                statCapture.SawPermissionError = true;
                            }
                            log?.Invoke($"[USBPcapCMD/{iface}] {line}");
                        }
                    }
                    catch { }
                }) { IsBackground = true, Name = "WheelDiscoveryStderr" };
                stderrThread.Start();

                parser = new Thread(() =>
                {
                    try
                    {
                        ScanPcapStream(p.StandardOutput.BaseStream, iface, statCapture);
                    }
                    catch { /* expected when we kill the process */ }
                }) { IsBackground = true, Name = "WheelDiscoveryParser" };
                parser.Start();

                // Poll until the parser sees at least one descriptor, the
                // process exits, or we time out. We deliberately wait the full
                // timeout even after the first descriptor lands so we can
                // collect every device for the manual picker.
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (p.HasExited) break;
                    Thread.Sleep(20);
                }
                if (sw.ElapsedMilliseconds >= timeoutMs && !p.HasExited)
                    stat.TimedOut = true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"WheelUsbDiscovery: scan on {iface} failed: {ex.Message}");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { parser?.Join(500); } catch { }
                try { proc?.Dispose(); } catch { }
            }

            return stat;
        }

        // ---------- pcap parser ----------

        private static void ScanPcapStream(Stream s, string iface, ScanStats stat)
        {
            // pcap global header
            byte[] gh = ReadExact(s, 24);
            uint magic    = BitConverter.ToUInt32(gh, 0);
            int  linkType = BitConverter.ToInt32(gh, 20);
            if (magic != 0xa1b2c3d4 || linkType != DLT_USBPCAP)
                return;
            stat.StreamOpened = true;

            byte[] payload = new byte[2048];
            var seenAddresses = new HashSet<long>();

            while (true)
            {
                byte[] rh = ReadExact(s, 16);
                int caplen = BitConverter.ToInt32(rh, 8);
                if (caplen <= 0 || caplen > 65535) return;
                if (payload.Length < caplen) payload = new byte[caplen];
                ReadExactInto(s, payload, 0, caplen);
                stat.PacketsScanned++;

                if (caplen < 27) continue;
                int headerLen = BitConverter.ToUInt16(payload, 0);
                if (headerLen < 27 || headerLen > caplen) continue;

                int  dev  = BitConverter.ToUInt16(payload, 19);
                byte xfer = payload[22];
                if (xfer != 0x02) continue; // control transfer only
                stat.ControlTransfers++;

                // Try two payload offsets: either the bytes immediately after
                // the pseudo-header (data stage), or 8 bytes further (setup +
                // inline data in a single packet). Inject-descriptors output
                // varies between USBPcap versions; supporting both is cheap.
                if (TryReadDeviceDescriptor(payload, headerLen, caplen, out var vid1, out var pid1))
                {
                    RecordCandidate(stat, iface, dev, vid1, pid1, seenAddresses);
                    continue;
                }
                if (TryReadDeviceDescriptor(payload, headerLen + 8, caplen, out var vid2, out var pid2))
                {
                    RecordCandidate(stat, iface, dev, vid2, pid2, seenAddresses);
                }
            }
        }

        private static void RecordCandidate(
            ScanStats stat, string iface, int dev, ushort vid, ushort pid, HashSet<long> seen)
        {
            stat.DescriptorPacketsSeen++;
            // De-dup by (address, vid, pid): descriptor injection can repeat the
            // same descriptor across packets, and the picker doesn't want
            // duplicates. Use a 64-bit key so the device-address shift doesn't
            // overflow int.
            long key = ((long)dev << 32) | ((long)vid << 16) | pid;
            if (!seen.Add(key)) return;

            string model = null;
            bool supported = false;
            if (vid == WheelDiscovery.LogitechVid)
            {
                foreach (var (supportedPid, supportedModel) in WheelDiscovery.SupportedPids)
                {
                    if (pid == supportedPid)
                    {
                        model = supportedModel;
                        supported = true;
                        break;
                    }
                }
            }
            stat.Candidates.Add(new UsbDeviceCandidate
            {
                Interface         = iface,
                DeviceAddress     = dev,
                Vid               = vid,
                Pid               = pid,
                Model             = model,
                IsSupportedWheel  = supported,
            });
        }

        private static bool TryReadDeviceDescriptor(
            byte[] buf, int offset, int caplen, out ushort vid, out ushort pid)
        {
            vid = 0; pid = 0;
            // USB device descriptor: bLength=18, bDescriptorType=1, then 16 more bytes.
            if (offset < 0 || offset + 12 > caplen) return false;
            if (buf[offset] != 0x12) return false;
            if (buf[offset + 1] != 0x01) return false;
            vid = (ushort)(buf[offset + 8] | (buf[offset + 9] << 8));
            pid = (ushort)(buf[offset + 10] | (buf[offset + 11] << 8));
            return true;
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
    }
}
