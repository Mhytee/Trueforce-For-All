using System.Linq;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>The per-channel LED colour trim. Determinism matters as much as
    /// correctness here: three separate sites compare what is on the wheel
    /// against a stored pattern run through Apply, so a rounding change would
    /// break every one of those comparisons and re-upload five slots on every
    /// tab open.</summary>
    public class LedColorGainTests
    {
        private static byte[] Rgb(params byte[] b) => b;

        [Fact]
        public void IdentityGainsLeaveEveryByteAlone()
        {
            var input = Rgb(0xFF, 0xFF, 0x00, 0x12, 0x34, 0x56, 0x00, 0x00, 0x00);
            var outp = LedColorGain.Apply(input, 1f, 1f, 1f);

            Assert.Equal(input, outp);
            Assert.True(LedColorGain.IsIdentity(1f, 1f, 1f));
        }

        [Fact]
        public void TheInputArrayIsNeverMutated()
        {
            var input = Rgb(0xFF, 0xFF, 0x00);
            LedColorGain.Apply(input, 1f, 0.5f, 1f);

            // BorrowSlot keeps reading the caller's slot object after the write,
            // and callers hold the same instance.
            Assert.Equal(Rgb(0xFF, 0xFF, 0x00), input);
        }

        [Fact]
        public void YellowLosesGreenAndKeepsRed()
        {
            // The measured case: #FFFF00 renders green until green is cut.
            var outp = LedColorGain.Apply(Rgb(0xFF, 0xFF, 0x00), 1f, 150f / 255f, 1f);

            Assert.Equal(0xFF, outp[0]);
            Assert.Equal(150, outp[1]);
            Assert.Equal(0x00, outp[2]);
        }

        [Fact]
        public void RedSurvivesAnyTrimSoRedlineNeverDims()
        {
            // Red is the reference channel: it is the weak die we correct
            // toward, so it is never the one cut.
            foreach (var g in new[] { 1f, 0.7f, 0.5f, 0.1f })
            {
                var outp = LedColorGain.Apply(Rgb(0xFF, 0x00, 0x00), 1f, g, g);
                Assert.Equal(Rgb(0xFF, 0x00, 0x00), outp);
            }
        }

        [Fact]
        public void ASinglyLitChannelKeepsItsFullBrightness()
        {
            // The point of renormalising. One lit channel has no ratio to get
            // wrong, so cutting it would cost brightness to fix nothing.
            var green = LedColorGain.Apply(Rgb(0x00, 0xFF, 0x00), 1f, 0.55f, 0.31f);
            var blue  = LedColorGain.Apply(Rgb(0x00, 0x00, 0xFF), 1f, 0.55f, 0.31f);
            var red   = LedColorGain.Apply(Rgb(0xFF, 0x00, 0x00), 1f, 0.55f, 0.31f);

            Assert.Equal(Rgb(0x00, 0xFF, 0x00), green);
            Assert.Equal(Rgb(0x00, 0x00, 0xFF), blue);
            Assert.Equal(Rgb(0xFF, 0x00, 0x00), red);
        }

        [Fact]
        public void ColoursWithRedAtTheirPeakDoNotMoveAtAll()
        {
            // Red is never cut, so it stays the peak and the scale is 1. These
            // are the colours the owner tuned by eye, so they must not shift
            // when renormalising was added.
            var yellow = LedColorGain.Apply(Rgb(0xFF, 0xFF, 0x00), 1f, 0.55f, 0.31f);
            var white  = LedColorGain.Apply(Rgb(0xFF, 0xFF, 0xFF), 1f, 0.55f, 0.31f);

            Assert.Equal(Rgb(0xFF, 140, 0x00), yellow);
            Assert.Equal(Rgb(0xFF, 140, 79), white);
        }

        [Fact]
        public void ATwoChannelMixKeepsItsRatioWhileRegainingBrightness()
        {
            // Cyan: green and blue only, so the peak comes back to 255 and the
            // green-to-blue ratio must survive the scaling.
            var cyan = LedColorGain.Apply(Rgb(0x00, 0xFF, 0xFF), 1f, 0.55f, 0.31f);

            Assert.Equal(0x00, cyan[0]);
            Assert.Equal(0xFF, cyan[1]);
            double wanted = 0.31 / 0.55;                       // blue relative to green
            Assert.InRange(cyan[2] / (double)cyan[1], wanted - 0.01, wanted + 0.01);
        }

        [Fact]
        public void EachLedIsNormalisedOnItsOwnSoGradientsSurvive()
        {
            // Per LED, not per pattern. A dim green and a bright green must both
            // keep their own level rather than one setting the scale for both.
            var outp = LedColorGain.Apply(
                Rgb(0x00, 0x14, 0x00, 0x00, 0xFF, 0x00), 1f, 0.55f, 0.31f);

            Assert.Equal(0x14, outp[1]);
            Assert.Equal(0xFF, outp[4]);
        }

        [Fact]
        public void OffStaysOff()
        {
            var outp = LedColorGain.Apply(Rgb(0x00, 0x00, 0x00), 1f, 0.5f, 0.5f);
            Assert.Equal(Rgb(0x00, 0x00, 0x00), outp);
        }

        [Fact]
        public void GainsAboveOneAreNormalisedRatherThanClipped()
        {
            // (2, 1.2, 1) carries the same ratio as (1, 0.6, 0.5). Without the
            // normalise it would instead mean "clip everything bright".
            var scaled = LedColorGain.Apply(Rgb(0xC8, 0xC8, 0xC8), 2f, 1.2f, 1f);
            var direct = LedColorGain.Apply(Rgb(0xC8, 0xC8, 0xC8), 1f, 0.6f, 0.5f);

            Assert.Equal(direct, scaled);
        }

        [Fact]
        public void ResultsNeverLeaveByteRange()
        {
            var input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            foreach (var g in new[] { 0f, 0.001f, 0.588f, 1f })
            {
                var outp = LedColorGain.Apply(input, 1f, g, g);
                Assert.Equal(input.Length, outp.Length);
            }
        }

        [Fact]
        public void ApplyIsDeterministicAcrossRepeatedCalls()
        {
            // The comparison sites re-derive the wire bytes on every tab open
            // and compare them against what the wheel returned. Any drift here
            // shows up as five needless flash writes each time.
            var input = Rgb(0xFF, 0x93, 0x00, 0x00, 0xC8, 0x00, 0xE0, 0xF8, 0xFF);
            var first = LedColorGain.Apply(input, 1f, 0.588f, 0.45f);
            for (int i = 0; i < 5; i++)
                Assert.Equal(first, LedColorGain.Apply(input, 1f, 0.588f, 0.45f));
        }

        [Fact]
        public void ATrailingPartialTripleCopiesThroughRatherThanZeroing()
        {
            var outp = LedColorGain.Apply(Rgb(0xFF, 0xFF, 0x00, 0x42), 1f, 0.5f, 1f);

            Assert.Equal(4, outp.Length);
            Assert.Equal(0x42, outp[3]);
        }

        [Theory]
        // Ice and Split are stored PRE-TRIMMED: solved backwards from the
        // appearance they had before the trim shipped, because they looked
        // better that way. That makes them the two builtins that break if the
        // shipped gains ever move, and nothing else would notice.
        [InlineData("559DFF4995FF3D8EFF3186FF247FFF186FFF125BF00C47E00633C8001EA0",
                    "E0F8FFC0ECFFA0E0FF80D4FF60C8FF40B0FF3090F02070E01050C80030A0")]
        [InlineData("0029FF0051FF007AFF318EFF61A2FF61A2FF318EFF007AFF0051FF0029FF",
                    "0040FF0080FF00C0FF80E0FFFFFFFFFFFFFF80E0FF00C0FF0080FF0040FF")]
        public void PreTrimmedBuiltinsStillRenderAsTheyWereAuthored(string storedHex, string wantHex)
        {
            var stored = Hex(storedHex);
            var want = Hex(wantHex);

            var got = LedColorGain.Apply(stored,
                LedColorGain.ShippedR, LedColorGain.ShippedG, LedColorGain.ShippedB);

            // One count of slack per channel. Blue cannot do better: dividing by
            // its shipped gain leaves only about 97 of the 256 levels reachable,
            // so most targets are simply not addressable exactly.
            for (int i = 0; i < want.Length; i++)
                Assert.InRange(got[i], want[i] - 1, want[i] + 1);
        }

        private static byte[] Hex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = System.Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        [Fact]
        public void NullIsPassedThrough()
        {
            Assert.Null(LedColorGain.Apply(null, 1f, 0.5f, 0.5f));
        }
    }
}
