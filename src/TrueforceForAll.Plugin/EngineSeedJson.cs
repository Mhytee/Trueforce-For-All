// Shared reader for the shipped per-game engine seeds.
//
// Two games ship one: iRacing (IRacingEngineSeed, keyed by the sim's CarPath)
// and RaceRoom (R3EEngineSeed, keyed by the sim's ModelId). The file format is
// the same for both, and so is the translation out of it, so only the resource
// name and the key shape differ. The tables are authored in EngineLayout terms
// because that is how a human thinks about an engine; the resolver speaks the
// legacy (cylinders, EngineConfig) pair, so the translation lives here rather
// than being baked into the data.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    /// <summary>One seeded car, in the shape CarCylinderResolver carries.</summary>
    internal sealed class EngineSeedEntry
    {
        public int Cylinders;
        public EngineConfig Config;
        public bool IsElectric;
        public string DisplayName;
    }

    internal static class EngineSeedJson
    {
        /// <summary>Parse an embedded seed into a key to entry map. Never
        /// throws: a missing or malformed seed just means every car in that
        /// game falls back to even-fire, which is what happened before the
        /// file existed.</summary>
        public static Dictionary<string, EngineSeedEntry> Load(string resourceName, string label)
        {
            var map = new Dictionary<string, EngineSeedEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null)
                    {
                        SimHub.Logging.Current.Warn(
                            "[TF4ALL] " + label + " engine seed resource missing; "
                            + "firing patterns fall back to even-fire.");
                        return map;
                    }
                    using (var rd = new StreamReader(s))
                    {
                        var root = JObject.Parse(rd.ReadToEnd());
                        var cars = root["cars"] as JObject;
                        if (cars == null) return map;
                        foreach (var kv in cars)
                        {
                            var o = kv.Value as JObject;
                            if (o == null) continue;
                            string layout = (string)o["layout"];
                            if (string.IsNullOrEmpty(layout)) continue;
                            var e = FromLayoutName(layout);
                            if (e == null) continue;
                            e.DisplayName = (string)o["name"];
                            map[kv.Key] = e;
                        }
                    }
                }
                SimHub.Logging.Current.Info(
                    "[TF4ALL] " + label + " engine seed loaded: "
                    + map.Count.ToString(CultureInfo.InvariantCulture) + " cars.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "[TF4ALL] " + label + " engine seed failed to load: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            return map;
        }

        /// <summary>Turn an EngineLayout NAME into the (cylinders, EngineConfig)
        /// pair the resolver carries, chosen so FiringPatternDb.LayoutFromLegacy
        /// maps it straight back to the same layout.</summary>
        public static EngineSeedEntry FromLayoutName(string name)
        {
            EngineLayout l;
            if (!Enum.TryParse(name, true, out l)) return null;
            switch (l)
            {
                case EngineLayout.Electric:  return new EngineSeedEntry { Cylinders = 0,  Config = EngineConfig.Auto, IsElectric = true };
                case EngineLayout.Single:    return new EngineSeedEntry { Cylinders = 1,  Config = EngineConfig.Single };
                case EngineLayout.Twin:      return new EngineSeedEntry { Cylinders = 2,  Config = EngineConfig.Inline };
                case EngineLayout.Inline3:   return new EngineSeedEntry { Cylinders = 3,  Config = EngineConfig.Inline };
                case EngineLayout.Inline4:   return new EngineSeedEntry { Cylinders = 4,  Config = EngineConfig.Inline };
                case EngineLayout.Inline5:   return new EngineSeedEntry { Cylinders = 5,  Config = EngineConfig.Inline };
                case EngineLayout.Inline6:   return new EngineSeedEntry { Cylinders = 6,  Config = EngineConfig.Inline };
                case EngineLayout.Boxer4:    return new EngineSeedEntry { Cylinders = 4,  Config = EngineConfig.Boxer };
                case EngineLayout.Boxer6:    return new EngineSeedEntry { Cylinders = 6,  Config = EngineConfig.Boxer };
                case EngineLayout.V6_60Even: return new EngineSeedEntry { Cylinders = 6,  Config = EngineConfig.V60 };
                case EngineLayout.V6_OddFire:return new EngineSeedEntry { Cylinders = 6,  Config = EngineConfig.V6OddFire };
                case EngineLayout.V8CrossPlane: return new EngineSeedEntry { Cylinders = 8, Config = EngineConfig.V8CrossPlane };
                case EngineLayout.V8FlatPlane:  return new EngineSeedEntry { Cylinders = 8, Config = EngineConfig.V8FlatPlane };
                case EngineLayout.V10_72:    return new EngineSeedEntry { Cylinders = 10, Config = EngineConfig.V90Even };
                case EngineLayout.V12_60:    return new EngineSeedEntry { Cylinders = 12, Config = EngineConfig.V60 };
                case EngineLayout.W12_W16:   return new EngineSeedEntry { Cylinders = 12, Config = EngineConfig.V90Even };
                case EngineLayout.VTwin90:   return new EngineSeedEntry { Cylinders = 2,  Config = EngineConfig.VTwin90 };
                case EngineLayout.VTwin45:   return new EngineSeedEntry { Cylinders = 2,  Config = EngineConfig.VTwin45 };
                case EngineLayout.VTwin60:   return new EngineSeedEntry { Cylinders = 2,  Config = EngineConfig.VTwin90 };
                // Rotary is carried as firing-equivalent cylinders (rotors x 2),
                // which is the legacy convention LayoutFromLegacy expects.
                case EngineLayout.Rotary1:   return new EngineSeedEntry { Cylinders = 2,  Config = EngineConfig.Rotary };
                case EngineLayout.Rotary2:   return new EngineSeedEntry { Cylinders = 4,  Config = EngineConfig.Rotary };
                case EngineLayout.Rotary3:   return new EngineSeedEntry { Cylinders = 6,  Config = EngineConfig.Rotary };
                case EngineLayout.Rotary4:   return new EngineSeedEntry { Cylinders = 8,  Config = EngineConfig.Rotary };
                // No legacy pair produces these, so they cannot round-trip.
                // Nearest even-fire equivalent rather than dropping the car.
                case EngineLayout.V4:               return new EngineSeedEntry { Cylinders = 4, Config = EngineConfig.Inline };
                case EngineLayout.Inline4CrossPlane:return new EngineSeedEntry { Cylinders = 4, Config = EngineConfig.Inline };
                default: return null;
            }
        }
    }
}
