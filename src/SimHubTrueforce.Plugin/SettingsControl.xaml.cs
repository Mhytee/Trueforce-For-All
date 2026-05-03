using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SimHubTrueforce.Plugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly TrueforcePlugin _plugin;
        private readonly DispatcherTimer _meterTimer;
        private bool _suppressEvents;

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(TrueforcePlugin plugin) : this()
        {
            _plugin = plugin;

            WheelText.Text  = plugin.WheelStatus;
            StreamText.Text = plugin.StreamStatus;
            VoicesText.Text = plugin.ActiveVoiceCount.ToString();

            _suppressEvents = true;
            try
            {
                MasterGainSlider.Value = plugin.Settings?.MasterGain ?? 0.5;
                MasterGainText.Text    = MasterGainSlider.Value.ToString("F2");

                AudioEnabledCheck.IsChecked = plugin.AudioCapture?.Enabled ?? false;
                AudioGainSlider.Value       = plugin.AudioCapture?.Gain ?? 1.0;
                AudioGainText.Text          = AudioGainSlider.Value.ToString("F2");
            }
            finally { _suppressEvents = false; }

            // Smooth-ish 30 Hz meter without burning CPU. The meter reads and
            // resets the peak each tick, so it'll naturally fall when audio stops.
            _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _meterTimer.Tick += MeterTimer_Tick;
            Loaded   += (_, __) => _meterTimer.Start();
            Unloaded += (_, __) => _meterTimer.Stop();
        }

        private void MeterTimer_Tick(object sender, EventArgs e)
        {
            var src = _plugin?.AudioCapture;
            if (src == null) return;

            float peak = src.ReadAndResetPeak();
            // Clamp to [0, 1] for the bar; values >1 are clipping which is fine to flatten.
            if (peak > 1f) peak = 1f;
            // Cheap ballistic decay: ease toward `peak` from current value, bias up fast / down slow.
            double cur = AudioLevelMeter.Value;
            double target = peak;
            double next = target > cur ? target : cur * 0.85;
            AudioLevelMeter.Value = next;

            CaptureStatusText.Text = _plugin.CaptureStatus;
        }

        // ---------- Master gain ----------

        private void MasterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            if (MasterGainText != null) MasterGainText.Text = v.ToString("F2");
            _plugin.MasterGain = v;
        }

        // ---------- Audio capture ----------

        private void AudioEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.AudioCapture == null) return;
            _plugin.AudioCapture.Enabled = AudioEnabledCheck.IsChecked == true;
        }

        private void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.AudioCapture == null) return;
            float v = (float)e.NewValue;
            if (AudioGainText != null) AudioGainText.Text = v.ToString("F2");
            _plugin.AudioCapture.Gain = v;
        }
    }
}
