// Decides when a finished Time Attack run is worth submitting, and exactly once.
//
// Separate from the reader and from the transport because the decision is where the mistakes are.
// Poll a memory reader at 7 Hz and the same finished race is visible for hundreds of consecutive
// samples; submit on "the race is finished" and you send the same lap over and over. Submit on the
// wrong field and every time is a few milliseconds fast. Both are silent failures, and neither
// shows up in a build. So the rule lives here, on its own, with tests.
//
// WHAT COUNTS AS SUBMITTABLE:
//   * the run reached the goal (EndReason == 0). A timeout is a different ending, not a slow lap.
//   * it was Time Attack. A battle time is not comparable with a time-trial board.
//   * the section sum is present and plausible. That sum, not the elapsed clock, is what the game
//     stores and displays: measured 202918 against a clock reading 202914.
//   * the course, direction and car are known, since all three key the board.
//   * we have not already submitted THIS run.

using System;

namespace TrueforceForAll.Core
{
    /// <summary>One finished run, ready to submit.</summary>
    public sealed class Id8FinishedRun
    {
        public int CourseId;
        public int Direction;
        public int CarId;

        /// <summary>The official time: the sum of the section times.</summary>
        public int GoalMs;

        /// <summary>Every section time, in order. The whole array, because a course can have more
        /// than three: the first Momiji run submitted had four, and the fourth (48803 ms) was
        /// silently dropped by carrying only three.</summary>
        public int[] Sections;

        /// <summary>The first three sections, for the columns that only hold three. Derived, so
        /// they cannot drift from <see cref="Sections"/>.</summary>
        public int Section1 { get { return At(0); } }
        public int Section2 { get { return At(1); } }
        public int Section3 { get { return At(2); } }

        private int At(int i) { return Sections != null && Sections.Length > i ? Sections[i] : 0; }

        /// <summary>The elapsed clock, carried only so a log line can show both. It is a few
        /// milliseconds under <see cref="GoalMs"/> and is NOT what gets submitted.</summary>
        public int ClockMs;
    }

    /// <summary>Watches samples and raises each finished run once.</summary>
    public sealed class Id8RunWatcher
    {
        /// <summary>The game mode value for Time Attack, from session + 0x8 (proven).</summary>
        public const int TimeAttackMode = 2;

        /// <summary>Below this a "lap" is not a lap. Guards against a partially filled section
        /// array being read as a complete run.</summary>
        public const int MinPlausibleMs = 10000;

        /// <summary>SEGA's filler time. At or beyond it the run timed out or the value is filler,
        /// and the server rejects it anyway.</summary>
        public const int MaxPlausibleMs = 360000;

        // Identity of the run already reported, so the same finished race seen on hundreds of
        // consecutive polls is only submitted once. Cleared when the race goes away.
        private bool _armed = true;
        private int _lastCourse = -1, _lastDirection = -1, _lastCar = -1, _lastGoal;

        /// <summary>Feed one sample. Returns the run to submit, or null.</summary>
        /// <param name="gameMode">session + 0x8, low byte. Time Attack is 2.</param>
        public Id8FinishedRun Observe(Id8Sample s, int gameMode)
        {
            // The race record is gone (menus, attract, loading), so the next finish is a new run.
            if (!s.Valid || s.EndReason != 0 || s.SectionSumMs <= 0)
            {
                if (s.EndReason != 0) _armed = true;
                return null;
            }

            if ((gameMode & 0xff) != TimeAttackMode) return null;
            if (s.CourseId < 0 || s.CourseId > 15) return null;
            if (s.Direction < 0 || s.Direction > 1) return null;
            if (s.CarId < 0) return null;
            if (s.SectionSumMs < MinPlausibleMs || s.SectionSumMs >= MaxPlausibleMs) return null;

            // Deliberately BEFORE the latch, and returning without setting it. A short array means
            // the last split has not landed yet, which is a "not ready" not a "no": the very next
            // poll normally has it, and consuming the run here would throw the real time away.
            if (!SectionsLookComplete(s.SectionSumMs, s.FinishTimeMs)) return null;

            // Same run still on screen.
            if (!_armed && s.CourseId == _lastCourse && s.Direction == _lastDirection
                        && s.CarId == _lastCar && s.SectionSumMs == _lastGoal)
                return null;

            _armed = false;
            _lastCourse = s.CourseId;
            _lastDirection = s.Direction;
            _lastCar = s.CarId;
            _lastGoal = s.SectionSumMs;

            int[] t = s.SectionTimes ?? new int[0];
            return new Id8FinishedRun
            {
                CourseId = s.CourseId,
                Direction = s.Direction,
                CarId = s.CarId,
                GoalMs = s.SectionSumMs,
                Sections = (int[])t.Clone(),
                ClockMs = s.FinishTimeMs,
            };
        }

        /// <summary>How far the section sum may sit under the elapsed clock and still be a whole
        /// run.
        ///
        /// The two are near-identical by construction: measured on the cabinet, a run whose clock
        /// read 202914 summed to 202918, four milliseconds ABOVE. A second is three orders of
        /// magnitude more slack than that, and still nowhere near a missing section, which is tens
        /// of seconds.</summary>
        public const int SectionSumSlackMs = 1000;

        /// <summary>True when the section array looks complete for this run.
        ///
        /// The reader takes section entries until it meets a zero, with no knowledge of how many
        /// the course actually has, and this class is fed at about 7 Hz on a race that reads
        /// finished for hundreds of consecutive polls. So there is a window, one poll wide, where
        /// the goal has been reached but the final split has not landed yet. The array is then
        /// SHORT but perfectly self-consistent: it sums cleanly, it passes every plausibility
        /// bound, and it is tens of seconds too fast.
        ///
        /// That is the worst possible shape for a leaderboard. It upserts as a personal best the
        /// player can never beat, and the correct time arriving moments later simply loses to it,
        /// so an honest player ends up with a permanent fake record and no way to remove it.
        ///
        /// The elapsed clock is the cross-check the sections cannot fake, because it comes from a
        /// different field written once at the goal.</summary>
        public static bool SectionsLookComplete(int sectionSumMs, int clockMs)
        {
            // No clock to check against: accept rather than reject. A missing cross-check is not
            // evidence of a bad run, and refusing here would silently drop honest laps.
            if (clockMs <= 0) return true;
            return sectionSumMs >= clockMs - SectionSumSlackMs;
        }

        /// <summary>Forget the last run, so a repeat of it would be reported again. For when the
        /// game exits or the reader reattaches and continuity cannot be assumed.</summary>
        public void Reset()
        {
            _armed = true;
            _lastCourse = _lastDirection = _lastCar = -1;
            _lastGoal = 0;
        }
    }
}
