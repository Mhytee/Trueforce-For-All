using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The G923 PlayStation-mode switch is a fire-and-forget write to hardware we
    // do not own, so the bytes themselves are the only thing we can guard. A
    // typo here would either do nothing or tell some other classic Logitech
    // wheel to become a G923, and neither shows up in a build.
    public class WheelPcModeSwitchTests
    {
        // Report id 0x30 then F8 09 07 01 01 00 00, per new-lg4ff
        // (hid-lg4ff.c:418-421) via mescon's dd_lg4ff_mode_switch_30_g923.
        private static readonly byte[] Expected =
            { 0x30, 0xF8, 0x09, 0x07, 0x01, 0x01, 0x00, 0x00 };

        [Fact]
        public void Report_CarriesTheLg4ffSwitchCommand()
        {
            Assert.Equal(Expected, WheelPcModeSwitch.BuildSwitchReport(8));
        }

        [Fact]
        public void Report_PadsUpToTheCollectionsReportLength()
        {
            var buf = WheelPcModeSwitch.BuildSwitchReport(16);

            Assert.Equal(16, buf.Length);
            for (int i = 0; i < Expected.Length; i++)
                Assert.Equal(Expected[i], buf[i]);
            for (int i = 8; i < buf.Length; i++)
                Assert.Equal(0, buf[i]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        [InlineData(-1)]
        public void Report_NeverTruncatesTheCommand(int reportLength)
        {
            // A collection too short for the command gets a well-formed 8-byte
            // buffer and Windows does the rejecting, rather than us sending a
            // clipped command that means something else.
            Assert.Equal(Expected, WheelPcModeSwitch.BuildSwitchReport(reportLength));
        }

        [Fact]
        public void PlayStationModePid_IsNotOneWeAlreadySupport()
        {
            // The whole gate: 0xC267 must stay out of SupportedPids, or discovery
            // would claim a wheel that exposes no Trueforce interface and the
            // switch would never run.
            Assert.False(WheelDiscovery.IsSupportedWheel(
                WheelDiscovery.LogitechVid, WheelPcModeSwitch.G923PsPid));
            Assert.True(WheelDiscovery.IsSupportedWheel(WheelDiscovery.LogitechVid, 0xC266));
        }
    }
}
