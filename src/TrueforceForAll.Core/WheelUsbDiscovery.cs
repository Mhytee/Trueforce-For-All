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

    public static class WheelUsbDiscovery
    {
        private const int DLT_USBPCAP = 249;

        // Per-interface budget. Descriptor injection happens within the first
        // few packets, so 1.5 s is generous; total worst case is ~N × 1.5 s
        // across N interfaces (typically 2–3 on a desktop).
        private const int DefaultPerInterfaceTimeoutMs = 1500;

        public static WheelDiscoveryResult Find(
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
            if (ifaces.Count == 0)
            {
                log?.Invoke("WheelUsbDiscovery: no USBPcap interfaces reported");
                return null;
            }

            foreach (string iface in ifaces)
            {
                var hit = TryDiscoverOnInterface(usbPcapCmdPath, iface, perInterfaceTimeoutMs, log);
                if (hit != null)
                {
                    log?.Invoke($"WheelUsbDiscovery: found {hit}");
                    return hit;
                }
            }

            log?.Invoke($"WheelUsbDiscovery: no supported wheel on any of {ifaces.Count} interface(s)");
            return null;
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

        private static WheelDiscoveryResult TryDiscoverOnInterface(
            string usbPcapCmdPath, string iface, int timeoutMs, Action<string> log)
        {
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
            WheelDiscoveryResult result = null;
            bool sawPermissionError = false;

            try
            {
                proc = Process.Start(psi);
                if (proc == null) return null;
                Process p = proc;

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
                                sawPermissionError = true;
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
                        var hit = ScanPcapStreamForWheel(p.StandardOutput.BaseStream);
                        if (hit != null) hit.Interface = iface;
                        result = hit;
                    }
                    catch { /* expected when we kill the process */ }
                }) { IsBackground = true, Name = "WheelDiscoveryParser" };
                parser.Start();

                // Poll until we find something, the process exits, or we time out.
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (Volatile.Read(ref result) != null) break;
                    if (p.HasExited) break;
                    Thread.Sleep(20);
                }
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

            if (result == null && sawPermissionError)
                log?.Invoke($"WheelUsbDiscovery: {iface} access denied — try running SimHub as administrator");

            return result;
        }

        // ---------- pcap parser ----------

        private static WheelDiscoveryResult ScanPcapStreamForWheel(Stream s)
        {
            // pcap global header
            byte[] gh = ReadExact(s, 24);
            uint magic    = BitConverter.ToUInt32(gh, 0);
            int  linkType = BitConverter.ToInt32(gh, 20);
            if (magic != 0xa1b2c3d4 || linkType != DLT_USBPCAP)
                return null;

            byte[] payload = new byte[2048];

            while (true)
            {
                byte[] rh = ReadExact(s, 16);
                int caplen = BitConverter.ToInt32(rh, 8);
                if (caplen <= 0 || caplen > 65535) return null;
                if (payload.Length < caplen) payload = new byte[caplen];
                ReadExactInto(s, payload, 0, caplen);

                if (caplen < 27) continue;
                int headerLen = BitConverter.ToUInt16(payload, 0);
                if (headerLen < 27 || headerLen > caplen) continue;

                int  dev  = BitConverter.ToUInt16(payload, 19);
                byte xfer = payload[22];
                if (xfer != 0x02) continue; // control transfer only

                // Try two payload offsets: either the bytes immediately after
                // the pseudo-header (data stage), or 8 bytes further (setup +
                // inline data in a single packet). Inject-descriptors output
                // varies between USBPcap versions; supporting both is cheap.
                if (TryMatchDeviceDescriptor(payload, headerLen, caplen, dev, out var hit))
                    return hit;
                if (TryMatchDeviceDescriptor(payload, headerLen + 8, caplen, dev, out hit))
                    return hit;
            }
        }

        private static bool TryMatchDeviceDescriptor(
            byte[] buf, int offset, int caplen, int deviceAddress, out WheelDiscoveryResult result)
        {
            result = null;
            // USB device descriptor: bLength=18, bDescriptorType=1, then 16 more bytes.
            if (offset < 0 || offset + 12 > caplen) return false;
            if (buf[offset] != 0x12) return false;
            if (buf[offset + 1] != 0x01) return false;

            ushort vid = (ushort)(buf[offset + 8] | (buf[offset + 9] << 8));
            ushort pid = (ushort)(buf[offset + 10] | (buf[offset + 11] << 8));
            if (vid != WheelDiscovery.LogitechVid) return false;

            foreach (var (supportedPid, model) in WheelDiscovery.SupportedPids)
            {
                if (pid != supportedPid) continue;
                result = new WheelDiscoveryResult
                {
                    Interface = null, // filled in by caller (knows the iface)
                    DeviceAddress = deviceAddress,
                    Vid = vid,
                    Pid = pid,
                    Model = model,
                };
                return true;
            }
            return false;
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
