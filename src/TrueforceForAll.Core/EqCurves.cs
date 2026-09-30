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

        /// <summary>The owner's rig-tuned curve (2026-09-30): the bass lifted
        /// about 12 dB from 15 to 40 Hz, a deep notch at 200 Hz where the low
        /// end turns muddy, and the texture above 350 Hz lifted about 11 dB
        /// up to a high cut at 1.5 kHz. Kept band for band as tuned, sorted by
        /// frequency so the points number left to right (order does not
        /// change the sound: the sections are linear and run in series).</summary>
        public static List<EqBand> CleanBass() => new List<EqBand>
        {
            new EqBand { Type = EqBandType.LowShelf,  FrequencyHz = 9.9,  GainDb = 4.8,   Q = 0.1 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 9.9,  GainDb = -30.0, Q = 2.21 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 11.2, GainDb = 9.3,   Q = 0.3 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 11.6, GainDb = 12.0,  Q = 0.78 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 47.6, GainDb = 3.3,   Q = 1.0 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 138,  GainDb = 4.8,   Q = 1.0 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 199,  GainDb = -29.9, Q = 3.71 },
            new EqBand { Type = EqBandType.Peak,      FrequencyHz = 232,  GainDb = 7.8,   Q = 1.0 },
            new EqBand { Type = EqBandType.HighShelf, FrequencyHz = 315,  GainDb = 11.3,  Q = 0.7071 },
            new EqBand { Type = EqBandType.HighCut,   FrequencyHz = 1525, Q = 0.66, SlopeDbPerOct = 12 },
        };
    }
}
