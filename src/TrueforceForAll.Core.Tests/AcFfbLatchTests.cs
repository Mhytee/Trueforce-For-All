using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Tap-free AC FFB latch (ACFFB): finalFF-to-LSB conversion and the
    // packed value+timestamp scheme shared with UsbPcapFfbTap. The pack /
    // age math must stay wrap-safe in the 48-bit tick space, and the LSB
    // conversion must clamp AC's past-full clipping range symmetrically.
    public class AcFfbLatchTests
    {
        private const long Mask = 0x0000_FFFF_FFFF_FFFFL;

        [Theory]
        [InlineData( 0.0f,      0)]
        [InlineData( 1.0f,  32767)]
        [InlineData(-1.0f, -32767)]
        [InlineData( 1.5f,  32767)]   // AC signals clipping past +-1; wheel full scale caps it
        [InlineData(-2.0f, -32767)]
        [InlineData( 0.5f,  16384)]   // Math.Round(16383.5) = 16384 (to-even)
        [InlineData(-0.5f, -16384)]
        [InlineData(float.NaN,  0)]   // NaN maps to silence, never to a force
        public void FfbToLsb_ClampsAndRounds(float finalFf, int expected)
        {
            Assert.Equal((short)expected, AcSharedMemoryTelemetrySource.FfbToLsb(finalFf));
        }

        [Theory]
        [InlineData((short)0)]
        [InlineData((short)1)]
        [InlineData((short)-1)]
        [InlineData(short.MaxValue)]
        [InlineData(short.MinValue)]
        [InlineData((short)-12345)]
        public void PackUnpack_RoundTripsSignedValues(short value)
        {
            long packed = AcSharedMemoryTelemetrySource.PackFfb(value, 123456789L);
            Assert.Equal(value, AcSharedMemoryTelemetrySource.UnpackFfbValue(packed));
        }

        [Fact]
        public void AgeTicks_NormalCase_IsElapsedDelta()
        {
            long packed = AcSharedMemoryTelemetrySource.PackFfb(-100, 1_000_000L);
            Assert.Equal(500L, AcSharedMemoryTelemetrySource.AgeTicks(packed, 1_000_500L));
        }

        [Fact]
        public void AgeTicks_SurvivesTickWrap()
        {
            // Latched just before the 48-bit tick space wraps, read just
            // after: modular subtraction must report the small true age, not
            // a huge negative-gone-positive one.
            long justBeforeWrap = Mask - 10;
            long packed = AcSharedMemoryTelemetrySource.PackFfb(42, justBeforeWrap);
            long justAfterWrap = 5;   // 16 ticks later, post-wrap
            Assert.Equal(16L, AcSharedMemoryTelemetrySource.AgeTicks(packed, justAfterWrap));
        }

        [Fact]
        public void PackFfb_NegativeValue_DoesNotCorruptTimestamp()
        {
            // (ushort) cast on store: a negative value's sign extension must
            // not bleed into the timestamp bits.
            long packed = AcSharedMemoryTelemetrySource.PackFfb(-1, 7L);
            Assert.Equal(0L, AcSharedMemoryTelemetrySource.AgeTicks(packed, 7L));
        }
    }
}
