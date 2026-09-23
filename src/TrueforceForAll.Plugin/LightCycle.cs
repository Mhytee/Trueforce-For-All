// The running order behind the wheel's "next light pattern" button, and the
// question of where in it we currently stand.
//
// Extracted from TrueforcePlugin because this logic produced four separate
// user-visible bugs in one week (each pattern appearing twice, Auto never being
// reachable, the cycle never stepping past the first sweep, and cycling in a car
// toggling between two stops). Every one was found on the rim by the owner
// rather than at build time, because it lived as a private method inside an
// 18,000-line SimHub-coupled file where nothing could reach it.
//
// It is pure by construction: the caller passes in what it knows about the wheel
// and gets back a list and an index. Deciding is here, DOING (writing a slot,
// releasing a borrow, applying a pattern) stays with the plugin.
//
// Self-contained (BCL + the library types) so it link-compiles into the tests.

using System;
using System.Collections.Generic;

namespace TrueforceForAll.Plugin
{
    /// <summary>One stop in the running order.</summary>
    public sealed class LightCycleStop
    {
        /// <summary>The wheel's own effect number, 1..9.</summary>
        public int Effect;
        /// <summary>Our pattern shown in the lent slot, or null for a stop that
        /// genuinely lives on the wheel.</summary>
        public LightPattern Pattern;
        /// <summary>"Let this car's own data decide."</summary>
        public bool Auto;
        public string Label;
    }

    /// <summary>What is physically lit on the wheel right now.
    ///
    /// Three separate flags used to answer this between them, and nothing owned
    /// the answer. That is exactly how a car with no published data came to keep
    /// the PREVIOUS car's colors: the auto path set one flag and left another
    /// naming a pattern it had already painted over, so the restore looked at the
    /// stale one, decided the pattern was already showing, and did nothing.</summary>
    public enum LightShowing
    {
        /// <summary>One of the wheel's own effects or stored slots.</summary>
        WheelEffect,
        /// <summary>One of our library patterns, in a slot we borrowed.</summary>
        LibraryPattern,
        /// <summary>The active car's own published colors.</summary>
        CarAutoColors,
    }

    public static class LightCycle
    {
        /// <summary>Identity of a stop that survives the list being rebuilt. The
        /// running order changes as slots fill and cars come and go, so a bare
        /// index is not a position we can remember between presses.</summary>
        public static string KeyFor(LightCycleStop s)
            => s == null ? null
             : s.Auto ? "auto"
             : s.Pattern != null ? "p:" + s.Pattern.Id
             : "e:" + s.Effect;

        /// <summary>Which of the three states the caller's flags describe.
        ///
        /// Auto wins over a library pattern deliberately: while the car's own
        /// colors are up, a stage slot may well be lent out too, so both flags
        /// can read true at once and only one of them is what the user sees. A
        /// pattern the user PINNED to this car is not Auto, which is what
        /// carChoicePinned rules out.</summary>
        public static LightShowing Showing(bool autoColorsApplied, bool carChoicePinned,
                                           bool libraryPatternShowing)
        {
            if (autoColorsApplied && !carChoicePinned) return LightShowing.CarAutoColors;
            if (libraryPatternShowing) return LightShowing.LibraryPattern;
            return LightShowing.WheelEffect;
        }

        /// <summary>The full running order, wheel-first: Auto, effects 1-4, then
        /// the five slots in order, with the lent slot expanded into every pattern
        /// in the library at its own position.</summary>
        /// <param name="autoAvailable">Whether the active car has published colors.</param>
        /// <param name="stage">The slot we borrow to show a library pattern, or
        /// negative when there is none to borrow.</param>
        /// <param name="programmed">Per slot: does it actually hold something.</param>
        /// <param name="effectLabel">The wheel's own name for an effect number.</param>
        public static List<LightCycleStop> Build(
            bool autoAvailable, int stage, bool[] programmed,
            IList<LightPattern> patterns, int customSlotCount,
            Func<int, string> effectLabel)
        {
            var stops = new List<LightCycleStop>();
            Func<int, string> label = effectLabel ?? (e => "Pattern " + e);

            // Auto first, where the pickers put it. Without a stop of its own a
            // user who cycles away from the car's own colors can never get back
            // to them from the rim: every other stop pins something, and pinning
            // is precisely what turns Auto off.
            if (autoAvailable)
                stops.Add(new LightCycleStop { Auto = true, Label = "Auto (this car's colors)" });

            for (int e = 1; e <= 4; e++)
                stops.Add(new LightCycleStop { Effect = e, Label = label(e) });

            // Then the wheel's own slots, but only the ones that actually hold
            // something. A blank factory slot as a stop is a dark strip and a
            // press that reads as broken.
            for (int slot = 0; slot < customSlotCount; slot++)
            {
                // The stage is left out. Whatever it held is in the library
                // already (imported on first use), so it still appears below
                // under its own name, and leaving it out means the running order
                // does not change the moment we borrow it.
                if (slot == stage) continue;
                if (programmed != null && slot < programmed.Length && programmed[slot])
                    stops.Add(new LightCycleStop { Effect = 5 + slot, Label = label(5 + slot) });
            }

            // Past the last slot the user has filled, the cycle carries straight
            // on into the library. Those patterns are not on the wheel, so each is
            // shown by writing it into the borrow slot. From the rim it is one
            // unbroken list and the user never has to know where the hardware ran
            // out of room.
            if (stage >= 0 && patterns != null)
                foreach (var p in patterns)
                {
                    // A pattern that lives in a programmed slot of its own was
                    // already added above, and adding it again here means every
                    // one of the top five is visited TWICE on a cycle: once by
                    // selecting its own slot, where the base shows that slot's
                    // stored name, and once through the stage under its real name.
                    // The stage is the exception, since it was skipped above
                    // precisely so its pattern appears here instead.
                    if (p == null) continue;
                    if (p.Slot >= 0 && p.Slot != stage && p.Slot < customSlotCount
                        && programmed != null && p.Slot < programmed.Length && programmed[p.Slot]) continue;

                    stops.Add(new LightCycleStop { Effect = 5 + stage, Pattern = p, Label = p.Name });
                }

            return stops;
        }

        /// <summary>Where in the running order we currently stand, or -1 if it
        /// cannot be worked out at all.
        ///
        /// The order of the checks is the whole subtlety. Auto is asked FIRST,
        /// because while the car's own colors are showing a stage slot may well
        /// be lent out too, and the library test would otherwise claim the
        /// position and step off from the wrong place.</summary>
        /// <param name="currentPatternId">The library pattern on the wheel, if
        /// any. May legitimately be null even while showing is LibraryPattern,
        /// in which case any pattern stop is a better answer than none.</param>
        /// <param name="knownSelection">What the WHEEL says is selected. In a car
        /// it cannot answer, because the game's FFB owns the pipe.</param>
        /// <param name="lastKey">Where WE last landed. The fallback for exactly
        /// that case.</param>
        public static int PositionOf(IList<LightCycleStop> stops, LightShowing showing,
                                     string currentPatternId, int knownSelection, string lastKey)
        {
            if (stops == null || stops.Count == 0) return -1;

            int at = -1;
            if (showing == LightShowing.CarAutoColors)
            {
                at = IndexOf(stops, s => s.Auto);
            }
            else if (showing == LightShowing.LibraryPattern)
            {
                if (!string.IsNullOrEmpty(currentPatternId))
                    at = IndexOf(stops, s => s.Pattern != null && s.Pattern.Id == currentPatternId);
                if (at < 0) at = IndexOf(stops, s => s.Pattern != null);
            }
            else
            {
                // !s.Auto matters. The Auto stop also has a null Pattern, and its
                // Effect is 0, so without this it matches whenever the wheel's
                // known selection reads 0. The cycle then resets to position 0 on
                // every press and can only ever step one place, which showed up
                // as toggling between Auto and the first sweep.
                at = IndexOf(stops, s => s.Pattern == null && !s.Auto && s.Effect == knownSelection);
            }

            // Everything above asks the WHEEL where we are, and in a car it
            // cannot answer: a pick is staged rather than written and the known
            // selection never moves off whatever it last managed to read. The
            // search then failed, at fell back to an end, and the next press
            // wrapped to index 0. That is why cycling in a car toggled between one
            // stop and Auto instead of stepping.
            //
            // So fall back to where WE last landed. The wheel is still asked
            // first, because the user may have moved it from the base's own menu.
            if (at < 0 && lastKey != null)
                at = IndexOf(stops, s => KeyFor(s) == lastKey);

            return at;
        }

        /// <summary>The stop one place along, wrapping at both ends. Cycling wraps
        /// because it is driven from a wheel button, where hitting an invisible
        /// end feels broken.</summary>
        /// <param name="at">Where we stand, or negative when that is unknown: a
        /// first press with nothing known still has to land somewhere sensible, so
        /// it starts from the end the direction is coming from.</param>
        public static LightCycleStop Step(IList<LightCycleStop> stops, int at, int direction)
        {
            if (stops == null || stops.Count == 0) return null;
            int step = direction < 0 ? -1 : 1;
            if (at < 0) at = direction < 0 ? 0 : stops.Count - 1;

            int n = stops.Count;
            return stops[((at + step) % n + n) % n];
        }

        private static int IndexOf(IList<LightCycleStop> stops, Func<LightCycleStop, bool> match)
        {
            for (int i = 0; i < stops.Count; i++)
                if (stops[i] != null && match(stops[i])) return i;
            return -1;
        }
    }
}
