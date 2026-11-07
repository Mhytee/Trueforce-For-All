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

        /// <summary>The owner's rig-tuned curve, third version (2026-11-07, the
        /// owner's "Clean Bass 2"). Each version replaces the last in place, so
        /// the preset keeps its name and only its sound changes. A steep low cut
        /// at 10.3 Hz with a sharpened corner: nothing below 8 Hz, a bump at 10
        /// to 15 Hz. A gentle 2 dB lift around 153 Hz, a deep narrow notch at
        /// 199 Hz where the low end turns muddy, and the texture above 300 Hz
        /// lifted about 4 dB up to a high cut at 1.5 kHz. Kept band for band
        /// as tuned, including the near-zero 199 Hz bell.</summary>
        public static List<EqBand> CleanBass() => new List<EqBand>
        {
            new EqBand { Type = EqBandType.LowCut,    FrequencyHz = 10.3, Q = 1.28, SlopeDbPerOct = 24 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 153,  GainDb = 2.2,   Q = 1.0 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 199,  GainDb = -29.7, Q = 5.24 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 199,  GainDb = -0.5,  Q = 1.0 },
            new EqBand { Type = EqBandType.HighShelf, FrequencyHz = 304,  GainDb = 4.1,   Q = 0.7071 },
            new EqBand { Type = EqBandType.HighCut,   FrequencyHz = 1525, Q = 0.66, SlopeDbPerOct = 12 },
        };
    }
}
