// Deterministic body-hash helper for preset upload tracking. Used by the
// Share button gate to detect whether the current local body still matches
// the bytes the user last uploaded, and by the auto-update path to detect
// whether a downloaded preset has been locally edited since download.
//
// Hash properties (must hold across runs / machines):
//   - SHA256 of the body's JSON serialization, returned as lowercase hex
//   - Alphabetical property order via OrderedContractResolver
//   - Formatting.None (no whitespace) + InvariantCulture (stable float repr)
//   - NullValueHandling.Include so a renamed-to-null field still changes hash
//
// All four entry points produce the same hash for equivalent input regardless
// of whether the caller has a JObject in hand (server-shaped) or a typed
// settings instance (local edit path).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace TrueforceForAll.Plugin
{
    internal static class PresetBodyHasher
    {
        private static readonly JsonSerializerSettings DeterministicSettings = new JsonSerializerSettings
        {
            ContractResolver = new OrderedContractResolver(),
            Formatting = Formatting.None,
            Culture = CultureInfo.InvariantCulture,
            FloatParseHandling = FloatParseHandling.Double,
            FloatFormatHandling = FloatFormatHandling.String,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Include
        };

        public static string ComputeBodyHash(JObject body)
        {
            if (body == null) return null;
            // Round-trip through typed serializer so JObject's source order is
            // replaced by the alphabetical OrderedContractResolver ordering.
            var sorted = SortJToken(body);
            string json = JsonConvert.SerializeObject(sorted, DeterministicSettings);
            return Sha256Hex(json);
        }

        public static string ComputeGameSnapshotBodyHash(GameSettingsSnapshot snap)
        {
            if (snap == null) return null;
            string json = JsonConvert.SerializeObject(snap, DeterministicSettings);
            return Sha256Hex(json);
        }

        public static string ComputeCarOverrideHash(CarOverride ovr)
        {
            if (ovr == null) return null;
            string json = JsonConvert.SerializeObject(ovr, DeterministicSettings);
            return Sha256Hex(json);
        }

        public static string ComputeCustomEngineHash(CustomEngineDef eng)
        {
            if (eng == null) return null;
            string json = JsonConvert.SerializeObject(eng, DeterministicSettings);
            return Sha256Hex(json);
        }

        private static JToken SortJToken(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var prop in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted.Add(prop.Name, SortJToken(prop.Value));
                return sorted;
            }
            if (token is JArray arr)
            {
                var sortedArr = new JArray();
                foreach (var item in arr)
                    sortedArr.Add(SortJToken(item));
                return sortedArr;
            }
            return token.DeepClone();
        }

        private static string Sha256Hex(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // Tracking metadata that travels alongside the body but does NOT
        // represent body content. Excluded from the hash so that stamping
        // these fields after a successful share doesn't itself invalidate
        // the hash the gate just stored - the next refresh would see a
        // different hash and the Share button would never disable.
        private static readonly HashSet<string> ExcludedFromHash =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "CommunitySourceId",
                "CommunityUploadedById",
                "CommunityUploadedByUserId",
                "CommunityUploadedBodyHash",
                "CommunityUploadedVersion",
            };

        private sealed class OrderedContractResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
            {
                var properties = base.CreateProperties(type, memberSerialization);
                return properties
                    .Where(p => !ExcludedFromHash.Contains(p.PropertyName))
                    .OrderBy(p => p.PropertyName, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }
}
