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
        /// <summary>The attract screen, measured on the rig 2026-09-08.
        ///
        /// One pass gave the whole vocabulary: 1 is attract, 2 is the mode and course select, 0 is
        /// loading, 5 is the race. The leaderboard is reached with an arrow FROM attract and does
        /// not change the scene, so the whole of the reading is scene 1, which is exactly the span
        /// the standings layout is for.
        ///
        /// KEYED ON THE POSITIVE VALUE ON PURPOSE. A failed memory read leaves the field at zero,
        /// and zero is loading, so a bad read can move the board to Target, which is harmless and
        /// self-correcting, and can never fake attract.</summary>
        private const int Id8AttractSceneId = 1;

        /// <summary>Loading, and also what a failed read looks like, which here is the same
        /// answer: do not move the boards on it.
        ///
        /// It sits between every other screen, including for the minute or so after a run, and it
        /// is where the game boots. Treating it as "not attract", and so as a reason to aim the
        /// board at a target, meant a fresh launch re-aimed the boards before the cabinet had
        /// reached its attract screen. The shop boards persist into the save, so that layout is
        /// what the next launch shows until we attach: the owner relaunched and found himself
        /// second on his own leaderboard.</summary>
        private const int Id8LoadingSceneId = 0;

        /// <summary>The scene from the last sample, and how many samples running it has held.
        ///
        /// Short, because this has to WIN A RACE against the select screen. The game reads the
        /// shop time as that screen builds, so every sample spent settling is a sample in which the
        /// wrong row can be copied. Measured: three and a half seconds of settling was enough for
        /// the target to be taken from the standings layout, four rungs too far up.</summary>
        private int _arcadeSceneWas = -1;
        private int _arcadeSceneHeld;

        /// <summary>How many samples a scene has to hold before the boards are rewritten.</summary>
        private const int ArcadeLadderSettleSamples = 3;

        /// <summary>Its OWN latch, deliberately not the one the fill uses.
        ///
        /// The fill is dispatched from the same per-frame step, a few lines earlier, and takes that
        /// latch EVERY frame even when it is about to do nothing because the boards are already
        /// filled. So the layout swap, running second, lost the race most frames and only landed
        /// when it happened to win: measured on the rig at eight seconds off one entry to the
        /// select screen and thirty-seven off the next, which is how the game came to read the
        /// standings layout and hand the player a target four rungs up.
        ///
        /// Two latches are safe because the service serialises the writes themselves, and a swap
        /// that loses THAT is simply asked for again on the next frame.</summary>
        private int _arcadeLadderBusy;

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

            // Attract is the ONLY place the reading layout belongs; everywhere else the board is
            // about to be read by the game rather than by a person.
            //
            // This was keyed on "a session exists but no race does", which is a true statement
            // about being in the menus and arrives too late to be useful: the session appears at
            // the same moment the select screen does, so the swap and the screen were racing, and
            // on the rig the screen won by three and a half seconds. The scene changes as they
            // LEAVE ATTRACT, before any of the select flow is built, which is the whole difference.
            Id8LadderView want = boards.LadderView;
            if (s.SceneId == Id8AttractSceneId) want = Id8LadderView.Standings;
            else if (s.SceneId != Id8LoadingSceneId) want = Id8LadderView.Target;

            if (s.SceneId != _arcadeSceneWas) { _arcadeSceneWas = s.SceneId; _arcadeSceneHeld = 1; }
            else if (_arcadeSceneHeld < ArcadeLadderSettleSamples) _arcadeSceneHeld++;

            // SETTLING IS ONLY FOR THE WAY BACK. Leaving attract is a race against a screen the
            // game is building right then, and every sample spent confirming is a sample in which
            // the wrong row can be copied, so Target goes on the FIRST sample that says so. Costing
            // nothing: the worst a stray reading can do in that direction is aim a board nobody is
            // looking at, and the next sample puts it back.
            //
            // Returning to Standings has the whole attract loop to happen in and is the direction
            // that can actually spoil something, so it waits to be sure.
            if (want == Id8LadderView.Standings && _arcadeSceneHeld < ArcadeLadderSettleSamples) return;

            // THE RACE SCENE ENDING is what "the run is over" looks like. Measured on the rig:
            // the goal at 02:33:19.569 and the scene left 5 ten seconds later at 02:33:29.552, so
            // this lands after the new-record flourish rather than in the middle of it, and well
            // before they are back at the stage select.
            //
            // It was keyed on the race RECORD going away, which was wrong in the other direction.
            // That record outlives the results, the earnings, the continue screen, the stage select
            // AND the start of the next run: on the rig it never came back at all, so the rebuild
            // simply did not happen and the player finished a run three seconds faster with the
            // ladder still aimed where it was. The record going is session end, not run end.
            bool refillNow = boards.RefillOwed
                          && s.SceneId != Id8MemoryTelemetry.RaceSceneId
                          && _arcadeSceneHeld >= ArcadeLadderSettleSamples;

            // Against the layout the boards are ACTUALLY holding, not one we remember asking
            // for. A swap that could not run is retried by itself on the next sample, and a game
            // relaunch, which resets that back to Standings, re-aims the boards without being
            // told the game restarted.
            if (!refillNow && want == boards.LadderView) return;

            if (Interlocked.CompareExchange(ref _arcadeLadderBusy, 1, 0) != 0) return;

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

                        // Cleared only once the rebuild has actually HAPPENED. Clearing on dispatch
                        // meant a rebuild dropped by a write already in flight was forgotten, and
                        // the lap they had just driven never reached the boards at all.
                        if (boards.FillAllAsync(CancellationToken.None, force: true)
                                  .GetAwaiter().GetResult())
                            boards.ClearRefillOwed();
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
                finally { Interlocked.Exchange(ref _arcadeLadderBusy, 0); }
            });
        }
    }
}
