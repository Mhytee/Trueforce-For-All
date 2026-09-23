// Shipped seed of RaceRoom engine layouts, keyed by the sim's own numeric
// VehicleInfo.ModelId.
//
// Why a table at all: RaceRoom publishes no engine configuration whatsoever.
// Its shared memory carries EngineType (combustion / electric / hybrid) and
// nothing else, the shipped GameData/Cars folders hold only camera XML, and
// Sector3's own car data file lists names, classes and liveries with no engine
// fields. So unlike iRacing, where only the CRANK needs a table, here the
// cylinder COUNT has no other source either. Without this file every RaceRoom
// car renders as a generic even-fire six.
//
// RaceRoom is the easy case for a complete table: a closed roster of real,
// documented cars rather than an open mod ecosystem, so every car the sim
// knows about is in here (356 at the time of writing).
//
// Precedence: this is the FLOOR. It is consulted from CarCylinderResolver,
// which sits BELOW the CarFacts branch, so a user pin and a community
// consensus both override it without any extra wiring.

using System;
using System.Collections.Generic;

namespace TrueforceForAll.Plugin
{
    internal static class R3EEngineSeed
    {
        private const string ResourceName = "TrueforceForAll.Plugin.raceroom-engine-layouts.json";

        private static readonly object Gate = new object();
        private static Dictionary<string, EngineSeedEntry> _map;

        /// <summary>Look a car up by the carId SimHub reports for RaceRoom,
        /// which is "&lt;modelId&gt;,&lt;car name&gt;" (its reader builds it
        /// that way from VehicleInfo). A bare model id is accepted too, so a
        /// caller holding the number itself does not have to fake the
        /// name.</summary>
        public static bool TryGet(string carId, out EngineSeedEntry entry)
        {
            entry = null;
            string modelId = ModelIdFrom(carId);
            if (modelId == null) return false;
            var m = Load();
            return m != null && m.TryGetValue(modelId, out entry) && entry != null;
        }

        /// <summary>The model id out of a RaceRoom carId: everything before the
        /// first comma, and only when that part is all digits. Null for any
        /// other shape, so a game whose ids happen to contain commas can never
        /// collide with this table.</summary>
        internal static string ModelIdFrom(string carId)
        {
            if (string.IsNullOrEmpty(carId)) return null;
            string s = carId.Trim();
            int comma = s.IndexOf(',');
            string head = (comma >= 0 ? s.Substring(0, comma) : s).Trim();
            if (head.Length == 0) return null;
            foreach (char c in head)
                if (!char.IsDigit(c)) return null;
            return head;
        }

        private static Dictionary<string, EngineSeedEntry> Load()
        {
            lock (Gate)
            {
                if (_map != null) return _map;
                _map = EngineSeedJson.Load(ResourceName, "RaceRoom");
                return _map;
            }
        }
    }
}
