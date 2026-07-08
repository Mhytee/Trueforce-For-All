// Round-trip proof for the new preset-scoped / personal snapshot fields.
//
// This does NOT link the real GameSettingsSnapshot (it pulls in the whole
// plugin/Effects graph). Instead it mirrors the EXACT field shapes that were
// added (bool DuckingEnabled defaulting true; nullable bool?/double? spring
// fields; float FfbScale) and round-trips them through the EXACT
// serializer calls the persistence code uses:
//   write: JsonConvert.SerializeObject(x, Formatting.Indented)   (plain)
//   read:  JsonConvert.DeserializeObject<T>(json, {MaxDepth=64}) (SafeJson.Settings)
// Verified against the source that these fields carry no [JsonIgnore],
// [JsonConverter], [DefaultValue], and that no path sets DefaultValueHandling,
// so this faithfully represents the real round-trip. The load-bearing case is
// DuckingEnabled=false: with DefaultValueHandling.Ignore it would be omitted
// and come back true; this asserts it survives.

using Newtonsoft.Json;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class SnapshotSerializationTests
    {
        // Mirror of the added GameSettingsSnapshot fields (same types + defaults).
        // MasterGain is intentionally absent: it is a global setting, not a
        // preset-scoped snapshot field.
        private sealed class SnapshotShape
        {
            public float  FfbScale                  { get; set; } = 0.80f;  // personal
            public bool   DuckingEnabled            { get; set; } = true;   // default-true bool (at-risk)
            public float  DuckDepth                 { get; set; } = 0.60f;
            public bool?  StationarySpringEnabled   { get; set; }           // nullable migration
            public double? StationarySpringStrength { get; set; }
            public double? StationarySpringCutoffKmh { get; set; }
        }

        // Same as the plugin's SafeJson.Settings.
        private static readonly JsonSerializerSettings ReadSettings =
            new JsonSerializerSettings { MaxDepth = 64 };

        private static SnapshotShape RoundTrip(SnapshotShape s)
        {
            string json = JsonConvert.SerializeObject(s, Formatting.Indented);
            return JsonConvert.DeserializeObject<SnapshotShape>(json, ReadSettings);
        }

        [Fact]
        public void EdgeValues_SurviveRoundTrip()
        {
            var src = new SnapshotShape
            {
                FfbScale = 0.55f,
                DuckingEnabled = false,   // the field most at risk of being dropped
                DuckDepth = 0.20f,
                StationarySpringEnabled = false,
                StationarySpringStrength = 0.77,
                StationarySpringCutoffKmh = 9.0,
            };

            // Prove the at-risk field is actually present in the written JSON.
            string json = JsonConvert.SerializeObject(src, Formatting.Indented);
            Assert.Contains("\"DuckingEnabled\": false", json);

            var rt = RoundTrip(src);
            Assert.False(rt.DuckingEnabled);                       // not silently flipped to true
            Assert.Equal(0.55f, rt.FfbScale);                      // personal field travels locally
            Assert.Equal(0.20f, rt.DuckDepth);
            Assert.True(rt.StationarySpringEnabled.HasValue);
            Assert.False(rt.StationarySpringEnabled.Value);
            Assert.Equal(0.77, rt.StationarySpringStrength.Value, 6);
            Assert.Equal(9.0, rt.StationarySpringCutoffKmh.Value, 6);
        }

        [Fact]
        public void MissingFields_MigrateToSafeDefaults()
        {
            // An OLD preset JSON predating these fields.
            var rt = JsonConvert.DeserializeObject<SnapshotShape>(
                "{ \"DuckDepth\": 0.6 }", ReadSettings);

            Assert.True(rt.DuckingEnabled);                         // absent -> stays ON (current behavior)
            Assert.False(rt.StationarySpringEnabled.HasValue);      // absent -> null -> "leave live value"
            Assert.False(rt.StationarySpringStrength.HasValue);
            Assert.False(rt.StationarySpringCutoffKmh.HasValue);
        }

        [Fact]
        public void NullSpring_RoundTripsAsNull()
        {
            var rt = RoundTrip(new SnapshotShape());               // spring left null
            Assert.False(rt.StationarySpringEnabled.HasValue);
            Assert.False(rt.StationarySpringStrength.HasValue);
            Assert.False(rt.StationarySpringCutoffKmh.HasValue);
            Assert.True(rt.DuckingEnabled);                        // default true
        }
    }
}
