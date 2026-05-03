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
using SimHubTrueforce.Core;

namespace SimHubTrueforce.Plugin
{
    [PluginDescription("Logitech Trueforce haptics for any SimHub-supported game on G PRO / RS50 wheels.")]
    [PluginAuthor("Mhytee")]
    [PluginName("SimHub Trueforce")]
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
        private Thread _producerThread;
        private volatile bool _shuttingDown;

        // Capture-targeting state. Updated on every DataUpdate (cheap), checked
        // by the capture timer (1 Hz) which actually retargets.
        private volatile string _currentGameName;
        private Timer _captureTimer;
        private string _captureStatus = "Idle (no game running)";

        // Status surfaced to the SettingsControl.
        public string WheelStatus    { get; private set; } = "Not detected";
        public string StreamStatus   { get; private set; } = "Stopped";
        public string CaptureStatus  => _captureStatus;
        public int    ActiveVoiceCount => _mixer.Sources.Count;
        public AudioCaptureSource AudioCapture => _audio;

        public float MasterGain
        {
            get => _mixer.MasterGain;
            set { _mixer.MasterGain = value; if (Settings != null) Settings.MasterGain = value; }
        }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[Trueforce] Init: loading settings...");
            Settings = this.ReadCommonSettings("GeneralSettings", () => new TrueforceSettings());
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

                SimHub.Logging.Current.Info("[Trueforce] Sending init sequence (68 packets x 2)...");
                _device.RunInitSequence();
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

            // Audio capture: create the source, hook it into the mixer, but
            // defer the actual loopback Start() until we know which game's PID
            // to capture. The timer below polls for that and retargets on change.
            _audio = new AudioCaptureSource
            {
                Enabled = Settings.AudioCapture.Enabled,
                Gain    = Settings.AudioCapture.Gain,
            };
            _mixer.Sources.Add(_audio);
            _captureTimer = new Timer(CaptureTick, null, dueTime: 500, period: 1000);
            SimHub.Logging.Current.Info("[Trueforce] Audio capture armed; waiting for a supported game to start.");
        }

        public void End(PluginManager pluginManager)
        {
            _shuttingDown = true;

            try { _captureTimer?.Dispose(); } catch { }
            _captureTimer = null;

            // Pull settings back from the source so any UI changes persist.
            if (_audio != null && Settings != null)
            {
                Settings.AudioCapture.Enabled = _audio.Enabled;
                Settings.AudioCapture.Gain    = _audio.Gain;
            }
            if (Settings != null)
            {
                Settings.MasterGain = _mixer.MasterGain;
                this.SaveCommonSettings("GeneralSettings", Settings);
            }

            try { _audio?.Dispose(); } catch { }
            _audio = null;

            try { _producerThread?.Join(500); } catch { }
            try { _device?.ClearStream(); } catch { }
            // Brief pause so the centre-wheel samples drain to the device.
            Thread.Sleep(60);
            CleanupDevice();
            SimHub.Logging.Current.Info("[Trueforce] Plugin stopped.");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Cheap update: just track the current game name. The capture
            // timer (1 Hz) does the heavy lifting of resolving PIDs and
            // retargeting the loopback.
            _currentGameName = data?.GameRunning == true ? data.GameName : null;
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager) => new SettingsControl(this);

        // ---------- capture targeting ----------

        // Best-effort SimHub-name → exe-basename mapping. Process.GetProcessesByName
        // takes the basename without ".exe". Fall back to the GameName itself if
        // we don't have a hardcoded entry.
        private static readonly Dictionary<string, string[]> GameExeNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "AssettoCorsa",             new[] { "AssettoCorsa", "acs" } },
            { "AssettoCorsaCompetizione", new[] { "AC2-Win64-Shipping", "acc" } },
            { "iRacing",                  new[] { "iRacingSim64DX11", "iRacingSim", "iracing" } },
            { "RaceRoomRacingExperience", new[] { "RRRE64", "RRRE" } },
            { "F1_22",                    new[] { "F1_22", "F1_22_dx12" } },
            { "F1_23",                    new[] { "F1_23", "F1_23_dx12" } },
            { "AutomobilistaII",          new[] { "AMS2", "AMS2AVX" } },
        };

        private void CaptureTick(object _)
        {
            if (_shuttingDown || _audio == null) return;

            try
            {
                string gameName = _currentGameName;
                int pid = ResolveGamePid(gameName);

                if (pid == 0)
                {
                    if (_audio.CapturedProcessId != 0)
                    {
                        _audio.Stop();
                        SimHub.Logging.Current.Info("[Trueforce] Audio capture stopped (game exited).");
                    }
                    _captureStatus = string.IsNullOrEmpty(gameName)
                        ? "Idle (no game running)"
                        : $"Idle ({gameName} not found among running processes)";
                    return;
                }

                if (_audio.CapturedProcessId == pid) return; // already capturing this PID
                _audio.Start(pid);
                _captureStatus = $"Capturing {gameName} (PID {pid})";
                SimHub.Logging.Current.Info($"[Trueforce] {_captureStatus}.");
            }
            catch (Exception ex)
            {
                _captureStatus = $"Capture error: {ex.Message}";
                SimHub.Logging.Current.Error("[Trueforce] Capture retarget failed", ex);
            }
        }

        private static int ResolveGamePid(string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return 0;

            string[] candidates;
            if (!GameExeNames.TryGetValue(gameName, out candidates))
                candidates = new[] { gameName };

            foreach (string exe in candidates)
            {
                Process[] hits;
                try { hits = Process.GetProcessesByName(exe); } catch { continue; }
                if (hits.Length > 0)
                {
                    int pid = hits[0].Id;
                    foreach (var p in hits) p.Dispose();
                    return pid;
                }
            }
            return 0;
        }

        // ---------- producer ----------

        private void ProducerLoop()
        {
            float[] buf = new float[BatchSamples];
            while (!_shuttingDown)
            {
                _mixer.Render(buf, BatchSamples);
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
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }
}
