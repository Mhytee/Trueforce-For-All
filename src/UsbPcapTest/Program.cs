// Standalone USBPcap FFB-tap proof of concept.
//
// Spawns USBPcapCMD.exe with stdout piped to us. Parses the pcap stream
// (global header + record headers + USBPcap pseudo-header + control-transfer
// setup + data fragment) and prints HID++ messages going from the host to the
// wheel on ep0 OUT. For AC's HID++ feature 0x0e long-form messages, extracts
// the FFB target value (signed 16-bit at offset 10-11 of the HID++ payload)
// and prints it live so we can confirm it tracks AC's actual force feedback.
//
// Usage:
//   1. Run AC and start driving.
//   2. Run this exe (no args). It auto-detects USBPcap interfaces.
//   3. Drive into a wall, hit a curb, etc. — watch the printed FFB value
//      track. Quiet straight = small values; impacts = larger.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TrueforceForAll.UsbPcapTest
{
    public static class Program
    {
        // Defaults: USBPcap2 with the wheel at device address 20 (we observed this
        // in earlier captures via descriptor inject). Override via env vars if needed.
        private const string DefaultInterface = "\\\\.\\USBPcap2";
        private const int DefaultDeviceAddress = 20;

        // pcap link type for USBPcap captures
        private const int DLT_USBPCAP = 249;

        public static int Main(string[] args)
        {
            // Offline validation: --file <path> reads a recorded pcap. Use this
            // to verify the parser against ours.pcap or ac.pcap before going live.
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--file")
                {
                    string path = args[i + 1];
                    int devAddrFile = int.TryParse(Environment.GetEnvironmentVariable("USBPCAP_DEVICE"), out var df) ? df : DefaultDeviceAddress;
                    Console.Error.WriteLine($"Reading offline pcap: {path}");
                    using var fs = File.OpenRead(path);
                    try { ParsePcapStream(fs, devAddrFile); }
                    catch (EndOfStreamException) { Console.Error.WriteLine("End of file."); }
                    return 0;
                }
            }

            string usbPcapInterface = Environment.GetEnvironmentVariable("USBPCAP_INTERFACE") ?? DefaultInterface;
            int devAddr = int.TryParse(Environment.GetEnvironmentVariable("USBPCAP_DEVICE"), out var d) ? d : DefaultDeviceAddress;

            string usbPcapCmd = @"C:\Program Files\USBPcap\USBPcapCMD.exe";
            if (!File.Exists(usbPcapCmd))
            {
                Console.Error.WriteLine($"USBPcapCMD.exe not found at {usbPcapCmd}");
                return 1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = usbPcapCmd,
                ArgumentList = { "-d", usbPcapInterface, "-o", "-", "--devices", devAddr.ToString() },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Console.Error.WriteLine($"Launching: {usbPcapCmd} -d {usbPcapInterface} -o - --devices {devAddr}");
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.Error.WriteLine("Failed to start USBPcapCMD");
                return 1;
            }

            // Forward stderr to our stderr in a background thread
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = proc.StandardError.ReadLine()) != null)
                        Console.Error.WriteLine($"[USBPcapCMD] {line}");
                }
                catch { }
            }) { IsBackground = true }.Start();

            try
            {
                ParsePcapStream(proc.StandardOutput.BaseStream, devAddr);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Parse error: {ex.Message}");
                return 2;
            }
            finally
            {
                try { proc.Kill(); } catch { }
            }

            return 0;
        }

        private static void ParsePcapStream(Stream s, int targetDevice)
        {
            // ---- pcap global header (24 bytes, little-endian) ----
            byte[] gh = ReadExact(s, 24);
            uint magic = BitConverter.ToUInt32(gh, 0);
            int linkType = BitConverter.ToInt32(gh, 20);
            if (magic != 0xa1b2c3d4)
                throw new InvalidDataException($"Not a pcap stream (magic = 0x{magic:x8})");
            if (linkType != DLT_USBPCAP)
                throw new InvalidDataException($"Not USBPcap link type (got {linkType}, expected 249)");
            Console.Error.WriteLine($"pcap stream OK (linktype DLT_USBPCAP={linkType})");
            Console.Error.WriteLine("Reading packets — drive in AC and watch FFB target update.");
            Console.Error.WriteLine();

            int packetCount = 0;
            int hidPlusPlusCount = 0;
            int ffbCount = 0;

            while (true)
            {
                byte[] rh = ReadExact(s, 16);
                uint tsSec  = BitConverter.ToUInt32(rh, 0);
                uint tsUsec = BitConverter.ToUInt32(rh, 4);
                int caplen  = BitConverter.ToInt32(rh, 8);
                int origLen = BitConverter.ToInt32(rh, 12);

                if (caplen <= 0 || caplen > 1 << 20)
                    throw new InvalidDataException($"Invalid caplen {caplen}");

                byte[] payload = ReadExact(s, caplen);
                packetCount++;

                // ---- USBPcap pseudo-header ----
                if (payload.Length < 27) continue;
                int headerLen = BitConverter.ToUInt16(payload, 0);
                if (headerLen < 27 || headerLen > payload.Length) continue;

                // info byte at offset 16: bit 0 indicates direction (0 = OUT/PDO->FDO, 1 = IN/FDO->PDO)
                // bus at offset 17 (u16), device at 19 (u16), endpoint at 21 (u8), transfer at 22 (u8)
                int dev      = BitConverter.ToUInt16(payload, 19);
                byte endpoint = payload[21];
                byte transfer = payload[22];

                if (dev != targetDevice) continue;
                if (transfer != 0x02) continue;        // 0x02 = control transfer
                if ((endpoint & 0x7f) != 0x00) continue; // ep0

                // For control transfers, USBPcap appends a single byte at offset 27 (stage):
                //   0 = setup, 1 = data, 2 = status
                // headerLen is 28 for control transfers. We want the SETUP stage which has
                // bmRequestType+bRequest+wValue+wIndex+wLength + data fragment immediately following.
                if (headerLen < 28) continue;
                byte stage = payload[27];
                if (stage != 0) continue; // setup stage only

                // Setup data: 8 bytes immediately after pseudo-header
                int setupOffset = headerLen;
                if (setupOffset + 8 > payload.Length) continue;
                byte bmRequestType = payload[setupOffset + 0];
                byte bRequest      = payload[setupOffset + 1];
                ushort wValue      = BitConverter.ToUInt16(payload, setupOffset + 2);
                ushort wIndex      = BitConverter.ToUInt16(payload, setupOffset + 4);
                ushort wLength     = BitConverter.ToUInt16(payload, setupOffset + 6);

                // We only care about Host-to-Device, Class, Interface = 0x21 (HID Set_Report)
                if (bmRequestType != 0x21) continue;
                if (bRequest != 0x09) continue;        // SET_REPORT

                // Data fragment follows the 8-byte setup
                int dataOffset = setupOffset + 8;
                int dataLen = payload.Length - dataOffset;
                if (dataLen <= 0) continue;

                hidPlusPlusCount++;

                // HID++ payload: [reportID][devIdx][featIdx][funcByte][params...]
                byte reportId = payload[dataOffset];
                byte devIdx   = (dataLen >= 2) ? payload[dataOffset + 1] : (byte)0;
                byte featIdx  = (dataLen >= 3) ? payload[dataOffset + 2] : (byte)0;
                byte funcByte = (dataLen >= 4) ? payload[dataOffset + 3] : (byte)0;

                // AC's FFB: feature 0x0e, long form (report 0x11), function 2 (high nibble of funcByte = 0x2x)
                // The FFB target is a signed 16-bit value at offset 10-11 of the HID++ payload, BIG-ENDIAN
                // (HID++ protocol uses big-endian for multi-byte numeric fields).
                if (reportId == 0x11 && featIdx == 0x0e && (funcByte & 0xf0) == 0x20 && dataLen >= 12)
                {
                    short ffbTarget = (short)((payload[dataOffset + 10] << 8) | payload[dataOffset + 11]);
                    ffbCount++;
                    double tSec = tsSec + (tsUsec / 1_000_000.0);
                    Console.WriteLine($"[{tSec:F3}] FFB feat=0x{featIdx:x2} func=0x{funcByte:x2}  target={ffbTarget,7}  raw={BytesToHex(payload, dataOffset, Math.Min(dataLen, 20))}");
                }
                else if (packetCount % 200 == 0)
                {
                    // Periodic heartbeat showing other HID++ traffic so we know it's flowing
                    Console.Error.WriteLine($"... {packetCount} pkts seen, {hidPlusPlusCount} HID++ Set_Reports, {ffbCount} feature-0x0e FFB targets");
                }
            }
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            byte[] buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
            return buf;
        }

        private static string BytesToHex(byte[] b, int offset, int count)
        {
            var sb = new System.Text.StringBuilder(count * 3);
            for (int i = 0; i < count; i++) sb.Append(b[offset + i].ToString("x2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }
}
