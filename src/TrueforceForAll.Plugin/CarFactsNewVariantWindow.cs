// Modal that pops the first time the user returns to the plugin after a
// telemetry signature mismatch is detected on the active car (typical
// Forza in-game engine swap). Pre-fills with the telemetry-observed
// cylinder count + redline + a heuristic EngineConfig so the typical
// flow is "click Save." Three exit paths:
//   * Save variant            -> RegisterNewEngineVariant + Close(true)
//   * Skip for now            -> Close(null) (will pop again next return)
//   * Don't ask again         -> DismissUnknownVariantSignature + Close(false)
//
// Styling mirrors UpdateVsNewChooserWindow / TrueforceDialog (dark bg
// #2A2A2A, gold header, panel-grey buttons) so the modal reads as part
// of the same family as every other styled dialog in the plugin.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    internal sealed class CarFactsNewVariantWindow : Window
    {
        private static readonly Brush WindowBg  = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg   = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg   = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        private static readonly Brush TextFg    = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg   = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg  = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg  = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        /// <summary>Saved field values when DialogResult == true; null
        /// otherwise.</summary>
        public string Label { get; private set; }
        public int Cylinders { get; private set; }
        public EngineConfig EngineConfig { get; private set; }
        public int? RedlineRpm { get; private set; }
        /// <summary>Rev-limiter engagement percent (0.50..1.00) when the
        /// user filled it in. Null when blank. Forza-family games
        /// without a telemetry redline rely on this value to know when
        /// to fire the limiter haptic.</summary>
        public float? EngagementPercent { get; private set; }
        /// <summary>True when the user clicked "Don't ask again." Caller
        /// reads this even on a false DialogResult to decide whether to
        /// persist the dismissal.</summary>
        public bool DontAskAgain { get; private set; }

        public CarFactsNewVariantWindow(string carDisplayName, string carId, string signature,
            int telemetryCyl, int? telemetryRedlineRpm, float? activeEngagementPercent)
        {
            Title         = "New engine variant detected";
            Width         = 480;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock
            {
                Text = "New engine variant detected",
                Foreground = HeaderFg,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 6),
            });

            string carLine = string.IsNullOrEmpty(carDisplayName) || carDisplayName == carId
                ? carId
                : carDisplayName + "   (" + carId + ")";
            root.Children.Add(new TextBlock
            {
                Text = "Telemetry from " + (string.IsNullOrEmpty(carLine) ? "this car" : carLine)
                     + " doesn't match any engine variant you've registered. "
                     + "Save the configuration so haptics for this engine stay accurate next time you load it.",
                Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            if (!string.IsNullOrEmpty(signature))
            {
                root.Children.Add(MakeLabel("Telemetry signature"));
                root.Children.Add(new TextBlock
                {
                    Text = signature,
                    Foreground = MutedFg, FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }

            root.Children.Add(MakeLabel("Variant name"));
            var tbLabel = MakeTextBox(InferLabel(telemetryCyl));
            root.Children.Add(tbLabel);

            // Two-column row for Cylinders + Redline (compact compared to
            // stacking each on its own line).
            var compactRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            compactRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            compactRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            compactRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cylColumn = new StackPanel();
            cylColumn.Children.Add(MakeLabel("Cylinders"));
            var tbCyl = MakeTextBox(telemetryCyl > 0 ? telemetryCyl.ToString() : "");
            cylColumn.Children.Add(tbCyl);
            Grid.SetColumn(cylColumn, 0);
            compactRow.Children.Add(cylColumn);
            var rpmColumn = new StackPanel();
            rpmColumn.Children.Add(MakeLabel("Redline RPM (optional)"));
            var tbRpm = MakeTextBox(telemetryRedlineRpm.HasValue
                ? telemetryRedlineRpm.Value.ToString()
                : "");
            rpmColumn.Children.Add(tbRpm);
            Grid.SetColumn(rpmColumn, 2);
            compactRow.Children.Add(rpmColumn);
            root.Children.Add(compactRow);

            // Engagement percent: critical for Forza-family games where
            // telemetry doesn't expose a redline. Defaults to whatever
            // the user already tuned on their active preset's slider so
            // a one-click save captures the value they're already using.
            root.Children.Add(MakeLabel("Rev-limiter engagement % (Forza only, 0.50..1.00)"));
            var tbPct = MakeTextBox(activeEngagementPercent.HasValue
                ? activeEngagementPercent.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : "");
            root.Children.Add(tbPct);

            root.Children.Add(MakeLabel("Engine configuration"));
            var cmbConfig = new ComboBox
            {
                Background = InputBg, Foreground = TextFg,
                BorderBrush = BorderFg, FontSize = 13,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(0, 0, 0, 14),
            };
            // EngineConfig.Custom is excluded (handled by the Custom
            // Engine library, not the variant prompt).
            foreach (EngineConfig ec in Enum.GetValues(typeof(EngineConfig)))
            {
                if (ec == EngineConfig.Custom) continue;
                cmbConfig.Items.Add(new ComboBoxItem
                {
                    Content    = ec.ToString(),
                    Tag        = ec,
                    Foreground = TextFg, Background = InputBg,
                });
            }
            EngineConfig defaultConfig = HeuristicConfig(telemetryCyl);
            for (int i = 0; i < cmbConfig.Items.Count; i++)
            {
                if (cmbConfig.Items[i] is ComboBoxItem ci && (EngineConfig)ci.Tag == defaultConfig)
                {
                    cmbConfig.SelectedIndex = i;
                    break;
                }
            }
            root.Children.Add(cmbConfig);

            // Button row: Don't-ask-again on the left (negative
            // intent), Skip in the middle, Save on the right (default
            // action where the eye finishes reading).
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var btnDontAsk = new Button
            {
                Content = "Don't ask again",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg,
            };
            btnDontAsk.Click += (s, e) =>
            {
                DontAskAgain = true;
                DialogResult = false;
                Close();
            };
            btnRow.Children.Add(btnDontAsk);

            var btnSkip = new Button
            {
                Content = "Skip for now",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            btnSkip.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(btnSkip);

            var btnSave = new Button
            {
                Content = "Save variant",
                Padding = new Thickness(14, 5, 14, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            btnSave.Click += (s, e) =>
            {
                if (!int.TryParse(tbCyl.Text, out int cyl) || cyl < 1 || cyl > 16)
                {
                    TrueforceDialog.Show(this, "Cylinders required",
                        "Enter a cylinder count between 1 and 16.",
                        DialogKind.Warning);
                    tbCyl.Focus();
                    return;
                }
                int? rpm = null;
                string rpmText = (tbRpm.Text ?? "").Trim();
                if (rpmText.Length > 0)
                {
                    if (!int.TryParse(rpmText, out int rpmVal) || rpmVal < 500 || rpmVal > 25000)
                    {
                        TrueforceDialog.Show(this, "Redline range",
                            "Redline RPM must be between 500 and 25000, or left blank.",
                            DialogKind.Warning);
                        tbRpm.Focus();
                        return;
                    }
                    rpm = rpmVal;
                }
                EngineConfig pickedConfig = defaultConfig;
                if (cmbConfig.SelectedItem is ComboBoxItem ci && ci.Tag is EngineConfig ecVal)
                    pickedConfig = ecVal;

                float? pct = null;
                string pctText = (tbPct.Text ?? "").Trim();
                if (pctText.Length > 0)
                {
                    if (!float.TryParse(pctText,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out float pctVal)
                        || pctVal < 0.50f || pctVal > 1.00f)
                    {
                        TrueforceDialog.Show(this, "Engagement range",
                            "Engagement percent must be between 0.50 and 1.00, or left blank.",
                            DialogKind.Warning);
                        tbPct.Focus();
                        return;
                    }
                    pct = pctVal;
                }

                Label             = (tbLabel.Text ?? "").Trim();
                Cylinders         = cyl;
                EngineConfig      = pickedConfig;
                RedlineRpm        = rpm;
                EngagementPercent = pct;
                DialogResult      = true;
                Close();
            };
            btnRow.Children.Add(btnSave);
            root.Children.Add(btnRow);

            Loaded += (s, e) => { tbLabel.Focus(); tbLabel.SelectAll(); };
        }

        private TextBlock MakeLabel(string text) => new TextBlock
        {
            Text = text,
            Foreground = MutedFg, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3),
        };

        private TextBox MakeTextBox(string text) => new TextBox
        {
            Text       = text ?? "",
            Margin     = new Thickness(0, 0, 0, 10),
            Padding    = new Thickness(4, 2, 4, 2),
            Background = InputBg, Foreground = TextFg,
            BorderBrush = BorderFg,
            CaretBrush = TextFg,
            FontSize   = 13,
        };

        // Suggest a sensible default label so a one-click save lands on
        // something readable. Tied to cylinder count (the most reliable
        // telemetry hint). User overwrites when they have a better name.
        private static string InferLabel(int cyl)
        {
            if (cyl <= 0) return "Custom variant";
            switch (cyl)
            {
                case 3: return "I3 swap";
                case 4: return "I4 swap";
                case 5: return "I5 swap";
                case 6: return "I6 swap";
                case 8: return "V8 swap";
                case 10: return "V10 swap";
                case 12: return "V12 swap";
                default: return cyl + "-cyl swap";
            }
        }

        // Pre-pick a plausible EngineConfig from the cylinder count so
        // the Save button works without the user touching the combobox.
        // Conservative defaults: Inline for low counts, V60 for 6/12,
        // V90Even for 8/10, Auto for anything weird.
        private static EngineConfig HeuristicConfig(int cyl)
        {
            switch (cyl)
            {
                case 1: return EngineConfig.Single;
                case 2: case 3: case 4: case 5: return EngineConfig.Inline;
                case 6: return EngineConfig.V60;
                case 8: return EngineConfig.V8CrossPlane;
                case 10: return EngineConfig.V90Even;
                case 12: return EngineConfig.V60;
                default: return EngineConfig.Auto;
            }
        }
    }
}
