using System;
using System.IO.MemoryMappedFiles;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The TF4ALL CSP bridge block (gamemods/AssettoCorsaCsp/tf4all/ffb.lua):
    // header check, seqlock, and the reader's new-sample dedupe.
    public class AcCspBridgeTests
    {
        private static byte[] Block(uint seq, float pure = 0.25f, float final = 0.0f, float gain = 0.0f,
                                    float torque = 3.5f, uint magic = AcCspBridgeLayout.Magic,
                                    uint version = AcCspBridgeLayout.Version)
        {
            var b = new byte[AcCspBridgeLayout.Size];
            BitConverter.GetBytes(magic).CopyTo(b, AcCspBridgeLayout.OffMagic);
            BitConverter.GetBytes(version).CopyTo(b, AcCspBridgeLayout.OffVersion);
            BitConverter.GetBytes(seq).CopyTo(b, AcCspBridgeLayout.OffSeq);
            BitConverter.GetBytes(0.1f).CopyTo(b, AcCspBridgeLayout.OffFfbValue);
            BitConverter.GetBytes(pure).CopyTo(b, AcCspBridgeLayout.OffFfbPure);
            BitConverter.GetBytes(final).CopyTo(b, AcCspBridgeLayout.OffFfbFinal);
            BitConverter.GetBytes(gain).CopyTo(b, AcCspBridgeLayout.OffFfbMultiplier);
            BitConverter.GetBytes(torque).CopyTo(b, AcCspBridgeLayout.OffSteerTorque);
            BitConverter.GetBytes(-0.3f).CopyTo(b, AcCspBridgeLayout.OffSteerInput);
            BitConverter.GetBytes(0.003f).CopyTo(b, AcCspBridgeLayout.OffDt);
            return b;
        }

        [Fact]
        public void Parse_DecodesEveryScalar()
        {
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(Block(42), out var s));
            Assert.Equal(42u, s.Seq);
            Assert.Equal(0.1f, s.FfbValue);
            Assert.Equal(0.25f, s.FfbPure);
            Assert.Equal(0.0f, s.FfbFinal);
            Assert.Equal(0.0f, s.FfbMultiplier);
            Assert.Equal(3.5f, s.SteerTorque);
            Assert.Equal(-0.3f, s.SteerInput);
            Assert.Equal(0.003f, s.Dt);
        }

        [Fact]
        public void Parse_OddSeq_IsWriterBusy()
            => Assert.Equal(AcCspBridgeParse.WriterBusy, AcCspBridgeLayout.TryParse(Block(43), out _));

        [Fact]
        public void Parse_RejectsForeignHeader()
        {
            Assert.Equal(AcCspBridgeParse.BadMagic,   AcCspBridgeLayout.TryParse(Block(2, magic: 0x11111111), out _));
            Assert.Equal(AcCspBridgeParse.BadVersion, AcCspBridgeLayout.TryParse(Block(2, version: 2), out _));
            Assert.Equal(AcCspBridgeParse.TooShort,   AcCspBridgeLayout.TryParse(new byte[10], out _));
        }

        [Fact]
        public void Reader_MissingMap_ReturnsFalseAndRetriesLater()
        {
            var r = new AcCspBridgeReader("TF4All.Test.NoSuchMap." + Guid.NewGuid().ToString("N"));
            Assert.False(r.TryReadNew(0, out _));
            Assert.Equal(1, r.OpenAttempts);
            Assert.False(r.TryReadNew(1000, out _));          // within the retry hold-off
            Assert.Equal(1, r.OpenAttempts);
            Assert.False(r.TryReadNew(System.Diagnostics.Stopwatch.Frequency * 3, out _));
            Assert.Equal(2, r.OpenAttempts);
        }

        [Fact]
        public void Reader_DeliversOnlyOnSeqAdvance_AndSkipsBusyWriter()
        {
            if (!OperatingSystem.IsWindows()) return;   // named maps are a Windows feature
            string name = "TF4All.Test." + Guid.NewGuid().ToString("N");
            using (var mmf = MemoryMappedFile.CreateNew(name, AcCspBridgeLayout.Size))
            using (var w = mmf.CreateViewAccessor())
            using (var r = new AcCspBridgeReader(name))
            {
                w.WriteArray(0, Block(2, pure: 0.5f), 0, AcCspBridgeLayout.Size);
                Assert.True(r.TryReadNew(0, out var s));
                Assert.True(r.IsOpen);
                Assert.Equal(0.5f, s.FfbPure);

                Assert.False(r.TryReadNew(1, out _));           // same seq: nothing new

                w.WriteArray(0, Block(3, pure: 0.9f), 0, AcCspBridgeLayout.Size);
                Assert.False(r.TryReadNew(2, out _));           // writer busy on every attempt

                w.WriteArray(0, Block(4, pure: 0.9f), 0, AcCspBridgeLayout.Size);
                Assert.True(r.TryReadNew(3, out s));
                Assert.Equal(0.9f, s.FfbPure);

                // Close and reopen the same frozen map: the stale block must
                // not come back as a "new" sample.
                r.Close();
                Assert.False(r.IsOpen);
                Assert.False(r.TryReadNew(System.Diagnostics.Stopwatch.Frequency * 10, out _));
                Assert.True(r.IsOpen);
            }
        }

        [Fact]
        public void Reader_ForeignMap_FlagsBadHeader()
        {
            if (!OperatingSystem.IsWindows()) return;
            string name = "TF4All.Test." + Guid.NewGuid().ToString("N");
            using (var mmf = MemoryMappedFile.CreateNew(name, AcCspBridgeLayout.Size))
            using (var w = mmf.CreateViewAccessor())
            using (var r = new AcCspBridgeReader(name))
            {
                w.WriteArray(0, Block(2, magic: 0xDEADBEEF), 0, AcCspBridgeLayout.Size);
                Assert.False(r.TryReadNew(0, out _));
                Assert.True(r.BadHeader);
            }
        }
    }
}
