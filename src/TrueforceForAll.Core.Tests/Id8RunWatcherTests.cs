// When a finished run is submittable, and that it is submitted exactly once.
//
// The reader is polled several times a second and a finished race stays on screen for a long time,
// so "the race is finished" is true for hundreds of consecutive samples. Every test here is really
// asking one of two questions: does the same lap get sent twice, and does anything get sent that
// should not have been.

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8RunWatcherTests
    {
        private const int TA = Id8RunWatcher.TimeAttackMode;

        private static Id8Sample Finished(int course = 3, int dir = 0, int car = 263,
                                          int s1 = 57203, int s2 = 41758, int s3 = 56995, int s4 = 46962)
        {
            var s = new Id8Sample
            {
                Valid = true,
                EndReason = 0,
                CourseId = course,
                Direction = dir,
                CarId = car,
                SectionTimes = new[] { s1, s2, s3, s4 },
                SectionSumMs = s1 + s2 + s3 + s4,
                FinishTimeMs = s1 + s2 + s3 + s4 - 4,   // the clock runs a shade under
            };
            return s;
        }

        private static Id8Sample InMenus() => new Id8Sample { Valid = true, EndReason = -1, CourseId = -1, Direction = -1 };

        [Fact]
        public void ReportsAFinishedTimeAttackRun()
        {
            var run = new Id8RunWatcher().Observe(Finished(), TA);
            Assert.NotNull(run);
            Assert.Equal(3, run.CourseId);
            Assert.Equal(0, run.Direction);
            Assert.Equal(263, run.CarId);
            Assert.Equal(202918, run.GoalMs);
            Assert.Equal(57203, run.Section1);
        }

        /// <summary>The measured case: the submitted time is the section SUM, not the clock.</summary>
        [Fact]
        public void SubmitsTheSectionSumRatherThanTheElapsedClock()
        {
            var run = new Id8RunWatcher().Observe(Finished(), TA);
            Assert.Equal(202918, run.GoalMs);
            Assert.Equal(202914, run.ClockMs);
            Assert.NotEqual(run.ClockMs, run.GoalMs);
        }

        /// <summary>The whole reason this class exists.</summary>
        [Fact]
        public void TheSameFinishedRunIsOnlyReportedOnce()
        {
            var w = new Id8RunWatcher();
            Assert.NotNull(w.Observe(Finished(), TA));
            for (int i = 0; i < 200; i++) Assert.Null(w.Observe(Finished(), TA));
        }

        [Fact]
        public void ANewRunAfterReturningToTheMenusIsReported()
        {
            var w = new Id8RunWatcher();
            Assert.NotNull(w.Observe(Finished(), TA));
            w.Observe(InMenus(), TA);
            Assert.NotNull(w.Observe(Finished(), TA));
        }

        /// <summary>Two identical times in a row on the same car and course is vanishingly unlikely
        /// but must still work: the menus in between are what makes it a new run.</summary>
        [Fact]
        public void AnIdenticalTimeAfterAMenuGapIsStillANewRun()
        {
            var w = new Id8RunWatcher();
            var first = w.Observe(Finished(), TA);
            w.Observe(InMenus(), TA);
            var second = w.Observe(Finished(), TA);
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first.GoalMs, second.GoalMs);
        }

        /// <summary>A timeout is a different ending, not a slow lap.</summary>
        [Fact]
        public void ATimedOutRunIsNotSubmitted()
        {
            var s = Finished();
            s.EndReason = 1;
            Assert.Null(new Id8RunWatcher().Observe(s, TA));
        }

        /// <summary>A battle time is not comparable with a time-trial board.</summary>
        [Theory]
        [InlineData(1)]   // RaceTest
        [InlineData(3)]   // Story
        [InlineData(4)]   // VS battle
        [InlineData(5)]   // Net battle
        public void OnlyTimeAttackCounts(int mode)
        {
            Assert.Null(new Id8RunWatcher().Observe(Finished(), mode));
        }

        [Fact]
        public void APartiallyFilledSectionArrayIsNotAFinishedLap()
        {
            var s = Finished();
            s.SectionTimes = new[] { 5000 };
            s.SectionSumMs = 5000;          // below MinPlausibleMs
            Assert.Null(new Id8RunWatcher().Observe(s, TA));
        }

        [Fact]
        public void AFillerLengthTimeIsNotSubmitted()
        {
            var s = Finished();
            s.SectionSumMs = 360000;
            Assert.Null(new Id8RunWatcher().Observe(s, TA));
        }

        [Theory]
        [InlineData(-1, 0, 263)]
        [InlineData(16, 0, 263)]
        [InlineData(3, -1, 263)]
        [InlineData(3, 2, 263)]
        [InlineData(3, 0, -1)]
        public void AnUnknownBoardKeyIsNotSubmitted(int course, int dir, int car)
        {
            var s = Finished(course, dir, car);
            Assert.Null(new Id8RunWatcher().Observe(s, TA));
        }

        [Fact]
        public void AnInvalidSampleIsIgnored()
        {
            var s = Finished();
            s.Valid = false;
            Assert.Null(new Id8RunWatcher().Observe(s, TA));
        }

        [Fact]
        public void ResetLetsTheSameRunBeReportedAgain()
        {
            var w = new Id8RunWatcher();
            Assert.NotNull(w.Observe(Finished(), TA));
            Assert.Null(w.Observe(Finished(), TA));
            w.Reset();
            Assert.NotNull(w.Observe(Finished(), TA));
        }
    }
}
