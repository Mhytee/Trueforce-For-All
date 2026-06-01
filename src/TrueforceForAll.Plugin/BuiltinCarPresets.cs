// Built-in car presets facade. Like the game built-ins, car built-ins are now
// data files in the shipped folder (cars/ subfolder; see BuiltinPresetStore)
// rather than C# string consts. This type just forwards the loaded set so the
// existing consumer (CarPresetStore.InstallOrUpdateBuiltinCarPresets, called
// from plugin Init via BuiltinCarPresets.PresetJsons) is unchanged.
//
// Each car file is a full CarPresetFile JSON snapshot (Type, Version,
// GameName, CarId, PresetName "<carId> (default)", IsBuiltin=true, Override).
// The IsBuiltin flag inside the JSON is the runtime authority.
//
// Adding / updating a default: drop a .json into the folder's cars/ subfolder
// and add a { key, file } entry to manifest.json "cars". The dev panel's
// The DEV-mode 'Set as built-in' promote button does this for you.

using System.Collections.Generic;

namespace TrueforceForAll.Plugin
{
    internal static class BuiltinCarPresets
    {
        /// <summary>Car preset key -> CarPresetFile JSON, loaded from the
        /// built-in folder's cars/ section. Key is informational (the
        /// authoritative carId / presetName live inside each JSON).</summary>
        public static IReadOnlyDictionary<string, string> PresetJsons => BuiltinPresets.CarPresetJsons;
    }
}
