// Shipped seed of iRacing engine FIRING PATTERNS, keyed by iRacing's own
// CarPath.
//
// Why a table at all, when iRacing publishes engine data itself: it publishes
// DriverCarEngCylinderCount, so the COUNT never needs a table and this file
// deliberately does not exist to supply one. What it never publishes is the
// crank, and cylinder count cannot infer it. Eight cylinders is either a
// cross-plane American V8 or a flat-plane Ferrari, and those are the two most
// recognisably different engine sounds in motorsport. Four is inline or boxer.
// Six is inline, boxer, 60 degree V or odd-fire. Without this, every one of
// those renders as a generic even-fire, and every cross-plane V8 in the
// service (85 of them) sounds flat-plane.
//
// Precedence: this is the FLOOR, not the answer. It is consulted from
// CarCylinderResolver, which sits BELOW the CarFacts branch, so a user pin and
// a community consensus both override it without any extra wiring. That is the
// "shipped seed, community override" model: works offline and on day one for a
// car nobody has driven, and still improves when the community knows better.
//
// Embedded rather than written to PluginsData because it is static reference
// data, not user content: no installer step, no folder to go stale, and no way
// for a half-written file to appear.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    internal static class IRacingEngineSeed
    {
        internal sealed class Entry
        {
            public int Cylinders;
            public EngineConfig Config;
            public bool IsElectric;
            public string DisplayName;
        }

        private const string ResourceName = "TrueforceForAll.Plugin.iracing-engine-layouts.json";

        private static readonly object Gate = new object();
        private static Dictionary<string, Entry> _map;

        /// <summary>Look a car up by iRacing CarPath (the sim's own key, e.g.
        /// "mx5 mx52016"). Case-insensitive; the sim publishes lowercase but a
        /// caller should not have to know that.</summary>
        public static bool TryGet(string carPath, out Entry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(carPath)) return false;
            var m = Load();
            return m != null && m.TryGetValue(carPath.Trim(), out entry) && entry != null;
        }

        private static Dictionary<string, Entry> Load()
        {
            lock (Gate)
            {
                if (_map != null) return _map;
                _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(ResourceName))
                    {
                        if (s == null)
                        {
                            SimHub.Logging.Current.Warn(
                                "[TF4ALL] iRacing engine seed resource missing; firing patterns fall back to even-fire.");
                            return _map;
                        }
                        using (var rd = new StreamReader(s))
                        {
                            var root = JObject.Parse(rd.ReadToEnd());
                            var cars = root["cars"] as JObject;
                            if (cars == null) return _map;
                            foreach (var kv in cars)
                            {
                                var o = kv.Value as JObject;
                                if (o == null) continue;
                                string layout = (string)o["layout"];
                                if (string.IsNullOrEmpty(layout)) continue;
                                var e = FromLayoutName(layout);
                                if (e == null) continue;
                                e.DisplayName = (string)o["name"];
                                _map[kv.Key] = e;
                            }
                        }
                    }
                    SimHub.Logging.Current.Info(
                        "[TF4ALL] iRacing engine seed loaded: "
                        + _map.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " cars.");
                }
                catch (Exception ex)
                {
                    // Never fatal. A missing or malformed seed just means every
                    // car falls back to even-fire, which is what happens today.
                    SimHub.Logging.Current.Warn(
                        "[TF4ALL] iRacing engine seed failed to load: " + ex.GetType().Name + ": " + ex.Message);
                }
                return _map;
            }
        }

        /// <summary>Turn an EngineLayout NAME into the (cylinders, EngineConfig)
        /// pair the resolver carries, chosen so FiringPatternDb.LayoutFromLegacy
        /// maps it straight back to the same layout. The table is authored in
        /// layout terms because that is how a human thinks about an engine; the
        /// resolver speaks the legacy pair, so the translation lives here rather
        /// than being baked into the data.</summary>
        private static Entry FromLayoutName(string name)
        {
            EngineLayout l;
            if (!Enum.TryParse(name, true, out l)) return null;
            switch (l)
            {
                case EngineLayout.Electric:  return new Entry { Cylinders = 0,  Config = EngineConfig.Auto,         IsElectric = true };
                case EngineLayout.Single:    return new Entry { Cylinders = 1,  Config = EngineConfig.Single };
                case EngineLayout.Twin:      return new Entry { Cylinders = 2,  Config = EngineConfig.Inline };
                case EngineLayout.Inline3:   return new Entry { Cylinders = 3,  Config = EngineConfig.Inline };
                case EngineLayout.Inline4:   return new Entry { Cylinders = 4,  Config = EngineConfig.Inline };
                case EngineLayout.Inline5:   return new Entry { Cylinders = 5,  Config = EngineConfig.Inline };
                case EngineLayout.Inline6:   return new Entry { Cylinders = 6,  Config = EngineConfig.Inline };
                case EngineLayout.Boxer4:    return new Entry { Cylinders = 4,  Config = EngineConfig.Boxer };
                case EngineLayout.Boxer6:    return new Entry { Cylinders = 6,  Config = EngineConfig.Boxer };
                case EngineLayout.V6_60Even: return new Entry { Cylinders = 6,  Config = EngineConfig.V60 };
                case EngineLayout.V6_OddFire:return new Entry { Cylinders = 6,  Config = EngineConfig.V6OddFire };
                case EngineLayout.V8CrossPlane: return new Entry { Cylinders = 8, Config = EngineConfig.V8CrossPlane };
                case EngineLayout.V8FlatPlane:  return new Entry { Cylinders = 8, Config = EngineConfig.V8FlatPlane };
                case EngineLayout.V10_72:    return new Entry { Cylinders = 10, Config = EngineConfig.V90Even };
                case EngineLayout.V12_60:    return new Entry { Cylinders = 12, Config = EngineConfig.V60 };
                case EngineLayout.W12_W16:   return new Entry { Cylinders = 12, Config = EngineConfig.V90Even };
                case EngineLayout.VTwin90:   return new Entry { Cylinders = 2,  Config = EngineConfig.VTwin90 };
                case EngineLayout.VTwin45:   return new Entry { Cylinders = 2,  Config = EngineConfig.VTwin45 };
                case EngineLayout.VTwin60:   return new Entry { Cylinders = 2,  Config = EngineConfig.VTwin90 };
                // Rotary is carried as firing-equivalent cylinders (rotors x 2),
                // which is the legacy convention LayoutFromLegacy expects.
                case EngineLayout.Rotary1:   return new Entry { Cylinders = 2,  Config = EngineConfig.Rotary };
                case EngineLayout.Rotary2:   return new Entry { Cylinders = 4,  Config = EngineConfig.Rotary };
                case EngineLayout.Rotary3:   return new Entry { Cylinders = 6,  Config = EngineConfig.Rotary };
                case EngineLayout.Rotary4:   return new Entry { Cylinders = 8,  Config = EngineConfig.Rotary };
                // No legacy pair produces these, so they cannot round-trip.
                // Nearest even-fire equivalent rather than dropping the car.
                case EngineLayout.V4:               return new Entry { Cylinders = 4, Config = EngineConfig.Inline };
                case EngineLayout.Inline4CrossPlane:return new Entry { Cylinders = 4, Config = EngineConfig.Inline };
                default: return null;
            }
        }
    }
}
