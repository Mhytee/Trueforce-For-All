// The Trueforce EQ's built-in curves and the shape of a saved one. Band data
// only: the names people see are language keys the plugin resolves, so this
// file stays free of display text and the tests can read the curves.

using System.Collections.Generic;

namespace TrueforceForAll.Core
{
    /// <summary>A named curve the user saved from the EQ editor. Plain
    /// properties so Newtonsoft round-trips it inside TrueforceSettings.</summary>
    public sealed class EqPreset
    {
        public string Name { get; set; } = "";
        public List<EqBand> Bands { get; set; } = new List<EqBand>();
    }

    public static class EqCurves
    {
        /// <summary>No change: the factory layout (end cuts parked, bells at 0 dB).</summary>
        public static List<EqBand> Flat() => ParametricEq.FactoryBands();

        /// <summary>The owner's rig-tuned curve, second version (2026-10-01). It
        /// replaces the first one in place, so the preset keeps its name and only
        /// its sound changes. A steep low cut at 10.3 Hz with a
        /// sharpened corner: nothing below 8 Hz, a bump of about 8 dB at 10 to
        /// 15 Hz. The 100 to 137 Hz bass lifted about 3 dB, a deep notch at
        /// 200 Hz where the low end turns muddy, and the texture above 350 Hz
        /// lifted about 11 dB up to a high cut at 1.5 kHz. Kept band for band
        /// as tuned.</summary>
        public static List<EqBand> CleanBass() => new List<EqBand>
        {
            new EqBand { Type = EqBandType.LowCut,    FrequencyHz = 10.3, Q = 1.28, SlopeDbPerOct = 24 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 137,  GainDb = 6.1,   Q = 1.0 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 199,  GainDb = -29.9, Q = 3.71 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 232,  GainDb = 7.8,   Q = 1.0 },
            new EqBand { Type = EqBandType.HighShelf, FrequencyHz = 315,  GainDb = 11.3,  Q = 0.7071 },
            new EqBand { Type = EqBandType.HighCut,   FrequencyHz = 1525, Q = 0.66, SlopeDbPerOct = 12 },
        };
    }
}
