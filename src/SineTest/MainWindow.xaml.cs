// Live test rig for the Trueforce wire path: pick a waveform, drag freq /
// amplitude sliders, hear the wheel respond in real time.

using System;
using System.Threading;
using System.Windows;

namespace SimHubTrueforce.SineTest
{
    public partial class MainWindow : Window
    {
        private enum Waveform { Sine, Square, Saw, Triangle, Noise }

        private const double SampleRateHz = 1000.0;
        // One packet's worth per push: matches the consumer's drain quantum so
        // the producer-consumer cadence is clean (push 4, drain 4, push 4, ...).
        // Lower latency than larger batches; matches Trueforce's NewPerPacket.
        private const int    BatchSamples = 4;

        private TrueforceDevice _device;
        private WheelMatch _match;

        private Thread _synthThread;
        private volatile bool _streaming;
        private volatile bool _shuttingDown;

        // Synth state (read by background thread, written by UI thread).
        // Coarse lock; uncontended at this rate.
        private readonly object _synthLock = new object();
        private Waveform _wave  = Waveform.Sine;
        private double   _freq  = 80.0;
        private double   _amp   = 0.3;
        private double   _phase;
        private readonly Random _rng = new Random();

        // Sweep state — guarded by _synthLock so the synth thread sees a
        // consistent snapshot of (active, startUtc).
        private bool _sweepActive;
        private DateTime _sweepStartUtc;
        private long _lastSweepUiUpdateTicks;  // for UI-update throttling

        public MainWindow()
        {
            InitializeComponent();
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
            lock (_synthLock) _sweepActive = false;

            // Wait for the synth thread to exit so it stops generating samples.
            try { _synthThread?.Join(500); } catch { }

            // Flush queued audio and centre the wheel before tearing down: clear
            // the ring (drops any buffered samples) and let a few stream ticks
            // emit silent (0x8000) packets so the wheel doesn't hang on the last
            // peak sample. Without this the motor can sit at full one-sided
            // torque until the firmware self-relaxes.
            try
            {
                _device?.ClearStream();
                Thread.Sleep(60); // ~15 packets at 250 Hz; plenty to drain.
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
            {
                lock (_synthLock) _wave = w;
            }
        }

        private void FreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // While a sweep is active, the synth itself drives _freq and writes
            // the readout; don't fight it. Don't update FreqText either — the
            // synth thread is busy showing "(sweep) ..." values.
            bool sweepActive;
            lock (_synthLock) sweepActive = _sweepActive;
            if (sweepActive) return;

            if (FreqText != null) FreqText.Text = $"{e.NewValue:F0} Hz";
            lock (_synthLock) _freq = e.NewValue;
        }

        private void AmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AmpText != null) AmpText.Text = $"{e.NewValue:F2}";
            lock (_synthLock) _amp = e.NewValue;
        }

        // ---------- sweep ----------

        private void SweepBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_streaming)
            {
                StatusText.Text = "Click Start first, then trigger the sweep.";
                return;
            }
            lock (_synthLock)
            {
                _sweepActive = true;
                _sweepStartUtc = DateTime.UtcNow;
            }
            StatusText.Text = "Sweeping 30 → 500 Hz over 5 s...";
        }

        // ---------- synth thread ----------

        private void SynthLoop()
        {
            float[] buf = new float[BatchSamples];

            while (!_shuttingDown)
            {
                Waveform w; double freq, amp;
                bool sweepActive; DateTime sweepStartUtc;
                lock (_synthLock)
                {
                    w = _wave; freq = _freq; amp = _amp;
                    sweepActive = _sweepActive; sweepStartUtc = _sweepStartUtc;
                }

                // Sweep override.
                if (sweepActive)
                {
                    double elapsed = (DateTime.UtcNow - sweepStartUtc).TotalSeconds;
                    if (elapsed >= 5.0)
                    {
                        lock (_synthLock) _sweepActive = false;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StatusText.Text = "Streaming. Drag the sliders to vary frequency / amplitude.";
                            // Restore freq display from slider position.
                            if (FreqText != null && FreqSlider != null)
                                FreqText.Text = $"{FreqSlider.Value:F0} Hz";
                        }));
                    }
                    else
                    {
                        // Logarithmic sweep across an audible-vibration range.
                        double t = elapsed / 5.0;
                        freq = 30.0 * Math.Pow(500.0 / 30.0, t);

                        // Throttle UI updates — every ~80 ms is plenty for a
                        // visible readout and keeps the dispatcher quiet.
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

                double phaseStep = freq / SampleRateHz;
                for (int i = 0; i < BatchSamples; i++)
                {
                    float sample = SampleAt(w, _phase) * (float)amp;
                    buf[i] = sample;
                    _phase += phaseStep;
                    if (_phase >= 1.0) _phase -= Math.Floor(_phase);
                }

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

        private float SampleAt(Waveform w, double phase)
        {
            switch (w)
            {
                case Waveform.Sine:     return (float)Math.Sin(2.0 * Math.PI * phase);
                case Waveform.Square:   return phase < 0.5 ? 1f : -1f;
                case Waveform.Saw:      return (float)(2.0 * phase - 1.0);
                case Waveform.Triangle:
                    return phase < 0.5
                        ? (float)(4.0 * phase - 1.0)
                        : (float)(3.0 - 4.0 * phase);
                case Waveform.Noise:    return (float)(_rng.NextDouble() * 2.0 - 1.0);
                default: return 0f;
            }
        }

        // ---------- shutdown ----------

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try { StopStreaming("Closing."); } catch { }
        }
    }

}
