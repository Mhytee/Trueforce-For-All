// SimHub plugin owning the Trueforce HID session and the audio-haptic Mixer.
//
// Lifecycle:
//   Init: load settings → discover wheel → open + init + start stream →
//         create AudioCaptureSource (per-process loopback, retargeted on
//         game start/stop) and add it to the Mixer.
//   DataUpdate: track current game name / process for the capture timer.
//   End: save settings, stop producer + capture, clean up the device.
//
// The producer thread runs independently of the SimHub data tick because
// Trueforce wants 1 kHz samples; SimHub's data ticks vary by game (60-200 Hz
// typical) and would be too coarse to drive the stream directly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    [PluginDescription("Trueforce For All — Logitech Trueforce-compatible haptics for any SimHub-supported game. Reverse-engineered from the wire protocol; works on G PRO and RS50 wheels.")]
    [PluginAuthor("Mhytee")]
    [PluginName("Trueforce For All")]
    public sealed class TrueforcePlugin : IDataPlugin, IWPFSettingsV2
    {
        private const int BatchSamples = TrueforceDevice.NewPerPacket; // one packet's worth

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle => "Trueforce";
        public ImageSource PictureIcon => null;

        public TrueforceSettings Settings { get; private set; }

        private readonly Mixer _mixer = new Mixer();

        private TrueforceDevice _device;
        private AudioCaptureSource _audio;
        private HelperHost _helperHost;
        private UsbPcapFfbTap _ffbTap;
        private Thread _producerThread;
        private volatile bool _shuttingDown;

        public EnginePulseEffect  EnginePulse  { get; private set; }
        public RoadBumpsEffect    RoadBumps    { get; private set; }
        public TractionLossEffect TractionLoss { get; private set; }
        public GearShiftEffect    GearShift    { get; private set; }
        public AbsClickEffect     AbsClick     { get; private set; }
        private TelemetryEffect[] _effects;

        // Per-car override tracking. Updated on each DataUpdate; if the CarId
        // changes we re-apply per-section overrides (or fall back to globals).
        private string _activeCarId;
        public string ActiveCarId => _activeCarId;

        // Active game + active preset tracking. Presets are a named library
        // (Settings.Presets) that the user can apply to any game. GameDefaults
        // optionally binds a game to auto-load a specific preset on game change.
        // _activePresetName is the most-recently-applied preset (or null if
        // current settings are unsaved/manually-tuned).
        private string _activeGame;
        private string _activePresetName;
        public string ActiveGame        => _activeGame;
        public string ActivePresetName  => _activePresetName;

        public IEnumerable<string> PresetNames =>
            Settings?.Presets != null ? (IEnumerable<string>)Settings.Presets.Keys : Array.Empty<string>();

        /// <summary>Preset name bound as the auto-load default for the active
        /// game, or null if the active game has no default assigned.</summary>
        public string DefaultPresetForActiveGame
        {
            get
            {
                if (string.IsNullOrEmpty(_activeGame) || Settings?.GameDefaults == null) return null;
                Settings.GameDefaults.TryGetValue(_activeGame, out var p);
                return p;
            }
        }

        // Capture-targeting state. The poll thread (1 Hz) walks the process
        // table, decides which sim to capture, and tells HelperHost to retarget.
        private volatile string _currentGameName;
        private Thread _capturePollThread;
        private string _captureStatus = "Idle (no game running)";

        // Status surfaced to the SettingsControl.
        public string WheelStatus    { get; private set; } = "Not detected";
        public string StreamStatus   { get; private set; } = "Stopped";
        public string CaptureStatus  => _captureStatus;
        public string FfbTapStatus   => _ffbTap?.Status ?? "Not started";
        public int    ActiveVoiceCount => _mixer.Sources.Count;
        public AudioCaptureSource AudioCapture => _audio;

        public float MasterGain
        {
            get => _mixer.MasterGain;
            set { _mixer.MasterGain = value; if (Settings != null) Settings.MasterGain = value; }
        }

        public bool PluginEnabled => Settings?.PluginEnabled ?? true;

        /// <summary>Toggle the master enable. When disabled, sends the protocol
        /// Stop command so the wheel returns to its native FFB / Trueforce
        /// path (e.g. iRacing's own Trueforce takes over) and the producer
        /// loop skips rendering. When re-enabled, sends Start and resumes.
        /// If <paramref name="persistForActiveGame"/> is true and a game is
        /// detected, the choice is auto-remembered for that game.</summary>
        public void SetPluginEnabled(bool enabled, bool persistForActiveGame = true)
        {
            if (Settings == null) return;
            bool wasEnabled = Settings.PluginEnabled;
            Settings.PluginEnabled = enabled;

            if (persistForActiveGame && !string.IsNullOrEmpty(_activeGame))
            {
                Settings.GameEnabled[_activeGame] = enabled;
                this.SaveCommonSettings("GeneralSettings", Settings);
            }

            if (wasEnabled == enabled) return;
            if (!enabled)
            {
                _device?.SendStopCommand();
                _device?.Pause();
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Plugin disabled{(string.IsNullOrEmpty(_activeGame) ? "" : $" for '{_activeGame}'")}.");
            }
            else
            {
                _device?.Resume();
                _device?.SendStartCommand();
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Plugin enabled{(string.IsNullOrEmpty(_activeGame) ? "" : $" for '{_activeGame}'")}.");
            }
        }

        public void SetFfbScale(float v)
        {
            if (_device != null) _device.FfbScale = v;
            if (Settings != null) Settings.FfbScale = v;
        }

        public void SetFfbInvertSign(bool v)
        {
            if (_device != null) _device.FfbInvertSign = v;
            if (Settings != null) Settings.FfbInvertSign = v;
        }

        public void SetFfbSmoothMs(float v)
        {
            if (_device != null) _device.FfbSmoothTimeConstantMs = v;
            if (Settings != null) Settings.FfbSmoothTimeConstantMs = v;
        }

        /// <summary>Trigger an effect's test playback. Forces the device into
        /// active ep3 mode for the duration so the test is audible even when
        /// AC isn't running (no FFB tap data → would otherwise be keepalive).
        /// Drives effect.TestUpdate(phase) at ~60 Hz over the test window so
        /// effects can simulate dynamic behavior (RPM ramps, slip pulses, etc).</summary>
        public void TestEffect(TelemetryEffect effect)
        {
            if (effect == null)
            {
                SimHub.Logging.Current.Info("[Trueforce] TestEffect: effect was null");
                return;
            }
            if (_device == null)
            {
                SimHub.Logging.Current.Info($"[Trueforce] TestEffect '{effect.Name}': device not initialized");
                return;
            }
            int durationMs = effect.TestPlay();
            SimHub.Logging.Current.Info($"[Trueforce] TestEffect '{effect.Name}' duration={durationMs} ms");
            if (durationMs <= 0) return;

            _device.ForceActiveFor(durationMs + 200);

            long startTicks = DateTime.UtcNow.Ticks;
            long endTicks   = startTicks + durationMs * TimeSpan.TicksPerMillisecond;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    while (DateTime.UtcNow.Ticks < endTicks && !_shuttingDown)
                    {
                        long now = DateTime.UtcNow.Ticks;
                        double elapsedMs = (now - startTicks) / (double)TimeSpan.TicksPerMillisecond;
                        double phase = Math.Min(1.0, Math.Max(0, elapsedMs / durationMs));
                        try { effect.TestUpdate(phase); } catch { }
                        Thread.Sleep(16);  // ~60 Hz update rate
                    }
                }
                catch { }
            });
        }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[Trueforce] Init: loading settings...");
            Settings = this.ReadCommonSettings("GeneralSettings", () => new TrueforceSettings());
            // Defensive nulls in case a pre-2.x settings file was deserialized
            // without the new dictionaries.
            if (Settings.Presets      == null) Settings.Presets      = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            if (Settings.GameEnabled  == null) Settings.GameEnabled  = new Dictionary<string, bool>();
            MigrateLegacyGamePresets();
            _mixer.MasterGain = Settings.MasterGain;

            SimHub.Logging.Current.Info("[Trueforce] Discovering wheel...");
            var matches = WheelDiscovery.FindAll();
            if (matches.Count == 0)
            {
                WheelStatus = "Not detected (close G HUB and reload plugins)";
                SimHub.Logging.Current.Warn(
                    "[Trueforce] No supported wheel found. Is G HUB closed? " +
                    "Plug in a G PRO / RS50 and reload SimHub plugins.");
                return;
            }

            var match = matches[0];
            WheelStatus = $"{match.Model}  (VID 0x{match.Vid:X4}, PID 0x{match.Pid:X4})";
            SimHub.Logging.Current.Info($"[Trueforce] Found {WheelStatus}.");

            try
            {
                _device = new TrueforceDevice(match.Device);
                _device.Open();

                // Init sequence is required: empirically, skipping it leaves the
                // wheel in slower-default-rate mode and Trueforce response is
                // noticeably delayed (~game tick of latency). It does NOT cause
                // the FFB-suppression problem either way — diagnosed 2026-05-03.
                SimHub.Logging.Current.Info("[Trueforce] Sending init sequence (68 packets x 2)...");
                _device.RunInitSequence();

                // Spawn the USBPcap FFB tap. Reads AC's outgoing HID++ FFB target
                // off the bus and feeds it to TrueforceDevice so we can mirror it
                // into ep3 bytes 6-9 — without this, our ep3 stream overrides AC's
                // FFB with zero motor torque whenever Trueforce content plays.
                // No args = auto-discover via WheelUsbDiscovery; env vars override
                // for debugging or unusual setups.
                string ifaceOverride = Environment.GetEnvironmentVariable("SIMHUBTF_USBPCAP_INTERFACE");
                int.TryParse(Environment.GetEnvironmentVariable("SIMHUBTF_USBPCAP_DEVICE"), out var devOverride);
                _ffbTap = new UsbPcapFfbTap(ifaceOverride, devOverride)
                {
                    Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                };
                _device.FfbTargetProvider = () => _ffbTap?.TryGetFreshFfbTarget(_device.FfbTargetMaxAgeMs);
                _device.FfbScale                 = Settings.FfbScale;
                _device.FfbInvertSign            = Settings.FfbInvertSign;
                _device.FfbSmoothTimeConstantMs  = Settings.FfbSmoothTimeConstantMs;
                _ffbTap.Start();

                _device.StartStream();

                _producerThread = new Thread(ProducerLoop)
                {
                    IsBackground = true,
                    Name = "TrueforceProducer",
                    Priority = ThreadPriority.AboveNormal,
                };
                _producerThread.Start();

                StreamStatus = "Streaming (1 kHz, 250 packets/s)";
                SimHub.Logging.Current.Info("[Trueforce] Stream started.");
            }
            catch (Exception ex)
            {
                StreamStatus = $"Init failed: {ex.Message}";
                SimHub.Logging.Current.Error("[Trueforce] Init failed", ex);
                CleanupDevice();
                return;
            }

            // Spawn the loopback helper child process. It does the actual
            // per-process WASAPI loopback in modern .NET (where COM interop is
            // reliable), and streams audio bytes back to us over stdout.
            try
            {
                string pluginDir = System.IO.Path.GetDirectoryName(typeof(TrueforcePlugin).Assembly.Location);
                string helperExe = System.IO.Path.Combine(pluginDir, "TrueforceForAll.LoopbackHelper.exe");
                _helperHost = new HelperHost(helperExe);
                _helperHost.Spawn();
                SimHub.Logging.Current.Info($"[Trueforce] Loopback helper spawned ({helperExe}).");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[Trueforce] Failed to spawn loopback helper", ex);
                _helperHost = null;
            }

            // Audio capture: create the source, attach it to the helper, hook
            // it into the mixer. Capture stays inactive (silent) until the poll
            // thread detects a sim and tells the host to retarget.
            _audio = new AudioCaptureSource
            {
                Enabled = Settings.AudioCapture.Enabled,
                Gain    = Settings.AudioCapture.Gain,
            };
            if (_helperHost != null) _audio.Attach(_helperHost);
            _mixer.Sources.Add(_audio);

            // Telemetry effects: instantiate from settings, register in the
            // mixer in display order. Each effect is fed via DataUpdate dispatch.
            EnginePulse  = new EnginePulseEffect();
            RoadBumps    = new RoadBumpsEffect();
            TractionLoss = new TractionLossEffect();
            GearShift    = new GearShiftEffect();
            AbsClick     = new AbsClickEffect();
            _effects = new TelemetryEffect[] { EnginePulse, RoadBumps, TractionLoss, GearShift, AbsClick };
            foreach (var fx in _effects) _mixer.Sources.Add(fx);
            // Pull initial values from globals (no car detected yet).
            ApplyActiveCarOverride();

            _capturePollThread = new Thread(CapturePollLoop)
            {
                IsBackground = true,
                Name = "TrueforceCapturePoll",
            };
            _capturePollThread.Start();
            SimHub.Logging.Current.Info("[Trueforce] Audio capture armed; waiting for a supported game to start.");
        }

        public void End(PluginManager pluginManager)
        {
            _shuttingDown = true;

            try { _capturePollThread?.Join(2000); } catch { }
            _capturePollThread = null;

            // UI changes are written through to Settings on the fly, so just save.
            if (Settings != null) this.SaveCommonSettings("GeneralSettings", Settings);

            try { _audio?.Dispose(); } catch { }
            _audio = null;

            try { _helperHost?.Dispose(); } catch { }
            _helperHost = null;

            try { _capturedProcess?.Dispose(); } catch { }
            _capturedProcess = null;

            try { _producerThread?.Join(500); } catch { }
            try { _device?.ClearStream(); } catch { }
            // Brief pause so the centre-wheel samples drain to the device.
            Thread.Sleep(60);
            CleanupDevice();
            SimHub.Logging.Current.Info("[Trueforce] Plugin stopped.");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            _currentGameName = data?.GameRunning == true ? data.GameName : null;

            // Track game changes and auto-apply that game's default preset
            // (if one is bound in GameDefaults). Done before per-car override
            // so the loaded preset's CarOverrides dict is in place by the
            // time ApplyActiveCarOverride runs below.
            string gameName = data?.GameName;
            if (gameName != _activeGame)
            {
                _activeGame = gameName;
                if (!string.IsNullOrEmpty(gameName) && Settings?.GameDefaults != null
                    && Settings.GameDefaults.TryGetValue(gameName, out var presetName)
                    && !string.IsNullOrEmpty(presetName)
                    && Settings.Presets != null
                    && Settings.Presets.TryGetValue(presetName, out var snap) && snap != null)
                {
                    ApplyGamePreset(snap);
                    _activePresetName = presetName;
                    SimHub.Logging.Current.Info($"[Trueforce] Loaded preset '{presetName}' as default for '{gameName}'.");
                }

                // Per-game master enable: default true for unseen games, else
                // honor the saved choice. Don't persist here — game-change
                // shouldn't write back the same value we just read.
                if (Settings != null)
                {
                    bool wantEnabled = true;
                    if (!string.IsNullOrEmpty(gameName) && Settings.GameEnabled != null
                        && Settings.GameEnabled.TryGetValue(gameName, out var saved))
                        wantEnabled = saved;
                    if (Settings.PluginEnabled != wantEnabled)
                        SetPluginEnabled(wantEnabled, persistForActiveGame: false);
                }
            }

            // Track car changes and apply per-car override (or revert).
            string carId = data?.NewData?.CarId ?? data?.NewData?.CarModel;
            if (carId != _activeCarId)
            {
                _activeCarId = carId;
                ApplyActiveCarOverride();
            }

            // Forward throttle normalized to AudioCaptureSource so it can apply
            // a throttle-driven gain boost (mirrors EnginePulseEffect's boost).
            if (_audio != null && data?.NewData != null)
                _audio.ThrottleNormalized = (float)Math.Max(0.0, Math.Min(1.0, data.NewData.Throttle / 100.0));

            // Forward telemetry to each effect. Effects are responsible for
            // their own state; if any throws we swallow it so a single bad
            // effect can't take down the SimHub data tick.
            if (_effects != null)
            {
                for (int i = 0; i < _effects.Length; i++)
                {
                    try { _effects[i].OnTelemetry(data); }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Error($"[Trueforce] {_effects[i].Name} telemetry error", ex);
                    }
                }
            }
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager) => new SettingsControl(this);

        // ---------- per-car overrides (per-section) ----------

        public CarOverride GetActiveCarOverride()
        {
            if (string.IsNullOrEmpty(_activeCarId) || Settings == null) return null;
            Settings.CarOverrides.TryGetValue(_activeCarId, out var o);
            return o;
        }

        /// <summary>Apply per-section overrides for the active car (or revert to globals).</summary>
        public void ApplyActiveCarOverride()
        {
            if (Settings == null) return;
            var ovr = GetActiveCarOverride();

            ApplyEngineSettings(ovr?.EnginePulse  ?? Settings.EnginePulse);
            ApplyBumpsSettings (ovr?.RoadBumps    ?? Settings.RoadBumps);
            ApplyTractionSettings(ovr?.TractionLoss ?? Settings.TractionLoss);
            ApplyShiftSettings (ovr?.GearShift    ?? Settings.GearShift);
            ApplyAbsSettings   (ovr?.AbsClick     ?? Settings.AbsClick);
        }

        // ----- per-section: is this section overridden for the active car? -----
        public bool IsEngineOverridden  => GetActiveCarOverride()?.EnginePulse  != null;
        public bool IsBumpsOverridden   => GetActiveCarOverride()?.RoadBumps    != null;
        public bool IsTractionOverridden=> GetActiveCarOverride()?.TractionLoss != null;
        public bool IsShiftOverridden   => GetActiveCarOverride()?.GearShift    != null;
        public bool IsAbsOverridden     => GetActiveCarOverride()?.AbsClick     != null;

        // ----- per-section: toggle override on/off (snapshots globals when on) -----
        public void SetEngineOverride(bool on)   => ToggleSectionOverride(on, get: o => o.EnginePulse,  set: (o, v) => o.EnginePulse  = v, snapshot: () => Clone(Settings.EnginePulse));
        public void SetBumpsOverride(bool on)    => ToggleSectionOverride(on, get: o => o.RoadBumps,    set: (o, v) => o.RoadBumps    = v, snapshot: () => Clone(Settings.RoadBumps));
        public void SetTractionOverride(bool on) => ToggleSectionOverride(on, get: o => o.TractionLoss, set: (o, v) => o.TractionLoss = v, snapshot: () => Clone(Settings.TractionLoss));
        public void SetShiftOverride(bool on)    => ToggleSectionOverride(on, get: o => o.GearShift,    set: (o, v) => o.GearShift    = v, snapshot: () => Clone(Settings.GearShift));
        public void SetAbsOverride(bool on)      => ToggleSectionOverride(on, get: o => o.AbsClick,     set: (o, v) => o.AbsClick     = v, snapshot: () => Clone(Settings.AbsClick));

        private void ToggleSectionOverride<T>(bool on,
            Func<CarOverride, T> get,
            Action<CarOverride, T> set,
            Func<T> snapshot) where T : class
        {
            if (string.IsNullOrEmpty(_activeCarId) || Settings == null) return;
            if (!Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr))
            {
                if (!on) return;     // toggling off when none exists is a no-op
                ovr = new CarOverride();
                Settings.CarOverrides[_activeCarId] = ovr;
            }
            set(ovr, on ? snapshot() : null);
            if (ovr.IsEmpty) Settings.CarOverrides.Remove(_activeCarId);
            ApplyActiveCarOverride();
        }

        // ----- write helpers used by the UI sliders -----
        // Each routes to the per-car section if it's overridden, else to the global section.
        public EnginePulseSettings  ActiveEngine   => GetActiveCarOverride()?.EnginePulse  ?? Settings.EnginePulse;
        public RoadBumpsSettings    ActiveBumps    => GetActiveCarOverride()?.RoadBumps    ?? Settings.RoadBumps;
        public TractionLossSettings ActiveTraction => GetActiveCarOverride()?.TractionLoss ?? Settings.TractionLoss;
        public GearShiftSettings    ActiveShift    => GetActiveCarOverride()?.GearShift    ?? Settings.GearShift;
        public AbsClickSettings     ActiveAbs      => GetActiveCarOverride()?.AbsClick     ?? Settings.AbsClick;

        // ----- apply settings to live effect -----
        private void ApplyEngineSettings(EnginePulseSettings s)
        {
            if (EnginePulse == null || s == null) return;
            EnginePulse.Enabled         = s.Enabled;
            EnginePulse.Gain            = s.Gain;
            EnginePulse.Cylinders       = s.Cylinders;
            EnginePulse.PitchMultiplier = s.Pitch;
            EnginePulse.LowpassHz       = s.LowpassHz;
            EnginePulse.Waveform        = s.Waveform;
        }
        private void ApplyBumpsSettings(RoadBumpsSettings s)
        {
            if (RoadBumps == null || s == null) return;
            RoadBumps.Enabled  = s.Enabled;
            RoadBumps.Gain     = s.Gain;
            RoadBumps.Waveform = s.Waveform;
            RoadBumps.Freq     = s.Freq;
        }
        private void ApplyTractionSettings(TractionLossSettings s)
        {
            if (TractionLoss == null || s == null) return;
            TractionLoss.Enabled     = s.Enabled;
            TractionLoss.Gain        = s.Gain;
            TractionLoss.Sensitivity = s.Sensitivity;
            TractionLoss.Waveform    = s.Waveform;
            TractionLoss.Freq        = s.Freq;
        }
        private void ApplyShiftSettings(GearShiftSettings s)
        {
            if (GearShift == null || s == null) return;
            GearShift.Enabled  = s.Enabled;
            GearShift.Gain     = s.Gain;
            GearShift.Freq     = s.Freq;
            GearShift.Waveform = s.Waveform;
        }
        private void ApplyAbsSettings(AbsClickSettings s)
        {
            if (AbsClick == null || s == null) return;
            AbsClick.Enabled        = s.Enabled;
            AbsClick.Gain           = s.Gain;
            AbsClick.Freq           = s.Freq;
            AbsClick.PulseFreq      = s.PulseFreq;
            AbsClick.DutyCycle      = s.DutyCycle;
            AbsClick.TickDurationMs = s.TickDurationMs;
            AbsClick.Mode           = s.Mode;
            AbsClick.Waveform       = s.Waveform;
        }

        // ----- shallow clones used when toggling override on -----
        private static EnginePulseSettings  Clone(EnginePulseSettings s)
            => new EnginePulseSettings  { Enabled = s.Enabled, Gain = s.Gain, Cylinders = s.Cylinders, Pitch = s.Pitch, LowpassHz = s.LowpassHz, Waveform = s.Waveform };
        private static RoadBumpsSettings    Clone(RoadBumpsSettings s)
            => new RoadBumpsSettings    { Enabled = s.Enabled, Gain = s.Gain, Waveform = s.Waveform, Freq = s.Freq };
        private static TractionLossSettings Clone(TractionLossSettings s)
            => new TractionLossSettings { Enabled = s.Enabled, Gain = s.Gain, Sensitivity = s.Sensitivity, Waveform = s.Waveform, Freq = s.Freq };
        private static GearShiftSettings    Clone(GearShiftSettings s)
            => new GearShiftSettings    { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, Waveform = s.Waveform };
        private static AbsClickSettings     Clone(AbsClickSettings s)
            => new AbsClickSettings     { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, PulseFreq = s.PulseFreq, DutyCycle = s.DutyCycle, TickDurationMs = s.TickDurationMs, Mode = s.Mode, Waveform = s.Waveform };

        // ---------- preset library ----------

        /// <summary>One-time migration of legacy per-game presets (keyed by
        /// game name with no separate "preset library" concept) into the new
        /// model: each becomes a preset named after the game, and the game's
        /// default is bound to it. Idempotent — runs once when GamePresets is
        /// non-empty and the new fields are still empty.</summary>
        private void MigrateLegacyGamePresets()
        {
            if (Settings?.GamePresets == null || Settings.GamePresets.Count == 0) return;
            if (Settings.Presets == null) Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            int moved = 0;
            foreach (var kv in Settings.GamePresets)
            {
                if (kv.Value == null) continue;
                if (!Settings.Presets.ContainsKey(kv.Key))
                    Settings.Presets[kv.Key] = kv.Value;
                if (!Settings.GameDefaults.ContainsKey(kv.Key))
                    Settings.GameDefaults[kv.Key] = kv.Key;
                moved++;
            }
            Settings.GamePresets.Clear();
            if (moved > 0)
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] Migrated {moved} legacy game-preset(s) to named library.");
            }
        }

        /// <summary>Apply a named preset from the library. Sets it as the
        /// currently-active preset. No game default is changed.</summary>
        /// <returns>true if applied; false if the preset name doesn't exist.</returns>
        public bool ApplyPreset(string presetName)
        {
            if (Settings?.Presets == null || string.IsNullOrEmpty(presetName)) return false;
            if (!Settings.Presets.TryGetValue(presetName, out var snap) || snap == null) return false;
            ApplyGamePreset(snap);
            _activePresetName = presetName;
            SimHub.Logging.Current.Info($"[Trueforce] Applied preset '{presetName}'.");
            return true;
        }

        /// <summary>Snapshot the current settings into the library under the
        /// given name. Overwrites any existing preset with that name. Sets it
        /// as the active preset.</summary>
        public void SavePresetAs(string presetName)
        {
            if (Settings == null || string.IsNullOrEmpty(presetName)) return;
            if (Settings.Presets == null) Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
            Settings.Presets[presetName] = SnapshotCurrentAsPreset();
            _activePresetName = presetName;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Saved preset '{presetName}'.");
        }

        /// <summary>Delete a preset from the library. Also clears any
        /// GameDefaults entries that pointed to this preset.</summary>
        public bool DeletePreset(string presetName)
        {
            if (Settings?.Presets == null || string.IsNullOrEmpty(presetName)) return false;
            if (!Settings.Presets.Remove(presetName)) return false;

            // Drop any game defaults that pointed to this preset.
            if (Settings.GameDefaults != null)
            {
                var orphans = new List<string>();
                foreach (var kv in Settings.GameDefaults)
                    if (kv.Value == presetName) orphans.Add(kv.Key);
                foreach (var k in orphans) Settings.GameDefaults.Remove(k);
            }

            if (_activePresetName == presetName) _activePresetName = null;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Deleted preset '{presetName}'.");
            return true;
        }

        /// <summary>Bind a preset as the auto-load default for the active game.
        /// Subsequent game changes into this game will apply the preset.</summary>
        public void SetDefaultPresetForActiveGame(string presetName)
        {
            if (Settings == null || string.IsNullOrEmpty(_activeGame) || string.IsNullOrEmpty(presetName)) return;
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            if (Settings.Presets == null || !Settings.Presets.ContainsKey(presetName)) return;
            Settings.GameDefaults[_activeGame] = presetName;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] '{presetName}' set as default for '{_activeGame}'.");
        }

        /// <summary>Remove the auto-load binding for the active game.</summary>
        public void ClearDefaultPresetForActiveGame()
        {
            if (Settings?.GameDefaults == null || string.IsNullOrEmpty(_activeGame)) return;
            if (Settings.GameDefaults.Remove(_activeGame))
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] Cleared default preset for '{_activeGame}'.");
            }
        }

        /// <summary>Copy snapshot fields into Settings and re-push to live components.</summary>
        private void ApplyGamePreset(GameSettingsSnapshot snap)
        {
            if (snap == null || Settings == null) return;

            Settings.MasterGain              = snap.MasterGain;
            Settings.FfbScale                = snap.FfbScale;
            Settings.FfbInvertSign           = snap.FfbInvertSign;
            Settings.FfbSmoothTimeConstantMs = snap.FfbSmoothTimeConstantMs;
            Settings.DuckDepth               = snap.DuckDepth;
            Settings.DuckAttackMs            = snap.DuckAttackMs;
            Settings.DuckReleaseMs           = snap.DuckReleaseMs;

            if (snap.AudioCapture != null) Settings.AudioCapture = CloneOrNull(snap.AudioCapture);
            if (snap.EnginePulse  != null) Settings.EnginePulse  = Clone(snap.EnginePulse);
            if (snap.RoadBumps    != null) Settings.RoadBumps    = Clone(snap.RoadBumps);
            if (snap.TractionLoss != null) Settings.TractionLoss = Clone(snap.TractionLoss);
            if (snap.GearShift    != null) Settings.GearShift    = Clone(snap.GearShift);
            if (snap.AbsClick     != null) Settings.AbsClick     = Clone(snap.AbsClick);
            if (snap.CarOverrides != null) Settings.CarOverrides = CloneOverrides(snap.CarOverrides);

            // Push live: master, FFB tap, audio, and effects (via car-override apply).
            _mixer.MasterGain = Settings.MasterGain;
            if (_device != null)
            {
                _device.FfbScale                = Settings.FfbScale;
                _device.FfbInvertSign           = Settings.FfbInvertSign;
                _device.FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs;
            }
            if (_audio != null)
            {
                _audio.Enabled          = Settings.AudioCapture.Enabled;
                _audio.Gain             = Settings.AudioCapture.Gain;
                _audio.LowpassCutoffHz  = Settings.AudioCapture.LowpassCutoffHz;
                _audio.HighpassCutoffHz = Settings.AudioCapture.HighpassCutoffHz;
            }
            ApplyActiveCarOverride();
        }

        private static AudioCaptureSettings CloneOrNull(AudioCaptureSettings s)
            => s == null ? null : new AudioCaptureSettings { Enabled = s.Enabled, Gain = s.Gain, LowpassCutoffHz = s.LowpassCutoffHz, HighpassCutoffHz = s.HighpassCutoffHz };

        private static Dictionary<string, CarOverride> CloneOverrides(Dictionary<string, CarOverride> src)
        {
            if (src == null) return new Dictionary<string, CarOverride>();
            var d = new Dictionary<string, CarOverride>(src.Count);
            foreach (var kv in src)
            {
                var o = kv.Value;
                if (o == null) continue;
                d[kv.Key] = new CarOverride
                {
                    EnginePulse  = o.EnginePulse  == null ? null : Clone(o.EnginePulse),
                    RoadBumps    = o.RoadBumps    == null ? null : Clone(o.RoadBumps),
                    TractionLoss = o.TractionLoss == null ? null : Clone(o.TractionLoss),
                    GearShift    = o.GearShift    == null ? null : Clone(o.GearShift),
                    AbsClick     = o.AbsClick     == null ? null : Clone(o.AbsClick),
                };
            }
            return d;
        }

        // ---------- single-preset export/import (sharing) ----------

        /// <summary>Snapshot of the current top-level settings + per-car overrides
        /// — used by both "Save preset for game" and "Export game preset", so the
        /// exported file always reflects what the user is hearing right now.</summary>
        private GameSettingsSnapshot SnapshotCurrentAsPreset()
        {
            return new GameSettingsSnapshot
            {
                MasterGain              = Settings.MasterGain,
                FfbScale                = Settings.FfbScale,
                FfbInvertSign           = Settings.FfbInvertSign,
                FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs,
                DuckDepth               = Settings.DuckDepth,
                DuckAttackMs            = Settings.DuckAttackMs,
                DuckReleaseMs           = Settings.DuckReleaseMs,
                AudioCapture            = CloneOrNull(Settings.AudioCapture),
                EnginePulse             = Clone(Settings.EnginePulse),
                RoadBumps               = Clone(Settings.RoadBumps),
                TractionLoss            = Clone(Settings.TractionLoss),
                GearShift               = Clone(Settings.GearShift),
                AbsClick                = Clone(Settings.AbsClick),
                CarOverrides            = CloneOverrides(Settings.CarOverrides),
            };
        }

        /// <summary>Write a named preset (or the current settings if the name
        /// doesn't exist in the library yet) to a shareable JSON file. The
        /// file carries the preset name but no game binding.</summary>
        public void ExportPreset(string presetName, string path)
        {
            if (Settings == null || string.IsNullOrEmpty(presetName)) return;
            GameSettingsSnapshot snap;
            if (Settings.Presets == null || !Settings.Presets.TryGetValue(presetName, out snap) || snap == null)
                snap = SnapshotCurrentAsPreset();
            var file = new PresetFile { PresetName = presetName, Snapshot = snap };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported preset '{presetName}' to {path}.");
        }

        /// <summary>Read a preset file and store it in the library under the
        /// name embedded in the file. Does NOT auto-apply or auto-bind to a
        /// game — the user explicitly chooses what to do with it next.</summary>
        /// <returns>The preset name imported (for UI feedback).</returns>
        public string ImportPreset(string path)
        {
            if (Settings == null) return null;
            string json = System.IO.File.ReadAllText(path);
            var file = Newtonsoft.Json.JsonConvert.DeserializeObject<PresetFile>(json);
            if (file == null || file.Snapshot == null || string.IsNullOrEmpty(file.PresetName))
                throw new System.IO.InvalidDataException("Not a valid Trueforce preset file.");
            if (file.Type != PresetFile.FileType)
                throw new System.IO.InvalidDataException($"Wrong file type '{file.Type}'. Expected '{PresetFile.FileType}'.");

            if (Settings.Presets == null) Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
            Settings.Presets[file.PresetName] = file.Snapshot;
            this.SaveCommonSettings("GeneralSettings", Settings);

            SimHub.Logging.Current.Info($"[Trueforce] Imported preset '{file.PresetName}' from {path}.");
            return file.PresetName;
        }

        /// <summary>Export the active car's override as a standalone file.
        /// If no override exists yet, captures the current ActiveX section
        /// values so the user can share their tuning without committing it
        /// to a per-car override first.</summary>
        public void ExportActiveCarPreset(string path)
        {
            if (Settings == null || string.IsNullOrEmpty(_activeCarId)) return;
            CarOverride ovr = GetActiveCarOverride();
            if (ovr == null || ovr.IsEmpty)
            {
                // Build a full override from the current active sections so the
                // exported file is self-contained even if the user hasn't
                // toggled "Override for this car" yet.
                ovr = new CarOverride
                {
                    EnginePulse  = Clone(ActiveEngine),
                    RoadBumps    = Clone(ActiveBumps),
                    TractionLoss = Clone(ActiveTraction),
                    GearShift    = Clone(ActiveShift),
                    AbsClick     = Clone(ActiveAbs),
                };
            }
            var file = new CarPresetFile { GameName = _activeGame, CarId = _activeCarId, Override = ovr };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported car preset for '{_activeCarId}' to {path}.");
        }

        /// <summary>Read a car-preset file and store under its CarId in
        /// CarOverrides. If the imported CarId matches the active car, the
        /// override is applied immediately.</summary>
        /// <returns>The car id from the imported file (for UI feedback).</returns>
        public string ImportCarPreset(string path)
        {
            if (Settings == null) return null;
            string json = System.IO.File.ReadAllText(path);
            var file = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(json);
            if (file == null || file.Override == null || string.IsNullOrEmpty(file.CarId))
                throw new System.IO.InvalidDataException("Not a valid Trueforce car-preset file.");
            if (file.Type != CarPresetFile.FileType)
                throw new System.IO.InvalidDataException($"Wrong file type '{file.Type}'. Expected '{CarPresetFile.FileType}'.");

            Settings.CarOverrides[file.CarId] = file.Override;
            this.SaveCommonSettings("GeneralSettings", Settings);

            if (file.CarId == _activeCarId) ApplyActiveCarOverride();
            SimHub.Logging.Current.Info($"[Trueforce] Imported car preset for '{file.CarId}' from {path}.");
            return file.CarId;
        }

        // ---------- export / import ----------

        public void ExportSettings(string path)
        {
            if (Settings == null) return;
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(Settings, Newtonsoft.Json.Formatting.Indented);
            System.IO.File.WriteAllText(path, json);
            SimHub.Logging.Current.Info($"[Trueforce] Settings exported to {path}.");
        }

        /// <summary>Replace settings from a JSON file; live effects are re-derived from the new settings.</summary>
        public void ImportSettings(string path)
        {
            string json = System.IO.File.ReadAllText(path);
            var imported = Newtonsoft.Json.JsonConvert.DeserializeObject<TrueforceSettings>(json);
            if (imported == null) throw new System.IO.InvalidDataException("File did not contain valid TrueforceSettings JSON.");
            Settings = imported;
            _mixer.MasterGain = Settings.MasterGain;
            if (_audio != null)
            {
                _audio.Enabled          = Settings.AudioCapture.Enabled;
                _audio.Gain             = Settings.AudioCapture.Gain;
                _audio.LowpassCutoffHz  = Settings.AudioCapture.LowpassCutoffHz;
                _audio.HighpassCutoffHz = Settings.AudioCapture.HighpassCutoffHz;
            }
            ApplyActiveCarOverride();
            SimHub.Logging.Current.Info($"[Trueforce] Settings imported from {path}.");
        }

        // ---------- capture targeting ----------

        // exe-basename → friendly label. Process names from Process.GetProcesses()
        // are the basename (no ".exe"), case-insensitive on Windows.
        private static readonly Dictionary<string, string> ExeLabels = BuildExeLabels(new Dictionary<string, string[]>
        {
            { "AssettoCorsa",             new[] { "AssettoCorsa", "acs" } },
            { "AssettoCorsaCompetizione", new[] { "AC2-Win64-Shipping", "acc" } },
            { "iRacing",                  new[] { "iRacingSim64DX11", "iRacingSim", "iracing" } },
            { "RaceRoomRacingExperience", new[] { "RRRE64", "RRRE" } },
            { "F1_22",                    new[] { "F1_22", "F1_22_dx12" } },
            { "F1_23",                    new[] { "F1_23", "F1_23_dx12" } },
            { "AutomobilistaII",          new[] { "AMS2", "AMS2AVX" } },
        });

        private static Dictionary<string, string> BuildExeLabels(Dictionary<string, string[]> games)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in games)
                foreach (var exe in kv.Value)
                    d[exe] = kv.Key;
            return d;
        }

        // The Process handle for the game we're currently capturing (or null).
        // We hold this so per-tick alive-checks use HasExited (cheap, uses the
        // existing handle) instead of re-walking the process table.
        private Process _capturedProcess;

        private void CapturePollLoop()
        {
            // Initial settle delay so plugin Init can finish on the SimHub side
            // before we start hammering the process table.
            Thread.Sleep(500);
            while (!_shuttingDown)
            {
                CaptureTick();
                // 1 Hz polling. Sleep in small slices so shutdown is responsive.
                for (int i = 0; i < 10 && !_shuttingDown; i++)
                    Thread.Sleep(100);
            }
        }

        private void CaptureTick()
        {
            if (_shuttingDown || _audio == null) return;

            try
            {
                // Fast path: if we already have a captured process, just check
                // whether it's still alive. No process-table scan.
                if (_capturedProcess != null)
                {
                    bool stillAlive = false;
                    try { stillAlive = !_capturedProcess.HasExited; } catch { /* invalid handle */ }
                    if (stillAlive) return;

                    // Process exited — tear down and fall through to the scan.
                    SimHub.Logging.Current.Info($"[Trueforce] Captured process {_capturedProcess.Id} exited; releasing.");
                    try { _capturedProcess.Dispose(); } catch { }
                    _capturedProcess = null;
                    _audio.Stop();
                    _helperHost?.SetTargetPid(0);
                }

                // Scan path: walk the process table once and match against our
                // pre-built basename lookup. Disposes every Process we don't keep
                // so handles aren't leaked.
                Process keep = null;
                string label = null;
                Process[] all;
                try { all = Process.GetProcesses(); }
                catch { all = Array.Empty<Process>(); }

                foreach (var p in all)
                {
                    if (keep == null && ExeLabels.TryGetValue(p.ProcessName, out string lbl))
                    {
                        keep = p;
                        label = lbl;
                    }
                    else
                    {
                        p.Dispose();
                    }
                }

                if (keep == null)
                {
                    _captureStatus = "Idle (no supported game running)";
                    return;
                }

                _capturedProcess = keep;
                _audio.Start(keep.Id);
                _helperHost?.SetTargetPid(keep.Id);
                _captureStatus = $"Capturing {label} (PID {keep.Id})";
                SimHub.Logging.Current.Info($"[Trueforce] {_captureStatus}.");
            }
            catch (Exception ex)
            {
                _captureStatus = $"Capture error: {ex.Message}";
                SimHub.Logging.Current.Error("[Trueforce] Capture retarget failed", ex);
            }
        }
        // ---------- producer ----------

        // Float-space silence floor. Samples with |v| < this are zeroed so the
        // u16 conversion produces exactly 0x8000 — TrueforceDevice's silence
        // detection requires exact-center samples to choose the keepalive packet
        // shape. ~3e-4 corresponds to ±10 LSB out of 32767, well below any
        // perceptible content but above floating-point noise.
        private const float SilenceFloor = 3e-4f;

        // Sidechain ducking state. Smoothed envelope tracks (1 - depth × activity).
        private float _duckSmoothed = 1.0f;

        private void UpdateDucking()
        {
            // Take the loudest transient effect's activity level.
            double maxTransient = 0;
            if (RoadBumps    != null) maxTransient = Math.Max(maxTransient, RoadBumps.ActivityLevel);
            if (TractionLoss != null) maxTransient = Math.Max(maxTransient, TractionLoss.ActivityLevel);
            if (GearShift    != null) maxTransient = Math.Max(maxTransient, GearShift.ActivityLevel);
            if (AbsClick     != null) maxTransient = Math.Max(maxTransient, AbsClick.ActivityLevel);

            float depth     = Settings?.DuckDepth     ?? 0.5f;
            float attackMs  = Settings?.DuckAttackMs  ?? 5.0f;
            float releaseMs = Settings?.DuckReleaseMs ?? 80.0f;

            float target = (float)Math.Max(0.0, 1.0 - depth * maxTransient);

            // IIR with attack-or-release time constant (dt ≈ 1 ms — producer
            // pushes ~1 batch per ms). alpha = 1 - exp(-dt/tau).
            float tauMs = (target < _duckSmoothed) ? attackMs : releaseMs;
            float alpha = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tauMs)));
            _duckSmoothed = _duckSmoothed * (1f - alpha) + target * alpha;

            if (EnginePulse != null) EnginePulse.DuckMultiplier = _duckSmoothed;
            if (_audio      != null) _audio.DuckMultiplier      = _duckSmoothed;
        }

        private void ProducerLoop()
        {
            float[] buf = new float[BatchSamples];

            while (!_shuttingDown)
            {
                // Master disable: skip rendering entirely. The wheel was told
                // to Stop in SetPluginEnabled, so it's running on native FFB.
                // Sleep ~the duration of one batch (4 samples × 0.25 ms) before
                // re-checking, to avoid a hot spin.
                if (Settings != null && !Settings.PluginEnabled)
                {
                    Thread.Sleep(20);
                    continue;
                }

                UpdateDucking();
                _mixer.Render(buf, BatchSamples);
                for (int i = 0; i < BatchSamples; i++)
                {
                    float v = buf[i];
                    if (v < SilenceFloor && v > -SilenceFloor) buf[i] = 0f;
                }
                try
                {
                    _device?.PushFloats(buf, BatchSamples);
                }
                catch
                {
                    break;
                }
            }
        }

        private void CleanupDevice()
        {
            try { _ffbTap?.Dispose(); } catch { }
            _ffbTap = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }
}
