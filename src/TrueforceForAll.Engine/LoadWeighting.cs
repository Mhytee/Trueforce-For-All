// Load-weighted whole-car average of a per-tyre channel: each wheel's value
// weighted by the vertical load it carries. Used by the direct telemetry
// sources for the traction-loss slip scalar (issue #30) and Forza's
// surface-rumble scalar (issue #35).
//
// The direct sources used to collapse a channel's four per-wheel values to
// their MAX before handing the effect a scalar. That let a single unloaded
// wheel own the reading: a driven wheel spun up in mid-air on a jump, or one
// corner lifted over a crest / kerb, reads high even though the car has lost no
// grip and touched no new surface, and the effect fired as if the whole car
// were sliding / on that surface.
//
// Weighting each wheel by the load it is actually carrying fixes that. An
// unloaded wheel drops out of the reading (airborne and one-wheel lifts go
// silent), a single affected wheel contributes only its load share, and a
// genuine all-four event still reads full strength. This deliberately changes
// what the scalar means, so it lives behind one named helper the sources call
// rather than being open-coded per channel.
//
// Distinct from the AC source's GuardByLoad (a binary per-wheel load cutoff on
// the TireCombinedSlip QUAD, feeding the CTM rollups): this produces the SCALAR
// a whole-car effect reads, and weights continuously by load rather than
// hard-zeroing below a threshold.

using System;

namespace TrueforceForAll.Core
{
    public static class LoadWeighting
    {
        /// <summary>
        /// sum(|value_i| * load_i) / sum(load_i) across the four tyres. Loads are
        /// only ever used as a ratio to their own sum, so the units cancel: AC's
        /// wheelLoad[] (newtons) and Forza's normalized suspension travel (0..1)
        /// both work unchanged, and the caller need not scale them.
        /// </summary>
        /// <param name="minTotalLoad">Summed load below which the weighting is
        /// undefined (every wheel unloaded, or the load channel not populated on
        /// this build) and <paramref name="fallback"/> is returned instead. Pick
        /// a value the four loads only fall under when no tyre is on the ground.</param>
        /// <param name="fallback">Returned when the summed load is below
        /// <paramref name="minTotalLoad"/>. The caller encodes intent here: a
        /// source whose load channel has proven live passes 0 (all wheels
        /// unloaded means airborne means silent); a source that has never seen a
        /// real load passes the legacy unweighted value (the channel is dead on
        /// this build, so don't go silent).</param>
        public static double Weighted(
            double valFL, double valFR, double valRL, double valRR,
            double loadFL, double loadFR, double loadRL, double loadRR,
            double minTotalLoad, double fallback)
        {
            loadFL = NonNegative(loadFL);
            loadFR = NonNegative(loadFR);
            loadRL = NonNegative(loadRL);
            loadRR = NonNegative(loadRR);

            double sum = loadFL + loadFR + loadRL + loadRR;
            // Negated compare so a NaN sum (shouldn't reach here, loads are
            // clamped) also takes the fallback rather than propagating.
            if (!(sum > minTotalLoad)) return fallback;

            double weighted =
                Math.Abs(valFL) * loadFL +
                Math.Abs(valFR) * loadFR +
                Math.Abs(valRL) * loadRL +
                Math.Abs(valRR) * loadRR;
            return weighted / sum;
        }

        // A tyre can only push down on its contact patch; a negative load is a
        // bad sample and must not subtract a real wheel's contribution. NaN
        // clamps to 0 too (NaN > 0 is false).
        private static double NonNegative(double v) => v > 0.0 ? v : 0.0;
    }
}
