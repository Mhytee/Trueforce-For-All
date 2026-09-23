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

namespace TrueforceForAll.Plugin
{
    internal static class IRacingEngineSeed
    {
        private const string ResourceName = "TrueforceForAll.Plugin.iracing-engine-layouts.json";

        private static readonly object Gate = new object();
        private static Dictionary<string, EngineSeedEntry> _map;

        /// <summary>Look a car up by iRacing CarPath (the sim's own key, e.g.
        /// "mx5 mx52016"). Case-insensitive; the sim publishes lowercase but a
        /// caller should not have to know that.</summary>
        public static bool TryGet(string carPath, out EngineSeedEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(carPath)) return false;
            var m = Load();
            return m != null && m.TryGetValue(carPath.Trim(), out entry) && entry != null;
        }

        private static Dictionary<string, EngineSeedEntry> Load()
        {
            lock (Gate)
            {
                if (_map != null) return _map;
                _map = EngineSeedJson.Load(ResourceName, "iRacing");
                return _map;
            }
        }
    }
}
