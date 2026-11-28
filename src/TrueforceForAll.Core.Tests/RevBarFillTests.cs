// The rev-light strip's fill: dark below the driver's chosen start, full at the
// redline, rising evenly between.
//
// The default has to reproduce the window the bar shipped with exactly, because
// nobody who leaves the slider alone should see their lights move.

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class RevBarFillTests
    {
        private const double Redline = 7000;

        [Fact]
        public void DefaultIsTheWindowTheBarAlwaysUsed()
        {
            // The old Forza-path arithmetic, verbatim: onset 0.85 of the redline.
            for (double rpm = 0; rpm <= 8000; rpm += 37)
            {
                double onset = Redline * 0.85;
                double old = rpm <= onset ? 0.0 : (rpm - onset) / (Redline - onset);
                if (old > 1.0) old = 1.0;
                Assert.Equal(old, RevBarFill.Fraction(rpm, Redline, RevBarFill.DefaultStartPct), 12);
            }
        }

        [Theory]
        [InlineData(20)]
        [InlineData(50)]
        [InlineData(85)]
        [InlineData(95)]
        public void DarkAtTheStartAndFullAtTheRedline(int startPct)
        {
            double start = Redline * startPct / 100.0;
            Assert.Equal(0.0, RevBarFill.Fraction(start, Redline, startPct));
            Assert.Equal(0.0, RevBarFill.Fraction(start - 1, Redline, startPct));
            Assert.Equal(1.0, RevBarFill.Fraction(Redline, Redline, startPct));
            Assert.Equal(1.0, RevBarFill.Fraction(Redline + 500, Redline, startPct));
        }

        [Fact]
        public void RisesEvenlyBetween()
        {
            // At 20 percent the strip spans 1400 to 7000 rpm, so every 560 rpm
            // is one tenth of the bar, at the bottom of the range as at the top.
            Assert.Equal(0.1, RevBarFill.Fraction(1960, Redline, 20), 9);
            Assert.Equal(0.5, RevBarFill.Fraction(4200, Redline, 20), 9);
            Assert.Equal(0.9, RevBarFill.Fraction(6440, Redline, 20), 9);
        }

        [Fact]
        public void LowerStartLightsEarlier()
        {
            double rpm = 5000;
            Assert.Equal(0.0, RevBarFill.Fraction(rpm, Redline, 85));
            Assert.True(RevBarFill.Fraction(rpm, Redline, 50) > 0.0);
            Assert.True(RevBarFill.Fraction(rpm, Redline, 20) > RevBarFill.Fraction(rpm, Redline, 50));
        }

        [Fact]
        public void IdleStaysDarkAtTheLowestStart()
        {
            // A typical idle is 800 to 1000 rpm on a 7000 redline: below 20 percent.
            Assert.Equal(0.0, RevBarFill.Fraction(900, Redline, RevBarFill.MinStartPct));
        }

        [Theory]
        [InlineData(0, RevBarFill.MinStartPct)]
        [InlineData(-40, RevBarFill.MinStartPct)]
        [InlineData(19, RevBarFill.MinStartPct)]
        [InlineData(20, 20)]
        [InlineData(60, 60)]
        [InlineData(95, 95)]
        [InlineData(100, RevBarFill.MaxStartPct)]
        public void StartIsClampedToTheSliderRange(int asked, int used)
        {
            Assert.Equal(used, RevBarFill.ClampStartPct(asked));
        }

        [Fact]
        public void OutOfRangeStartBehavesLikeTheNearestEnd()
        {
            // A hand-edited settings file must not light the bar at idle or
            // make a start above the redline.
            Assert.Equal(RevBarFill.Fraction(3000, Redline, RevBarFill.MinStartPct),
                         RevBarFill.Fraction(3000, Redline, 0));
            Assert.Equal(RevBarFill.Fraction(6900, Redline, RevBarFill.MaxStartPct),
                         RevBarFill.Fraction(6900, Redline, 140));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-7000)]
        [InlineData(double.NaN)]
        public void NoUsableRedlineIsADarkBar(double redline)
        {
            Assert.Equal(0.0, RevBarFill.Fraction(6000, redline, 50));
        }

        [Fact]
        public void NanRevsIsADarkBar()
        {
            Assert.Equal(0.0, RevBarFill.Fraction(double.NaN, Redline, 50));
        }
    }
}
