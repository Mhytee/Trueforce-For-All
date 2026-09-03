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
                                    uint version = AcCspBridgeLayout.Version,
                                    float damper = 0.6f, float steerSpeed = 1.5f,
                                    uint acLeds = 0, float[] ledRpms = null,
                                    float ledBlinkRpm = 0f, float ledBlinkHz = 0f,
                                    float[] ledRgb = null)
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
            if (version >= 2)
            {
                BitConverter.GetBytes(damper).CopyTo(b, AcCspBridgeLayout.OffFfbDamper);
                BitConverter.GetBytes(steerSpeed).CopyTo(b, AcCspBridgeLayout.OffSteerInputSpeed);
            }
            if (version >= 3) BitConverter.GetBytes(acLeds).CopyTo(b, AcCspBridgeLayout.OffAcLeds);
            if (version >= 4)
            {
                int cnt = ledRpms?.Length ?? 0;
                BitConverter.GetBytes((uint)cnt).CopyTo(b, AcCspBridgeLayout.OffAcLedCount);
                for (int i = 0; i < cnt; i++)
                    BitConverter.GetBytes(ledRpms[i]).CopyTo(b, AcCspBridgeLayout.OffAcLedRpm + i * 4);
                BitConverter.GetBytes(ledBlinkRpm).CopyTo(b, AcCspBridgeLayout.OffAcLedBlinkRpm);
                BitConverter.GetBytes(ledBlinkHz).CopyTo(b, AcCspBridgeLayout.OffAcLedBlinkHz);
                for (int i = 0; ledRgb != null && i < ledRgb.Length; i++)
                    BitConverter.GetBytes(ledRgb[i]).CopyTo(b, AcCspBridgeLayout.OffAcLedRgb + i * 4);
            }
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
            Assert.Equal(0.6f, s.FfbDamper);
            Assert.Equal(1.5f, s.SteerInputSpeed);
        }

        [Fact]
        public void Parse_V1Block_DecodesWithZeroTailFields()
        {
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(Block(42, version: 1), out var s));
            Assert.Equal(3.5f, s.SteerTorque);
            Assert.Equal(0f, s.FfbDamper);
            Assert.Equal(0f, s.SteerInputSpeed);
        }

        [Fact]
        public void Parse_V3Block_DecodesAcLedOwnership()
        {
            // byte 0 = module active, byte 1 = its MODE.
            uint word = 1u | ((uint)AcLedMode.Disabled << 8);
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(Block(42, acLeds: word), out var s));
            Assert.True(s.AcLedsKnown);
            Assert.True(s.AcLedsModuleActive);
            Assert.Equal(AcLedMode.Disabled, s.AcLedsMode);
        }

        [Fact]
        public void Parse_V3Block_UnknownModeCodeFallsBackToUnknown()
        {
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(Block(42, acLeds: 1u | (99u << 8)), out var s));
            Assert.True(s.AcLedsKnown);
            Assert.Equal(AcLedMode.Unknown, s.AcLedsMode);
        }

        [Fact]
        public void Parse_V2Block_LeavesAcLedOwnershipUNKNOWN()
        {
            // Not merely "inactive": a script too old to report it must be
            // distinguishable from one reporting that AC is not driving the
            // bar, or we would take the wheel's lights on no evidence.
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(Block(42, version: 2), out var s));
            Assert.False(s.AcLedsKnown);
            Assert.False(s.AcLedsModuleActive);
            Assert.Equal(AcLedMode.Unknown, s.AcLedsMode);
        }

        [Fact]
        public void Parse_V4Block_DecodesTheCarsOwnShiftLights()
        {
            var rpms = new[] { 2400f, 3600f, 4800f, 5400f, 6000f };
            Assert.Equal(AcCspBridgeParse.Ok,
                AcCspBridgeLayout.TryParse(Block(42, ledRpms: rpms, ledBlinkRpm: 6050f, ledBlinkHz: 12f), out var s));
            Assert.Equal(5, s.AcLedCount);
            Assert.Equal(6050f, s.AcLedBlinkRpm);
            Assert.Equal(12f, s.AcLedBlinkHz);
        }

        [Fact]
        public void Parse_V4Block_ClampsAnAbsurdLedCount()
        {
            // The count indexes a fixed-width array on the wire; a wild value
            // from a bad writer must not become an out-of-range read.
            var b = Block(42);
            BitConverter.GetBytes(9999u).CopyTo(b, AcCspBridgeLayout.OffAcLedCount);
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(b, out var s));
            Assert.Equal(AcCspBridgeLayout.AcLedMax, s.AcLedCount);
        }

        [Fact]
        public void Parse_CarWithNoShiftLights_ReportsNone()
        {
            // Most cars model none, and that has to read as "no data" so the
            // existing fill behaviour is left alone rather than zeroed.
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(Block(42), out var s));
            Assert.Equal(0, s.AcLedCount);
        }

        [Fact]
        public void Parse_V3Block_HasNoShiftLightData()
        {
            Assert.Equal(AcCspBridgeParse.Ok, AcCspBridgeLayout.TryParse(Block(42, version: 3), out var s));
            Assert.Equal(0, s.AcLedCount);
            Assert.Equal(0f, s.AcLedBlinkHz);
        }

        [Theory]
        // Hue preserved, brightest channel taken to full: AC stores these as
        // emissive intensities, so the magnitude carries no meaning.
        [InlineData(150f, 150f, 150f, 255, 255, 255)]   // dim white -> white
        [InlineData(450f, 70f, 10f, 255, 40, 6)]        // over 255 is ordinary here
        [InlineData(1f, 0f, 0f, 255, 0, 0)]             // tiny values are still red
        [InlineData(0.5f, 0.25f, 0f, 255, 128, 0)]      // amber stays amber
        public void NormalizeEmissive_KeepsTheHueAndFillsTheRange(
            float r, float g, float b, int er, int eg, int eb)
        {
            Assert.True(AcCspBridgeLayout.TryNormalizeEmissive(r, g, b, out byte or_, out byte og, out byte ob));
            Assert.Equal(er, or_);
            Assert.Equal(eg, og);
            Assert.Equal(eb, ob);
        }

        [Theory]
        [InlineData(0f, 0f, 0f)]              // the "no colour given" case
        [InlineData(-1f, -2f, -3f)]           // nonsense reads as no colour, not as black
        [InlineData(float.NaN, 1f, 1f)]
        public void NormalizeEmissive_RejectsWhatIsNotAColour(float r, float g, float b)
            => Assert.False(AcCspBridgeLayout.TryNormalizeEmissive(r, g, b, out _, out _, out _));

        [Fact]
        public void Parse_V5Block_CarriesColoursAlongsideTheThresholds()
        {
            var rpms = new[] { 2400f, 4800f, 6000f };
            var cols = new[] { 150f, 150f, 150f, 255f, 120f, 0f, 255f, 0f, 0f };
            Assert.Equal(AcCspBridgeParse.Ok,
                AcCspBridgeLayout.TryParse(Block(42, ledRpms: rpms, ledRgb: cols), out var s));
            Assert.Equal(3, s.AcLedCount);
        }

        [Fact]
        public void Parse_OddSeq_IsWriterBusy()
            => Assert.Equal(AcCspBridgeParse.WriterBusy, AcCspBridgeLayout.TryParse(Block(43), out _));

        [Fact]
        public void Parse_RejectsForeignHeader()
        {
            Assert.Equal(AcCspBridgeParse.BadMagic,   AcCspBridgeLayout.TryParse(Block(2, magic: 0x11111111), out _));
            Assert.Equal(AcCspBridgeParse.BadVersion, AcCspBridgeLayout.TryParse(Block(2, version: 6), out _));
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
