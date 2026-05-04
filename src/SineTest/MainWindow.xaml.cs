// Live test rig for the Trueforce wire path: pick a waveform, drag freq /
// amplitude sliders, hear the wheel respond in real time.
//
// As of Phase 2b this drives a Mixer + OscillatorSource (single voice for
// now); the SimHub plugin and any future multi-voice expansion of the GUI
// will reuse the same TrueforceForAll.Core abstractions.

using System;
using System.Threading;
using System.Windows;
using TrueforceForAll.Core;

namespace TrueforceForAll.SineTest
{
    public partial class MainWindow : Window
    {
        private const double SampleRateHz = 1000.0;
        // One packet's worth per push: matches the consumer's drain quantum so
        // the producer-consumer cadence is clean (push 4, drain 4, push 4, ...).
        private const int    BatchSamples = 4;

        private TrueforceDevice _device;
        private WheelMatch _match;

        // Audio graph: Mixer drives one OscillatorSource. SineTest is a test
        // rig, so MasterGain is 1.0 (the slider directly controls peak amp,
        // capped at 0.7 in the XAML). The SimHub plugin will use 0.5.
        private readonly Mixer _mixer = new Mixer { MasterGain = 1.0f };
        private readonly OscillatorSource _voice = new OscillatorSource
        {
            SampleRate = SampleRateHz,
            Waveform = Waveform.Sine,
            Freq = 80.0,
            Amp = 0.3,
            Enabled = true,
        };

        private Thread _synthThread;
        private volatile bool _streaming;
        private volatile bool _shuttingDown;

        // Sweep state owned by the UI thread; the synth thread reads these
        // each batch. Since each field is independently atomic and we only
        // need eventual consistency, no lock is required.
        private volatile bool _sweepActive;
        private DateTime _sweepStartUtc;
        private long _lastSweepUiUpdateTicks;

        public MainWindow()
        {
            InitializeComponent();
            _mixer.Sources.Add(_voice);
            Loaded   += (_, __) => Discover();
            Closing  += MainWindow_Closing;
        }

        // ---------- discovery ----------

        private void Discover()
        {
            try
            {
                var matches = WheelDiscovery.FindAll();
                if (matches.Count == 0)
                {
                    DeviceText.Text = "No supported wheel found";
                    StatusText.Text = "Plug in a G PRO / RS50 and close G HUB, then click Refresh.";
                    StartBtn.IsEnabled = false;
                    SweepBtn.IsEnabled = false;
                    return;
                }
                _match = matches[0];
                DeviceText.Text = $"{_match.Model}  (VID 0x{_match.Vid:X4}, PID 0x{_match.Pid:X4})";
                StatusText.Text = "Idle. Click Start to send Trueforce init and begin streaming.";
                StartBtn.IsEnabled = true;
                SweepBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                DeviceText.Text = "Discovery error";
                StatusText.Text = ex.Message;
                StartBtn.IsEnabled = false;
                SweepBtn.IsEnabled = false;
            }
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_streaming)
            {
                StatusText.Text = "Stop streaming before refreshing.";
                return;
            }
            Discover();
        }

        // ---------- start / stop ----------

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_streaming) return;
            if (_match == null) { Discover(); if (_match == null) return; }

            try
            {
                StatusText.Text = "Opening device...";
                _device = new TrueforceDevice(_match.Device);
                _device.Open();

                StatusText.Text = "Sending init sequence (68 packets x 2)... HOLD THE WHEEL";
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                Dispatcher.Invoke(() => { /* yield */ }, System.Windows.Threading.DispatcherPriority.Background);

                _device.RunInitSequence();
                _device.StartStream();

                _streaming = true;
                _shuttingDown = false;
                _synthThread = new Thread(SynthLoop)
                {
                    IsBackground = true,
                    Name = "SineTestSynth",
                    Priority = ThreadPriority.AboveNormal,
                };
                _synthThread.Start();

                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled  = true;
                RefreshBtn.IsEnabled = false;
                StatusText.Text = "Streaming. Drag the sliders to vary frequency / amplitude.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Start failed: {ex.Message}";
                CleanupDevice();
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e) => StopStreaming("Stopped.");

        private void StopStreaming(string statusMessage)
        {
            if (!_streaming) return;
            _shuttingDown = true;
            _streaming = false;
            _sweepActive = false;

            try { _synthThread?.Join(500); } catch { }

            // Flush queued audio and centre the wheel before tearing down.
            try
            {
                _device?.ClearStream();
                Thread.Sleep(60); // ~15 packets at 250 Hz
            }
            catch { }

            CleanupDevice();

            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled  = false;
            RefreshBtn.IsEnabled = true;
            StatusText.Text = statusMessage;
        }

        private void CleanupDevice()
        {
            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        // ---------- slider events ----------

        private void Wave_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.RadioButton rb)) return;
            if (rb.Tag is string s && Enum.TryParse<Waveform>(s, out var w))
                _voice.Waveform = w;
        }

        private void FreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // While sweeping, the synth thread owns Freq and the readout.
            if (_sweepActive) return;
            if (FreqText != null) FreqText.Text = $"{e.NewValue:F0} Hz";
            _voice.Freq = e.NewValue;
        }

        private void AmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AmpText != null) AmpText.Text = $"{e.NewValue:F2}";
            _voice.Amp = e.NewValue;
        }

        // ---------- sweep ----------

        private void SweepBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_streaming)
            {
                StatusText.Text = "Click Start first, then trigger the sweep.";
                return;
            }
            _sweepStartUtc = DateTime.UtcNow;
            _sweepActive = true;
            StatusText.Text = "Sweeping 30 → 500 Hz over 5 s...";
        }

        // ---------- synth thread ----------

        // Sweep parameters: log sweep from FreqLo to FreqHi over SweepDurSec.
        private const double SweepFreqLo = 30.0;
        private const double SweepFreqHi = 500.0;
        private const double SweepDurSec = 5.0;

        private void SynthLoop()
        {
            float[] buf = new float[BatchSamples];

            while (!_shuttingDown)
            {
                // If sweep is active, drive _voice.Freq from the elapsed-time
                // log curve. Per-batch updates at 4 ms ~= 250 Hz update rate;
                // pitch step is well below the human perception threshold
                // (~1% pitch difference) at this batch granularity.
                if (_sweepActive)
                {
                    double elapsed = (DateTime.UtcNow - _sweepStartUtc).TotalSeconds;
                    if (elapsed >= SweepDurSec)
                    {
                        _sweepActive = false;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StatusText.Text = "Streaming. Drag the sliders to vary frequency / amplitude.";
                            if (FreqText != null && FreqSlider != null)
                                FreqText.Text = $"{FreqSlider.Value:F0} Hz";
                            if (FreqSlider != null) _voice.Freq = FreqSlider.Value;
                        }));
                    }
                    else
                    {
                        double freq = SweepFreqLo * Math.Pow(SweepFreqHi / SweepFreqLo, elapsed / SweepDurSec);
                        _voice.Freq = freq;

                        // Throttle UI updates (~12 Hz).
                        long nowTicks = Environment.TickCount;
                        long lastTicks = Interlocked.Read(ref _lastSweepUiUpdateTicks);
                        if (nowTicks - lastTicks > 80)
                        {
                            Interlocked.Exchange(ref _lastSweepUiUpdateTicks, nowTicks);
                            double freqShow = freq;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (FreqText != null) FreqText.Text = $"{freqShow:F0} Hz (sweep)";
                            }));
                        }
                    }
                }

                _mixer.Render(buf, BatchSamples);

                try
                {
                    _device?.PushFloats(buf, BatchSamples);
                }
                catch
                {
                    // Device gone or shutting down.
                    break;
                }
            }
        }

        // ---------- shutdown ----------

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try { StopStreaming("Closing."); } catch { }
        }
    }
}
