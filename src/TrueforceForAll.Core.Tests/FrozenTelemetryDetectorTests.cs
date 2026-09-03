using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>The SimHub fallback has no packet counter, so the frozen-feed
    /// detector treats the physics values themselves as one: live simulation
    /// never repeats its floats exactly, and a reader with no pause flag
    /// replaying one frozen snapshot does nothing else (the RaceRoom pause
    /// report: effects hummed the pre-pause engine forever because frames
    /// kept arriving). The costs are asymmetric, which the tests pin down:
    /// declaring a live feed frozen mutes a real wheel (the RS50 regression
    /// class), declaring a frozen feed live just delays silence, so the
    /// detector demands a long window of exact stillness and recovers on the
    /// first changed frame.</summary>
    public class FrozenTelemetryDetectorTests
    {
        // Ticks are caller-supplied, so the tests run on a millisecond clock:
        // ticksPerSecond = 1000 makes every literal below read as ms.
        private const long TickHz = 1000;
        private const long Window = 2000;   // DefaultFreezeAfterSeconds in ms

        private static bool Note(FrozenTelemetryDetector d, long atMs,
                                 double rpm = 3000, double speed = 80,
                                 double heave = 0.1, double sway = 0.2,
                                 double surge = 0.3, double yaw = 1.5)
            => d.Note(rpm, speed, heave, sway, surge, yaw, atMs, TickHz);

        [Fact]
        public void TheFirstFrameEverSeenIsNeverFrozen()
        {
            var d = new FrozenTelemetryDetector();
            Assert.False(Note(d, 0));
        }

        [Fact]
        public void JitteringTelemetryNeverFreezesHoweverLongItRuns()
        {
            var d = new FrozenTelemetryDetector();
            // Ten seconds of an idling engine: rpm wobbles a little every
            // frame, everything else static. One live channel is enough.
            for (long t = 0; t < 10000; t += 16)
                Assert.False(Note(d, t, rpm: 850 + (t % 3)), "went frozen at " + t + " ms");
        }

        [Fact]
        public void AnAudibleSnapshotRepeatingPastTheWindowIsFrozen()
        {
            var d = new FrozenTelemetryDetector();
            Note(d, 0);
            for (long t = 16; t < Window; t += 16)
                Assert.False(Note(d, t), "froze early at " + t + " ms");
            Assert.True(Note(d, Window + 16));
            // And it stays frozen while the replay continues.
            Assert.True(Note(d, Window + 5000));
        }

        [Fact]
        public void AnyOneChannelChangingResetsTheClock()
        {
            var d = new FrozenTelemetryDetector();
            Note(d, 0);
            for (long t = 16; t < 1900; t += 16) Note(d, t);
            // Just shy of the window, a single channel moves: the feed is
            // alive, and the stillness clock starts over from here.
            Assert.False(Note(d, 1900, heave: 0.10001));
            for (long t = 1916; t < 1900 + Window; t += 16)
                Assert.False(Note(d, t, heave: 0.10001), "froze early at " + t + " ms");
            Assert.True(Note(d, 1900 + Window + 16, heave: 0.10001));
        }

        [Fact]
        public void RecoveryIsImmediateOnTheFirstChangedFrame()
        {
            var d = new FrozenTelemetryDetector();
            Note(d, 0);
            Assert.True(Note(d, Window + 100));
            // Unpause: physics resumes, values differ, the very next frame
            // must dispatch (this is what re-latches the effects).
            Assert.False(Note(d, Window + 116, rpm: 3010));
            // A fresh freeze then needs a fresh window.
            Assert.False(Note(d, Window + 132, rpm: 3010));
        }

        [Fact]
        public void ASilentSnapshotMayRepeatForever()
        {
            var d = new FrozenTelemetryDetector();
            // Parked, engine off: all zeros legitimately repeat, and there
            // is nothing audible to settle, so the verdict stays live (which
            // also keeps menu frames flowing for consumers like the FS
            // synthetic spring gate).
            for (long t = 0; t < 20000; t += 16)
                Assert.False(Note(d, t, rpm: 0, speed: 0, heave: 0, sway: 0, surge: 0, yaw: 0),
                             "silent snapshot froze at " + t + " ms");
        }

        [Fact]
        public void RollingWithTheEngineOffIsStillAudibleAndStillFreezes()
        {
            var d = new FrozenTelemetryDetector();
            // Speed alone keeps road/slip effects running, so a frozen
            // rolling snapshot must be caught too (rpm 0 is not a pass).
            Note(d, 0, rpm: 0, speed: 60);
            Assert.True(Note(d, Window + 16, rpm: 0, speed: 60));
        }
    }
}
