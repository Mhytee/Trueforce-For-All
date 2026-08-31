using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // A G PRO switched to G923 compatibility mode in G HUB: G923 PID, G PRO
    // product string. Capabilities must follow the hardware, not the PID.
    public class WheelDisguiseTests
    {
        private const string ProString = "PRO Racing Wheel";

        [Theory]
        [InlineData(0xC26E)]
        [InlineData(0xC26D)]
        [InlineData(0xC266)]
        public void GProProductString_OnAnyG923Pid_IsDisguise(int pid)
        {
            Assert.True(WheelDiscovery.IsGProInG923Mode((ushort)pid, ProString));
        }

        [Fact]
        public void RealG923_IsNotADisguise()
        {
            // A real G923 reports its own name, or (console mode) nothing at all.
            Assert.False(WheelDiscovery.IsGProInG923Mode(0xC26E, "G923 Racing Wheel"));
            Assert.False(WheelDiscovery.IsGProInG923Mode(0xC26E, null));
            Assert.False(WheelDiscovery.IsGProInG923Mode(0xC26E, ""));
        }

        [Fact]
        public void GProOnItsOwnPid_IsNotADisguise()
        {
            Assert.False(WheelDiscovery.IsGProInG923Mode(0xC272, ProString));
            Assert.False(WheelDiscovery.IsGProInG923Mode(0xC268, ProString));
        }

        [Fact]
        public void DisplayModel_NamesTheMode_AndShortModelStaysGPro()
        {
            var m = new WheelMatch
            {
                Vid = WheelDiscovery.LogitechVid, Pid = 0xC26E,
                Model = "Logitech G923 (Xbox/PC)", ProductString = ProString,
            };
            string label = WheelDiscovery.DisplayModel(m);
            Assert.Equal("Logitech G PRO Racing Wheel (G923 compatibility mode)", label);
            // The per-wheel defaults recipe keys on this: it must not pick the
            // G923 strength and damper for a G PRO motor.
            Assert.Equal("G PRO", WheelDiscovery.ShortModel(label));
        }

        [Fact]
        public void DisplayModel_LeavesARealG923Alone()
        {
            var m = new WheelMatch
            {
                Vid = WheelDiscovery.LogitechVid, Pid = 0xC26E,
                Model = "Logitech G923 (Xbox/PC)", ProductString = null,
            };
            Assert.Equal("Logitech G923 (Xbox/PC)", WheelDiscovery.DisplayModel(m));
            Assert.Equal("G923", WheelDiscovery.ShortModel(WheelDiscovery.DisplayModel(m)));
        }
    }
}
