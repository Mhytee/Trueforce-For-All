using System;
using System.IO;

namespace TrueforceForAll.Plugin
{
    /// <summary>The plugin's on-disk data root, single-sourced. Everything the
    /// plugin persists lives under SimHub's PluginsData\Common (user-writable,
    /// no admin needed); this class is the only place that root is assembled.
    /// The legacy names below are load-bearing for one-time migrations: they
    /// must stay byte-identical to what old versions wrote, or upgrades stop
    /// finding user data (do not "fix" the branding here).</summary>
    internal static class TfPaths
    {
        /// <summary>SimHub's install folder (the plugin loads from there).
        /// Empty string, never null, if the AppDomain has no base directory.</summary>
        internal static string BaseDir => AppDomain.CurrentDomain.BaseDirectory ?? "";

        /// <summary>PluginsData\Common under the SimHub install: the parent of
        /// every folder and file the plugin owns.</summary>
        internal static string CommonRoot => Path.Combine(BaseDir, "PluginsData", "Common");

        /// <summary>Pre-rebrand car-preset folder consumed (and renamed) by the
        /// one-time legacy-cars migration.</summary>
        internal static string LegacyCarsFolder => Path.Combine(CommonRoot, "TrueforceCars");

        /// <summary>The plugin's settings JSON. SimHub owns the name; we touch
        /// the file directly only for pre-migration sibling backups.</summary>
        internal static string GeneralSettingsFile => Path.Combine(CommonRoot, "TrueforcePlugin.GeneralSettings.json");
    }
}
