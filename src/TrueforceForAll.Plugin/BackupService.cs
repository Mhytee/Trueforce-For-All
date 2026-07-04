// Phase 2 backup/sync, milestone M2: codec + merge for the backup envelope. PURE
// (no network, no plugin state): turns the live setup into an envelope + JSON, parses
// one back, and composes a merged envelope for the conflict "smart merge" path. The
// plugin (TrueforcePlugin) owns transport (BackupClient) and applies a restored or
// merged envelope to its live state.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace TrueforceForAll.Plugin
{
    /// <summary>Outcome status of a backup/restore orchestration step.</summary>
    internal enum BackupStatus { Success, Diverged, NotSignedIn, NotConfigured, NothingToRestore, Forbidden, Failed, TooLarge }

    /// <summary>User's choice in the divergence conflict dialog.</summary>
    internal enum BackupConflictChoice { Cancel, SmartMerge, UseThisPc, UseCloud }

    /// <summary>Result of a backup/restore op, surfaced to the settings UI.</summary>
    internal sealed class BackupOutcome
    {
        public BackupStatus Status           { get; set; }
        public string       Message          { get; set; }
        public string       CloudDeviceLabel { get; set; }
        public string       CloudWhenUtc     { get; set; }
    }

    internal static class BackupService
    {
        private static readonly JsonSerializerSettings _json = new JsonSerializerSettings
        {
            ContractResolver  = new DefaultContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Formatting        = Formatting.None,
        };

        /// <summary>Build the full backup envelope from the live setup: the portable
        /// settings projection + Forza split + a verbatim bundle of the user preset
        /// library folder. deviceLabel identifies the PC (Environment.MachineName).</summary>
        public static BackupEnvelope BuildEnvelope(TrueforceSettings settings,
            string userLibraryFolder, string deviceLabel, DateTime utcNow)
        {
            var env = BackupProjection.Build(settings, deviceLabel, utcNow);
            env.Library = BackupLibrary.Bundle(userLibraryFolder);
            return env;
        }

        public static string Serialize(BackupEnvelope env)
            => JsonConvert.SerializeObject(env, _json);

        public static BackupEnvelope Parse(string json)
            => string.IsNullOrEmpty(json) ? null
               : JsonConvert.DeserializeObject<BackupEnvelope>(json, _json);

        /// <summary>Compose a merged envelope from this PC's current setup and the cloud
        /// envelope. Preset libraries union with newest-wins on a same-path clash; the
        /// scalar settings (which can't field-merge) take one whole side: this PC's by
        /// default, or the cloud's when <paramref name="keepCloudSettings"/>.</summary>
        public static BackupEnvelope Merge(BackupEnvelope local, BackupEnvelope cloud,
            bool keepCloudSettings, string deviceLabel, DateTime utcNow)
        {
            var settingsSide = keepCloudSettings ? cloud : local;
            // Fall back to the other side if the chosen side has no settings (e.g. a
            // malformed/old envelope), so a merge never silently drops ALL settings.
            if (settingsSide?.Settings == null)
                settingsSide = (settingsSide == cloud) ? local : cloud;
            return new BackupEnvelope
            {
                SchemaVersion = BackupProjection.SchemaVersion,
                CreatedUtc    = utcNow.ToString("o"),
                DeviceLabel   = deviceLabel ?? string.Empty,
                Settings      = settingsSide?.Settings,
                Forza         = settingsSide?.Forza,
                Library       = BackupLibrary.MergeNewestWins(local?.Library, cloud?.Library),
            };
        }

        /// <summary>Field-level 3-way merge for the auto-sync path. Preset libraries union
        /// newest-wins; the portable SETTINGS + Forza objects merge per field against the
        /// last-synced baseline (the common ancestor), so a change on each PC to DIFFERENT fields
        /// both survive instead of one side winning the whole object. A leaf both PCs changed to
        /// different values is a true conflict, resolved to <paramref name="local"/> (the device
        /// doing the merge). Baselines may be null when none is stored yet (degrades to: equal -&gt;
        /// keep; present on one side -&gt; keep; both present + different -&gt; recurse / local wins).</summary>
        public static BackupEnvelope Merge(BackupEnvelope local, BackupEnvelope cloud,
            JObject baselineSettings, JObject baselineForza,
            IDictionary<string, string> baselineLibraryHashes, string deviceLabel, DateTime utcNow)
        {
            return new BackupEnvelope
            {
                SchemaVersion = BackupProjection.SchemaVersion,
                CreatedUtc    = utcNow.ToString("o"),
                DeviceLabel   = deviceLabel ?? string.Empty,
                Settings      = MergeObject(baselineSettings, local?.Settings, cloud?.Settings),
                Forza         = MergeObject(baselineForza, local?.Forza, cloud?.Forza),
                Library       = MergeLibrary3Way(baselineLibraryHashes, local?.Library, cloud?.Library),
            };
        }

        /// <summary>3-way library merge for auto-sync: union additions, newest-wins on concurrent
        /// edits, and PROPAGATE DELETIONS via the last-synced baseline hashes. A file in the
        /// baseline now absent on one side (and unchanged on the other) is treated as deleted and
        /// omitted; a file edited on one side but deleted on the other survives (edit beats delete);
        /// a file never synced (absent from the baseline) is always kept. Inputs not mutated.</summary>
        public static Dictionary<string, BackupFile> MergeLibrary3Way(
            IDictionary<string, string> baseHashes,
            IDictionary<string, BackupFile> local, IDictionary<string, BackupFile> cloud)
        {
            var merged = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (local != null) foreach (var k in local.Keys) keys.Add(k);
            if (cloud != null) foreach (var k in cloud.Keys) keys.Add(k);

            foreach (var path in keys)
            {
                BackupFile lf = (local != null && local.TryGetValue(path, out var lv)) ? lv : null;
                BackupFile cf = (cloud != null && cloud.TryGetValue(path, out var cv)) ? cv : null;
                string bh = (baseHashes != null && baseHashes.TryGetValue(path, out var b)) ? b : null;

                if (lf != null && cf != null)
                {
                    if (string.Equals(lf.Text, cf.Text, StringComparison.Ordinal)) { merged[path] = lf; continue; }
                    bool lEdited = bh == null || BackupLibrary.Hash(lf.Text) != bh;
                    bool cEdited = bh == null || BackupLibrary.Hash(cf.Text) != bh;
                    if (cEdited && !lEdited)      merged[path] = cf;
                    else if (lEdited && !cEdited) merged[path] = lf;
                    else                          merged[path] = (cf.ModifiedUtc > lf.ModifiedUtc) ? cf : lf;   // both edited -> newest-wins
                }
                else if (lf != null)
                {
                    // Local only. Keep if never synced (added here) or edited here; otherwise the
                    // other PC deleted it and we didn't touch it -> omit (delete propagates).
                    if (bh == null || BackupLibrary.Hash(lf.Text) != bh) merged[path] = lf;
                }
                else if (cf != null)
                {
                    if (bh == null || BackupLibrary.Hash(cf.Text) != bh) merged[path] = cf;
                }
            }
            return merged;
        }

        // Null-safe object merge: returns the non-null side if either is null, else a 3-way merge.
        private static JObject MergeObject(JObject baseline, JObject local, JObject cloud)
        {
            if (local == null) return cloud;
            if (cloud == null) return local;
            return Merge3(baseline, local, cloud) as JObject ?? local;
        }

        // Recursive 3-way merge. local = this device, cloud = the other device, baseline = their
        // last common ancestor. DIFFERENT-field edits on each side both survive; a leaf/array both
        // changed differently resolves to local (the active device merging).
        private static JToken Merge3(JToken baseline, JToken local, JToken cloud)
        {
            if (JToken.DeepEquals(local, cloud)) return local;                  // agree (incl. identical change)
            bool localChanged = !JToken.DeepEquals(local, baseline);
            bool cloudChanged = !JToken.DeepEquals(cloud, baseline);
            if (cloudChanged && !localChanged) return cloud;                    // only the other device changed it
            if (localChanged && !cloudChanged) return local;                    // only this device changed it
            // Both changed. Recurse into objects so DIFFERENT sub-fields each survive.
            if (local is JObject lo && cloud is JObject co)
            {
                var bo = baseline as JObject;
                var result = new JObject();
                var keys = lo.Properties().Select(p => p.Name)
                    .Union(co.Properties().Select(p => p.Name), StringComparer.Ordinal);
                foreach (var k in keys)
                {
                    JToken lv = lo[k], cv = co[k];
                    if (lv == null) { result[k] = cv; continue; }               // only the other device has this key
                    if (cv == null) { result[k] = lv; continue; }               // only this device has it
                    result[k] = Merge3(bo?[k], lv, cv);
                }
                return result;
            }
            // Both changed and both are arrays (e.g. CustomEngines): union the
            // elements so neither device's additions are silently dropped.
            // Without this, returning `local` wholesale loses the other device's
            // new entries permanently once the merge is stamped as the baseline.
            if (local is JArray la && cloud is JArray ca)
                return MergeArray(la, ca);
            return local;   // leaf both changed differently -> this (active) device wins the tie
        }

        // Union two arrays, local first so it wins on a same-identity conflict.
        // Object elements are keyed by their Id/id field when present (an edited
        // entry replaces rather than duplicates); other elements dedup by value.
        // Additive bias: a deleted-on-one-side element that still exists on the
        // other is resurrected, which for additive lists (custom engines) is far
        // less harmful than silently losing an addition.
        private static JArray MergeArray(JArray local, JArray cloud)
        {
            var result = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in local) AddUnioned(result, seen, el);
            foreach (var el in cloud) AddUnioned(result, seen, el);
            return result;
        }

        private static void AddUnioned(JArray result, HashSet<string> seen, JToken el)
        {
            string key = ElementKey(el) ?? ("val:" + (el?.ToString(Formatting.None) ?? "null"));
            if (seen.Add(key)) result.Add(el);
        }

        private static string ElementKey(JToken el)
        {
            if (el is JObject o)
            {
                var id = o["Id"] ?? o["id"];
                if (id != null && id.Type != JTokenType.Null)
                {
                    string s = id.ToString();
                    if (!string.IsNullOrEmpty(s)) return "id:" + s;
                }
            }
            return null;
        }
    }
}
