// Modal that fires from the preset manager's "Share..." button. Shows the
// preset being uploaded (car, game, preset name, which effect sections it
// touches), lets the user confirm an Author (pre-filled from
// Settings.SharingAuthor) + optional Description, then uploads through
// PresetSharingClient. On success the new preset id is returned via the
// UploadedPresetId property so the caller can record it / surface a
// success toast.
//
// Styled to match CarFactsShareWindow / CarNameInputWindow.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class PresetShareWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush OkFg     = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        private static readonly Brush ErrFg    = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));

        public string UploadedPresetId { get; private set; }

        private readonly TrueforcePlugin _plugin;
        private readonly string _presetName;
        private readonly string _game;
        private readonly string _carId;
        private readonly string _carDisplay;
        private readonly Newtonsoft.Json.Linq.JObject _body;
        private readonly List<string> _effectTags;

        public PresetShareWindow(
            TrueforcePlugin plugin,
            string presetName, string game, string carId, string carDisplay,
            Newtonsoft.Json.Linq.JObject body, List<string> effectTags)
        {
            _plugin     = plugin;
            _presetName = presetName ?? "";
            _game       = game ?? "";
            _carId      = carId ?? "";
            _carDisplay = string.IsNullOrEmpty(carDisplay) ? carId : carDisplay;
            _body       = body;
            _effectTags = effectTags ?? new List<string>();

            Title         = "Share preset with the community";
            Width         = 480;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Share preset with the community",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // What we're sharing - a passive readout so the user can't
            // forget which preset is going up.
            root.Children.Add(MakeFactLine("Preset",  _presetName));
            root.Children.Add(MakeFactLine("Game",    _game));
            root.Children.Add(MakeFactLine("Car",     _carDisplay));
            root.Children.Add(MakeFactLine("Sections", _effectTags.Count == 0
                ? "(none)" : string.Join(", ", _effectTags)));

            // Identity is server-authoritative: signed-in users upload
            // under their username, anonymous users upload as "Anonymous".
            // No freeform Author field; read-only display so the user
            // sees exactly how the upload will be credited.
            string sharingAs = _plugin?.AuthIsSignedIn == true
                ? (_plugin?.Settings?.SharingAuthor ?? "(your username)")
                : "Anonymous";
            root.Children.Add(MakeFactLine("Sharing as", sharingAs));

            root.Children.Add(new TextBlock {
                Text = "Description (optional, what makes this tune feel good):",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
            });
            var descInput = new TextBox {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12, MaxLength = 1024,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 60,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(descInput);

            // Status line for in-flight upload / errors. Initially blank.
            var statusText = new TextBlock {
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "Anonymous beyond the Author field. No account needed.",
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(statusText);

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var uploadBtn = new Button {
                Content = "Upload", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            uploadBtn.Click += async (s, e) =>
            {
                uploadBtn.IsEnabled = false;
                cancelBtn.IsEnabled = false;
                statusText.Foreground = MutedFg;
                statusText.Text = "Uploading...";

                string desc = (descInput.Text ?? "").Trim();
                // Author is server-authoritative now: profile.username if
                // signed in, null otherwise. We pass null and the server
                // stamps the right value.
                string newId = null;
                try
                {
                    newId = await _plugin.UploadCarPresetToCommunityAsync(
                        _presetName, _game, _carId, _body,
                        null, desc, _effectTags);
                }
                catch (Exception ex)
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Upload exception: " + ex.Message;
                    uploadBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }

                if (string.IsNullOrEmpty(newId))
                {
                    statusText.Foreground = ErrFg;
                    statusText.Text = "Upload failed (network or rate limit). Try again later.";
                    uploadBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }
                UploadedPresetId = newId;
                statusText.Foreground = OkFg;
                statusText.Text = "Uploaded. Thanks for contributing.";
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(uploadBtn);
            root.Children.Add(btnRow);
        }

        private FrameworkElement MakeFactLine(string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblBlock = new TextBlock {
                Text = label, Foreground = MutedFg, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lblBlock, 0);
            grid.Children.Add(lblBlock);
            var valBlock = new TextBlock {
                Text = value ?? "", Foreground = TextFg, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(valBlock, 1);
            grid.Children.Add(valBlock);
            return grid;
        }
    }
}
