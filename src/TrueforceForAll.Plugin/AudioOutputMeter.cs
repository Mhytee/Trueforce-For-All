// How loud the computer's own audio output is right now, so the wheel's rim
// LEDs can be driven as a level meter.
//
// Deliberately a METER, not a capture. Windows keeps a running peak for every
// audio endpoint (IAudioMeterInformation) and handing it over costs one COM
// call, so nothing here is recorded, decoded or mixed. That matters twice: it
// is cheap enough to poll at 60 Hz on a background thread, and it opens no
// loopback stream, so it cannot contend with the per-process capture the
// haptic path already runs through the helper exe.
//
// The reading is a LINEAR peak (0..1), which is not what a meter should show:
// speech and music spend most of their time in the bottom tenth of that range.
// Turning it into a bar that uses the bar is AudioLevelEnvelope's job, and the
// two simpler mappings that failed before it are recorded there. This class is
// just the endpoint plumbing: find the default output, read its peak, hand it
// over, publish the answer.

using System;
using System.Threading;
using NAudio.CoreAudioApi;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    public sealed class AudioOutputMeter : IDisposable
    {
        private readonly Action<string> _log;

        // 60 Hz. The strip has ten steps at most, so nothing finer is visible;
        // this is about the meter feeling ATTACHED to the sound, and anything
        // much slower than 50 Hz reads as lag.
        private const int PollMs = 16;
        // Attack instant, release 100 ms from full scale to dark. A level meter
        // has to jump onto a transient and then fall away; a symmetric filter
        // smears every drum hit into the next one.
        //
        // Owner's call, asked for twice and set near the fast end of what the
        // rate can still mean: the endpoint is only polled every ~31 ms, so a
        // full-scale fall inside about three polls stops smoothing anything and
        // the bar simply follows the peak reading. 100 ms is those three polls.
        //
        // The release makes no difference to how much of the strip gets used
        // (identical level histograms from 3 to 6 per second on a real capture),
        // so it is purely feel and safe to set by ear.
        //
        // Per SECOND rather than per poll, which was a fix and not a tidy-up:
        // Thread.Sleep(16) actually returns in about 31 ms at Windows' default
        // timer resolution (measured on the owner's rig, 97 polls in 3030 ms),
        // so the original per-poll rate ran at half its intended speed and fell
        // in 515 ms where it meant to take 250 ms.
        private const double ReleasePerSec = 10.0;
        // How often to re-ask Windows which endpoint is the default one, so
        // moving from speakers to a headset moves the meter with it.
        private const int DeviceRecheckMs = 2000;
        // After a failure. There may simply be no output device; retrying at
        // the poll rate would be sixty pointless COM calls a second.
        private const int DeviceRetryMs = 5000;

        private Thread _thread;
        private volatile bool _stop;
        private volatile float _level01;
        private volatile string _status = "off";
        private readonly AudioLevelEnvelope _envelope = new AudioLevelEnvelope();

        public AudioOutputMeter(Action<string> log) { _log = log ?? (_ => { }); }

        /// <summary>Where the bar should sit, 0..1, already smoothed and
        /// already mapped through the decibel window. Read from any thread.</summary>
        public double Level01 => _level01;

        public bool IsRunning => _thread != null;

        /// <summary>Which output is being metered, or why nothing is, for the
        /// settings panel.</summary>
        public string Status => _status;

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            var t = new Thread(Run)
            {
                IsBackground = true,
                Name = "TF4ALL AudioOutputMeter",
            };
            _thread = t;
            t.Start();
        }

        public void Stop()
        {
            var t = _thread;
            if (t == null) return;
            _stop = true;
            _thread = null;
            // Short join: the loop's longest wait is one poll. Not waiting at
            // all would let a restart run two threads at once, both writing
            // _level01, which is a meter that flickers for no visible reason.
            try { t.Join(200); } catch { }
            _level01 = 0f;
            _status = "off";
            _envelope.Reset();
        }

        private void Run()
        {
            MMDeviceEnumerator enumerator = null;
            MMDevice device = null;
            string deviceId = null;
            int nextDeviceCheck = Environment.TickCount;
            double level = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastTicks = 0;

            try
            {
                while (!_stop)
                {
                    int now = Environment.TickCount;
                    if (device == null || unchecked(now - nextDeviceCheck) >= 0)
                    {
                        nextDeviceCheck = now + DeviceRecheckMs;
                        try
                        {
                            if (enumerator == null) enumerator = new MMDeviceEnumerator();
                            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                            if (def == null) throw new InvalidOperationException("no default output device");
                            if (string.Equals(def.ID, deviceId, StringComparison.Ordinal))
                            {
                                // Same endpoint as last time: keep the one we
                                // already hold, whose meter object is resolved.
                                def.Dispose();
                            }
                            else
                            {
                                try { device?.Dispose(); } catch { }
                                device = def;
                                deviceId = def.ID;
                                _status = def.FriendlyName;
                                _log($"[AUDIO-LED] metering \"{def.FriendlyName}\"");
                            }
                        }
                        catch (Exception ex)
                        {
                            // No output device, or the audio service is
                            // restarting. Drop the lot and come back in a few
                            // seconds with a fresh enumerator.
                            try { device?.Dispose(); } catch { }
                            try { enumerator?.Dispose(); } catch { }
                            device = null; enumerator = null; deviceId = null;
                            if (_status != "no audio output found")
                                _log($"[AUDIO-LED] no output device: {ex.Message}");
                            _status = "no audio output found";
                            _level01 = 0f;
                            level = 0;
                            nextDeviceCheck = now + DeviceRetryMs;
                        }
                    }

                    double peak = 0;
                    if (device != null)
                    {
                        try { peak = device.AudioMeterInformation.MasterPeakValue; }
                        catch
                        {
                            // The endpoint went away under us (unplugged,
                            // disabled). Re-resolve on the next pass.
                            try { device.Dispose(); } catch { }
                            device = null; deviceId = null;
                            nextDeviceCheck = Environment.TickCount;
                        }
                    }

                    // Measured, not assumed: the release is a rate per second
                    // and the poll interval is whatever the OS actually gave us.
                    // A first pass, or a gap left by a suspended thread, falls
                    // back to the nominal interval so nothing takes one enormous
                    // step.
                    long ticks = clock.ElapsedTicks;
                    double elapsedSec = lastTicks == 0
                        ? PollMs / 1000.0
                        : (ticks - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                    lastTicks = ticks;
                    if (!(elapsedSec > 0) || elapsedSec > 1.0) elapsedSec = PollMs / 1000.0;

                    double target = _envelope.Push(peak, elapsedSec);
                    level = target >= level
                        ? target
                        : Math.Max(target, level - ReleasePerSec * elapsedSec);
                    _level01 = (float)level;

                    Thread.Sleep(PollMs);
                }
            }
            catch (Exception ex)
            {
                _log($"[AUDIO-LED] meter stopped: {ex.Message}");
                _status = "stopped (see log)";
            }
            finally
            {
                try { device?.Dispose(); } catch { }
                try { enumerator?.Dispose(); } catch { }
                _level01 = 0f;
            }
        }

        public void Dispose() => Stop();
    }
}
