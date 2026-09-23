using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The "in-game gain is at 0" detector for the finalFF force path.
    public class FinalFfZeroPinTests
    {
        private const long Hz = 1000;   // one tick per millisecond

        [Fact]
        public void ZeroWhileDriving_PinsAfterThreeSeconds()
        {
            var p = new FinalFfZeroPin();
            for (int t = 0; t < 2900; t += 3) Assert.False(p.Note(0f, 80f, t, Hz));
            Assert.True(p.Note(0f, 80f, 3100, Hz));
        }

        [Fact]
        public void ZeroWhileParked_NeverPins()
        {
            var p = new FinalFfZeroPin();
            for (int t = 0; t < 20000; t += 3) Assert.False(p.Note(0f, 0f, t, Hz));
        }

        [Fact]
        public void AStop_DoesNotCountTowardThePin()
        {
            // Two seconds of zero at speed, a stop, two more seconds at speed:
            // never three continuous driving seconds, so no pin.
            var p = new FinalFfZeroPin();
            for (int t = 0; t < 2000; t += 3) p.Note(0f, 80f, t, Hz);
            p.Note(0f, 0f, 2100, Hz);
            for (int t = 2200; t < 4200; t += 3) Assert.False(p.Note(0f, 80f, t, Hz));
        }

        [Fact]
        public void AnyForce_ClearsThePin()
        {
            var p = new FinalFfZeroPin();
            for (int t = 0; t < 3500; t += 3) p.Note(0f, 80f, t, Hz);
            Assert.True(p.IsPinned);
            Assert.False(p.Note(0.02f, 80f, 3600, Hz));
            Assert.False(p.IsPinned);
        }

        [Fact]
        public void RealDriving_IsNeverExactlyZeroForLong_SoNeverPins()
        {
            var p = new FinalFfZeroPin();
            for (int t = 0; t < 10000; t += 3)
            {
                float ff = (t % 900 < 20) ? 0f : 0.05f * ((t / 3) % 5 - 2);   // brief exact zeros, mostly moving
                Assert.False(p.Note(ff, 120f, t, Hz));
            }
        }
    }
}
