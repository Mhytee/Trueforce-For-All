// Modal dialog that lists every USB device the USBPcap descriptor scan can
// see, lets the user pick one as the FFB tap target, and persists the choice.
//
// Auto-discovery via WheelUsbDiscovery.Find() filters to Logitech wheels on
// our supported PIDs. When that fails (typically because the wheel was
// hot-plugged after USBPcap's descriptor cache was warmed), this picker
// shows EVERY device the scan can see so the user can map their wheel by
// hand. Selection is saved to TrueforceSettings and survives restarts.
//
// USBPcap interaction: a live FFB tap holds a capture open on the wheel's
// interface, which can prevent a parallel descriptor-injection scan from
// the same dialog from seeing the cached descriptors (USBPcap's injection
// is per-capture; competing -A captures on the same interface have been
// observed to come up empty). The picker therefore stops the active tap
// before scanning and restarts it via the plugin when the dialog closes.
//
// Built as a code-only WPF Window rather than XAML to keep the diagnostics
// surface area tightly scoped, no theme resources, no other call sites.
// SimHub's app-level theme is not inherited by Windows we create from code,
// so colors are set explicitly to match SimHub's dark Metro palette.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    internal sealed class UsbDevicePickerWindow : Window
    {
        // Dark Metro-ish palette. SimHub itself uses MahApps; we don't have
        // its resource dictionary in a code-built Window, so values are
        // hardcoded to match what users see in the rest of the plugin UI.
        private static readonly Brush PanelBg     = (Brush)new BrushConverter().ConvertFromString("#252526");
        private static readonly Brush PanelFg     = (Brush)new BrushConverter().ConvertFromString("#DDDDDD");
        private static readonly Brush SubtleFg    = (Brush)new BrushConverter().ConvertFromString("#AAAAAA");
        private static readonly Brush ListBg      = (Brush)new BrushConverter().ConvertFromString("#1E1E1F");
        private static readonly Brush ListAltBg   = (Brush)new BrushConverter().ConvertFromString("#2A2A2C");
        private static readonly Brush HeaderBg    = (Brush)new BrushConverter().ConvertFromString("#333337");

        private readonly TrueforcePlugin _plugin;
        private readonly ListView _list;
        private readonly TextBlock _statusText;
        private readonly Button _rescanButton;
        private readonly Button _applyButton;
        private readonly Button _clearButton;

        // Snapshot of which device the tap was using when we opened, so the
        // scan can fall back to showing it even if a fresh scan misses it.
        private string _previousTapInterface;
        private int    _previousTapAddress;
        private bool   _tapWasRunning;

        private CancellationTokenSource _scanCts;

        public UsbDevicePickerWindow(TrueforcePlugin plugin)
        {
            _plugin = plugin;
            Title  = "Pick USB device for FFB pass-through";
            Width  = 760;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Background = PanelBg;
            Foreground = PanelFg;
            // Inherit text/control foreground via TextElement.Foreground so
            // child TextBlock / Button / ListView text is readable on dark.
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextElement.SetForeground(this, PanelFg);

            var root = new DockPanel { Margin = new Thickness(12), Background = PanelBg };

            // Header explanation
            var header = new TextBlock
            {
                Text = "Pick the wheel's USB device for FFB pass-through. " +
                       "Rows that match a supported wheel or your currently-detected HID wheel are flagged in Notes. " +
                       "Selection persists across restarts. Clear it to return to auto-discovery.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = SubtleFg,
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Top bar: re-scan + status
            var topBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _rescanButton = MakeButton("Re-scan USB devices", 160);
            _rescanButton.Click += (s, e) => StartScan();
            topBar.Children.Add(_rescanButton);
            _statusText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SubtleFg,
                Margin = new Thickness(12, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            topBar.Children.Add(_statusText);
            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);

            // Bottom bar: apply / clear / cancel
            var bottomBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
            };
            _clearButton = MakeButton("Clear override (use auto)", 200);
            _clearButton.Click += ClearButton_Click;
            _clearButton.IsEnabled = _plugin.HasManualUsbPcapDevice;
            bottomBar.Children.Add(_clearButton);

            _applyButton = MakeButton("Use selected device", 170);
            _applyButton.Click += ApplyButton_Click;
            _applyButton.IsEnabled = false;
            _applyButton.IsDefault = true;
            bottomBar.Children.Add(_applyButton);

            var cancelButton = MakeButton("Close", 80);
            cancelButton.IsCancel = true;
            bottomBar.Children.Add(cancelButton);

            DockPanel.SetDock(bottomBar, Dock.Bottom);
            root.Children.Add(bottomBar);

            // Main list. Build a custom row-template style so selection +
            // alternating rows are visible against the dark panel.
            _list = new ListView
            {
                Background    = ListBg,
                Foreground    = PanelFg,
                BorderBrush   = HeaderBg,
                BorderThickness = new Thickness(1),
                AlternationCount = 2,
            };
            _list.ItemContainerStyle = BuildRowStyle();

            var gridView = new GridView();
            gridView.ColumnHeaderContainerStyle = BuildHeaderStyle();
            gridView.Columns.Add(MakeColumn("Interface",  140, "Interface"));
            gridView.Columns.Add(MakeColumn("Address",     70, "Address"));
            gridView.Columns.Add(MakeColumn("VID:PID",    100, "VidPid"));
            gridView.Columns.Add(MakeColumn("Device",     220, "Description"));
            gridView.Columns.Add(MakeColumn("Notes",      180, "Notes"));
            _list.View = gridView;
            _list.SelectionMode = SelectionMode.Single;
            _list.SelectionChanged += (s, e) => _applyButton.IsEnabled = _list.SelectedItem != null;
            _list.MouseDoubleClick += (s, e) =>
            {
                if (_list.SelectedItem != null) ApplyButton_Click(s, null);
            };
            root.Children.Add(_list);

            Content = root;
            Closing += OnClosing;
            Loaded += (s, e) =>
            {
                // Snapshot current tap state, then stop it so our scan gets
                // clean access to USBPcap.
                _previousTapInterface = _plugin.ActiveFfbTapInterface;
                _previousTapAddress   = _plugin.ActiveFfbTapDeviceAddress;
                _tapWasRunning        = !string.IsNullOrEmpty(_previousTapInterface) && _previousTapAddress > 0;
                if (_tapWasRunning)
                {
                    try { _plugin.StopFfbTap(); } catch { }
                }
                StartScan();
            };
        }

        // Restart the FFB tap on close so the user's wheel keeps working
        // after they back out of the picker. Apply/Clear paths also call
        // RestartFfbTap internally (via ApplyManualUsbPcapDevice), so this
        // covers the cancel/close-X path.
        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try { _scanCts?.Cancel(); } catch { }
            // Only the cancel/close path needs to restart; Apply/Clear have
            // already restarted via ApplyManualUsbPcapDevice. We detect that
            // by checking DialogResult: true = apply/clear set it, null/false = cancel.
            if (DialogResult != true)
            {
                try { _plugin.RestartFfbTap(); } catch { }
            }
        }

        private static GridViewColumn MakeColumn(string header, double width, string bindPath)
            => new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(bindPath) };

        // Button styled to match the dark panel. WPF default buttons render
        // light grey with black text, which is fine on the dark background
        // but feels alien, match the rest of the plugin UI loosely.
        private static Button MakeButton(string content, double width)
        {
            return new Button
            {
                Content = content,
                Width   = width,
                Height  = 28,
                Margin  = new Thickness(0, 0, 8, 0),
                Background = (Brush)new BrushConverter().ConvertFromString("#3F3F46"),
                Foreground = PanelFg,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#555"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2),
            };
        }

        private static Style BuildHeaderStyle()
        {
            var style = new Style(typeof(System.Windows.Controls.GridViewColumnHeader));
            style.Setters.Add(new Setter(Control.BackgroundProperty, HeaderBg));
            style.Setters.Add(new Setter(Control.ForegroundProperty, PanelFg));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, (Brush)new BrushConverter().ConvertFromString("#444")));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            return style;
        }

        private static Style BuildRowStyle()
        {
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, PanelFg));
            style.Setters.Add(new Setter(Control.BackgroundProperty, ListBg));
            // Alternating row colors. WPF reads ItemsControl.AlternationIndex
            // via triggers; the index is set on _list above.
            var altTrigger = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            altTrigger.Setters.Add(new Setter(Control.BackgroundProperty, ListAltBg));
            style.Triggers.Add(altTrigger);

            // Selection highlight that's actually visible. WPF's default
            // selected-row color is system-blue; on a dark background with
            // white text it's still readable, but we re-skin for consistency.
            var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, (Brush)new BrushConverter().ConvertFromString("#FFB300")));
            selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, (Brush)new BrushConverter().ConvertFromString("#1A1A1A")));
            // Non-colour cue too (bold text), so the selected device is
            // distinguishable without relying on the amber highlight alone.
            selTrigger.Setters.Add(new Setter(Control.FontWeightProperty, System.Windows.FontWeights.Bold));
            style.Triggers.Add(selTrigger);

            return style;
        }

        private void StartScan()
        {
            try { _scanCts?.Cancel(); } catch { }
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            _list.ItemsSource = null;
            _rescanButton.IsEnabled = false;
            _statusText.Text = _tapWasRunning
                ? "Scanning USB devices (FFB pass-through paused during scan)..."
                : "Scanning USB devices...";

            string usbPcapCmdPath = UsbPcapFfbTap.LocateUsbPcapCmd(_plugin.Settings?.UsbPcapCmdPathOverride);
            if (usbPcapCmdPath == null)
            {
                _statusText.Text = "USBPcapCMD.exe not found. Install or reinstall USBPcap first.";
                _rescanButton.IsEnabled = true;
                return;
            }

            // Snapshot so the async lambda is decoupled from field changes.
            ushort hidVid = _plugin.HidWheelVid;
            ushort hidPid = _plugin.HidWheelPid;
            string activeIface = _plugin.Settings?.ManualUsbPcapInterface ?? "";
            int activeAddr = _plugin.Settings?.ManualUsbPcapDeviceAddress ?? 0;
            string prevIface = _previousTapInterface;
            int prevAddr     = _previousTapAddress;

            Task.Run(() =>
            {
                try
                {
                    var scans = WheelUsbDiscovery.ScanAllInterfaces(
                        usbPcapCmdPath,
                        msg => SimHub.Logging.Current.Info($"[Trueforce/picker] {msg}"));
                    if (ct.IsCancellationRequested) return;

                    var rows = new List<Row>();
                    bool sawPrevious = false;
                    if (scans != null)
                    {
                        foreach (var s in scans)
                        {
                            foreach (var c in s.Candidates)
                            {
                                if (!string.IsNullOrEmpty(prevIface) && c.Interface == prevIface && c.DeviceAddress == prevAddr)
                                    sawPrevious = true;
                                rows.Add(MakeRow(c, hidVid, hidPid, activeIface, activeAddr, prevIface, prevAddr));
                            }
                        }
                    }

                    // Fallback: if the FFB tap was running on a device the
                    // fresh scan didn't see (USBPcap caching weirdness on a
                    // recently-active interface), synthesize a row from the
                    // tap's last known state. The user can still select it.
                    if (!sawPrevious && !string.IsNullOrEmpty(prevIface) && prevAddr > 0)
                    {
                        rows.Add(new Row
                        {
                            Interface   = prevIface,
                            Address     = prevAddr,
                            VidPid      = hidVid != 0 ? $"{hidVid:X4}:{hidPid:X4}" : "(unknown)",
                            Description = "FFB tap was here before the picker opened",
                            Notes       = "previously active (not in fresh scan)",
                            Candidate   = new UsbDeviceCandidate
                            {
                                Interface = prevIface,
                                DeviceAddress = prevAddr,
                                Vid = hidVid,
                                Pid = hidPid,
                                IsSupportedWheel = hidVid == WheelDiscovery.LogitechVid,
                            },
                        });
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        _list.ItemsSource = rows;
                        _rescanButton.IsEnabled = true;

                        if (rows.Count == 0)
                        {
                            _statusText.Text = "No USB devices found. " +
                                "If your wheel is plugged in, try replugging it (USBPcap caches descriptors at enumeration time) " +
                                "or running SimHub as administrator.";
                            return;
                        }
                        _statusText.Text = $"Found {rows.Count} device(s). Pick your wheel and click 'Use selected device'.";

                        // Pre-select: prefer active override, then HID-found
                        // wheel, then previously-active tap row.
                        Row preselect = null;
                        foreach (var r in rows)
                            if (r.Interface == activeIface && r.Address == activeAddr) { preselect = r; break; }
                        if (preselect == null && hidVid != 0)
                            foreach (var r in rows)
                                if (r.Candidate.Vid == hidVid && r.Candidate.Pid == hidPid) { preselect = r; break; }
                        if (preselect == null && !string.IsNullOrEmpty(prevIface))
                            foreach (var r in rows)
                                if (r.Interface == prevIface && r.Address == prevAddr) { preselect = r; break; }
                        if (preselect != null)
                        {
                            _list.SelectedItem = preselect;
                            _list.ScrollIntoView(preselect);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        _statusText.Text = $"Scan failed: {ex.Message}";
                        _rescanButton.IsEnabled = true;
                    });
                }
            }, ct);
        }

        private static Row MakeRow(UsbDeviceCandidate c, ushort hidVid, ushort hidPid,
                                   string activeIface, int activeAddr,
                                   string prevIface, int prevAddr)
        {
            var notes = new List<string>();
            if (c.Interface == activeIface && c.DeviceAddress == activeAddr) notes.Add("ACTIVE");
            else if (c.Interface == prevIface && c.DeviceAddress == prevAddr) notes.Add("previously active");
            if (c.IsSupportedWheel) notes.Add("supported wheel");
            else if (c.Vid == WheelDiscovery.LogitechVid) notes.Add("Logitech (unknown PID)");
            if (hidVid != 0 && c.Vid == hidVid && c.Pid == hidPid) notes.Add("matches HID wheel");

            return new Row
            {
                Interface   = c.Interface,
                Address     = c.DeviceAddress,
                VidPid      = $"{c.Vid:X4}:{c.Pid:X4}",
                Description = !string.IsNullOrEmpty(c.Model) ? c.Model : "USB device",
                Notes       = string.Join(", ", notes),
                Candidate   = c,
            };
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(_list.SelectedItem is Row row)) return;

            // Confirm when picking a non-Logitech VID, the FFB tap won't get
            // any data and we'd just be wasting a USBPcap process.
            if (row.Candidate.Vid != 0 && row.Candidate.Vid != WheelDiscovery.LogitechVid)
            {
                var result = TrueforceDialog.Show(
                    this,
                    "Trueforce",
                    $"This device isn't a Logitech wheel ({row.VidPid}). The FFB tap won't get any data from it. Apply anyway?",
                    DialogKind.Confirm, "Apply anyway", "Cancel");
                if (result != true) return;
            }

            bool ok = _plugin.ApplyManualUsbPcapDevice(row.Interface, row.Address, row.Candidate.Vid, row.Candidate.Pid);
            if (ok)
            {
                _statusText.Text = $"Applied: {row.Interface} dev {row.Address}. FFB tap restarting.";
                _clearButton.IsEnabled = true;
            }
            else
            {
                _statusText.Text = "Could not restart the FFB tap. Check the log for details.";
            }
            DialogResult = true;
            Close();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ApplyManualUsbPcapDevice("", 0);
            _statusText.Text = "Cleared. Auto-discovery will run on the next FFB tap start.";
            _clearButton.IsEnabled = false;
            DialogResult = true;
            Close();
        }

        // Row VM. Bound via display-member bindings on the GridView columns.
        private sealed class Row
        {
            public string Interface   { get; set; }
            public int    Address     { get; set; }
            public string VidPid      { get; set; }
            public string Description { get; set; }
            public string Notes       { get; set; }
            public UsbDeviceCandidate Candidate { get; set; }
        }
    }
}
