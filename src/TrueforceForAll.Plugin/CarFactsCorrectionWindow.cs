// Modal dialog used by the engine-layout "Correct..." affordance on the
// Effects tab. Lets the user say "the bake/heuristic detection for this car
// is wrong; the real cylinder count + engine config is X" and persists that
// override as a User-source CarFacts variant for (game, carId).
//
// A User-source variant outranks Baked / Scanner / Community in
// TryResolveActiveVariant's ordering, so the correction applies on the next
// resolution pass without restarting SimHub. Remove restores the auto-
// detected baseline.
//
// Built in code (not XAML) for the same reason as the other modal windows in
// this folder (see PackPickerWindow.cs): the plugin's XAML build pipeline
// doesn't auto-pick up new .xaml files without a csproj change, and a few
// dozen lines of WPF is cheaper than wiring a compiled-XAML resource.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    internal sealed class CarFactsCorrectionWindow : Window
    {
        // Hard-coded palette: this is a top-level Window so it doesn't
        // inherit SimHub's panel dark theme. Match the palette used by the
        // other plugin modals (PackPickerWindow etc.).
        private static readonly Brush WindowBg     = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg       = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg      = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush BorderFg     = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush HeaderFg     = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));

        /// <summary>Outcome the caller acts on:
        /// Save = write/update the User-source variant with the dialog's values;
        /// Remove = delete any existing User-source variant for this car;
        /// Cancel = no change. DialogResult is set to true for Save/Remove,
        /// false (or null) for Cancel.</summary>
        public enum CorrectionAction { Cancel, Save, Remove }

        public CorrectionAction Action { get; private set; } = CorrectionAction.Cancel;
        public int Cylinders        { get; private set; }
        public EngineConfig Config  { get; private set; } = EngineConfig.Auto;

        private readonly ComboBox _cylCombo;
        private readonly ComboBox _configCombo;
        private readonly Button   _removeBtn;

        /// <param name="carDisplayName">Human-readable name for the car
        /// ("1997 Mazda RX-7"). Falls back to carId in the title when null.</param>
        /// <param name="carId">Identifier the plugin sees for this car.</param>
        /// <param name="autoDetectedSummary">One-line "Currently detected as: ..."
        /// text, built by the caller from EnginePulse.AutoLayout +
        /// AutoLayoutSource. Null hides the line.</param>
        /// <param name="existingCorrection">User-source variant already on
        /// file, or null when the user is creating a fresh correction. Pre-
        /// fills the inputs (when non-null) and enables the Remove button.</param>
        /// <param name="prefillSeed">When <paramref name="existingCorrection"/>
        /// is null, fall back to this variant's cyl + EngineConfig for the
        /// pre-fill so the dialog opens showing what the plugin currently
        /// thinks the car is. Null falls through to the hardcoded 8/Auto
        /// default. Does NOT enable Remove (only User-source corrections
        /// can be removed).</param>
        public CarFactsCorrectionWindow(
            string carDisplayName, string carId, string autoDetectedSummary,
            EngineVariant existingCorrection,
            EngineVariant prefillSeed = null)
        {
            Title           = "Correct engine layout";
            Width           = 460;
            SizeToContent   = SizeToContent.Height;
            Background      = WindowBg;
            Foreground      = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar   = false;
            ResizeMode      = ResizeMode.NoResize;

            string nameDisplay = string.IsNullOrEmpty(carDisplayName) ? carId : carDisplayName;

            var root = new StackPanel { Margin = new Thickness(16) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Correct engine layout for this car",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold,
                FontSize = 14, Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock {
                Text = nameDisplay + (string.IsNullOrEmpty(carDisplayName) ? "" : "  (" + carId + ")"),
                Foreground = MutedFg, Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(new TextBlock {
                Text = "Save what's actually in this car. Wins over the built-in "
                     + "table and any heuristic guess. Applies on every preset "
                     + "you use with this car. Remove to restore the auto value.",
                Foreground = TextFg, Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            if (!string.IsNullOrEmpty(autoDetectedSummary))
            {
                root.Children.Add(new Border {
                    Background = PanelBg, BorderBrush = BorderFg, BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 12),
                    Child = new TextBlock {
                        Text = autoDetectedSummary, Foreground = MutedFg,
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }

            // Cylinder count (1..16). ComboBox so the user can't type an
            // invalid value; matches the discrete-pick feel of EngineConfig.
            var cylRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            cylRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            cylRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cylLabel = new TextBlock {
                Text = "Cylinders:", Foreground = TextFg, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cylLabel, 0);
            cylRow.Children.Add(cylLabel);
            _cylCombo = new ComboBox { Foreground = TextFg, Background = PanelBg, BorderBrush = BorderFg };
            for (int i = 1; i <= 16; i++)
                _cylCombo.Items.Add(new ComboBoxItem { Content = i.ToString(), Foreground = TextFg, Background = PanelBg });
            Grid.SetColumn(_cylCombo, 1);
            cylRow.Children.Add(_cylCombo);
            root.Children.Add(cylRow);

            // EngineConfig (Auto + V60 + V90Even + V8CrossPlane + V8FlatPlane
            // + Boxer + Inline + Rotary + Single + V6OddFire + VTwin90 +
            // VTwin45 + Custom). Auto means "derive from cyl count" - useful
            // when the user is correcting cyl count but doesn't care which
            // specific cross-/flat-plane V8 it is.
            var cfgRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            cfgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            cfgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cfgLabel = new TextBlock {
                Text = "Engine type:", Foreground = TextFg, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cfgLabel, 0);
            cfgRow.Children.Add(cfgLabel);
            _configCombo = new ComboBox { Foreground = TextFg, Background = PanelBg, BorderBrush = BorderFg };
            foreach (EngineConfig c in Enum.GetValues(typeof(EngineConfig)))
            {
                // Custom is authored via the Custom Engine library, not here.
                // Allowing it through this combo would write a User CarFacts
                // variant that has no firing pattern; the runtime would then
                // fall back to even-fire and silently disagree with the
                // label the user picked. Matches the same exclusion at
                // SettingsControl.xaml.cs:3585 in the preset-side Layout combo.
                if (c == EngineConfig.Custom) continue;
                _configCombo.Items.Add(new ComboBoxItem {
                    Content = c.ToString(), Tag = c,
                    Foreground = TextFg, Background = PanelBg,
                });
            }
            Grid.SetColumn(_configCombo, 1);
            cfgRow.Children.Add(_configCombo);
            root.Children.Add(cfgRow);

            // Pre-fill from the existing correction (edit mode) or sensible
            // defaults (new mode).
            // Pre-fill source priority:
            //   1. existingCorrection (the user's prior User-source correction)
            //      so editing-then-saving doesn't lose what they last typed.
            //   2. prefillSeed (whatever the resolver currently picked: Baked
            //      virtual variant, Community, Scanner) so a first-time
            //      correction starts from what the plugin shows, not from a
            //      stale 8 / Auto.
            //   3. Hardcoded 8 / Auto as last resort when neither is set.
            // Custom is coerced to Auto everywhere since the dialog doesn't
            // author firing patterns.
            int defaultCyl = 8;
            EngineConfig defaultCfg = EngineConfig.Auto;
            EngineVariant seed = existingCorrection ?? prefillSeed;
            if (seed != null
                && seed.Cylinders >= 1 && seed.Cylinders <= 16)
            {
                defaultCyl = seed.Cylinders;
                defaultCfg = seed.EngineConfig == EngineConfig.Custom
                    ? EngineConfig.Auto
                    : seed.EngineConfig;
            }
            _cylCombo.SelectedIndex = defaultCyl - 1;
            for (int i = 0; i < _configCombo.Items.Count; i++)
            {
                var item = _configCombo.Items[i] as ComboBoxItem;
                if (item != null && item.Tag is EngineConfig cv && cv == defaultCfg)
                { _configCombo.SelectedIndex = i; break; }
            }

            // Buttons. Save always; Remove only when editing an existing
            // correction; Cancel always.
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            _removeBtn = new Button {
                Content = "Remove correction", Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0), Foreground = TextFg, Background = PanelBg,
                IsEnabled = existingCorrection != null,
                ToolTip = existingCorrection != null
                    ? "Delete your local correction and restore the auto-detected layout."
                    : "No correction to remove for this car.",
            };
            _removeBtn.Click += (s, e) => { Action = CorrectionAction.Remove; DialogResult = true; Close(); };
            btnRow.Children.Add(_removeBtn);

            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0), Foreground = TextFg, Background = PanelBg,
            };
            cancelBtn.Click += (s, e) => { Action = CorrectionAction.Cancel; DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var saveBtn = new Button {
                Content = "Save correction", Padding = new Thickness(10, 4, 10, 4),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
            };
            saveBtn.Click += SaveBtn_Click;
            btnRow.Children.Add(saveBtn);

            root.Children.Add(btnRow);
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!(_cylCombo.SelectedItem is ComboBoxItem cylItem)
                || !int.TryParse(cylItem.Content?.ToString(), out int cyl)
                || cyl < 1 || cyl > 16)
            {
                MessageBox.Show(this, "Pick a cylinder count between 1 and 16.",
                    "Trueforce", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            EngineConfig cfg = EngineConfig.Auto;
            if (_configCombo.SelectedItem is ComboBoxItem cfgItem && cfgItem.Tag is EngineConfig cv)
                cfg = cv;

            Cylinders = cyl;
            Config    = cfg;
            Action    = CorrectionAction.Save;
            DialogResult = true;
            Close();
        }
    }
}
