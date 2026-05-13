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
                // Forza Horizon variants share the Data Out wire format
                // (FH4/FH5/FH6) so one preset covers them all. Forza
                // Motorsport and the F1 22-25 line ship native Trueforce
                // on PC and the plugin auto-disables for them (see
                // IsNativeTrueforceGame), so no mappings or built-in
                // presets for those titles.
                { "FH4",             "Forza Horizon (default)"     },
                { "FH5",             "Forza Horizon (default)"     },
                { "FH6",             "Forza Horizon (default)"     },
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
            };

        public static bool IsBuiltin(string presetName)
            => !string.IsNullOrEmpty(presetName) && BuiltinPresetJsons.ContainsKey(presetName);

        // ----- Snapshots -----
        // These are GameSettingsSnapshot shapes (no top-level wrapper). Source:
        // exported from a real tuning session, then minified.

        // Refreshed in 0.1.3 from a recent live "Assetto Corsa" preset
        // export. Earlier baseline (May 2026) predated PitLimiter / DRS /
        // Collision as effects and the RoadBumps surface channel / engine
        // load-layer + high-RPM boost fields. The values here are also the
        // basis for the C# class defaults in TrueforceSettings.cs.
        private const string AssettoCorsaJson = @"{
            ""MasterGain"":0.9995428,
            ""FfbScale"":0.8008723,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.0,
            ""FfbSpikeTamingEnabled"":true,
            ""FfbSpikeMaxLsbPerMs"":2508.35864,
            ""FfbPeakSoftLimitLsb"":2061.90381,
            ""SkipFfbPassthrough"":false,
            ""DuckDepth"":0.5953513,
            ""DuckAttackMs"":5.0,
            ""DuckReleaseMs"":80.0,
            ""AudioCapture"":{""Enabled"":true,""Gain"":0.05952296,""LowpassCutoffHz"":567.0934,""HighpassCutoffHz"":35.20595},
            ""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Pitch"":1.00160933,""LowpassHz"":510.1833,""Waveform"":""Sine"",""ElectricMode"":""MutedHum"",""Layout"":""Auto"",""CustomEngineId"":"""",""CustomFiringPattern"":"""",""CustomFiringPatternName"":"""",""LoadLayerEnabled"":true,""LoadLayerGain"":0.8,""HighRpmBoostEnabled"":true,""HighRpmBoostAmount"":0.7,""Cylinders"":0,""EngineConfig"":""Auto"",""FiringOrderEnabled"":true},
            ""RoadBumps"":{""Enabled"":true,""Gain"":0.448169053,""Freq"":61.45767,""Waveform"":""Triangle"",""SurfaceEnabled"":true,""SurfaceGain"":0.69514066,""SurfaceFreq"":120.0,""SurfaceRumbleScale"":1.0,""SurfaceLowpassHz"":800.0,""SurfaceHighpassHz"":60.0,""SurfaceWaveform"":""Noise"",""RumbleStripPulseAmp"":0.0172855314,""RumbleStripPulseMs"":120},
            ""TractionLoss"":{""Enabled"":true,""Gain"":0.0387813151,""Sensitivity"":0.178701326,""Freq"":133.901657,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":40.9325,""Waveform"":""Noise""},
            ""GearShift"":{""Enabled"":true,""Gain"":0.396566778,""Freq"":34.6132431,""Waveform"":""Square""},
            ""AbsClick"":{""Enabled"":true,""Gain"":0.140768245,""Freq"":150.0,""PulseFreq"":9.821309,""DutyCycle"":0.331281453,""TickDurationMs"":35.0,""Mode"":""Pulse"",""Waveform"":""Square""},
            ""PitLimiter"":{""Enabled"":true,""Gain"":0.0832266361,""Freq"":50.49936,""PulseFreq"":4.340589,""DutyCycle"":0.483226657,""ActiveAmp"":0.3,""Waveform"":""Square""},
            ""Drs"":{""Enabled"":true,""Gain"":0.280409724,""ActivationFreq"":60.3841171,""ActivationMs"":80,""ActivationAmp"":0.5016645,""SustainedFreq"":120.371323,""SustainedAmp"":0.0481434,""Waveform"":""Square"",""SustainedWaveform"":""Sine""},
            ""Collision"":{""Enabled"":true,""Gain"":0.208867252,""Freq"":50.0,""EnvelopeMs"":120,""MinThreshold"":0.139180541,""MinAmp"":0.2,""MaxAmp"":0.85,""NormalizationScale"":2.0,""RefractoryMs"":250,""Waveform"":""Square""}
        }";

        // Forza Horizon baseline. Tuned for arcade-leaning physics where
        // tire slip saturates harder than ACC; Sensitivity is bumped up to
        // ~0.4 so light slides actually trigger before the fully-saturated
        // hard drift state. Cylinders=4 is just the slider-default fallback;
        // EnginePulse.AutoCylinders takes over from Forza's NumCylinders the
        // moment the user enters any car. SkipFfbPassthrough=false because
        // FH does not ship native Trueforce — its FFB rides on ep0 like any
        // standard DirectInput game, so we mirror the captured target into
        // ep3 cur ourselves. (Forza Motorsport ships native Trueforce and
        // would want this true, but FM is in the auto-disable list.)
        private const string ForzaHorizonJson = @"{
            ""MasterGain"":1.0,
            ""FfbScale"":1.0,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.0,
            ""FfbSpikeTamingEnabled"":false,
            ""FfbSpikeMaxLsbPerMs"":2060.923,
            ""FfbPeakSoftLimitLsb"":1561.78564,
            ""SkipFfbPassthrough"":false,
            ""DuckDepth"":0.6952232,
            ""DuckAttackMs"":5.0,
            ""DuckReleaseMs"":80.0,
            ""AudioCapture"":{""Enabled"":true,""Gain"":0.06,""LowpassCutoffHz"":350.0,""HighpassCutoffHz"":30.0},
            ""EnginePulse"":{""Enabled"":true,""Gain"":0.07,""Cylinders"":4,""Pitch"":1.0,""LowpassHz"":450.0,""Waveform"":""Triangle"",""LoadLayerEnabled"":true,""LoadLayerGain"":0.8,""HighRpmBoostEnabled"":true,""HighRpmBoostAmount"":0.7},
            ""RoadBumps"":{""Enabled"":true,""Gain"":0.5,""Freq"":60.0,""Waveform"":""Noise"",""SurfaceEnabled"":true,""SurfaceGain"":0.6,""SurfaceFreq"":140.0,""SurfaceWaveform"":""Noise"",""SurfaceLowpassHz"":900.0,""SurfaceHighpassHz"":70.0,""SurfaceRumbleScale"":1.2,""RumbleStripPulseAmp"":0.0,""RumbleStripPulseMs"":120},
            ""TractionLoss"":{""Enabled"":true,""Gain"":0.08,""Sensitivity"":0.4,""Freq"":133.9,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":40.0,""Waveform"":""Noise""},
            ""GearShift"":{""Enabled"":true,""Gain"":0.4,""Freq"":40.0,""Waveform"":""Sine""},
            ""AbsClick"":{""Enabled"":false,""Gain"":0.15,""Freq"":150.0,""PulseFreq"":9.8,""DutyCycle"":0.33,""TickDurationMs"":35.0,""Mode"":""Pulse"",""Waveform"":""Square""},
            ""PitLimiter"":{""Enabled"":true,""Gain"":0.0832266361,""Freq"":50.49936,""PulseFreq"":4.340589,""DutyCycle"":0.483226657,""ActiveAmp"":0.3,""Waveform"":""Square""},
            ""Drs"":{""Enabled"":true,""Gain"":0.280409724,""ActivationFreq"":60.3841171,""ActivationMs"":80,""ActivationAmp"":0.5016645,""SustainedFreq"":120.371323,""SustainedAmp"":0.0481434,""Waveform"":""Square"",""SustainedWaveform"":""Sine""},
            ""Collision"":{""Enabled"":true,""Gain"":0.208867252,""Freq"":50.0,""EnvelopeMs"":120,""MinThreshold"":0.139180541,""MinAmp"":0.2,""MaxAmp"":0.85,""NormalizationScale"":2.0,""RefractoryMs"":250,""Waveform"":""Square""}
        }";

        // Wreckfest 2 baseline. Per project owner: use the same effect
        // settings as the AC default since they're a reasonable
        // cross-game starting point on a GPRO; only the CarOverrides
        // differ (Wreckfest has its own car-id namespace, so the AC
        // overrides don't transfer).
        private const string Wreckfest2Json = @"{
            ""MasterGain"":0.9995428,
            ""FfbScale"":0.8008723,
            ""FfbInvertSign"":true,
            ""FfbSmoothTimeConstantMs"":0.0,
            ""FfbSpikeTamingEnabled"":true,
            ""FfbSpikeMaxLsbPerMs"":2508.35864,
            ""FfbPeakSoftLimitLsb"":2061.90381,
            ""SkipFfbPassthrough"":false,
            ""DuckDepth"":0.5953513,
            ""DuckAttackMs"":5.0,
            ""DuckReleaseMs"":80.0,
            ""AudioCapture"":{""Enabled"":true,""Gain"":0.05952296,""LowpassCutoffHz"":567.0934,""HighpassCutoffHz"":35.20595},
            ""EnginePulse"":{""Enabled"":true,""Gain"":0.06518083,""Pitch"":1.00160933,""LowpassHz"":510.1833,""Waveform"":""Sine"",""ElectricMode"":""MutedHum"",""Layout"":""Auto"",""CustomEngineId"":"""",""CustomFiringPattern"":"""",""CustomFiringPatternName"":"""",""LoadLayerEnabled"":true,""LoadLayerGain"":0.8,""HighRpmBoostEnabled"":true,""HighRpmBoostAmount"":0.7,""Cylinders"":0,""EngineConfig"":""Auto"",""FiringOrderEnabled"":true},
            ""RoadBumps"":{""Enabled"":true,""Gain"":0.448169053,""Freq"":61.45767,""Waveform"":""Triangle"",""SurfaceEnabled"":true,""SurfaceGain"":0.69514066,""SurfaceFreq"":120.0,""SurfaceRumbleScale"":1.0,""SurfaceLowpassHz"":800.0,""SurfaceHighpassHz"":60.0,""SurfaceWaveform"":""Noise"",""RumbleStripPulseAmp"":0.0172855314,""RumbleStripPulseMs"":120},
            ""TractionLoss"":{""Enabled"":true,""Gain"":0.0387813151,""Sensitivity"":0.178701326,""Freq"":133.901657,""NoiseLowpassHz"":250.0,""NoiseHighpassHz"":40.9325,""Waveform"":""Noise""},
            ""GearShift"":{""Enabled"":true,""Gain"":0.396566778,""Freq"":34.6132431,""Waveform"":""Square""},
            ""AbsClick"":{""Enabled"":true,""Gain"":0.140768245,""Freq"":150.0,""PulseFreq"":9.821309,""DutyCycle"":0.331281453,""TickDurationMs"":35.0,""Mode"":""Pulse"",""Waveform"":""Square""},
            ""PitLimiter"":{""Enabled"":true,""Gain"":0.0832266361,""Freq"":50.49936,""PulseFreq"":4.340589,""DutyCycle"":0.483226657,""ActiveAmp"":0.3,""Waveform"":""Square""},
            ""Drs"":{""Enabled"":true,""Gain"":0.280409724,""ActivationFreq"":60.3841171,""ActivationMs"":80,""ActivationAmp"":0.5016645,""SustainedFreq"":120.371323,""SustainedAmp"":0.0481434,""Waveform"":""Square"",""SustainedWaveform"":""Sine""},
            ""Collision"":{""Enabled"":true,""Gain"":0.208867252,""Freq"":50.0,""EnvelopeMs"":120,""MinThreshold"":0.139180541,""MinAmp"":0.2,""MaxAmp"":0.85,""NormalizationScale"":2.0,""RefractoryMs"":250,""Waveform"":""Square""}
        }";
    }
}
