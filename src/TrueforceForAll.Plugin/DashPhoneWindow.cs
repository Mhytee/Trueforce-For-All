// Phone-access dialog for TF4ALL Dash: QR code + copyable link that land
// straight on our dash in SimHub's web dash server. Our own window rather
// than SimHub's MobileAccessAssistant because that dialog has no way to
// copy the URL and no room for the same-network / add-to-home-screen
// guidance. QR generation binds to the QRCoder.dll that ships inside
// SimHub (compile-only reference, never shipped by us); the SimHub-typed
// reads sit in NoInlining helpers so a future SimHub build changing them
// degrades to a fallback instead of crashing the dialog.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    internal sealed class DashPhoneWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

        public DashPhoneWindow()
        {
            Title         = "TF4ALL Dash on your phone";
            Width         = 420;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "TF4ALL Dash on your phone",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = "Control effects, gains, and presets from your phone or tablet while driving.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            string ip = GetLocalIPv4();
            if (ip == null)
            {
                // No usable network: a QR/URL would point nowhere. Say so
                // instead of rendering a dead code.
                root.Children.Add(new TextBlock {
                    Text = "No network connection detected. Connect this PC to your network, then reopen this window.",
                    Foreground = TextFg, FontSize = 12,
                    Margin = new Thickness(0, 6, 0, 12),
                    TextWrapping = TextWrapping.Wrap,
                });
                AddCloseRow(root, openButton: null);
                return;
            }

            // Same deep-link SimHub's own dashboard list uses, so the phone
            // lands straight on our dash instead of the dash list. The name
            // is pre-escaped; the v= cachebuster mirrors SimHub's links.
            string url = "http://" + ip + ":" + GetWebPort()
                       + "/Dash?v=" + DateTime.UtcNow.Ticks + "#TF4ALL%20Dash";

            ImageSource qrImage = null;
            try { qrImage = MakeQr(url); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] QR generation failed: " + ex.Message);
            }
            if (qrImage != null)
            {
                root.Children.Add(new Image {
                    Source = qrImage, Width = 250, Height = 250,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8),
                });
                root.Children.Add(new TextBlock {
                    Text = "Scan with your phone's camera, or type the link into its browser.",
                    Foreground = MutedFg, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 10),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                root.Children.Add(new TextBlock {
                    Text = "Type the link below into your phone's browser.",
                    Foreground = MutedFg, FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 10),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            // Copyable link row: read-only-style TextBox so the URL is
            // selectable, plus an explicit Copy button.
            var linkRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var linkBox = new TextBox {
                Text = url, IsReadOnly = true,
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4), FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            linkBox.GotKeyboardFocus += (s, e) => linkBox.SelectAll();
            Grid.SetColumn(linkBox, 0);
            linkRow.Children.Add(linkBox);
            var copyBtn = new Button {
                Content = "Copy", Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(8, 0, 0, 0), MinWidth = 64,
                Foreground = TextFg, Background = PanelBg,
            };
            copyBtn.Click += (s, e) =>
            {
                // Clipboard can transiently fail when another process holds
                // it (classic CLIPBRD_E_CANT_OPEN); a silent miss with no
                // "Copied" confirmation is the honest signal.
                try { Clipboard.SetText(url); }
                catch { return; }
                copyBtn.Content = "Copied";
                var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                revert.Tick += (s2, e2) => { revert.Stop(); copyBtn.Content = "Copy"; };
                revert.Start();
            };
            Grid.SetColumn(copyBtn, 1);
            linkRow.Children.Add(copyBtn);
            root.Children.Add(linkRow);

            root.Children.Add(new TextBlock {
                Text = "Phone and PC must be on the same network.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
                TextWrapping = TextWrapping.Wrap,
            });
            root.Children.Add(new TextBlock {
                Text = "To keep the dash like an app, tap your browser's share icon and choose Add to Home Screen.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            var openBtn = new Button {
                Content = "Open on this PC", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg,
                ToolTip = "Opens the dash in this PC's browser to check it's being served.",
            };
            openBtn.Click += (s, e) =>
            {
                try { Process.Start(url); }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info("[TF4ALL] Open dash in browser failed: " + ex.Message);
                }
            };
            AddCloseRow(root, openBtn);
        }

        // Bottom button row: optional "Open on this PC" on the left, Close
        // pinned right. Shared with the no-network layout (Close only).
        private void AddCloseRow(StackPanel root, Button openButton)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (openButton != null)
            {
                Grid.SetColumn(openButton, 0);
                row.Children.Add(openButton);
            }
            var closeBtn = new Button {
                Content = "Close", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg,
                IsCancel = true, IsDefault = true,
            };
            closeBtn.Click += (s, e) => Close();
            Grid.SetColumn(closeBtn, 2);
            row.Children.Add(closeBtn);
            root.Children.Add(row);
        }

        // ---- guarded SimHub / QRCoder reads -------------------------------
        // Each SimHub-typed touch lives in its own NoInlining method so a
        // signature change in a future SimHub build surfaces as a catchable
        // exception at the call site instead of a JIT failure in the caller.

        private static int GetWebPort()
        {
            try { return ReadSimHubWebPort(); }
            catch { return 8888; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadSimHubWebPort()
        {
            return SimHub.Plugins.OutputPlugins.GraphicalDash.GraphicalDashPlugin.WebPort;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImageSource MakeQr(string text)
        {
            using (var generator = new QRCoder.QRCodeGenerator())
            using (var code = new QRCoder.QRCode(generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.Q)))
            using (var bitmap = code.GetGraphic(20))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        // Mirrors SimHub's NetworkHelpers.GetLocalIPv4 preference order
        // (up + has gateway, prefer 192. then 172. then 10.) so our URL
        // matches the one SimHub's own assistant would show, without
        // depending on the SimHub-internal helper.
        private static string GetLocalIPv4()
        {
            var candidates = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    var props = nic.GetIPProperties();
                    if (props.GatewayAddresses.FirstOrDefault() == null) continue;
                    foreach (var addr in props.UnicastAddresses)
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            candidates.Add(addr.Address.ToString());
                }
            }
            catch { }
            return candidates.FirstOrDefault(i => i.StartsWith("192."))
                ?? candidates.FirstOrDefault(i => i.StartsWith("172."))
                ?? candidates.FirstOrDefault(i => i.StartsWith("10."))
                ?? candidates.FirstOrDefault();
        }
    }
}
