// Per-car preset file storage.
//
// One file per car at <SimHub>/PluginsData/Common/TrueforceCars/<sanitized-carId>.tfcar.json.
// Each file contains a CarPresetFile (CarId, GameName, Override) — same shape
// as the existing user-shareable car-preset export format, so a file in this
// folder is directly importable/exportable without translation.
//
// Decoupling rationale: a car's haptic profile is tied to its physical
// model, not to whichever game preset the user has active. Storing per-car
// overrides separately lets the user switch presets without losing per-car
// tuning, and makes per-car files independently shareable.
//
// On Init the plugin loads all files into an in-memory map; subsequent
// reads are dict lookups, writes go to both the map and the file
// synchronously. Files use the existing CarPresetFile schema for symmetry
// with Export/Import — read once, written on every CarOverride mutation.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    internal sealed class CarPresetStore
    {
        private const string FolderName = "TrueforceCars";
        private const string FileExtension = ".tfcar.json";

        private readonly string _folderPath;
        private readonly Action<string> _log;

        public CarPresetStore(Action<string> log = null)
        {
            // SimHub's plugin host sets BaseDirectory to its install dir; the
            // common-settings folder is the convention for plugin data.
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            _folderPath = Path.Combine(baseDir, "PluginsData", "Common", FolderName);
            _log = log;
        }

        public string FolderPath => _folderPath;

        /// <summary>Walks the folder and returns every car preset file as a
        /// (carId → override) dict. Skips malformed files with a log line —
        /// never throws, since a corrupt user file shouldn't break Init.</summary>
        public Dictionary<string, CarOverride> LoadAll()
        {
            var result = new Dictionary<string, CarOverride>();
            try
            {
                if (!Directory.Exists(_folderPath)) return result;
                foreach (var path in Directory.GetFiles(_folderPath, "*" + FileExtension))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        var f = JsonConvert.DeserializeObject<CarPresetFile>(json);
                        if (f?.CarId != null && f.Override != null)
                            result[f.CarId] = f.Override;
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"[Trueforce] Skipping malformed car preset '{Path.GetFileName(path)}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] LoadAll car presets failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>Writes (or overwrites) the per-car file for this car.
        /// Empty overrides get deleted instead of written, since storing an
        /// all-null CarOverride is meaningless.</summary>
        public void Save(string carId, string gameName, CarOverride ovr)
        {
            if (string.IsNullOrEmpty(carId)) return;
            if (ovr == null || ovr.IsEmpty)
            {
                Delete(carId);
                return;
            }
            try
            {
                Directory.CreateDirectory(_folderPath);
                var path = PathFor(carId);
                var f = new CarPresetFile { GameName = gameName ?? "", CarId = carId, Override = ovr };
                File.WriteAllText(path, JsonConvert.SerializeObject(f, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Save car preset for '{carId}' failed: {ex.Message}");
            }
        }

        /// <summary>Deletes the per-car file. No-op if it doesn't exist.</summary>
        public void Delete(string carId)
        {
            if (string.IsNullOrEmpty(carId)) return;
            try
            {
                var path = PathFor(carId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Trueforce] Delete car preset for '{carId}' failed: {ex.Message}");
            }
        }

        /// <summary>True iff a file already exists for this car. Used by
        /// migration to avoid overwriting an existing per-car file with
        /// older nested-in-preset data.</summary>
        public bool Exists(string carId)
            => !string.IsNullOrEmpty(carId) && File.Exists(PathFor(carId));

        // Sanitize carId for filesystem — Windows-invalid chars → '_'. Same
        // helper as MakeFileSafe in the UI; duplicated here so this class
        // doesn't depend on UI code.
        private string PathFor(string carId)
        {
            var arr = carId.ToCharArray();
            var invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(invalid, arr[i]) >= 0) arr[i] = '_';
            return Path.Combine(_folderPath, new string(arr) + FileExtension);
        }
    }
}
