// Built-in preset library — shipped with the plugin, installed into the
// user's preset library on first run if not already present.
//
// Built-in preset names are the key into BuiltinPresetJsons. Names ending
// in " (default)" are treated as factory defaults: refuse delete, refuse
// in-place overwrite (the Save flow forks to a user-named preset instead).
//
// Why JSON strings instead of object literals: snapshots are derived from
// real tunings exported as JSON; pasting the JSON here keeps them
// round-trippable with the existing serializer (StringEnumConverter for
// Waveform / AbsMode), and updating a baseline is just paste-replace.

using System.Collections.Generic;

namespace TrueforceForAll.Plugin
{
    internal static class BuiltinPresets
    {
        /// <summary>Per-game default preset name — bound automatically as the
        /// game's default if no user-chosen default exists yet. SimHub
        /// GameName → built-in preset name (must be a key in
        /// <see cref="BuiltinPresetJsons"/>).</summary>
        public static readonly IReadOnlyDictionary<string, string> GameDefaultBindings =
            new Dictionary<string, string>
            {
                { "AssettoCorsa", "Assetto Corsa (default)" },
                { "Wreckfest2",   "Wreckfest 2 (default)"   },
            };

        /// <summary>Built-in preset name → serialized GameSettingsSnapshot
        /// JSON. Deserialized via Newtonsoft.Json into the same shape as
        /// user presets. Each entry is the user-tested baseline that
        /// shipped with the plugin.</summary>
        public static readonly IReadOnlyDictionary<string, string> BuiltinPresetJsons =
            new Dictionary<string, string>
            {
                ["Assetto Corsa (default)"] = AssettoCorsaJson,
                ["Wreckfest 2 (default)"]   = Wreckfest2Json,
            };

        public static bool IsBuiltin(string presetName)
            => !string.IsNullOrEmpty(presetName) && BuiltinPresetJsons.ContainsKey(presetName);

        // ----- Snapshots -----
        // These are GameSettingsSnapshot shapes (no top-level wrapper). Source:
        // exported from a real tuning session, then minified.

        private const string AssettoCorsaJson = @"{
            ""MasterGain"":0.9995428,
            ""FfbScale"":0.8008723,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.1301488,
            ""FfbSpikeMaxLsbPerMs"":2060.923,
            ""FfbPeakSoftLimitLsb"":1561.78564,
            ""SkipFfbPassthrough"":false,
            ""DuckDepth"":0.6952232,
            ""DuckAttackMs"":5.0,
            ""DuckReleaseMs"":80.0,
            ""AudioCapture"":{""Enabled"":true,""Gain"":0.05952296,""LowpassCutoffHz"":567.0934,""HighpassCutoffHz"":35.20595},
            ""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Cylinders"":7,""Pitch"":1.00160933,""LowpassHz"":510.1833,""Waveform"":""Triangle""},
            ""RoadBumps"":{""Enabled"":true,""Gain"":0.448169053,""Freq"":61.45767,""Waveform"":""Triangle""},
            ""TractionLoss"":{""Enabled"":true,""Gain"":0.06434366,""Sensitivity"":0.178701326,""Freq"":133.901657,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":40.9325,""Waveform"":""Noise""},
            ""GearShift"":{""Enabled"":true,""Gain"":0.396566778,""Freq"":34.6132431,""Waveform"":""Sine""},
            ""AbsClick"":{""Enabled"":true,""Gain"":0.140768245,""Freq"":150.0,""PulseFreq"":9.821309,""DutyCycle"":0.331281453,""TickDurationMs"":35.0,""Mode"":""Pulse"",""Waveform"":""Square""},
            ""CarOverrides"":{
                ""ks_nissan_skyline_r34"":{""EnginePulse"":{""Enabled"":true,""Gain"":0.07882283,""Cylinders"":6,""Pitch"":1.00160933,""LowpassHz"":338.3869,""Waveform"":""Triangle""}},
                ""nohesi_realistic_nissan_gtr_r35_vlct"":{""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Cylinders"":7,""Pitch"":1.00160933,""LowpassHz"":510.1833,""Waveform"":""Triangle""},""TractionLoss"":{""Enabled"":true,""Gain"":0.121609129,""Sensitivity"":0.178701326,""Freq"":133.901657,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":30.0,""Waveform"":""Noise""},""AbsClick"":{""Enabled"":true,""Gain"":0.244887292,""Freq"":150.0,""PulseFreq"":9.821309,""DutyCycle"":0.331281453,""TickDurationMs"":35.0,""Mode"":""Pulse"",""Waveform"":""Square""}},
                ""bdc_streetspec_ae86_v4"":{""EnginePulse"":{""Enabled"":true,""Gain"":0.0755927339,""Cylinders"":4,""Pitch"":1.00160933,""LowpassHz"":513.3297,""Waveform"":""Sine""}},
                ""ks_toyota_ae86_tuned"":{""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Cylinders"":4,""Pitch"":1.00160933,""LowpassHz"":333.1809,""Waveform"":""Triangle""}},
                ""gravygarage_street_e36_touring"":{""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Cylinders"":8,""Pitch"":1.00160933,""LowpassHz"":775.6869,""Waveform"":""Triangle""},""TractionLoss"":{""Enabled"":true,""Gain"":0.06434366,""Sensitivity"":0.1,""Freq"":133.901657,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":40.9325,""Waveform"":""Noise""},""AudioCapture"":{""Enabled"":true,""Gain"":0.0517140329,""LowpassCutoffHz"":567.0934,""HighpassCutoffHz"":35.20595}}
            }
        }";

        private const string Wreckfest2Json = @"{
            ""MasterGain"":0.9995428,
            ""FfbScale"":0.8008723,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.0,
            ""FfbSpikeMaxLsbPerMs"":2993.42236,
            ""FfbPeakSoftLimitLsb"":1822.08325,
            ""SkipFfbPassthrough"":false,
            ""DuckDepth"":0.6952232,
            ""DuckAttackMs"":5.0,
            ""DuckReleaseMs"":80.0,
            ""AudioCapture"":{""Enabled"":true,""Gain"":0.04390511,""LowpassCutoffHz"":352.0876,""HighpassCutoffHz"":30.0},
            ""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Cylinders"":8,""Pitch"":1.00160933,""LowpassHz"":401.7727,""Waveform"":""Sine""},
            ""RoadBumps"":{""Enabled"":true,""Gain"":0.2867846,""Freq"":61.45767,""Waveform"":""Triangle""},
            ""TractionLoss"":{""Enabled"":true,""Gain"":0.06434366,""Sensitivity"":0.5108411,""Freq"":133.901657,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":30.0,""Waveform"":""Noise""},
            ""GearShift"":{""Enabled"":true,""Gain"":0.396566778,""Freq"":34.6132431,""Waveform"":""Sine""},
            ""AbsClick"":{""Enabled"":true,""Gain"":0.2657111,""Freq"":132.820358,""PulseFreq"":9.821309,""DutyCycle"":0.331281453,""TickDurationMs"":35.0,""Mode"":""PerTick"",""Waveform"":""Square""},
            ""CarOverrides"":{
                ""ks_nissan_skyline_r34"":{""EnginePulse"":{""Enabled"":false,""Gain"":0.0996466354,""Cylinders"":6,""Pitch"":0.25,""LowpassHz"":0.0,""Waveform"":""Sine""}},
                ""car11:default"":{""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Cylinders"":8,""Pitch"":1.00160933,""LowpassHz"":401.7727,""Waveform"":""Sine""}}
            }
        }";
    }
}
