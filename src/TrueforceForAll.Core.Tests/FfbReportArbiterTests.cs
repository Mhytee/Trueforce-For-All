using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The 0x11 / 0x12 live-report rule, driven with an explicit clock. Every
    // scenario here is one the owner's G PRO or an RS50 has actually produced.
    public class FfbReportArbiterTests
    {
        private const short Real = 2000;    // a driving-strength force
        private const short Tiny = 3;       // the RS50's idle noise on 0x11

        // Feed n packets of one report, evenly spaced over spanMs, starting at
        // t0. A real force stream is change-driven, so each packet carries a
        // slightly different value around the given force.
        private static int Stream(FfbReportArbiter a, byte rid, short force, int n, int t0, int spanMs, out int accepted)
        {
            accepted = 0;
            int t = t0;
            for (int i = 0; i < n; i++)
            {
                t = t0 + (int)((long)spanMs * i / n);
                short v = (short)(force + (i % 7) - 3);
                if (a.Accept(rid, v, t)) accepted++;
            }
            return t;
        }

        // The same, but every packet repeats one value: a heartbeat, not force.
        private static int Constant(FfbReportArbiter a, byte rid, short value, int n, int t0, int spanMs, out int accepted)
        {
            accepted = 0;
            int t = t0;
            for (int i = 0; i < n; i++)
            {
                t = t0 + (int)((long)spanMs * i / n);
                if (a.Accept(rid, value, t)) accepted++;
            }
            return t;
        }

        [Fact]
        public void LongReportStream_BecomesLive_AfterAShortRun()
        {
            var a = new FfbReportArbiter();
            Stream(a, 0x11, Real, 10, 0, 60, out int accepted);
            Assert.Equal((byte)0x11, a.LiveReport);
            // The first three arm the run; the fourth and after are extracted.
            Assert.Equal(7, accepted);
            Assert.Contains("0x11", a.TakeDecision());
            Assert.Null(a.TakeDecision());
        }

        [Fact]
        public void VeryLongOnlyStream_BecomesLive_WithoutAnyOptIn()
        {
            // The 2026-08-28 G PRO mode: 0x12 at ~25/s, nothing on 0x11.
            var a = new FfbReportArbiter();
            Stream(a, 0x12, Real, 25, 0, 1000, out int accepted);
            Assert.Equal((byte)0x12, a.LiveReport);
            Assert.True(accepted >= 20);
        }

        [Fact]
        public void LoneManagementWrite_NeverBecomesLive()
        {
            var a = new FfbReportArbiter();
            Assert.False(a.Accept(0x12, -30000, 0));     // a "hard left" garbage value at game open
            Assert.False(a.Accept(0x12, -30000, 700));
            Assert.Equal((byte)0, a.LiveReport);
            // The real stream then takes the channel at once.
            Stream(a, 0x11, Real, 10, 800, 60, out _);
            Assert.Equal((byte)0x11, a.LiveReport);
        }

        [Fact]
        public void OtherReportsTrickle_IsIgnored_WhileLiveIsStreaming()
        {
            var a = new FfbReportArbiter();
            Stream(a, 0x11, Real, 40, 0, 250, out _);
            // 0x12 management writes at 4/s alongside: rejected, channel unchanged.
            for (int t = 300; t < 3000; t += 250)
                Assert.False(a.Accept(0x12, -30000, t));
            Assert.Equal((byte)0x11, a.LiveReport);
        }

        [Fact]
        public void ParkedQuietSpell_DoesNotFlipTheChannel()
        {
            // 0x11 live, then the car parks: 0x11 says nothing for 7 s while
            // 0x12 trickles at 4/s. 4/s never reaches the switch rate.
            var a = new FfbReportArbiter();
            int t = Stream(a, 0x11, Real, 40, 0, 250, out _);
            for (int q = t + 250; q < t + 7000; q += 250)
                Assert.False(a.Accept(0x12, 500, q));
            Assert.Equal((byte)0x11, a.LiveReport);
            // And the returning 0x11 stream is accepted straight away.
            Assert.True(a.Accept(0x11, Real, t + 7100));
        }

        [Fact]
        public void TransportMove_SwitchesAfterSilencePlusSustainedRate()
        {
            var a = new FfbReportArbiter();
            int t = Stream(a, 0x11, Real, 40, 0, 250, out _);
            // Driver moves the stream to 0x12 at 25/s; 0x11 falls silent.
            // Inside the 3 s silence the challenger is refused...
            Stream(a, 0x12, Real, 25, t + 100, 1000, out int early);
            Assert.Equal(0, early);
            Assert.Equal((byte)0x11, a.LiveReport);
            // ...after it, one second of 25/s takes the channel.
            Stream(a, 0x12, Real, 25, t + 3200, 1000, out int late);
            Assert.Equal((byte)0x12, a.LiveReport);
            Assert.True(late >= 5);
            Assert.Contains("moved", a.TakeDecision());
        }

        [Fact]
        public void Rs50IdleNoiseOnLong_NeverOutranksRealForceOnVeryLong()
        {
            var a = new FfbReportArbiter();
            // 0x11 chatters +/-3 at 100/s: never non-trivial, never live.
            for (int t = 0; t < 1000; t += 10) Assert.False(a.Accept(0x11, Tiny, t));
            Assert.Equal((byte)0, a.LiveReport);
            Stream(a, 0x12, Real, 10, 1000, 100, out _);
            Assert.Equal((byte)0x12, a.LiveReport);
            // The noise keeps being rejected while 0x12 is live.
            Assert.False(a.Accept(0x11, Tiny, 1200));
        }

        [Fact]
        public void Rs50InAssettoCorsa_ConstantHeartbeatOnVeryLong_NeverOutranksRealForceOnLong()
        {
            // Tester trace 2026-08-29: 0x12 carries 32767 on every packet at
            // ~100/s from the first second; the real force is on 0x11 at ~26/s
            // and only starts a few seconds later. Rate alone would pick 0x12
            // and pin the wheel at full lock.
            var a = new FfbReportArbiter();
            Constant(a, 0x12, 32767, 100, 0, 1000, out int heartbeat);
            Assert.Equal(0, heartbeat);
            Assert.Equal((byte)0, a.LiveReport);
            // The heartbeat keeps running while the force arrives.
            int t = 1000;
            int accepted11 = 0;
            for (int i = 0; i < 52; i++)
            {
                t = 1000 + i * 38;
                Assert.False(a.Accept(0x12, 32767, t));
                if (a.Accept(0x11, (short)(-127 - i * 200), t + 1)) accepted11++;
            }
            Assert.Equal((byte)0x11, a.LiveReport);
            Assert.True(accepted11 >= 48);
            // Still rejected once the channel is decided, at any rate.
            Assert.False(a.Accept(0x12, 32767, t + 10));
        }

        [Fact]
        public void FrozenForceReemittedOnTheLiveReport_StillPasses()
        {
            // FH6 re-emits the same pre-pause force through a pause; the pause
            // handling downstream needs to see it, so the live report is never
            // filtered by change.
            var a = new FfbReportArbiter();
            Stream(a, 0x11, Real, 10, 0, 60, out _);
            Constant(a, 0x11, 12000, 20, 100, 400, out int passed);
            Assert.Equal(20, passed);
        }

        [Fact]
        public void Reset_ForgetsTheChannel()
        {
            var a = new FfbReportArbiter();
            Stream(a, 0x11, Real, 10, 0, 60, out _);
            a.Reset();
            Assert.Equal((byte)0, a.LiveReport);
            Assert.False(a.Accept(0x11, Real, 100));
        }
    }
}
