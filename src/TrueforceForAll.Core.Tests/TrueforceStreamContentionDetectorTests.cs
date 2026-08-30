using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The second-writer verdict: what the capture saw on the stream endpoint
    // beyond what we wrote, with the capture's own lag ruled out. Windows are
    // driven by hand (the detector is clock-free).
    public class TrueforceStreamContentionDetectorTests
    {
        private const int W = TrueforceStreamContentionDetector.WindowMs;

        // Drives one window: our stream wrote `ours` packets and the capture
        // saw `tap` stream packets in it. Returns whether the verdict flipped.
        private sealed class Sim
        {
            public readonly TrueforceStreamContentionDetector D = new TrueforceStreamContentionDetector();
            private long _tap, _ours, _ms;
            public Sim() { D.Observe(0, 0, 0); }   // baseline sample
            public bool Window(long tap, long ours)
            {
                _tap += tap; _ours += ours; _ms += W;
                return D.Observe(_tap, _ours, _ms);
            }
            public void Restart(long tap, long ours) { _tap = tap; _ours = ours; _ms += W; D.Observe(_tap, _ours, _ms); }
        }

        [Fact]
        public void OurStreamAlone_NeverDetects()
        {
            var s = new Sim();
            for (int i = 0; i < 60; i++) Assert.False(s.Window(1000, 1000));
            Assert.False(s.D.Detected);
            Assert.Equal(0, s.D.LastForeignPerSec);
        }

        [Fact]
        public void SecondWriter_DetectsOnTheSecondWindow()
        {
            var s = new Sim();
            s.Window(1000, 1000);
            Assert.False(s.Window(2000, 1000));   // one window of foreign traffic is not a verdict
            Assert.True(s.Window(2000, 1000));    // two is
            Assert.True(s.D.Detected);
            Assert.Equal(1000, s.D.LastForeignPerSec);
        }

        [Fact]
        public void AlternatingWriters_HalfRateEach_StillDetects()
        {
            // Two 1 kHz writers share the endpoint: each lands about 500/s.
            var s = new Sim();
            s.Window(1000, 1000);
            s.Window(1000, 500);
            Assert.True(s.Window(1000, 500));
            Assert.Equal(500, s.D.LastForeignPerSec);
        }

        [Fact]
        public void CaptureLagThatCatchesUp_NeverDetects()
        {
            // The pipe delivers late and then in a burst: the burst window
            // looks like +1000 foreign, but the cumulative excess is zero.
            var s = new Sim();
            for (int i = 0; i < 20; i++)
            {
                Assert.False(s.Window(0, 1000));
                Assert.False(s.Window(2000, 1000));
            }
            Assert.False(s.D.Detected);
        }

        [Fact]
        public void BurstyPipe_TwoBurstWindowsInARow_NeverDetects()
        {
            // 1500-packet bursts every 1.5 s land in two consecutive 1 s
            // windows now and then (+500 each), but never add up to more
            // than we wrote.
            var s = new Sim();
            long[] pattern = { 0, 1500, 1500, 0, 1500, 1500 };   // 6000 in 6 s
            for (int i = 0; i < 60; i++) Assert.False(s.Window(pattern[i % pattern.Length], 1000));
            Assert.False(s.D.Detected);
        }

        [Fact]
        public void OneTimeUncountedBurst_NeverDetects()
        {
            // Something small we did not count is a one-window blip under
            // the rate threshold.
            var s = new Sim();
            s.Window(1000, 1000);
            s.Window(1136, 1000);
            s.Window(1000, 1000);
            Assert.False(s.D.Detected);
        }

        [Fact]
        public void HeldStream_ForeignOnly_StaysDetected()
        {
            // We stopped writing; the game keeps streaming. The verdict must
            // hold, or the hold would release into the clash.
            var s = new Sim();
            s.Window(2000, 1000);
            s.Window(2000, 1000);
            Assert.True(s.D.Detected);
            for (int i = 0; i < 30; i++) Assert.False(s.Window(1000, 0));
            Assert.True(s.D.Detected);
            Assert.Equal(1000, s.D.LastForeignPerSec);
        }

        [Fact]
        public void ForeignStops_ClearsAfterQuietWindows_AndRebaselines()
        {
            var s = new Sim();
            s.Window(2000, 1000);
            s.Window(2000, 1000);
            Assert.True(s.D.Detected);
            bool flipped = false;
            for (int i = 0; i < TrueforceStreamContentionDetector.OffWindows; i++)
            {
                flipped = s.Window(1000, 1000);
                if (i < TrueforceStreamContentionDetector.OffWindows - 1) Assert.False(flipped);
            }
            Assert.True(flipped);
            Assert.False(s.D.Detected);

            // The 2000 foreign packets of the old session must not count
            // toward the next verdict: a fresh writer still needs two windows.
            Assert.False(s.Window(2000, 1000));
            Assert.True(s.Window(2000, 1000));
        }

        [Fact]
        public void QuietRunIsConsecutive_AGapRestartsIt()
        {
            var s = new Sim();
            s.Window(2000, 1000);
            s.Window(2000, 1000);
            for (int i = 0; i < TrueforceStreamContentionDetector.OffWindows - 1; i++) s.Window(1000, 1000);
            s.Window(1500, 1000);   // the game is back for a moment
            for (int i = 0; i < TrueforceStreamContentionDetector.OffWindows - 1; i++) Assert.False(s.Window(1000, 1000));
            Assert.True(s.D.Detected);
            Assert.True(s.Window(1000, 1000));
            Assert.False(s.D.Detected);
        }

        [Fact]
        public void CounterGoingBackwards_RebaselinesWithoutAVerdict()
        {
            // A restarted capture process starts its count at zero.
            var s = new Sim();
            for (int i = 0; i < 5; i++) s.Window(1000, 1000);
            s.Restart(0, 5000);
            Assert.False(s.D.Detected);
            for (int i = 0; i < 5; i++) Assert.False(s.Window(1000, 1000));
            Assert.False(s.D.Detected);
            // A real writer after the restart is still found.
            s.Window(2000, 1000);
            Assert.True(s.Window(2000, 1000));
        }

        [Fact]
        public void SlowCaptureLoss_CannotHideAWriterForLong()
        {
            // 1% of records dropped for 400 s would leave the excess at -4000
            // and a real writer invisible for four windows more; the floor
            // keeps the drift bounded.
            var s = new Sim();
            for (int i = 0; i < 400; i++) Assert.False(s.Window(990, 1000));
            Assert.False(s.D.Detected);
            int windows = 0;
            while (!s.D.Detected && windows < 20) { s.Window(2000, 1000); windows++; }
            Assert.True(s.D.Detected);
            Assert.True(windows <= 2 + TrueforceStreamContentionDetector.DriftFloor / 1000 + 1,
                "took " + windows + " windows");
        }

        [Fact]
        public void SubWindowSamples_DoNothing()
        {
            var d = new TrueforceStreamContentionDetector();
            d.Observe(0, 0, 0);
            for (int t = 100; t < W; t += 100) Assert.False(d.Observe(t * 2, t, t));
            Assert.False(d.Detected);
            Assert.Equal(0, d.LastForeignPerSec);
        }

        [Fact]
        public void Reset_ForgetsTheVerdict()
        {
            var s = new Sim();
            s.Window(2000, 1000);
            s.Window(2000, 1000);
            Assert.True(s.D.Detected);
            s.D.Reset();
            Assert.False(s.D.Detected);
            Assert.Equal(0, s.D.LastForeignPerSec);
        }
    }
}
