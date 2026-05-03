// Persisted plugin settings. SimHub serializes this to JSON via
// PluginManager.GetCommonSettings / SaveCommonSettings.
//
// Keep the fields simple (POCO with public getters/setters); avoid types
// that don't round-trip through JSON.NET cleanly.

namespace SimHubTrueforce.Plugin
{
    public sealed class TrueforceSettings
    {
        // Master gain into the wheel from the Mixer (after all sources are
        // summed). 1.0 = unity, matching what G HUB renders; the wheel-side
        // profile Trueforce intensity is the user's primary loudness control.
        public float MasterGain { get; set; } = 1.0f;

        public AudioCaptureSettings AudioCapture { get; set; } = new AudioCaptureSettings();
    }

    public sealed class AudioCaptureSettings
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 1.0f;  // multiplier on the captured stream
    }
}
