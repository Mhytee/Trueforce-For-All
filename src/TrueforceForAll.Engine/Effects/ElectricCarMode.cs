// Moved from TrueforceSettings.cs in the phase-0b Engine extraction:
// EnginePulseEffect consumes it, so it lives with the effect. Namespace kept
// (TrueforceForAll.Plugin) so the settings classes and the effect resolve it
// unchanged; the rename is a phase 7 cleanup.

namespace TrueforceForAll.Plugin
{
    /// <summary>What EnginePulse should do when the resolver flags the
    /// active car as a pure EV. Combustion cars ignore this entirely.</summary>
    public enum ElectricCarMode
    {
        /// <summary>Play the same firing-frequency hum as a combustion car
        /// but at half amplitude. Real EVs aren't silent, many pump
        /// synthetic engine sound, so a muted hum reads more correctly
        /// than dead silence. Default.</summary>
        MutedHum,

        /// <summary>EnginePulse is fully muted on EVs. For users who want
        /// authentic silence (or just don't like the synthetic-engine
        /// approach). Other effects (RoadBumps, TractionLoss, etc.) still
        /// run normally, only the firing-rate hum is suppressed.</summary>
        Silent,
    }
}
