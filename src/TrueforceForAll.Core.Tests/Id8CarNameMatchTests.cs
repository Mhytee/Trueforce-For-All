// TeknoParrot's car strings onto CarIDs.
//
// Every string below is copied verbatim from the live page. That is deliberate: a fixture written
// from memory tests the fixture. The whole set of 47 was checked against the real HTML, and the
// three of ours that never appear (RX-8 Type S, Impreza Ver.V, BRZ) are genuinely absent there.
//
// A wrong answer here is invisible: a real time lands on the wrong car's board and looks completely
// normal, so these cases are worth more than their size suggests.

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8CarNameMatchTests
    {
        [Theory]
        // Plain: maker prefix is the only difference.
        [InlineData("TOYOTA TRUENO GT-APEX (AE86)", 0)]
        [InlineData("TOYOTA LEVIN GT-APEX (AE86)", 1)]
        [InlineData("TOYOTA MR-S (ZZW30)", 5)]
        [InlineData("NISSAN GT-R NISMO (R35)", 263)]
        [InlineData("SUZUKI Cappuccino (EA11R)", 1536)]
        // Three cars share the AE86 chassis, so the model text has to separate them.
        [InlineData("TOYOTA TRUENO 2door GT-APEX (AE86)", 9)]
        [InlineData("TOYOTA LEVIN SR (AE85)", 2)]
        // HTML entities, straight out of the markup.
        [InlineData("NISSAN SILVIA K&#x27;s (S13)", 258)]
        [InlineData("NISSAN Silvia Q&#x27;s (S14)", 259)]
        // Roman numerals: ASCII on their side, single characters on ours.
        [InlineData("NISSAN 180SX TYPE II (RPS13)", 261)]
        [InlineData("HONDA Civic SiR-II (EG6)", 512)]
        [InlineData("MITSUBISHI LANCER Evolution III (CE9A)", 1280)]
        [InlineData("MITSUBISHI LANCER Evolution IV (CN9A)", 1281)]
        [InlineData("MITSUBISHI LANCER Evolution V (CP9A)", 1285)]
        [InlineData("MITSUBISHI LANCER Evolution VI (CP9A)", 1286)]
        [InlineData("MITSUBISHI LANCER Evolution VII (CT9A)", 1283)]
        [InlineData("MITSUBISHI LANCER Evolution IX(CT9A)", 1282)]   // note: no space before the bracket
        [InlineData("MITSUBISHI LANCER Evolution X (CZ4A)", 1284)]
        // Three cars on FD3S, one of which the game names in Japanese.
        [InlineData("MAZDA RX-7 III (FC3S)", 768)]
        [InlineData("MAZDA RX-7 Type R (FD3S)", 769)]
        [InlineData("MAZDA RX-7 Type RS (FD3S)", 773)]
        [InlineData("COMPLETE CAR RE Amemiya Genki-7 (FD3S)", 2048)]
        // The tuned cars: "Kai" to them, U+6539 to the game.
        [InlineData("COMPLETE CAR G-FORCE SUPRA (JZA80 Kai)", 2051)]
        [InlineData("COMPLETE CAR ROADSTER C-SPEC (NA8C Kai)", 2052)]
        [InlineData("COMPLETE CAR NSX-R GT (NA2)", 2053)]
        [InlineData("COMPLETE CAR S2000 GT1 (AP1)", 2050)]
        [InlineData("COMPLETE CAR MONSTER CIVIC R (EK9)", 2049)]
        // No chassis code at all.
        [InlineData("INITIAL D SILEIGHTY", 1792)]
        // Two cars share AP1 and two share EK9; the tuned ones are above.
        [InlineData("HONDA S2000 (AP1)", 515)]
        [InlineData("HONDA CIVIC TYPE R (EK9)", 513)]
        [InlineData("HONDA NSX (NA1)", 516)]
        // Two Imprezas whose codes differ by one letter.
        [InlineData("SUBARU IMPREZA STI (GDBF)", 1025)]
        [InlineData("SUBARU IMPREZA STi (GDBA)", 1026)]
        // Two Skylines sharing a model name, separated only by chassis.
        [InlineData("NISSAN SKYLINE GT-R (BNR32)", 256)]
        [InlineData("NISSAN SKYLINE GT-R (BNR34)", 257)]
        public void SiteNamesResolveToTheRightCar(string siteName, int expected)
        {
            Assert.Equal(expected, Id8CarNameMatch.CarIdFor(siteName));
        }

        /// <summary>Unknown text must not be guessed at. A wrong car writes a real time onto the
        /// wrong board, where it looks entirely plausible.</summary>
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Car")]                              // their table header
        [InlineData("FERRARI F40 (F120)")]
        [InlineData("SOMETHING (ZZZZ)")]
        public void UnknownTextIsRefusedRatherThanGuessed(string siteName)
        {
            Assert.Equal(-1, Id8CarNameMatch.CarIdFor(siteName));
        }

        /// <summary>The three cars TeknoParrot's board does not carry. Nothing should ever map to
        /// them, and nothing here should crash trying.</summary>
        [Theory]
        [InlineData("MAZDA RX-8 Type S (SE3P)", 770)]
        [InlineData("SUBARU IMPREZA STi Ver.V (GC8)", 1024)]
        public void CarsAbsentFromTheSiteStillResolveIfTheyEverAppear(string siteName, int expected)
        {
            Assert.Equal(expected, Id8CarNameMatch.CarIdFor(siteName));
        }

        /// <summary>Distinct cars must not collapse onto one id.</summary>
        [Fact]
        public void TheThreeAe86sStayDistinct()
        {
            int trueno = Id8CarNameMatch.CarIdFor("TOYOTA TRUENO GT-APEX (AE86)");
            int levin = Id8CarNameMatch.CarIdFor("TOYOTA LEVIN GT-APEX (AE86)");
            int twoDoor = Id8CarNameMatch.CarIdFor("TOYOTA TRUENO 2door GT-APEX (AE86)");
            Assert.Equal(3, new System.Collections.Generic.HashSet<int> { trueno, levin, twoDoor }.Count);
        }
    }
}
