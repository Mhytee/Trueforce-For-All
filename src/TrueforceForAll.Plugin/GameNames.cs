using System;

namespace TrueforceForAll.Plugin
{
    /// <summary>SimHub's game code to the name a person would call it.
    ///
    /// One map, because there were two and both were incomplete in different
    /// places: the general one knew Assetto Corsa and the Forzas but not
    /// RaceRoom, and the Mode B one knew RaceRoom but was only consulted on that
    /// one tab. A player who saw "RRRE" somewhere and "RaceRoom" elsewhere was
    /// looking at that split.
    ///
    /// Codes are SimHub's and are NEVER changed by this: they are dictionary
    /// keys, preset bindings and folder names, and renaming one orphans
    /// everything filed under it. This is the display layer and nothing else.
    ///
    /// A code with no entry returns unchanged, which is right far more often
    /// than it is wrong. SimHub already reports most titles under a readable
    /// name, so the entries here are the exceptions rather than a catalogue to
    /// keep complete.</summary>
    internal static class GameNames
    {
        public static string Display(string game)
        {
            switch (game)
            {
                case "FM8":  return "Forza Motorsport";
                case "FH6":  return "Forza Horizon 6";
                case "FH5":  return "Forza Horizon 5";
                case "FH4":  return "Forza Horizon 4";
                case "AssettoCorsa": return "Assetto Corsa";
                case "AssettoCorsaCompetizione": return "Assetto Corsa Competizione";
                case "IRacing": return "iRacing";
                case "Wreckfest2": return "Wreckfest 2";
                case "RaceRoomRacingExperience":
                case "RRRE64":
                case "RRRE": return "RaceRoom";
                case "FarmingSimulator22": return "Farming Simulator 22";
                case "FarmingSimulator25": return "Farming Simulator 25";
                default: return game;
            }
        }

        /// <summary>The same, with a stand-in for "no game" rather than an empty
        /// string. For sentences and for grouping headers, where a blank reads as
        /// a bug and "Other" reads as a category.</summary>
        public static string DisplayOrOther(string game)
            => string.IsNullOrEmpty(game) ? "Other" : Display(game);

        /// <summary>The same, for a sentence that names the game inline.</summary>
        public static string DisplayOrThisGame(string game)
            => string.IsNullOrEmpty(game) ? "this game" : Display(game);
    }
}
