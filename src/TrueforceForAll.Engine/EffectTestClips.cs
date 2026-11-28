// The recorded telemetry clips behind the settings UI's Test buttons, one per
// effect, embedded in this assembly (the plugin deploys as three DLLs and a
// loose data folder would be a fourth thing to go stale).
//
// A clip is an ordinary '#tfcap' capture, cut down to a few seconds and to
// the columns its effect reads. The file is named for the effect's class:
// TestClips/TractionLossEffect.csv is TractionLossEffect's clip. An effect
// with no file here has no clip, and its Test button falls back to the
// synthetic TestPlay. See TestClips/README.txt for how each was cut.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Core
{
    public static class EffectTestClips
    {
        // Matches the LogicalName the csproj gives each embedded clip.
        private const string ResourcePrefix = "TestClips.";
        private const string ResourceSuffix = ".csv";

        // Parsed once, kept: a clip is a few hundred rows and the same one is
        // replayed every time its button is pressed. A missing or unreadable
        // clip caches as null so the lookup is not repeated either.
        private static readonly ConcurrentDictionary<string, CsvFrameReader.Capture> Cache =
            new ConcurrentDictionary<string, CsvFrameReader.Capture>(StringComparer.Ordinal);

        /// <summary>The test clip for this effect, or null when none ships.</summary>
        public static CsvFrameReader.Capture For(TelemetryEffect effect)
            => effect == null ? null : ForEffectType(effect.GetType().Name);

        /// <summary>The test clip for an effect by class name
        /// ("TractionLossEffect"), or null when none ships.</summary>
        public static CsvFrameReader.Capture ForEffectType(string effectTypeName)
        {
            if (string.IsNullOrEmpty(effectTypeName)) return null;
            return Cache.GetOrAdd(effectTypeName, Load);
        }

        /// <summary>Class names of every effect that ships a clip.</summary>
        public static IReadOnlyList<string> EffectTypeNames
        {
            get
            {
                var names = new List<string>();
                foreach (string res in typeof(EffectTestClips).Assembly.GetManifestResourceNames())
                {
                    if (res.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && res.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                        names.Add(res.Substring(ResourcePrefix.Length,
                            res.Length - ResourcePrefix.Length - ResourceSuffix.Length));
                }
                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        /// <summary>The multiplier that maps a clip's rev range onto the live
        /// car's (see EffectClipTest). 1 when either side is unknown, so a test
        /// with no car loaded plays the clip's own revs. Clamped: a tractor and
        /// a superbike both stay within what the clip can be stretched to
        /// without its rev changes turning absurd.</summary>
        public static double RpmScaleFor(CsvFrameReader.Capture clip, double liveMaxRpm)
        {
            if (clip == null || !(liveMaxRpm > 100.0) || double.IsInfinity(liveMaxRpm)) return 1.0;
            double clipMaxRpm = 0;
            foreach (var row in clip.Rows)
                if (row.Frame.MaxRpm > clipMaxRpm) clipMaxRpm = row.Frame.MaxRpm;
            if (!(clipMaxRpm > 100.0)) return 1.0;
            double scale = liveMaxRpm / clipMaxRpm;
            return scale < 0.2 ? 0.2 : (scale > 4.0 ? 4.0 : scale);
        }

        private static CsvFrameReader.Capture Load(string effectTypeName)
        {
            try
            {
                var asm = typeof(EffectTestClips).Assembly;
                using (Stream s = asm.GetManifestResourceStream(ResourcePrefix + effectTypeName + ResourceSuffix))
                {
                    if (s == null) return null;
                    using (var reader = new StreamReader(s))
                    {
                        var clip = CsvFrameReader.Read(reader);
                        return clip.Rows.Count >= 2 ? clip : null;
                    }
                }
            }
            catch
            {
                // A clip that will not parse is a clip that does not exist: the
                // button still works, on the synthetic test.
                return null;
            }
        }
    }
}
