// The Initial D 8 car table and the tachometer conversion.
//
// The conversion earns its own tests because it has been wrong twice against a real cabinet, and
// both times the only thing that caught it was the wheel firing at the wrong moment. The first
// attempt published the internal rpm, which is about 10 percent above the dial. The second scaled
// by the face's printed maximum, which happened to work on one car and could never fire on another.

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8CarTableTests
    {
        [Fact]
        public void HasEveryCar()
        {
            Assert.Equal(50, Id8CarTable.Count);
        }

        [Fact]
        public void TruenoMatchesItsTachoFaces()
        {
            Id8Car c = Id8CarTable.Find(0);
            Assert.NotNull(c);
            Assert.Equal("AE86T", c.Code);
            Assert.Equal(7300, c.Redline(false));
            Assert.Equal(8000, c.DialMax(false));
            Assert.Equal(11000, c.Redline(true));
            Assert.Equal(13000, c.DialMax(true));
            Assert.Equal(5, c.GearCount);
            Assert.Equal(4, c.Cylinders);
            Assert.False(c.IsRotary);
        }

        [Fact]
        public void NismoMatchesItsTachoFace()
        {
            Id8Car c = Id8CarTable.Find(263);
            Assert.NotNull(c);
            Assert.Equal("R35", c.Code);
            Assert.Equal(7000, c.Redline(false));
            Assert.Equal(9000, c.DialMax(false));
            Assert.Equal(6, c.GearCount);
            Assert.Equal(6, c.Cylinders);
        }

        [Fact]
        public void RotariesReportNoCylindersButPulseLikeAFour()
        {
            // A 13B twin rotor has no cylinders, but it fires twice per crankshaft revolution,
            // which is the same rate as a four cylinder four stroke. That rate is what an engine
            // pulse effect actually needs, so it must not fall back to a generic guess.
            Id8Car c = Id8CarTable.Find(768);
            Assert.NotNull(c);
            Assert.True(c.IsRotary);
            Assert.Equal(0, c.Cylinders);
            Assert.Equal(4, c.PulseCylinderEquivalent);
        }

        [Fact]
        public void UnknownCarIsNull()
        {
            Assert.Null(Id8CarTable.Find(99999));
        }

        [Fact]
        public void TunedFaceBitIsBitTwenty()
        {
            Assert.Equal(1u << 20, Id8CarTable.TunedTachoFaceBit);
        }
    }

    public class Id8DialConversionTests
    {
        // The limiter pins the normalised rpm here on every car, measured live at 0.813 on a GT-R
        // and 0.814 on a tuned Trueno. The exe builds it as 6500 + 1540 against a 10000 scale.
        private const double LimiterNorm = 0.804;

        [Fact]
        public void NeedleReadsTheRedlineWhenTheLimiterBites()
        {
            // The whole rule this conversion encodes: the limiter cuts at the redline.
            Assert.Equal(7000, Id8MemoryTelemetry.DialRpmFor(LimiterNorm, 7000), 0);
            Assert.Equal(11000, Id8MemoryTelemetry.DialRpmFor(LimiterNorm, 11000), 0);
            Assert.Equal(7300, Id8MemoryTelemetry.DialRpmFor(LimiterNorm, 7300), 0);
        }

        [Fact]
        public void EveryCarCanActuallyReachItsRedline()
        {
            // The regression that mattered. Scaling by the face maximum meant the tuned Trueno's
            // needle stopped at 10582 against a redline of 11000, so its buzz never fired at all.
            // The limiter overshoots its centre by about 0.01, which is what puts the needle into
            // the red rather than merely up to it.
            const double limiterPeak = 0.814;
            foreach (int carId in new[] { 0, 263, 768 })
            {
                Id8Car c = Id8CarTable.Find(carId);
                foreach (bool tuned in new[] { false, true })
                {
                    int redline = c.Redline(tuned);
                    double atPeak = Id8MemoryTelemetry.DialRpmFor(limiterPeak, redline);
                    Assert.True(atPeak >= redline,
                        $"CarID {carId} {(tuned ? "tuned" : "stock")}: the needle reaches {atPeak:0} " +
                        $"but the red starts at {redline}, so the redline buzz could never fire.");
                }
            }
        }

        [Fact]
        public void ScalesLinearlyFromIdle()
        {
            // Idle sits near 0.08 of the scale, so it must read well clear of the redline.
            double idle = Id8MemoryTelemetry.DialRpmFor(0.08, 7000);
            Assert.InRange(idle, 600, 800);
            Assert.Equal(2 * idle, Id8MemoryTelemetry.DialRpmFor(0.16, 7000), 3);
        }

        [Fact]
        public void RefusesNonsenseRatherThanPropagatingIt()
        {
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(double.NaN, 7000));
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(double.PositiveInfinity, 7000));
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(0.5, 0));
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(-1.0, 7000));
        }
    }
}

namespace TrueforceForAll.Core.Tests
{
    // The wheel's panel draws ASCII only, and this is a Japanese game whose default driver name is
    // katakana. Without romanisation the name row is simply blank, which reads as a fault rather
    // than as an unsupported character set.
    public class Id8NameRomanisationTests
    {
        [Fact]
        public void RomanisesTheGamesOwnDefaultName()
        {
            // What the cabinet actually reported for a driver with no card.
            Assert.Equal("PUREIYA", Id8MemoryTelemetry.ToAscii("プレイヤー"));
        }

        [Fact]
        public void KeepsPlainAsciiAsItIs()
        {
            Assert.Equal("MHYTEE", Id8MemoryTelemetry.ToAscii("MHYTEE"));
        }

        [Fact]
        public void ConvertsFullWidthLatin()
        {
            // Japanese input produces full width letters for latin text.
            Assert.Equal("AB1", Id8MemoryTelemetry.ToAscii("ＡＢ１"));
        }

        [Fact]
        public void PrefersTheLongerSyllable()
        {
            // A naive per-character pass turns SHA into SIYA, because the small kana modifies the
            // one before it rather than standing alone.
            Assert.Equal("SHA", Id8MemoryTelemetry.ToAscii("シャ"));
            Assert.Equal("TOKYO", Id8MemoryTelemetry.ToAscii("トウキョウ")
                .Replace("U", ""));
        }

        [Fact]
        public void ReturnsNullWhenNothingCanBeShown()
        {
            // Kanji cannot be romanised without a dictionary, so the caller says GUEST rather than
            // drawing an empty row.
            Assert.Null(Id8MemoryTelemetry.ToAscii("赤城"));
            Assert.Null(Id8MemoryTelemetry.ToAscii(""));
            Assert.Null(Id8MemoryTelemetry.ToAscii(null));
        }
    }
}
