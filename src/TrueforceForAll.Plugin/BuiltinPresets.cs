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
                { "AssettoCorsa",    "Assetto Corsa (default)"     },
                { "Wreckfest2",      "Wreckfest 2 (default)"       },
                // Forza titles share the Data Out wire format end-to-end (FM7 →
                // FH4 → FH5 → FM2023, almost certainly FH6 too), so one preset
                // covers the whole family. Pre-bind the known SimHub GameNames
                // here; FH6's name will be added in a follow-up if MS picks
                // anything other than "ForzaHorizon6".
                { "ForzaHorizon5",   "Forza Horizon (default)"     },
                { "ForzaHorizon6",   "Forza Horizon (default)"     },
                { "ForzaHorizon4",   "Forza Horizon (default)"     },
                { "ForzaMotorsport", "Forza Motorsport (default)"  },
                { "ForzaMotorsport8","Forza Motorsport (default)"  },
                { "ForzaMotorsport7","Forza Motorsport (default)"  },
            };

        /// <summary>Built-in preset name → serialized GameSettingsSnapshot
        /// JSON. Deserialized via Newtonsoft.Json into the same shape as
        /// user presets. Each entry is the user-tested baseline that
        /// shipped with the plugin.</summary>
        public static readonly IReadOnlyDictionary<string, string> BuiltinPresetJsons =
            new Dictionary<string, string>
            {
                ["Assetto Corsa (default)"]    = AssettoCorsaJson,
                ["Wreckfest 2 (default)"]      = Wreckfest2Json,
                ["Forza Horizon (default)"]    = ForzaHorizonJson,
                ["Forza Motorsport (default)"] = ForzaMotorsportJson,
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
            ""FfbSpikeTamingEnabled"":true,
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

        // Forza Horizon baseline. Tuned for arcade-leaning physics where
        // tire slip saturates harder than ACC; Sensitivity is bumped up to
        // ~0.4 so light slides actually trigger before the fully-saturated
        // hard drift state. Cylinders=4 is just the slider-default fallback;
        // EnginePulse.AutoCylinders takes over from Forza's NumCylinders the
        // moment the user enters any car. SkipFfbPassthrough=true because
        // Forza writes its own ep3 cur via the wheel's native path and our
        // FFB tap mirroring would fight it.
        private const string ForzaHorizonJson = @"{
            ""MasterGain"":1.0,
            ""FfbScale"":1.0,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.0,
            ""FfbSpikeTamingEnabled"":false,
            ""FfbSpikeMaxLsbPerMs"":2060.923,
            ""FfbPeakSoftLimitLsb"":1561.78564,
            ""SkipFfbPassthrough"":true,
            ""DuckDepth"":0.6952232,
            ""DuckAttackMs"":5.0,
            ""DuckReleaseMs"":80.0,
            ""AudioCapture"":{""Enabled"":true,""Gain"":0.06,""LowpassCutoffHz"":350.0,""HighpassCutoffHz"":30.0},
            ""EnginePulse"":{""Enabled"":true,""Gain"":0.07,""Cylinders"":4,""Pitch"":1.0,""LowpassHz"":450.0,""Waveform"":""Triangle""},
            ""RoadBumps"":{""Enabled"":true,""Gain"":0.5,""Freq"":60.0,""Waveform"":""Noise"",""SurfaceEnabled"":true,""SurfaceGain"":0.6,""SurfaceFreq"":140.0,""SurfaceWaveform"":""Noise"",""SurfaceLowpassHz"":900.0,""SurfaceHighpassHz"":70.0,""SurfaceRumbleScale"":1.2,""RumbleStripPulseAmp"":0.0,""RumbleStripPulseMs"":120},
            ""TractionLoss"":{""Enabled"":true,""Gain"":0.08,""Sensitivity"":0.4,""Freq"":133.9,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":40.0,""Waveform"":""Noise""},
            ""GearShift"":{""Enabled"":true,""Gain"":0.4,""Freq"":40.0,""Waveform"":""Sine""},
            ""AbsClick"":{""Enabled"":false,""Gain"":0.15,""Freq"":150.0,""PulseFreq"":9.8,""DutyCycle"":0.33,""TickDurationMs"":35.0,""Mode"":""Pulse"",""Waveform"":""Square""}
        }";

        // Forza Motorsport (2023) ships native Trueforce, so SkipFfbPassthrough
        // is true here too — we layer audio-haptics on top of the game's own
        // FFB rather than replacing it. Slight Sensitivity bump on Traction
        // and a more conservative Master gain to leave headroom for the
        // game's already-rich force feedback.
        private const string ForzaMotorsportJson = @"{
            ""MasterGain"":0.85,
            ""FfbScale"":1.0,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.0,
            ""FfbSpikeTamingEnabled"":false,
            ""FfbSpikeMaxLsbPerMs"":2060.923,
            ""FfbPeakSoftLimitLsb"":1561.78564,
            ""SkipFfbPassthrough"":true,
            ""DuckDepth"":0.6952232,
            ""DuckAttackMs"":5.0,
            ""DuckReleaseMs"":80.0,
            ""AudioCapture"":{""Enabled"":true,""Gain"":0.06,""LowpassCutoffHz"":350.0,""HighpassCutoffHz"":30.0},
            ""EnginePulse"":{""Enabled"":true,""Gain"":0.06,""Cylinders"":4,""Pitch"":1.0,""LowpassHz"":450.0,""Waveform"":""Triangle""},
            ""RoadBumps"":{""Enabled"":true,""Gain"":0.4,""Freq"":60.0,""Waveform"":""Noise"",""SurfaceEnabled"":true,""SurfaceGain"":0.5,""SurfaceFreq"":140.0,""SurfaceWaveform"":""Noise"",""SurfaceLowpassHz"":900.0,""SurfaceHighpassHz"":70.0,""SurfaceRumbleScale"":1.0,""RumbleStripPulseAmp"":0.0,""RumbleStripPulseMs"":120},
            ""TractionLoss"":{""Enabled"":true,""Gain"":0.08,""Sensitivity"":0.5,""Freq"":133.9,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":40.0,""Waveform"":""Noise""},
            ""GearShift"":{""Enabled"":true,""Gain"":0.4,""Freq"":40.0,""Waveform"":""Sine""},
            ""AbsClick"":{""Enabled"":true,""Gain"":0.15,""Freq"":150.0,""PulseFreq"":9.8,""DutyCycle"":0.33,""TickDurationMs"":35.0,""Mode"":""Pulse"",""Waveform"":""Square""}
        }";

        private const string Wreckfest2Json = @"{
            ""MasterGain"":0.9995428,
            ""FfbScale"":0.8008723,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.0,
            ""FfbSpikeTamingEnabled"":false,
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
