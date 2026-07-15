using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Pins the counter semantics the whole-bus liveness fix depends on:
    // PacketsParsed counts every record on the stream, PacketsForOurDevice
    // counts only records whose USBPcap device address matches the tap's.
    // In whole-bus (-A) capture the watchdog watches the device-matched
    // counter, so a re-enumerated wheel (records now carry a new address)
    // must stall it even while other devices keep the raw counter climbing.
    public class UsbPcapTapCounterTests
    {
        private const int DltUsbPcap = 249;

        private static byte[] BuildCapture(params ushort[] deviceAddresses)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                // pcap global header: magic, ver/zone/sigfigs (zeros), snaplen, linktype.
                w.Write(0xa1b2c3d4u);
                w.Write(new byte[12]);
                w.Write(65535);
                w.Write(DltUsbPcap);

                foreach (ushort dev in deviceAddresses)
                {
                    // 16-byte record header: ts_sec, ts_usec, caplen, origlen.
                    w.Write(0); w.Write(0); w.Write(27); w.Write(27);
                    // Minimal 27-byte USBPcap pseudo-header-only payload.
                    var p = new byte[27];
                    p[0]  = 27;                        // headerLen (u16 LE, low byte)
                    p[19] = (byte)(dev & 0xff);        // device address (u16 LE)
                    p[20] = (byte)(dev >> 8);
                    p[21] = 0x81;                      // ep1 IN
                    p[22] = 0x01;                      // interrupt transfer
                    w.Write(p);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void Parse(UsbPcapFfbTap tap, byte[] capture)
        {
            using (var s = new MemoryStream(capture))
            {
                // The parse loop runs until the stream ends; end-of-stream
                // surfaces as an exception from the exact-read helpers.
                try { tap.ParseFrom(s); }
                catch (Exception) { /* EOF = end of synthetic capture */ }
            }
        }

        [Fact]
        public void DeviceCounter_CountsOnlyOurAddress()
        {
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: 7);
            Parse(tap, BuildCapture(7, 3, 7, 12, 7));

            Assert.Equal(5, tap.PacketsParsed);
            Assert.Equal(3, tap.PacketsForOurDevice);
        }

        [Fact]
        public void DeviceCounter_StallsWhenWheelReenumerates()
        {
            // Whole-bus scenario: the wheel moved from address 7 to 9; the tap
            // still filters on 7. Raw keeps climbing, device-matched must not.
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: 7);
            Parse(tap, BuildCapture(9, 3, 9, 9, 3));

            Assert.Equal(5, tap.PacketsParsed);
            Assert.Equal(0, tap.PacketsForOurDevice);
        }

        [Fact]
        public void RejectsNonUsbPcapStream()
        {
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: 7);
            var bogus = new byte[24];   // zero magic
            using (var s = new MemoryStream(bogus))
                Assert.Throws<InvalidDataException>(() => tap.ParseFrom(s));
        }
    }
}
