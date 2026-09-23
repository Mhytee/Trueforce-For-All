using System;

namespace TrueforceForAll.Core
{
    /// <summary>Pure RPM-banding used to build the per-variant engine
    /// signature. Lives in Core so it's unit-testable; the plugin's signature
    /// builder and variant matcher delegate here.
    ///
    /// Two axes with deliberately different widths:
    ///   - Redline (500): loose, so cross-user submissions for the same engine
    ///     pool into one community bucket.
    ///   - MaxRpm (50): tight, near-exact. MaxRpm is the only discriminator
    ///     Forza exposes (its UDP has no separate redline field) AND it's a
    ///     deterministic engine spec, not a noisy reading: two users on the
    ///     identical build report the identical value, so the band has nothing
    ///     to "ride out" except float / representation noise (a 50 bucket
    ///     tolerates +/-25). It's otherwise kept tight to maximize separation
    ///     of distinct engines, so a small upgrade that nudges the ceiling gets
    ///     its own community bucket instead of being merged with the stock
    ///     build. (Trade-off: a game / SimHub patch that shifts a reported
    ///     MaxRpm by more than ~25 re-keys the bucket; acceptable because
    ///     there's no real same-build variance to absorb.)
    ///
    /// Both floor at <see cref="MinBandableRpm"/>: below that the reading isn't
    /// a usable rev-range value (engine off / partial frame), so the caller
    /// drops the component (returns 0).</summary>
    public static class VariantSignatureMath
    {
        public const int RedlineBandRpm = 500;
        public const int MaxRpmBandRpm = 50;
        public const int MinBandableRpm = 500;

        /// <summary>Band a redline RPM to <see cref="RedlineBandRpm"/>.</summary>
        public static int BandRedline(double rpm) => Band(rpm, RedlineBandRpm);

        /// <summary>Band a max RPM to <see cref="MaxRpmBandRpm"/>.</summary>
        public static int BandMaxRpm(double rpm) => Band(rpm, MaxRpmBandRpm);

        private static int Band(double rpm, int band)
        {
            if (rpm < MinBandableRpm) return 0;
            return (int)Math.Round(rpm / band) * band;
        }
    }
}
