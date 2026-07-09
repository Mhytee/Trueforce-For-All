using System;
using System.Collections.Generic;

namespace TrueforceForAll.Plugin
{
    // Maps the lowercase effect_tags (produced by PresetManagerControl.BuildEffectTags
    // and stored on community uploads) to the friendly labels shown everywhere else
    // (the section picker, the preview window). Keeps the community browser's tag
    // surfaces reading "Engine pulse, Rev limiter, ABS click" instead of the raw
    // machine strings "engine, revlimiter, abs".
    internal static class EffectTagLabels
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "engine",       "Engine pulse" },
            { "revlimiter",   "Rev limiter" },
            { "roadbumps",    "Road bumps" },
            { "tractionloss", "Traction loss" },
            { "axleslip",     "Axle slip" },
            { "kerbthump",    "Kerb thump" },
            { "lockupjudder", "Lockup judder" },
            { "gearshift",    "Gear shift" },
            { "abs",          "ABS click" },
            { "pitlimiter",   "Pit limiter" },
            { "drs",          "DRS" },
            { "collision",    "Collision" },
            { "audio",        "Audio rumble" },
            { "airborne",     "Airborne ducking" },
        };

        // Unknown tag -> pass the raw string through so a newer server/plugin tag
        // still renders something rather than silently vanishing.
        public static string Label(string tag)
            => string.IsNullOrEmpty(tag) ? null
             : (Map.TryGetValue(tag, out var l) ? l : tag);

        public static string JoinLabels(IEnumerable<string> tags)
        {
            if (tags == null) return "";
            var parts = new List<string>();
            foreach (var t in tags)
            {
                var l = Label(t);
                if (!string.IsNullOrEmpty(l)) parts.Add(l);
            }
            return string.Join(", ", parts);
        }
    }
}
