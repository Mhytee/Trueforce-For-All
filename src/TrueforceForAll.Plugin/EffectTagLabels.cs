using System;
using System.Collections.Generic;
using TrueforceForAll.Plugin.Localization;

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
            { "engine",       "EffectTag_EnginePulse" },
            { "revlimiter",   "EffectTag_RedlineBuzz" },
            { "roadbumps",    "EffectTag_RoadBumps" },
            { "tractionloss", "EffectTag_TractionLoss" },
            { "axleslip",     "EffectTag_AxleSlip" },
            { "kerbthump",    "EffectTag_CurbThump" },
            { "lockupjudder", "EffectTag_LockupJudder" },
            { "gearshift",    "EffectTag_GearShift" },
            { "abs",          "EffectTag_AbsClick" },
            { "pitlimiter",   "EffectTag_PitLimiter" },
            { "drs",          "EffectTag_Drs" },
            { "collision",    "EffectTag_Collision" },
            { "audio",        "EffectTag_AudioRumble" },
            { "airborne",     "EffectTag_AirborneDucking" },
        };

        // Unknown tag -> pass the raw string through so a newer server/plugin tag
        // still renders something rather than silently vanishing.
        // The map holds KEY NAMES, not labels. Resolving here rather than in the
        // initializer means a language change reaches these labels, and it keeps
        // the table out of a static initializer that would run before the
        // language table exists.
        public static string Label(string tag)
            => string.IsNullOrEmpty(tag) ? null
             : (Map.TryGetValue(tag, out var k) ? Loc.T(k) : tag);

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
