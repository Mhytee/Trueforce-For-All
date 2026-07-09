// Section picker shown when the user clicks Download on a community
// preset. Lists every non-null section in the incoming CarOverride with a
// checkbox so the user can take just the parts they want. Checked
// sections come back via ChosenSections on Apply; an empty result means
// "nothing applied" and the caller should treat it as cancel.
//
// Styling matches the other modals (CarFactsShareWindow, etc.).

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class CommunitySectionPickerWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));

        // Section tag → display label (matches the upload effect_tags whitelist).
        private static readonly (string tag, string label)[] AllSections = new (string, string)[]
        {
            ("engine",       "Engine pulse"),
            ("revlimiter",   "Rev limiter"),
            ("roadbumps",    "Road bumps"),
            ("tractionloss", "Traction loss"),
            ("axleslip",     "Axle slip"),
            ("kerbthump",    "Kerb thump"),
            ("lockupjudder", "Lockup judder"),
            ("gearshift",    "Gear shift"),
            ("abs",          "ABS click"),
            ("pitlimiter",   "Pit limiter"),
            ("drs",          "DRS"),
            ("collision",    "Collision"),
            ("audio",        "Audio rumble"),
            ("airborne",     "Airborne ducking"),
        };

        public HashSet<string> ChosenSections { get; private set; } = new HashSet<string>();

        public CommunitySectionPickerWindow(string presetName, string author, CarOverride ovr)
        {
            Title         = "Download community preset";
            Width         = 440;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Pick what to import",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            string subtitle = string.IsNullOrEmpty(author)
                ? presetName ?? ""
                : (presetName ?? "") + "   by " + author;
            root.Children.Add(new TextBlock {
                Text = subtitle, Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });
            root.Children.Add(new TextBlock {
                Text = "Sections the preset contains. Uncheck anything you'd rather keep your current values for.",
                Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            });

            // One checkbox per non-null section. Built so the import path
            // can read state via the checkbox Tag = tag string.
            var checkboxes = new List<CheckBox>();
            foreach (var (tag, label) in AllSections)
            {
                if (!IsSectionPresent(ovr, tag)) continue;
                var cb = new CheckBox {
                    Content = label, IsChecked = true,
                    Foreground = TextFg, Margin = new Thickness(0, 0, 0, 4),
                    Tag = tag,
                };
                checkboxes.Add(cb);
                root.Children.Add(cb);
            }
            if (checkboxes.Count == 0)
            {
                root.Children.Add(new TextBlock {
                    Text = "The preset doesn't contain any importable sections.",
                    Foreground = MutedFg, FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }

            var btnRow = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var cancelBtn = new Button {
                Content = "Cancel", Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancelBtn);

            var applyBtn = new Button {
                Content = "Apply", Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsDefault = true,
                IsEnabled = checkboxes.Count > 0,
            };
            applyBtn.Click += (s, e) =>
            {
                ChosenSections = new HashSet<string>();
                foreach (var cb in checkboxes)
                    if (cb.IsChecked == true && cb.Tag is string t)
                        ChosenSections.Add(t);
                if (ChosenSections.Count == 0)
                {
                    DialogResult = false;  // nothing checked; treat as cancel
                    Close();
                    return;
                }
                DialogResult = true;
                Close();
            };
            btnRow.Children.Add(applyBtn);

            // Keep Apply disabled while nothing is checked, so unchecking
            // everything and clicking Apply can't silently close as a cancel.
            void SyncApplyEnabled()
            {
                bool any = false;
                foreach (var cb in checkboxes) if (cb.IsChecked == true) { any = true; break; }
                applyBtn.IsEnabled = any;
            }
            foreach (var cb in checkboxes)
            {
                cb.Checked   += (s, e) => SyncApplyEnabled();
                cb.Unchecked += (s, e) => SyncApplyEnabled();
            }
            SyncApplyEnabled();

            root.Children.Add(btnRow);
        }

        private static bool IsSectionPresent(CarOverride ovr, string tag)
        {
            if (ovr == null) return false;
            switch (tag)
            {
                case "engine":       return ovr.EnginePulse  != null;
                case "revlimiter":   return ovr.RevLimiter   != null;
                case "roadbumps":    return ovr.RoadBumps    != null;
                case "tractionloss": return ovr.TractionLoss != null;
                case "axleslip":     return ovr.AxleSlip     != null;
                case "kerbthump":    return ovr.KerbThump    != null;
                case "lockupjudder": return ovr.LockupJudder != null;
                case "gearshift":    return ovr.GearShift    != null;
                case "abs":          return ovr.AbsClick     != null;
                case "pitlimiter":   return ovr.PitLimiter   != null;
                case "drs":          return ovr.Drs          != null;
                case "collision":    return ovr.Collision    != null;
                case "audio":        return ovr.AudioCapture != null;
                case "airborne":     return ovr.Airborne     != null;
                default: return false;
            }
        }
    }
}
