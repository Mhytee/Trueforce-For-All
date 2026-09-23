namespace TrueforceForAll.Core
{
    /// <summary>Pure rev-limiter engage-point math, in Core so it's
    /// unit-testable.</summary>
    public static class RevLimiterMath
    {
        /// <summary>A crowd-sourced community redline can sit a hair above the
        /// reported hard ceiling, so allow up to 1.05x.</summary>
        public const double CommunityRedlineCeilingFactor = 1.05;

        /// <summary>No real combustion redline sits below half the rev ceiling
        /// (EVs, which could, never reach the community path). Below this it's
        /// bad data.</summary>
        public const double CommunityRedlineFloorFactor = 0.5;

        /// <summary>Whether a crowd-sourced community redline is trustworthy
        /// enough to drive the engage point. It is the SOLE driver on the
        /// Forza-style case (game reports no telemetry redline), so an
        /// implausible value (typo, wrong-car mapping, modded outlier) must be
        /// rejected and the caller falls through to its default rather than
        /// buzzing at a wrong RPM. Plausible = within 0.5x..1.05x of MaxRpm.
        /// When MaxRpm is unknown (<= minEngineRpm) there's no ceiling to band
        /// against, so the value is accepted.</summary>
        public static bool IsCommunityRedlinePlausible(double redlineRpm, double maxRpm, double minEngineRpm)
        {
            if (redlineRpm <= minEngineRpm) return false;
            if (maxRpm <= minEngineRpm) return true;
            return redlineRpm <= maxRpm * CommunityRedlineCeilingFactor
                && redlineRpm >= maxRpm * CommunityRedlineFloorFactor;
        }
    }
}
