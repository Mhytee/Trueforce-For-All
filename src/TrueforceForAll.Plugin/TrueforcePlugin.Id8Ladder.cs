// Ladder climb: which of the two shop-board layouts is in front of the player, and when it moves.
//
// The shop top ten is read by two different readers who want opposite things from it.
//
// A PERSON reads it on the leaderboard screen off the attract loop. They want to see themselves on
// it, with the drivers they are chasing above and the ones chasing them below. That is the
// Standings layout: fifth, four above, five below.
//
// THE GAME reads its top row. That row is the shop time shown at stage select, and it is copied
// into the in-race HUD as the time to beat when the race scene is built. If the top row is a world
// record then the target is a fantasy and the ladder is decoration. That is the Target layout: the
// driver one place above the player first, the player second, so the time they are shown is the one
// rung they are actually trying to take.
//
// So the layout has to follow who is looking. Signed in means the game is about to read it; nobody
// signed in means a person might be.
//
// Because the HUD COPIES rather than reads, the Target layout has to be in place BEFORE the race
// scene is built, not at the moment it starts. The window this uses is the whole mode, car and
// course select, which on the rig ran about thirty seconds.
//
// WHY NOT THE SCREEN NUMBER. The scene manager gives one, but only 5, the race, has ever been
// identified, and nothing else on that object was ever mapped. Zero there is the unread sentinel
// rather than a screen, so keying on it would be reading a failed memory read as an attract screen.
// The transition log is still printing, so those numbers can be had for one rig pass, and nothing
// here needs them.
//
// WHY NOT THE CARD. HasCard was the obvious answer and it is the wrong one. It is the OR of a card
// id and a name test, and on the rig the name half flipped to true partway through a GUEST run with
// the card id still -1, so it does not mark the start of a session.
//
// WHAT IT ACTUALLY USES. A session exists but no race record does. That is the state the reader
// calls "not in a race", and it is the one state seen only when a person is playing: across about
// fourteen attract-demo cycles the demo built its session and its race record inside a single poll,
// so the demo is never caught in it, while a real player sat in it for half a minute picking a mode
// and a course. The demo does build a session and it does set InRace, so neither alone can mean
// somebody is playing; the pair can.
//
// And the session pointer really does go back to zero, on a timer, with nobody at the machine. So
// no session at all means the cabinet is idling at attract, which is when the board should be
// something a person can find themselves on.

using System;
using System.Threading;
using System.Threading.Tasks;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    public sealed partial class TrueforcePlugin
    {
        /// <summary>The session and race pair from the last sample, and how many samples running
        /// it has looked like that.
        ///
        /// A FAILED MEMORY READ IS INDISTINGUISHABLE FROM THE SESSION ENDING. Id8Sample is a struct
        /// and a failed read leaves its fields at zero, so one unlucky read of the session root
        /// reads exactly like the player walking away, and the boards would be rewritten twice for
        /// nothing. Acting only on a reading that has held for a while costs about half a second at
        /// each real transition, against a menu window of about thirty.</summary>
        private bool _arcadeSessionWas, _arcadeRaceWas;
        private int _arcadeStateHeld;

        /// <summary>How many samples a reading has to hold before the boards are rewritten.</summary>
        private const int ArcadeLadderSettleSamples = 20;

        /// <summary>Menu-state work, and the reason it runs OUTSIDE the validity guard.
        ///
        /// Id8Sample.Valid means the whole car chain resolved, which only happens inside a race.
        /// Everything here is about a MENU, so none of it can sit behind that test. The reader
        /// stores the sample either way, and the identity and scene halves survive the menus, which
        /// is exactly what this needs.</summary>
        private void ObserveArcadeLadder(Id8Sample s)
        {
            var boards = _arcadeBoards;
            if (boards == null) return;

            if (!boards.Attached) return;

            // Settle first. Both tests below key on fields that read as zero when a memory read
            // fails, so a single bad read would otherwise look like the player walking away.
            if (s.SessionValid != _arcadeSessionWas || s.InRace != _arcadeRaceWas)
            {
                _arcadeSessionWas = s.SessionValid;
                _arcadeRaceWas = s.InRace;
                _arcadeStateHeld = 1;
                return;
            }
            if (_arcadeStateHeld < ArcadeLadderSettleSamples) { _arcadeStateHeld++; return; }

            // A session with no race is a person in the menus. No session at all is the cabinet
            // idling at attract. In a race, which includes the attract demo, leave the boards be:
            // the HUD has already taken its copy and nothing on screen is going to read them.
            Id8LadderView want = boards.LadderView;
            if (s.SessionValid && !s.InRace) want = Id8LadderView.Target;
            else if (!s.SessionValid) want = Id8LadderView.Standings;

            // THE RACE RECORD GOING AWAY is what "they left the results screen" looks like. The
            // race record and the car state both stay readable through the results, the earnings
            // and the continue screen, so this is the far side of all of it.
            //
            // The scene number is NOT the test, although it looks like the obvious one. The game
            // leaves scene 5 at the GOAL, not at the end of the results: on the rig the old
            // scene-based rewrite started twenty-two milliseconds after the run-finished line, in
            // the middle of the new-record flourish, which is precisely the moment its own comment
            // says it was written to avoid.
            bool refillNow = boards.RefillOwed && !s.InRace;

            // Against the layout the boards are ACTUALLY holding, not one we remember asking
            // for. A swap that could not run is retried by itself on the next sample, and a game
            // relaunch, which resets that back to Standings, re-aims the boards without being
            // told the game restarted.
            if (!refillNow && want == boards.LadderView) return;

            if (Interlocked.CompareExchange(ref _arcadeBoardBusy, 1, 0) != 0) return;

            // Cleared only once the work is DISPATCHED, not on the decision, so a refill that lost
            // the latch to the opening fill is still owed on the next sample.
            if (refillNow) boards.ClearRefillOwed();

            Task.Run(() =>
            {
                try
                {
                    if (refillNow)
                    {
                        // A full rebuild, which is what folds the lap they just drove into the
                        // ranking and so moves them past whoever they beat. Told the layout first
                        // rather than swapped afterwards: the fill writes every board once and
                        // there is no reason to write the shop boards a second time.
                        boards.SetLadderView(want);
                        boards.FillAllAsync(CancellationToken.None, force: true)
                              .GetAwaiter().GetResult();
                    }
                    else
                    {
                        boards.ApplyLadderView(want);
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info("[TF4ALL] Arcade ladder layout failed: " + ex.Message);
                }
                finally { Interlocked.Exchange(ref _arcadeBoardBusy, 0); }
            });
        }
    }
}
