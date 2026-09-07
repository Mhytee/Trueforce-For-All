// When a finished run is submittable, and that it is submitted exactly once.
//
// The reader is polled several times a second and a finished race stays on screen for a long time,
// so "the race is finished" is true for hundreds of consecutive samples. Every test here is really
// asking one of two questions: does the same lap get sent twice, and does anything get sent that
// should not have been.

using System.Linq;
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
    
        // ---- the truncated-section bug ----
        //
        // The reader takes section entries until it meets a zero, knowing nothing about how many
        // the course has, and this class is polled at ~7 Hz on a race that reads finished for
        // hundreds of consecutive polls. One poll before the last split lands, the array is SHORT
        // but self-consistent: it sums cleanly and passes every plausibility bound while being tens
        // of seconds too fast. That upserts as a personal best the player can never beat, and the
        // correct time arriving moments later loses to it.

        private static Id8Sample MidFinish(int s1, int s2, int s3, int clock)
        {
            var s = Finished();
            s.SectionTimes = new[] { s1, s2, s3 };
            s.SectionSumMs = s1 + s2 + s3;
            s.FinishTimeMs = clock;
            return s;
        }

        [Fact]
        public void ARunMissingItsLastSplitIsNotSubmitted()
        {
            // Three of four sections landed; the clock already says the whole run.
            var partial = MidFinish(57203, 41758, 56995, 202914);
            Assert.Null(new Id8RunWatcher().Observe(partial, TA));
        }

        /// <summary>And it must not CONSUME the run: the complete array is one poll away, and
        /// latching here would discard the real time permanently.</summary>
        [Fact]
        public void AShortArrayDoesNotConsumeTheRun()
        {
            var w = new Id8RunWatcher();
            Assert.Null(w.Observe(MidFinish(57203, 41758, 56995, 202914), TA));

            var complete = Finished();   // all four sections, sum 202918, clock 202914
            var run = w.Observe(complete, TA);
            Assert.NotNull(run);
            Assert.Equal(202918, run.GoalMs);
        }

        /// <summary>The measured relationship: the sum sits a few ms ABOVE the clock, never below.
        /// A complete run must never be rejected by the guard.</summary>
        [Fact]
        public void ACompleteRunPassesEvenThoughTheSumExceedsTheClock()
        {
            Assert.True(Id8RunWatcher.SectionsLookComplete(202918, 202914));
        }

        [Theory]
        [InlineData(202918, 0)]        // no clock: accept, a missing cross-check is not evidence
        [InlineData(202918, -1)]
        [InlineData(202000, 202914)]   // 914 ms under, inside the slack
        public void TheGuardDoesNotRejectHonestRuns(int sum, int clock)
        {
            Assert.True(Id8RunWatcher.SectionsLookComplete(sum, clock));
        }

        [Theory]
        [InlineData(159218, 208021)]   // the live Momiji row's three-of-four sections
        [InlineData(150000, 202914)]
        public void TheGuardCatchesAShortArray(int sum, int clock)
        {
            Assert.False(Id8RunWatcher.SectionsLookComplete(sum, clock));
        }

        // ---- carrying every section ----
        //
        // The live Momiji row stored goal 208021 against sections summing to 159218: a 48803 ms
        // fourth split thrown away, because the run copied three while the time summed all of them.

        [Fact]
        public void EverySectionIsCarried()
        {
            var run = new Id8RunWatcher().Observe(Finished(), TA);
            Assert.Equal(new[] { 57203, 41758, 56995, 46962 }, run.Sections);
            Assert.Equal(run.Sections.Sum(), run.GoalMs);
        }

        /// <summary>The three legacy fields are derived, so they cannot drift from the array.</summary>
        [Fact]
        public void TheThreeLegacyFieldsMirrorTheArray()
        {
            var run = new Id8RunWatcher().Observe(Finished(), TA);
            Assert.Equal(57203, run.Section1);
            Assert.Equal(41758, run.Section2);
            Assert.Equal(56995, run.Section3);
        }

        /// <summary>A two-section course must not read a third from nowhere.</summary>
        [Fact]
        public void AShorterCourseReportsZeroForTheMissingLegacyFields()
        {
            var s = Finished();
            s.SectionTimes = new[] { 76327, 76015 };
            s.SectionSumMs = 152342;
            s.FinishTimeMs = 152340;
            var run = new Id8RunWatcher().Observe(s, TA);
            Assert.NotNull(run);
            Assert.Equal(2, run.Sections.Length);
            Assert.Equal(0, run.Section3);
            Assert.Equal(152342, run.GoalMs);
        }
    }
}
