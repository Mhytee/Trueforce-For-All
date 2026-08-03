namespace TrueforceForAll.Core
{
    /// <summary>Forza dash-block fields that the force path does not use but a
    /// dashboard does: tyre temperatures, wear, fuel, boost, lap times and
    /// race position.
    ///
    /// These deliberately live outside <see cref="TelemetryFrame"/>. That type
    /// is the contract between a telemetry source and the FFB engine, and
    /// nothing here feeds a force; mixing display data into it would invite
    /// exactly the kind of accidental coupling the effects rely on not having.
    ///
    /// Why it exists at all: a Forza player points the game's Data Out at our
    /// listener, and "Also forward to SimHub" ships OFF, so SimHub's own game
    /// properties can be empty for the whole session. Anything the dash wants
    /// to show a Forza driver therefore has to come from our parse, not from
    /// SimHub's.
    ///
    /// Availability varies by title and is NOT assumed: Motorsport fills tyre
    /// temperatures and lap data, Horizon may leave some of it at zero, and
    /// tyre wear only exists in the longer FM2023 packet (<see cref="HasWear"/>).
    /// Consumers treat a zero as "not reported" and say so rather than drawing
    /// a confident zero.</summary>
    public sealed class ForzaDashExtras
    {
        public float TireTempFL, TireTempFR, TireTempRL, TireTempRR;

        /// <summary>True only for the FM2023-length packet, which is the only
        /// one carrying tyre wear.</summary>
        public bool HasWear;
        public float TireWearFL, TireWearFR, TireWearRL, TireWearRR;

        /// <summary>Brake pedal, 0..1. Throttle and steering already travel on
        /// <see cref="TelemetryFrame"/> because the force path uses them;
        /// brake does not, so it rides here for the dash's inputs box.</summary>
        public float Brake01;

        public float Boost;
        /// <summary>Forza reports fuel as a 0..1 fraction of the tank, not
        /// litres.</summary>
        public float FuelFraction;

        public float BestLapSec, LastLapSec, CurrentLapSec;
        public int LapNumber;
        public int RacePosition;
    }
}
