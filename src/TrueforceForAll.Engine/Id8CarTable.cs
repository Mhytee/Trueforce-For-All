// Per-car engine facts for Initial D Arcade Stage 8, recovered by static analysis of the game
// executable and verified against its raw bytes. See memscan/engine-table/ENGINE-TABLE.md.
//
// The redline is NOT a number the game stores anywhere. It is a property of the tachometer FACE,
// which the game picks from a nibble in the car's flag word; bit 20 swaps to the tuned face. A face
// of "80-73" means a dial reading to 8000 with the red starting at 7300. So a car's redline can only
// come from a table like this one. Searching memory for it can never work.
//
// Cylinders are inferred from the engine sound family the exe assigns each car. The families are
// cylinder homogeneous, so this is a sound proxy rather than a stored fact. The five rotaries carry
// a cylinder count of 0 and IsRotary instead, since the idea does not apply to a Wankel.
//
// Generated from engine_table_verified.csv. Do not hand edit: regenerate from the CSV.

namespace TrueforceForAll.Core
{
    /// <summary>What the exe knows about one car: its two tachometer faces and its engine.</summary>
    public sealed class Id8Car
    {
        public readonly int CarId;
        public readonly string Code;
        public readonly string Name;
        public readonly int StockDialMax;
        public readonly int StockRedline;
        public readonly int TunedDialMax;
        public readonly int TunedRedline;
        /// <summary>Cylinder count, or 0 for the rotaries where the idea does not apply.</summary>
        public readonly int Cylinders;

        /// <summary>True for the five 13B twin-rotor cars.</summary>
        public readonly bool IsRotary;

        public readonly int GearCount;

        public Id8Car(int carId, string code, string name, int stockDialMax, int stockRedline,
                      int tunedDialMax, int tunedRedline, int cylinders, bool isRotary, int gearCount)
        {
            CarId = carId; Code = code; Name = name;
            StockDialMax = stockDialMax; StockRedline = stockRedline;
            TunedDialMax = tunedDialMax; TunedRedline = tunedRedline;
            Cylinders = cylinders; IsRotary = isRotary; GearCount = gearCount;
        }

        /// <summary>What to feed an effect that pulses once per firing event. A 13B twin-rotor
        /// fires twice per crankshaft revolution, the same rate as a four-cylinder four-stroke,
        /// so four is the right number to pulse at even though it has no cylinders.</summary>
        public int PulseCylinderEquivalent { get { return IsRotary ? 4 : Cylinders; } }

        /// <summary>Dial ceiling for whichever face is selected.</summary>
        public int DialMax(bool tuned) { return tuned ? TunedDialMax : StockDialMax; }

        /// <summary>Where the red starts on whichever face is selected.</summary>
        public int Redline(bool tuned) { return tuned ? TunedRedline : StockRedline; }
    }

    /// <summary>The cars, keyed by the CarID the game keeps in the car object.</summary>
    public static class Id8CarTable
    {
        /// <summary>Bit 20 of the car flag word selects the tuned tachometer face.</summary>
        public const uint TunedTachoFaceBit = 1u << 20;

        private static readonly System.Collections.Generic.Dictionary<int, Id8Car> ById =
            new System.Collections.Generic.Dictionary<int, Id8Car>
        {
            { 0, new Id8Car(0, "AE86T", "TRUENO GT-APEX (AE86)", 8000, 7300, 13000, 11000, 4, false, 5) },
            { 1, new Id8Car(1, "AE86L", "LEVIN GT-APEX (AE86)", 8000, 7300, 13000, 11000, 4, false, 5) },
            { 2, new Id8Car(2, "AE85L", "LEVIN SR (AE85)", 8000, 6000, 13000, 11000, 4, false, 5) },
            { 3, new Id8Car(3, "SW20", "MR2 G-Limited (SW20)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 4, new Id8Car(4, "SXE10", "ALTEZZA RS200 (SXE10)", 9000, 7500, 9000, 7500, 4, false, 6) },
            { 5, new Id8Car(5, "ZZW30", "MR-S (ZZW30)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 6, new Id8Car(6, "JZA80", "SUPRA RZ (JZA80)", 9000, 7000, 9000, 7000, 6, false, 6) },
            { 7, new Id8Car(7, "ZN6", "86 GT (ZN6)", 9000, 7500, 9000, 7500, 4, false, 6) },
            { 8, new Id8Car(8, "ZVW30", "PRIUS (ZVW30)", 8000, 7300, 13000, 11000, 4, false, 5) },
            { 9, new Id8Car(9, "AE86T2", "TRUENO 2door GT-APEX (AE86)", 8000, 7300, 8000, 7300, 4, false, 5) },
            { 10, new Id8Car(10, "ST205", "CELICA GT-FOUR (ST205)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 256, new Id8Car(256, "BNR32", "SKYLINE GT-R (BNR32)", 9000, 7500, 9000, 7500, 6, false, 5) },
            { 257, new Id8Car(257, "BNR34", "SKYLINE GT-R (BNR34)", 10000, 8000, 10000, 8000, 6, false, 6) },
            { 258, new Id8Car(258, "S13K", "SILVIA K$u[0027]s (S13)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 259, new Id8Car(259, "S14Q", "Silvia Q$u[0027]s (S14)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 260, new Id8Car(260, "S15", "Silvia spec-R (S15)", 9000, 7000, 9000, 7000, 4, false, 6) },
            { 261, new Id8Car(261, "RPS13", "180SX TYPE $u[2161] (RPS13)", 9000, 7500, 9000, 7500, 4, false, 5) },
            { 262, new Id8Car(262, "Z33", "FAIRLADY Z (Z33)", 9000, 7000, 9000, 7000, 6, false, 6) },
            { 263, new Id8Car(263, "R35", "GT-R NISMO (R35)", 9000, 7000, 9000, 7000, 6, false, 6) },
            { 264, new Id8Car(264, "ER34", "SKYLINE 25GT TURBO (ER34)", 9000, 7000, 9000, 7000, 6, false, 5) },
            { 512, new Id8Car(512, "EG6", "Civic SiR$u[00B7]$u[2161] (EG6)", 10000, 8000, 10000, 8000, 4, false, 5) },
            { 513, new Id8Car(513, "EK9", "CIVIC TYPE R (EK9)", 10000, 8500, 10000, 8500, 4, false, 5) },
            { 514, new Id8Car(514, "DC2", "INTEGRA TYPE R (DC2)", 10000, 8500, 10000, 8500, 4, false, 5) },
            { 515, new Id8Car(515, "AP1", "S2000 (AP1)", 10000, 9000, 10000, 9000, 4, false, 6) },
            { 516, new Id8Car(516, "NA1", "NSX (NA1)", 10000, 8000, 10000, 8000, 6, false, 5) },
            { 768, new Id8Car(768, "FC3S", "RX-7 ∞$u[2162] (FC3S)", 9000, 7000, 9000, 7000, 0, true, 5) },
            { 769, new Id8Car(769, "FD3S", "RX-7 Type R (FD3S)", 9000, 7500, 9000, 7500, 0, true, 5) },
            { 770, new Id8Car(770, "SE3P", "RX-8 Type S (SE3P)", 10000, 9000, 10000, 9000, 0, true, 6) },
            { 771, new Id8Car(771, "NA6CE", "ROADSTER (NA6CE)", 9000, 7500, 9000, 7500, 4, false, 5) },
            { 772, new Id8Car(772, "NB8C", "ROADSTER RS (NB8C)", 9000, 7500, 9000, 7500, 4, false, 6) },
            { 773, new Id8Car(773, "FD3S6", "RX-7 Type RS (FD3S)", 9000, 7500, 9000, 7500, 0, true, 5) },
            { 1024, new Id8Car(1024, "GC8S5", "IMPREZA STi Ver.$u[2164] (GC8)", 9000, 7500, 9000, 7500, 4, false, 5) },
            { 1025, new Id8Car(1025, "GDBF", "IMPREZA STI (GDBF)", 10000, 8000, 10000, 8000, 4, false, 6) },
            { 1026, new Id8Car(1026, "GDBA", "IMPREZA STi (GDBA)", 10000, 8000, 10000, 8000, 4, false, 6) },
            { 1027, new Id8Car(1027, "ZC6", "BRZ S  (ZC6)", 9000, 7500, 9000, 7500, 4, false, 6) },
            { 1280, new Id8Car(1280, "CE9A", "LANCER Evolution $u[2162] (CE9A)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 1281, new Id8Car(1281, "CN9A", "LANCER EVOLUTION $u[2163] (CN9A)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 1282, new Id8Car(1282, "CT9A9", "LANCER Evolution $u[2168] (CT9A)", 9000, 7000, 9000, 7000, 4, false, 6) },
            { 1283, new Id8Car(1283, "CT9A7", "LANCER EVOLUTION $u[2166] (CT9A)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 1284, new Id8Car(1284, "CZ4A", "LANCER EVOLUTION $u[2169] (CZ4A)", 9000, 7000, 9000, 7000, 4, false, 6) },
            { 1285, new Id8Car(1285, "CP9A5", "LANCER EVOLUTION $u[2164] (CP9A)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 1286, new Id8Car(1286, "CP9A6T", "LANCER EVOLUTION $u[2165] (CP9A)", 9000, 7000, 9000, 7000, 4, false, 5) },
            { 1536, new Id8Car(1536, "EA11R", "Cappuccino (EA11R)", 10000, 8500, 10000, 8500, 3, false, 5) },
            { 1792, new Id8Car(1792, "RPS13K", "SILEIGHTY", 9000, 7500, 9000, 7500, 4, false, 5) },
            { 2048, new Id8Car(2048, "FD3SC", "幻気-7 (FD3S)", 9000, 7500, 9000, 7500, 0, true, 5) },
            { 2049, new Id8Car(2049, "EK9C", "MONSTER CIVIC R (EK9)", 10000, 8500, 10000, 8500, 4, false, 5) },
            { 2050, new Id8Car(2050, "AP1C", "S2000 GT1 (AP1)", 10000, 8000, 10000, 8000, 4, false, 6) },
            { 2051, new Id8Car(2051, "JZA80C", "G-FORCE SUPRA (JZA80改)", 9000, 7000, 9000, 7000, 6, false, 6) },
            { 2052, new Id8Car(2052, "NA8CC", "ROADSTER C-SPEC (NA8C改)", 9000, 7500, 9000, 7500, 4, false, 5) },
            { 2053, new Id8Car(2053, "NA2C", "NSX-R GT (NA2)", 10000, 8000, 10000, 8000, 6, false, 6) },
        };

        public static int Count { get { return ById.Count; } }

        /// <summary>The car for this CarID, or null when the id is not one we mapped.</summary>
        public static Id8Car Find(int carId)
        {
            Id8Car c;
            return ById.TryGetValue(carId, out c) ? c : null;
        }
    }
}
