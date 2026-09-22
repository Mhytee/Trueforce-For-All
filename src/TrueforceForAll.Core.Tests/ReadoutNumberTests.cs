// The click-to-type readout parse. Every slider on the settings screen commits
// through this, and it used to drop everything after a comma, so a European
// user typing "1,25" got 1 with no sign anything had gone wrong.

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class ReadoutNumberTests
    {
        [Theory]
        // Plain, in the form the box is filled with when it takes focus.
        [InlineData("1.25", 1.25)]
        [InlineData("0", 0.0)]
        [InlineData("8", 8.0)]
        [InlineData("-3.5", -3.5)]
        [InlineData("+2.5", 2.5)]
        // The bug: a comma-decimal keyboard.
        [InlineData("1,25", 1.25)]
        [InlineData("0,5", 0.5)]
        // A bare fractional part, which the old pattern could not match at all.
        [InlineData(".5", 0.5)]
        [InlineData(",5", 0.5)]
        [InlineData("-.25", -0.25)]
        // Part way through typing.
        [InlineData("12.", 12.0)]
        [InlineData("12,", 12.0)]
        // Units the readouts actually carry, which must not reach the number.
        [InlineData("45 dB", 45.0)]
        [InlineData("2.5 s", 2.5)]
        [InlineData("2,5 s", 2.5)]
        [InlineData("100%", 100.0)]
        [InlineData("8 (2ms)", 8.0)]
        [InlineData("200 Hz", 200.0)]
        // Both marks present: the last one is the decimal, whichever way round.
        [InlineData("1.234,5", 1234.5)]
        [InlineData("1,234.5", 1234.5)]
        public void Parses(string text, double expected)
        {
            Assert.True(ReadoutNumber.TryParse(text, out double v));
            Assert.Equal(expected, v, 6);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   ")]
        [InlineData("auto")]
        [InlineData("off")]
        public void RejectsWhatIsNotANumber(string text)
        {
            Assert.False(ReadoutNumber.TryParse(text, out _));
        }

        // A rejected parse is what makes CommitReadout restore the display
        // rather than write a zero into the setting.
        [Fact]
        public void RejectedParseLeavesValueAtZero()
        {
            Assert.False(ReadoutNumber.TryParse("nonsense", out double v));
            Assert.Equal(0.0, v);
        }
    }
}
