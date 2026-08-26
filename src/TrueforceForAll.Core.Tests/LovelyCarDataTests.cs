// Tests for the lovely-car-data parse + light math.
//
// The fixtures are REAL published shapes, not invented ones: the BMW M4 GT3
// iRacing entry is reproduced verbatim (a 12-LED outside-in dash with two dead
// slots and per-gear redlines), and the malformed values pinned here are the
// exact two that exist upstream. That matters because the value of this layer is
// tolerating real data, and a fixture we made up would not have caught either.

using System;
using System.Linq;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LovelyNameCleanerTests
    {
        [Theory]
        // The four worked examples from the upstream README.
        [InlineData("F12024 / AIX Racing 24", "f12024-aix-racing-24")]
        [InlineData("AssettoCorsa / alpine_a110_gt4", "assettocorsa-alpine-a110-gt4")]
        [InlineData("LMU / Algarve Pro Racing 2024", "lmu-algarve-pro-racing-2024")]
        [InlineData("F12024 / Dams ‘23", "f12024-dams-23")]
        public void CleansTheUpstreamWorkedExamples(string raw, string expected)
            => Assert.Equal(expected, LovelyNameCleaner.Clean(raw));

        [Theory]
        [InlineData("bmwm4gt3", "bmwm4gt3")]
        [InlineData("BMW M4 GT3", "bmw-m4-gt3")]
        [InlineData("  spaced  out  ", "spaced-out")]
        [InlineData("double__underscore", "double-underscore")]
        [InlineData("---leading-and-trailing---", "leading-and-trailing")]
        [InlineData("Nürburgring", "nurburgring")]      // accents fold to ASCII
        [InlineData("Citroën C3", "citroen-c3")]
        [InlineData("!!!", "")]                                // nothing survives
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NormalizesToTheDatasetsFileNameRules(string raw, string expected)
            => Assert.Equal(expected, LovelyNameCleaner.Clean(raw));

        [Fact]
        public void BuildsTheRepositoryRelativePath()
            => Assert.Equal("iracing/bmwm4gt3.json", LovelyNameCleaner.RelativePath("iRacing", "bmwm4gt3"));

        [Fact]
        public void RefusesToGuessWhenEitherIdIsEmpty()
        {
            Assert.Equal(string.Empty, LovelyNameCleaner.RelativePath("", "bmwm4gt3"));
            Assert.Equal(string.Empty, LovelyNameCleaner.RelativePath("iracing", "!!!"));
        }
    }

    public class LovelyColorParsingTests
    {
        [Fact]
        public void ParsesTheEightDigitArgbTheDatasetUses()
        {
            var c = LovelyCarDataParser.ParseColor("#FF00FF00");
            Assert.True(c.HasValue);
            Assert.Equal(0xFF, c.Value.A);
            Assert.Equal(0x00, c.Value.R);
            Assert.Equal(0xFF, c.Value.G);
            Assert.Equal(0x00, c.Value.B);
        }

        [Fact]
        public void TreatsFullyTransparentAsAPhysicalGap()
        {
            var c = LovelyCarDataParser.ParseColor("#00000000");
            Assert.True(c.HasValue);
            Assert.True(c.Value.IsGap);
        }

        [Theory]
        // The two malformed values that genuinely exist upstream: a 6-digit and a
        // 7-digit string in an 8-digit convention. The 7-digit one is the reason
        // this rejects rather than left-pads, since padding would silently shift
        // the channels and paint a wrong color.
        [InlineData("#FFFFF00")]
        [InlineData("#GGGGGGGG")]
        [InlineData("#")]
        [InlineData("nonsense")]
        [InlineData("")]
        [InlineData(null)]
        public void RejectsUnreadableValuesRatherThanGuessing(string raw)
            => Assert.False(LovelyCarDataParser.ParseColor(raw).HasValue);

        [Fact]
        public void AcceptsSixDigitRgbAsOpaque()
        {
            // The other malformed upstream value (#FFFF00) is a legal six-digit
            // RGB, so it reads as opaque yellow rather than being discarded.
            var c = LovelyCarDataParser.ParseColor("#FFFF00");
            Assert.True(c.HasValue);
            Assert.Equal(0xFF, c.Value.A);
            Assert.Equal(0xFF, c.Value.R);
            Assert.Equal(0xFF, c.Value.G);
            Assert.Equal(0x00, c.Value.B);
        }

        [Fact]
        public void AcceptsTheHtmlColorNamesTheFormatAllows()
        {
            var c = LovelyCarDataParser.ParseColor("CornflowerBlue");
            Assert.True(c.HasValue);
            Assert.Equal(0x64, c.Value.R);
            Assert.Equal(0x95, c.Value.G);
            Assert.Equal(0xED, c.Value.B);
        }
    }

    public class LovelyCarDataParserTests
    {
        // The real iRacing BMW M4 GT3 entry: 12 LEDs, outside-in, two dead slots,
        // and a redline that genuinely moves per gear (7150 in first, 7250 in
        // sixth). Reproduced verbatim from the published file.
        public const string M4Gt3Json = @"{
  ""carName"": ""BMW M4 GT3"",
  ""carId"": ""bmwm4gt3"",
  ""carClass"": ""GT3"",
  ""ledNumber"": 12,
  ""redlineBlinkInterval"": 0,
  ""ledColor"": [""#FFFF0000"",""#FF00FF00"",""#FF00FF00"",""#00000000"",""#FFFFFF00"",""#FFFFFF00"",""#FFFF0000"",""#FFFF0000"",""#FFFFFF00"",""#FFFFFF00"",""#00000000"",""#FF00FF00"",""#FF00FF00""],
  ""ledRpm"": [
    {
      ""R"": [6600,5600,5800,0,6000,6200,6400,6400,6200,6000,0,5800,5600],
      ""N"": [6600,5600,5800,0,6000,6200,6400,6400,6200,6000,0,5800,5600],
      ""1"": [7150,4800,5370,0,5920,6490,7050,7050,6490,5920,0,5370,4800],
      ""2"": [7150,5400,5850,0,6250,6650,7050,7050,6650,6250,0,5850,5400],
      ""3"": [6900,5520,5840,0,6160,6480,6800,6800,6480,6160,0,5840,5520],
      ""4"": [6900,5715,5985,0,6255,6528,6800,6800,6528,6255,0,5985,5715],
      ""5"": [6725,5665,5905,0,6145,6385,6625,6625,6385,6145,0,5905,5665],
      ""6"": [7250,6000,6250,0,6500,6750,7000,7000,6750,6500,0,6250,6000]
    }
  ]
}";

        [Fact]
        public void ReadsTheIdentityFields()
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            Assert.NotNull(p);
            Assert.Equal("BMW M4 GT3", p.CarName);
            Assert.Equal("bmwm4gt3", p.CarId);
            Assert.Equal("GT3", p.CarClass);
            Assert.Equal(12, p.LedNumber);
            Assert.Equal(0, p.RedlineBlinkIntervalMs);
        }

        [Fact]
        public void SplitsTheLeadingRedlineColorOffTheLedColors()
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            // 13 published entries = 1 redline color + 12 LEDs.
            Assert.Equal(12, p.LedColors.Length);
            Assert.True(p.RedlineColor.HasValue);
            Assert.Equal(0xFF, p.RedlineColor.Value.R);   // red
            Assert.Equal(0x00, p.RedlineColor.Value.G);
            // LED 1 is green, and the third LED is a dash gap.
            Assert.Equal(0xFF, p.LedColors[0].G);
            Assert.True(p.LedColors[2].IsGap);
        }

        [Fact]
        public void ReadsPerGearRedlinesSeparately()
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            Assert.Equal(7150, p.Gears["1"].RedlineRpm);
            Assert.Equal(7250, p.Gears["6"].RedlineRpm);
            Assert.Equal(6600, p.Gears["N"].RedlineRpm);
        }

        [Fact]
        public void KeepsDeadSlotsInPositionButOutOfTheSequence()
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            var first = p.Gears["1"];
            Assert.Equal(12, first.Thresholds.Length);      // positions preserved
            Assert.Equal(0, first.Thresholds[2]);           // the gap stays a gap
            Assert.Equal(10, first.ActiveThresholds.Length); // but never counts as a step
        }

        [Fact]
        public void FallsBackToTheHighestForwardGearForAnUnknownGear()
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            // A game reporting an eighth gear on a six-speed car must still light.
            Assert.Equal(7250, p.RampForGear(8).RedlineRpm);
            Assert.Equal(7150, p.RampForGear(1).RedlineRpm);
        }

        [Fact]
        public void MatchesGearsOnSimHubsOwnNamingWithoutAMappingStep()
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            Assert.Equal(7150, p.RampForGear("1").RedlineRpm);
            Assert.Equal(7250, p.RampForGear("6").RedlineRpm);
            Assert.Equal(6600, p.RampForGear("N").RedlineRpm);
            Assert.Equal(6600, p.RampForGear("R").RedlineRpm);
        }

        [Theory]
        // Multi-reverse gearboxes (Farming Simulator, truck sims) report several
        // reverse ratios. All of them are reverse, and none may be read as a
        // forward gear.
        [InlineData("R1")]
        [InlineData("R2")]
        [InlineData("reverse")]
        [InlineData("-1")]
        public void FoldsEveryReverseRatioOntoReverse(string gear)
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            Assert.Equal(6600, p.RampForGear(gear).RedlineRpm);   // the "R" entry, not 6th
        }

        [Fact]
        public void TreatsZeroAsNeutralNotAsAForwardGear()
        {
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            Assert.Equal(6600, p.RampForGear("0").RedlineRpm);    // the "N" entry
        }

        [Fact]
        public void UsesTopGearForAForwardRatioTheCarDoesNotPublish()
        {
            // A gearbox with more ratios than the entry allows for still lights.
            var p = LovelyCarDataParser.Parse(M4Gt3Json);
            Assert.Equal(7250, p.RampForGear("12").RedlineRpm);
        }

        [Fact]
        public void RefusesToLightReverseFromTopGearWhenTheCarPublishesNoReverse()
        {
            // Borrowing sixth gear's thresholds while reversing would light the
            // strip off numbers that have nothing to do with the situation. Say
            // nothing instead and let the built-in ramp handle it.
            const string forwardOnly = @"{""carName"":""ForwardOnly"",""ledNumber"":4,
                ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FF00FF00"",""#FFFFFF00"",""#FFFF0000""],
                ""ledRpm"":[{""1"":[7000,5000,5500,6000,6500]}]}";
            var p = LovelyCarDataParser.Parse(forwardOnly);
            Assert.NotNull(p.RampForGear("1"));
            Assert.Null(p.RampForGear("R"));
            Assert.Null(p.RampForGear("R3"));
            Assert.Null(p.RampForGear("N"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("{")]
        [InlineData("[]")]
        [InlineData("{\"carName\":\"no ramp\"}")]
        [InlineData(null)]
        public void ReturnsNullRatherThanThrowingOnAnythingUnusable(string json)
            => Assert.Null(LovelyCarDataParser.Parse(json));

        [Fact]
        public void FillsInACoarseTwoStageDashRatherThanReproducingIt()
        {
            // Shape of the real AC Evo BMW M4 GT3 EVO entry: two blocks, 6000 then
            // 6600, redline 6800. Reproducing it would give the strip two states
            // across the whole rev band, and the same physical car carries five
            // progressive points in iRacing's entry, so this is an approximation
            // rather than the car's real dash. We keep the two facts it does
            // establish (lights start at 6000, redline 6800) and fill between.
            const string twoStage = @"{""carName"":""TwoStage"",""ledNumber"":4,
                ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FF00FF00"",""#FFFF0000"",""#FFFF0000""],
                ""ledRpm"":[{""1"":[6800,6000,6000,6600,6600]}]}";
            var p = LovelyCarDataParser.Parse(twoStage);
            Assert.NotNull(p);
            var ramp = p.Gears["1"];
            Assert.True(ramp.IsUsable);
            Assert.False(ramp.IsFaithful);

            Assert.Equal(0,  LovelyLightMath.LevelForRpm(ramp, 5900, 10));
            Assert.Equal(1,  LovelyLightMath.LevelForRpm(ramp, 6000, 10));   // first light where the car says
            Assert.Equal(10, LovelyLightMath.LevelForRpm(ramp, 6800, 10));   // full at its redline
            // The point of filling in: real resolution through the middle, which
            // the published two-state entry cannot express.
            int mid = LovelyLightMath.LevelForRpm(ramp, 6400, 10);
            Assert.InRange(mid, 4, 6);
        }

        [Fact]
        public void FillsInASingleStageCarBetweenItsOneLightAndItsRedline()
        {
            // iRacing's NASCAR stock cars publish exactly one switch-on point over
            // a 2100 rpm band. Rendered literally the strip would sit inert for
            // 2000 rpm and then slam full.
            const string oneStage = @"{""carName"":""OneStage"",""ledNumber"":2,
                ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FF00FF00""],
                ""ledRpm"":[{""1"":[9800,7700,7700]}]}";
            var p = LovelyCarDataParser.Parse(oneStage);
            Assert.NotNull(p);
            var ramp = p.Gears["1"];
            Assert.True(ramp.IsUsable);
            Assert.False(ramp.IsFaithful);
            Assert.Equal(0,  LovelyLightMath.LevelForRpm(ramp, 7600, 10));
            Assert.Equal(1,  LovelyLightMath.LevelForRpm(ramp, 7700, 10));
            Assert.Equal(10, LovelyLightMath.LevelForRpm(ramp, 9800, 10));
            Assert.InRange(LovelyLightMath.LevelForRpm(ramp, 8750, 10), 4, 7);  // climbs through the band
        }

        [Fact]
        public void LeavesARichCarsOwnCurveExactlyAsPublished()
        {
            // The nonlinear shape is the reason to consume this dataset at all, so
            // a car with enough points must never be smoothed.
            var ramp = LovelyCarDataParser.Parse(M4Gt3Json).Gears["1"];
            Assert.True(ramp.IsFaithful);
            Assert.Equal(ramp.ActiveThresholds, ramp.RenderThresholds(10));
        }

        [Fact]
        public void KeepsACarWithNoUsableRampAtAllForItsRedline()
        {
            // No switch-on point below the redline means we cannot know where the
            // lights start, so the strip falls back entirely. The per-gear redline
            // is still real and no sim publishes one, so the car is kept.
            const string redlineOnly = @"{""carName"":""RedlineOnly"",""ledNumber"":2,
                ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FF00FF00""],
                ""ledRpm"":[{""1"":[7000,7000,7000]}]}";
            var p = LovelyCarDataParser.Parse(redlineOnly);
            Assert.NotNull(p);
            Assert.False(p.Gears["1"].IsUsable);
            Assert.Equal(7000, p.RampForGear(1).RedlineRpm);
            Assert.Equal(0, LovelyLightMath.LevelForRpm(p.Gears["1"], 6500, 10));
        }

        [Fact]
        public void SurvivesAColorArrayShorterThanTheDataClaims()
        {
            // Six files upstream break the ledColor length rule. The ramp is still
            // usable, so the car must parse; only its colors are short.
            const string shortColors = @"{""carName"":""Short"",""ledNumber"":6,
                ""ledColor"":[""#FFFF0000"",""#FF00FF00""],
                ""ledRpm"":[{""1"":[7000,5000,5500,6000,6500,7000]}]}";
            var p = LovelyCarDataParser.Parse(shortColors);
            Assert.NotNull(p);
            Assert.Single(p.LedColors);
            Assert.True(p.Gears["1"].IsUsable);
        }

        [Fact]
        public void RecoversAMissingRedlineFromTheHighestSwitchOnPoint()
        {
            const string noRedline = @"{""carName"":""NoRedline"",""ledNumber"":4,
                ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FF00FF00"",""#FFFFFF00"",""#FFFF0000""],
                ""ledRpm"":[{""1"":[0,5000,5500,6000,6500]}]}";
            var p = LovelyCarDataParser.Parse(noRedline);
            Assert.NotNull(p);
            Assert.Equal(6500, p.Gears["1"].RedlineRpm);
        }
    }

    public class LovelyLevelMappingTests
    {
        private static LovelyGearRamp FirstGear() =>
            LovelyCarDataParser.Parse(LovelyCarDataParserTests.M4Gt3Json).Gears["1"];

        [Fact]
        public void IsDarkBelowTheFirstSwitchOnPoint()
            => Assert.Equal(0, LovelyLightMath.LevelForRpm(FirstGear(), 4000, 10));

        [Fact]
        public void IsFullyLitAtAndBeyondTheLastSwitchOnPoint()
        {
            Assert.Equal(10, LovelyLightMath.LevelForRpm(FirstGear(), 7050, 10));
            Assert.Equal(10, LovelyLightMath.LevelForRpm(FirstGear(), 99999, 10));
        }

        [Fact]
        public void ClimbsMonotonicallyWithRevs()
        {
            var ramp = FirstGear();
            int previous = -1;
            for (int rpm = 4000; rpm <= 7500; rpm += 25)
            {
                int level = LovelyLightMath.LevelForRpm(ramp, rpm, 10);
                Assert.True(level >= previous, "level fell at " + rpm);
                Assert.InRange(level, 0, 10);
                previous = level;
            }
        }

        [Fact]
        public void ReproducesTheCarsOwnNonlinearBuildup()
        {
            // The point of the whole feature: this car's first pair lights at
            // 4800 but the bar is only half full by 5920, whereas a linear ramp
            // between the same endpoints would already be past that. Assert the
            // shape rather than a single number.
            var ramp = FirstGear();
            Assert.Equal(2, LovelyLightMath.LevelForRpm(ramp, 4800, 10));   // outer pair only
            Assert.Equal(6, LovelyLightMath.LevelForRpm(ramp, 5920, 10));
            Assert.Equal(8, LovelyLightMath.LevelForRpm(ramp, 6490, 10));
        }

        [Fact]
        public void ScalesToAShorterStrip()
        {
            // A G923 addresses five states, not ten.
            var ramp = FirstGear();
            Assert.Equal(5, LovelyLightMath.LevelForRpm(ramp, 7050, 5));
            Assert.InRange(LovelyLightMath.LevelForRpm(ramp, 5920, 5), 0, 5);
        }

        [Fact]
        public void BlinksFromThisGearsOwnRedline()
        {
            var p = LovelyCarDataParser.Parse(LovelyCarDataParserTests.M4Gt3Json);
            Assert.False(LovelyLightMath.IsAtRedline(p.Gears["1"], 7100));  // 7150 in first
            Assert.True(LovelyLightMath.IsAtRedline(p.Gears["1"], 7150));
            Assert.False(LovelyLightMath.IsAtRedline(p.Gears["6"], 7150));  // 7250 in sixth
        }

        [Fact]
        public void HandlesNullAndEmptyWithoutThrowing()
        {
            Assert.Equal(0, LovelyLightMath.LevelForRpm(null, 5000, 10));
            Assert.Equal(0, LovelyLightMath.LevelForRpm(new LovelyGearRamp(), 5000, 10));
            Assert.Equal(0, LovelyLightMath.LevelForRpm(FirstGear(), 5000, 0));
        }
    }

    public class LovelyLayoutClassificationTests
    {
        [Fact]
        public void RecognisesTheM4Gt3AsOutsideIn()
        {
            var ramp = LovelyCarDataParser.Parse(LovelyCarDataParserTests.M4Gt3Json).Gears["1"];
            Assert.Equal(LightDirection.OutsideIn, LovelyLightMath.ClassifyLayout(ramp.Thresholds));
        }

        [Fact]
        public void RecognisesAscendingThresholdsAsLeftToRight()
            => Assert.Equal(LightDirection.LeftToRight,
                   LovelyLightMath.ClassifyLayout(new[] { 5000, 5500, 6000, 6500, 7000 }));

        [Fact]
        public void RecognisesDescendingThresholdsAsRightToLeft()
            => Assert.Equal(LightDirection.RightToLeft,
                   LovelyLightMath.ClassifyLayout(new[] { 7000, 6500, 6000, 5500, 5000 }));

        [Fact]
        public void RecognisesLowestInTheMiddleAsInsideOut()
            => Assert.Equal(LightDirection.InsideOut,
                   LovelyLightMath.ClassifyLayout(new[] { 7000, 6000, 5000, 6000, 7000 }));

        [Fact]
        public void RecognisesLowestAtTheEdgesAsOutsideIn()
            => Assert.Equal(LightDirection.OutsideIn,
                   LovelyLightMath.ClassifyLayout(new[] { 5000, 6000, 7000, 6000, 5000 }));

        [Fact]
        public void IgnoresDeadSlotsWhenJudgingShape()
        {
            // The same left-to-right ramp with gaps punched through it must not
            // read as a different layout.
            Assert.Equal(LightDirection.LeftToRight,
                LovelyLightMath.ClassifyLayout(new[] { 5000, 0, 5500, 6000, 0, 6500, 7000 }));
        }

        [Theory]
        [InlineData(new int[0])]
        [InlineData(new[] { 5000 })]
        [InlineData(new[] { 5000, 6000 })]
        [InlineData(new[] { 6000, 6000, 6000, 6000 })]   // flat: no shape to read
        public void FallsBackToLeftToRightWhenThereIsNothingToGoOn(int[] thresholds)
            => Assert.Equal(LightDirection.LeftToRight, LovelyLightMath.ClassifyLayout(thresholds));

        [Fact]
        public void DoesNotThrowOnNull()
            => Assert.Equal(LightDirection.LeftToRight, LovelyLightMath.ClassifyLayout(null));
    }

    public class LovelyStripPlacementTests
    {
        [Fact]
        public void MirroredLayoutsHaveHalfAsManySteps()
        {
            Assert.Equal(10, LovelyLightMath.StepCount(LightDirection.LeftToRight, 10));
            Assert.Equal(5,  LovelyLightMath.StepCount(LightDirection.OutsideIn, 10));
            Assert.Equal(5,  LovelyLightMath.StepCount(LightDirection.InsideOut, 10));
        }

        [Fact]
        public void OutsideInLightsTheEdgesFirstAndTheCentreLast()
        {
            Assert.Equal(0, LovelyLightMath.StepIndexForLed(0, LightDirection.OutsideIn, 10));
            Assert.Equal(0, LovelyLightMath.StepIndexForLed(9, LightDirection.OutsideIn, 10));
            Assert.Equal(4, LovelyLightMath.StepIndexForLed(4, LightDirection.OutsideIn, 10));
            Assert.Equal(4, LovelyLightMath.StepIndexForLed(5, LightDirection.OutsideIn, 10));
        }

        [Fact]
        public void InsideOutLightsTheCentreFirstAndTheEdgesLast()
        {
            Assert.Equal(0, LovelyLightMath.StepIndexForLed(4, LightDirection.InsideOut, 10));
            Assert.Equal(0, LovelyLightMath.StepIndexForLed(5, LightDirection.InsideOut, 10));
            Assert.Equal(4, LovelyLightMath.StepIndexForLed(0, LightDirection.InsideOut, 10));
            Assert.Equal(4, LovelyLightMath.StepIndexForLed(9, LightDirection.InsideOut, 10));
        }

        [Fact]
        public void RightToLeftReversesTheOrder()
        {
            Assert.Equal(9, LovelyLightMath.StepIndexForLed(0, LightDirection.RightToLeft, 10));
            Assert.Equal(0, LovelyLightMath.StepIndexForLed(9, LightDirection.RightToLeft, 10));
        }

        [Fact]
        public void MirroredPlacementIsAlwaysSymmetric()
        {
            var strip = LovelyLightMath.DefaultRampColors(LightDirection.OutsideIn, 10);
            for (int i = 0; i < 5; i++)
                Assert.Equal(strip[i].ToString(), strip[9 - i].ToString());
        }
    }

    public class LovelyDefaultPaletteTests
    {
        private static int CountOf(LovelyColor[] strip, LovelyColor want)
            => strip.Count(c => c.ToString() == want.ToString());

        private static readonly LovelyColor Green  = LovelyColor.Rgb(0x00, 0xFF, 0x00);
        private static readonly LovelyColor Yellow = LovelyColor.Rgb(0xFF, 0xFF, 0x00);
        private static readonly LovelyColor Red    = LovelyColor.Rgb(0xFF, 0x00, 0x00);

        [Fact]
        public void LinearTenLedStripIsFiveGreenThreeYellowTwoRed()
        {
            var strip = LovelyLightMath.DefaultRampColors(LightDirection.LeftToRight, 10);
            Assert.Equal(5, CountOf(strip, Green));
            Assert.Equal(3, CountOf(strip, Yellow));
            Assert.Equal(2, CountOf(strip, Red));
            // Red must be the last thing to light, not the first.
            Assert.Equal(Red.ToString(), strip[9].ToString());
            Assert.Equal(Green.ToString(), strip[0].ToString());
        }

        [Fact]
        public void RightToLeftPutsRedAtTheOtherEnd()
        {
            var strip = LovelyLightMath.DefaultRampColors(LightDirection.RightToLeft, 10);
            Assert.Equal(Red.ToString(), strip[0].ToString());
            Assert.Equal(Green.ToString(), strip[9].ToString());
            Assert.Equal(2, CountOf(strip, Red));
        }

        [Fact]
        public void MirroredTenLedStripIsPairBandedFourFourTwo()
        {
            // Five pairs banded green, green, yellow, yellow, red: still exactly
            // two red LEDs, and no color split across a mirrored pair.
            var strip = LovelyLightMath.DefaultRampColors(LightDirection.OutsideIn, 10);
            Assert.Equal(4, CountOf(strip, Green));
            Assert.Equal(4, CountOf(strip, Yellow));
            Assert.Equal(2, CountOf(strip, Red));
        }

        [Fact]
        public void NoColorEverSplitsAPair()
        {
            foreach (var layout in new[] { LightDirection.OutsideIn, LightDirection.InsideOut })
            {
                var strip = LovelyLightMath.DefaultRampColors(layout, 10);
                for (int i = 0; i < 5; i++)
                    Assert.Equal(strip[i].ToString(), strip[9 - i].ToString());
            }
        }

        [Fact]
        public void RedIsNeverMoreThanTwoLedsOnAnyLayout()
        {
            foreach (LightDirection layout in Enum.GetValues(typeof(LightDirection)))
                Assert.True(CountOf(LovelyLightMath.DefaultRampColors(layout, 10), Red) <= 2);
        }

        [Fact]
        public void ShorterStripsKeepTheSameShape()
        {
            // A G923's five addressable states still climb green to red.
            var strip = LovelyLightMath.DefaultRampColors(LightDirection.LeftToRight, 5);
            Assert.Equal(5, strip.Length);
            Assert.Equal(Green.ToString(), strip[0].ToString());
            Assert.Equal(Red.ToString(), strip[4].ToString());
            Assert.True(CountOf(strip, Red) <= 2);
        }

        [Fact]
        public void DegenerateStripLengthsDoNotThrow()
        {
            Assert.Empty(LovelyLightMath.DefaultRampColors(LightDirection.LeftToRight, 0));
            Assert.Empty(LovelyLightMath.DefaultRampColors(LightDirection.OutsideIn, -3));
            Assert.Single(LovelyLightMath.DefaultRampColors(LightDirection.LeftToRight, 1));
        }
    }

    public class LovelyProfileCompositionTests
    {
        private static LovelyCarProfile M4() =>
            LovelyCarDataParser.Parse(LovelyCarDataParserTests.M4Gt3Json);

        [Fact]
        public void CollapsesMirrorPairsIntoOneProgressionStepEach()
        {
            var car = M4();
            var steps = LovelyLightMath.ProgressionColors(car, car.Gears["1"]);
            // 12 LEDs, 2 dead, 10 lit in 5 mirrored pairs = 5 distinct steps.
            Assert.Equal(5, steps.Length);
            Assert.Equal(0xFF, steps[0].G);          // first step is green
            Assert.Equal(0xFF, steps[4].R);          // last step is red
            Assert.Equal(0x00, steps[4].G);
        }

        [Fact]
        public void BuildsAnOutsideInProfileFromTheCarsOwnColors()
        {
            var car = M4();
            var profile = LovelyLightMath.BuildProfile(car, car.Gears["1"], 10);

            Assert.Equal(LightDirection.OutsideIn, profile.Layout);
            Assert.True(profile.UsedCarColors);
            Assert.Equal(10, profile.Colors.Length);
            Assert.Equal(7150, profile.RedlineRpm);

            // Edges light first and are green; the centre is the car's red.
            Assert.Equal(0xFF, profile.Colors[0].G);
            Assert.Equal(0xFF, profile.Colors[4].R);
            Assert.Equal(0x00, profile.Colors[4].G);
        }

        [Fact]
        public void MapsTheLayoutOntoTheDevicesOwnDirectionNumbering()
        {
            // The device numbers directions 1..4 in an order that is not ours, so
            // this pins the mapping the slot writer depends on.
            Assert.Equal(1, new LightProfile { Layout = LightDirection.InsideOut }.DirectionWire);
            Assert.Equal(2, new LightProfile { Layout = LightDirection.OutsideIn }.DirectionWire);
            Assert.Equal(3, new LightProfile { Layout = LightDirection.LeftToRight }.DirectionWire);
            Assert.Equal(4, new LightProfile { Layout = LightDirection.RightToLeft }.DirectionWire);
        }

        [Fact]
        public void FallsBackToTheConventionalRampWhenTheCarHasNoColors()
        {
            const string noColors = @"{""carName"":""NoColors"",""ledNumber"":5,
                ""ledColor"":[],
                ""ledRpm"":[{""1"":[7000,5000,5500,6000,6500,7000]}]}";
            var car = LovelyCarDataParser.Parse(noColors);
            var profile = LovelyLightMath.BuildProfile(car, car.Gears["1"], 10);

            Assert.False(profile.UsedCarColors);
            Assert.Equal(10, profile.Colors.Length);
            Assert.Equal(2, profile.Colors.Count(c => c.R == 0xFF && c.G == 0x00));  // two red
        }

        [Fact]
        public void ProducesAFullStripForEveryPublishedGear()
        {
            var car = M4();
            foreach (var gear in car.Gears.Values)
            {
                var profile = LovelyLightMath.BuildProfile(car, gear, 10);
                Assert.Equal(10, profile.Colors.Length);
                Assert.True(profile.RedlineRpm > 0);
            }
        }

        [Fact]
        public void HandlesNullInputsWithoutThrowing()
        {
            var profile = LovelyLightMath.BuildProfile(null, null, 10);
            Assert.Equal(10, profile.Colors.Length);      // conventional ramp
            Assert.False(profile.UsedCarColors);
        }
    }
}
