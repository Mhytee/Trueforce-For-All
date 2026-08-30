using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The tap's count of Trueforce stream packets on the wheel, by shape: an
    // interrupt OUT REQUEST (USBPcap info bit 0 clear) carrying exactly one
    // 64-byte report 0x01. Completions, other report ids, other lengths,
    // other devices and IN traffic are not stream packets.
    public class UsbPcapTrueforceStreamCounterTests
    {
        private const int DltUsbPcap = 249;
        private const ushort Dev = 7;

        private static byte[] Record(ushort dev, byte endpoint, byte transfer, bool completion, byte[] data)
        {
            const int HeaderLen = 27;
            var p = new byte[HeaderLen + data.Length];
            p[0]  = HeaderLen;
            p[16] = (byte)(completion ? 0x01 : 0x00);   // info: bit 0 = PDO->FDO (completion)
            p[19] = (byte)(dev & 0xff);
            p[20] = (byte)(dev >> 8);
            p[21] = endpoint;
            p[22] = transfer;
            p[23] = (byte)(data.Length & 0xff);
            p[24] = (byte)(data.Length >> 8);
            Array.Copy(data, 0, p, HeaderLen, data.Length);
            return p;
        }

        private static byte[] StreamPacket(byte reportId = 0x01, int len = TrueforceDevice.PacketLen, byte seq = 0)
        {
            var d = new byte[len];
            d[0] = reportId;
            if (len > 9) { d[4] = 0x01; d[5] = seq; d[7] = 0x80; d[9] = 0x80; }
            return d;
        }

        private static byte[] Capture(params byte[][] records)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0xa1b2c3d4u);
                w.Write(new byte[12]);
                w.Write(65535);
                w.Write(DltUsbPcap);
                foreach (var r in records)
                {
                    w.Write(0); w.Write(0); w.Write(r.Length); w.Write(r.Length);
                    w.Write(r);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static UsbPcapFfbTap Run(params byte[][] records)
        {
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: Dev);
            using (var s = new MemoryStream(Capture(records)))
            {
                try { tap.ParseFrom(s); }
                catch (Exception) { /* EOF = end of synthetic capture */ }
            }
            return tap;
        }

        [Fact]
        public void CountsEachStreamRequestOnce()
        {
            var tap = Run(
                Record(Dev, 0x03, 0x01, false, StreamPacket(seq: 1)),
                Record(Dev, 0x03, 0x01, true,  new byte[0]),            // its completion: header only
                Record(Dev, 0x03, 0x01, false, StreamPacket(seq: 2)),
                Record(Dev, 0x03, 0x01, true,  new byte[0]));
            Assert.Equal(2, tap.TrueforceStreamPacketsOnOurDevice);
        }

        [Fact]
        public void ACompletionCarryingDataIsNotCountedTwice()
        {
            var tap = Run(
                Record(Dev, 0x03, 0x01, false, StreamPacket()),
                Record(Dev, 0x03, 0x01, true,  StreamPacket()));
            Assert.Equal(1, tap.TrueforceStreamPacketsOnOurDevice);
        }

        [Fact]
        public void EndpointNumberDoesNotMatter()
        {
            // The stream endpoint differs by wheel; the shape is the key.
            var tap = Run(
                Record(Dev, 0x02, 0x01, false, StreamPacket()),
                Record(Dev, 0x04, 0x01, false, StreamPacket()));
            Assert.Equal(2, tap.TrueforceStreamPacketsOnOurDevice);
        }

        [Fact]
        public void OtherShapesAreNotStreamPackets()
        {
            var tap = Run(
                Record(Dev, 0x03, 0x01, false, StreamPacket(reportId: 0x12)),         // HID++ very long, 64 bytes
                Record(Dev, 0x03, 0x01, false, StreamPacket(len: 63)),                // wrong length
                Record(Dev, 0x03, 0x01, false, StreamPacket(len: 65)),
                Record(Dev, 0x01, 0x01, false, new byte[] { 0x11, 0x08, 0, 0, 0, 0, 0 }),   // classic slot command
                Record(Dev, 0x03, 0x02, false, StreamPacket()),                       // control transfer
                Record(Dev, 0x83, 0x01, true,  StreamPacket()),                       // IN report
                Record(9,   0x03, 0x01, false, StreamPacket()));                      // another device
            Assert.Equal(0, tap.TrueforceStreamPacketsOnOurDevice);
        }
    }
}
