// The "?" button in the header card: the plugin's guides, and the standing
// Farming Simulator install control one of them opens.
//
// Setup is what the first entries happen to be about, not what the list is:
// some games need something done outside the plugin before the wheel does
// anything (iRacing's two switches, Forza's Data Out, Farming Simulator's game
// mod), and every one of those was delivered by a banner that appears when the
// setup is already wrong, or a dialog that fires once and is gone the moment it
// is answered. Neither is somewhere a user can GO. This is. Anything else worth
// walking someone through belongs here too, so nothing in the button or its
// tooltip promises setup specifically.
//
// EVERY entry is a document. An earlier version mixed documents with entries
// that silently navigated the panel, which meant the list could not be browsed:
// you had to already know which rows were safe to click. Going somewhere is now
// always the second step, behind the guide's own action button, and it happens
// after the window closes rather than underneath it.
//
// The text lives in Guides\*.md, embedded in the DLL (see GuideText) and
// rendered by MarkdownView, the same renderer the release notes use. Two of
// those files are ALSO rendered into the settings panel itself (the Forza and
// Farming Simulator steps), so the steps a reader follows in the guide and the
// steps printed beside the controls are one text, not two that drift.
//
// Its own partial class: SettingsControl.xaml.cs is past 13,000 lines and this
// list will grow.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        // ---- the list -----------------------------------------------------------

        private void HelpGuides_Click(object sender, RoutedEventArgs e)
            => OpenGuides(null);

        /// <summary>Open the browser, optionally straight to one guide (the "why?"
        /// links that sit beside individual settings).</summary>
        private void OpenGuides(string initialKey)
        {
            if (_plugin == null) return;
            GuideBrowserWindow.Show(Window.GetWindow(this), BuildGuideEntries(), initialKey, GoToPanelTab);
        }

        /// <summary>Resolve a [label](tab:key) link inside a guide to a place in
        /// the settings panel: a tab, or a section within one. The browser has
        /// already closed when this runs.</summary>
        private void GoToPanelTab(string key)
        {
            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case "controls":  SelectTab(ControlsTab); break;
                case "effects":   SelectTab(EffectsTab); break;
                case "lightsync": SelectTab(LightsyncTab); break;
                case "settings":  SelectTab(SettingsTab); break;
                case "ffb":       SelectTab(TelemetryFfbTab); break;
                case "color-trim":
                    // A section rather than a tab: LIGHTSYNC forward, the Color
                    // Trim expander open, and the scroll deferred until the tab
                    // has laid out (the JumpToForzaTelemetrySetup pattern).
                    SelectTab(LightsyncTab);
                    if (LedTrimExpander != null)
                    {
                        LedTrimExpander.IsExpanded = true;
                        Dispatcher.BeginInvoke(new Action(() => LedTrimExpander.BringIntoView()),
                            DispatcherPriority.Background);
                    }
                    break;
            }
        }

        private const string GroupSetup   = "Game setup";
        private const string GroupTrouble = "When something is wrong";
        private const string GroupAbout   = "Good to know";

        /// <summary>"Open the &lt;tab&gt; tab", using the name the tab is actually
        /// wearing right now.
        ///
        /// That header is not fixed: it is "Telemetry FFB" normally, "FFB" in the
        /// reshape games, and "Wheel (FFB, LED)" when the lights have not been
        /// given their own tab. A hard-coded label sends the reader looking for a
        /// tab that is not on screen, and it does so worst in iRacing, which is
        /// the one game whose guide is mostly about that tab.</summary>
        private string TelemetryFfbTabLabel()
        {
            string header = TelemetryFfbTab?.Header as string;
            return "Open the " + (string.IsNullOrEmpty(header) ? "Telemetry FFB" : header) + " tab";
        }

        private List<GuideEntry> BuildGuideEntries()
        {
            var list = new List<GuideEntry>
            {
                new GuideEntry
                {
                    Key = "iracing-setup", Group = GroupSetup,
                    Title = "iRacing: setup guide",
                    ActionLabel = TelemetryFfbTabLabel(),
                    Action = () => SelectTab(TelemetryFfbTab),
                },
                new GuideEntry
                {
                    Key = "forza-setup", Group = GroupSetup,
                    Title = "Forza: UDP & Data Out setup",
                    ActionLabel = "Open the UDP settings",
                    Action = () => JumpToForzaTelemetrySetup(openTroubleshooter: false),
                    // The forwarding half lives in its own file because the panel
                    // shows it in a different place: down beside the forward host
                    // and port boxes, not under the Data Out steps. One text, two
                    // homes, rather than a second copy written for each.
                    Extra = () => GuideText.Load("forza-forward"),
                },
                new GuideEntry
                {
                    Key = "farming-sim", Group = GroupSetup,
                    Title = "Farming Simulator: install the telemetry mod",
                    ActionLabel = "Open the mod installer",
                    Action = JumpToFsModSetup,
                },
                new GuideEntry
                {
                    Key = "assetto-corsa-setup", Group = GroupSetup,
                    Title = "Assetto Corsa: the TF4ALL CSP Bridge",
                    ActionLabel = "Install the TF4ALL CSP Bridge",
                    Action = () => _plugin.InstallAcCspBridgeInteractive(),
                },
                new GuideEntry
                {
                    Key = "telemetry-ffb", Group = GroupSetup,
                    Title = "Telemetry Based FFB: what it does",
                    ActionLabel = TelemetryFfbTabLabel(),
                    Action = () => SelectTab(TelemetryFfbTab),
                },

                new GuideEntry
                {
                    Key = "ffb-not-working", Group = GroupTrouble,
                    Title = "Force feedback: limp, weak, or silent",
                    ActionLabel = "Run the self-test",
                    Action = RunWheelCheck,
                },
                new GuideEntry
                {
                    Key = "weak-effects", Group = GroupTrouble,
                    Title = "Effects feel weak, and the dial on the wheel does nothing",
                    ActionLabel = "Open the gain controls",
                    Action = () =>
                    {
                        SelectTab(EffectsTab);
                        Dispatcher.BeginInvoke(new Action(() => MasterGainSlider?.BringIntoView()),
                            DispatcherPriority.Background);
                    },
                },
                new GuideEntry
                {
                    Key = "wheel-lights", Group = GroupTrouble,
                    Title = "Rev lights and wheel screen: why they only work in some games",
                    ActionLabel = "Open the lights settings",
                    Action = JumpToWheelLights,
                },

                new GuideEntry
                {
                    // Gated on the tab existing, which is BOTH terms of the rule in
                    // ApplyLightsyncTabVisibility, not just the unlock. A detected
                    // wheel whose strip has a fixed look (the G923) never gets the
                    // tab however unlocked the setting is, and listing a guide whose
                    // button selects a collapsed tab strands the reader on an empty
                    // pane. Fails OPEN on an undetected wheel, same as the tab does,
                    // so a wheel powered on after SimHub does not lose the entry.
                    Key = "light-patterns", Group = GroupAbout,
                    Title = "Light patterns: the library, cycling, and color tuning",
                    Visible = () => _plugin?.Settings?.LightsyncTabUnlocked == true
                                 && !(_plugin.WheelDetected && !_plugin.WheelHasSelectableLightPattern),
                    ActionLabel = "Open the LIGHTSYNC tab",
                    Action = () => SelectTab(LightsyncTab),
                },
                new GuideEntry
                {
                    Key = "lovely-car-data", Group = GroupAbout,
                    Title = "Per-car data: what it sets, and where it comes from",
                    ActionLabel = "Open the Lovely dataset",
                    Action = () => OpenUrl("https://github.com/Lovely-Sim-Racing/lovely-car-data"),
                },
                new GuideEntry
                {
                    Key = "native-trueforce", Group = GroupAbout,
                    Title = "Games with native Trueforce: why the plugin steps aside",
                    ActionLabel = "Open the full table",
                    Action = () => OpenUrl(
                        "https://github.com/Mhytee/Trueforce-For-All#games-with-native-trueforce"),
                },
                new GuideEntry
                {
                    Key = "bindings", Group = GroupAbout,
                    Title = "Bindings: what you can control without opening the panel",
                    ActionLabel = "Open the Controls tab",
                    Action = () => SelectTab(ControlsTab),
                },
                new GuideEntry
                {
                    Key = "normal-ffb", Group = GroupAbout,
                    Title = "Will this change or replace my normal force feedback?",
                },
                new GuideEntry
                {
                    Key = "simhub-license", Group = GroupAbout,
                    Title = "Do I need to pay for SimHub?",
                    Extra = LiveTelemetryRateLine,
                },
                new GuideEntry
                {
                    Key = "usbpcap", Group = GroupAbout,
                    Title = "Why does it need USBPcap, and is that safe?",
                },
                new GuideEntry
                {
                    Key = "anti-cheat", Group = GroupAbout,
                    Title = "Is this anti-cheat safe?",
                },
            };
            return list;
        }

        /// <summary>The one thing a document cannot do: tell the reader what rate
        /// they are getting right now. We cannot ask SimHub whether it is licensed,
        /// but the rate IS the difference, and we measure it.</summary>
        private string LiveTelemetryRateLine()
        {
            var src = _plugin?.TelemetrySource;
            double hz = src?.MeasuredHz ?? 0;
            if (src == null || hz <= 0)
                return "Start a game and the Diagnostics section on the Settings tab will show "
                     + "the rate you are getting.";
            return $"**Right now: {src.Name} at {hz:0} Hz.**"
                 + (src.IsEnhanced
                     ? " This data comes straight from the game, as fast as it sends "
                       + "it, which in the Forza titles follows your frame rate, so a licence "
                       + "changes nothing here."
                     : "");
        }

        private void SelectTab(object tab)
        {
            if (MainTabs != null && tab != null) MainTabs.SelectedItem = tab;
        }

        // ---- the panel's own copies of two guides -------------------------------

        /// <summary>Render the Forza and Farming Simulator step lists into the
        /// settings panel from the SAME Markdown the guides show.
        ///
        /// Those steps used to be four and three hand-written TextBlocks in the
        /// XAML. Once the guides carried the same steps there were two texts to
        /// keep in agreement, and the one nobody would remember to update is the
        /// one printed next to the controls. Called once, from the panel's
        /// first refresh.</summary>
        private void RenderEmbeddedGuideSteps()
        {
            if (_guideStepsRendered) return;
            _guideStepsRendered = true;
            try
            {
                if (ForzaStepsHost != null)
                {
                    ForzaStepsHost.Children.Clear();
                    ForzaStepsHost.Children.Add(MarkdownView.Render(
                        GuideText.Load("forza-setup", GuideContext.Panel)));
                }
                if (ForzaForwardStepsHost != null)
                {
                    ForzaForwardStepsHost.Children.Clear();
                    ForzaForwardStepsHost.Children.Add(
                        MarkdownView.Render(GuideText.Load("forza-forward", GuideContext.Panel)));
                }
                // The Farming Simulator block is NOT rendered here. It is the
                // one whose text depends on state that changes while the panel is
                // open, so RefreshModsList owns it.
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TF4ALL] Guide steps render failed: " + ex.Message);
            }
        }
        private bool _guideStepsRendered;

        // ---- routes the guides' action buttons take -----------------------------

        /// <summary>Settings tab, Diagnostics open, and the self-test run. Deferred
        /// so the tab has laid out before the result lands under it.</summary>
        private void RunWheelCheck()
        {
            SelectTab(SettingsTab);
            if (DiagnosticsExpander != null) DiagnosticsExpander.IsExpanded = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SelfTestButton?.BringIntoView();
                SelfTest_Click(null, null);
            }), DispatcherPriority.Background);
        }

        private void WheelLightsWhy_Click(object sender, RoutedEventArgs e)
            => OpenGuides("wheel-lights");

        /// <summary>The "What this covers" link beside the Lovely credit. The
        /// credit itself stays in the panel because the licence asks for it;
        /// this is the disclosure that used to sit under it.</summary>
        private void LovelyGuide_Click(object sender, RoutedEventArgs e)
            => OpenGuides("lovely-car-data");

        /// <summary>Open the lights section, wherever it currently lives: the block
        /// is REPARENTED into the LIGHTSYNC tab when that tab is unlocked, so its
        /// tab cannot be named here without being wrong for half of users. Walking
        /// up to whichever tab owns it right now is the only version that stays
        /// true.</summary>
        private void JumpToWheelLights()
        {
            if (WheelLightsBlock == null) return;
            var tab = FindAncestorTab(WheelLightsBlock);
            if (tab != null) SelectTab(tab);
            Dispatcher.BeginInvoke(new Action(() => WheelLightsBlock.BringIntoView()),
                DispatcherPriority.Background);
        }

        private static TabItem FindAncestorTab(DependencyObject d)
        {
            while (d != null)
            {
                if (d is TabItem tab) return tab;
                // Logical first: a reparented panel keeps its logical parent chain
                // even before the visual tree for that tab has been built.
                var next = LogicalTreeHelper.GetParent(d);
                if (next == null && d is System.Windows.Media.Visual)
                    next = System.Windows.Media.VisualTreeHelper.GetParent(d);
                d = next;
            }
            return null;
        }

        // ---- Farming Simulator mod: the standing install control ----------------

        /// <summary>Settings tab, Farming Simulator section open and scrolled to.
        /// The FS twin of JumpToForzaTelemetrySetup.</summary>
        private void JumpToFsModSetup()
        {
            SelectTab(SettingsTab);
            // Expanding raises Expanded, which refreshes the rows, so the state
            // shown is read at the moment it is looked at.
            if (FsModExpander == null) return;
            if (FsModExpander.IsExpanded) RefreshModsList();
            else FsModExpander.IsExpanded = true;
            Dispatcher.BeginInvoke(new Action(() => FsModExpander.BringIntoView()),
                DispatcherPriority.Background);
        }

        private void FsModExpander_Expanded(object sender, RoutedEventArgs e)
            => RefreshModsList();

        /// <summary>Rebuild one row per Farming Simulator title found on this PC.
        ///
        /// Read on open rather than on the refresh tick: this is disk state that
        /// only changes when someone installs something, and rebuilding buttons
        /// under the pointer several times a second is its own bug.</summary>
        // Rebuild the mods list: one card per installable mod found on this PC.
        // This is the home for every game mod (Farming Simulator telemetry, the
        // Assetto Corsa TF4ALL CSP Bridge, and any future one). Each card is
        // uniform: title + version, a short description, a status line, and
        // Install/Remove buttons.
        private void RefreshModsList()
        {
            if (_plugin == null || FsModTargetsPanel == null) return;
            FsModTargetsPanel.Children.Clear();

            // Group as we build so installed mods can lead the list, then the
            // available-but-not-installed, then the greyed unavailable ones.
            var installedCards = new List<UIElement>();
            var availableCards = new List<UIElement>();
            var otherCards = new List<UIElement>();
            void Add(bool installed, bool available, UIElement card)
                => (installed ? installedCards : available ? availableCards : otherCards).Add(card);

            // Farming Simulator: one card per title we ship a mod for, whether
            // or not it is on this PC. A title that is not installed still shows,
            // greyed, with a note, so the list is the same on every machine.
            foreach (var t in _plugin.FsModTargets())
            {
                var target = t;
                bool found = t.GameFound;
                bool inst = found && t.Installed;
                string status = !found
                    ? "Game not installed on this PC."
                    : t.Installed
                        ? "Installed. Tick it in the game's mod list when you load your save."
                        : "Not installed.";
                Add(inst, found, BuildModCard(
                    t.DisplayName,
                    "Enhanced telemetry: ground texture through the wheel, the implement thud, and the vibration cut while your wheels are off the ground.",
                    "v" + _plugin.FsModVersionString,
                    found, t.Installed, status,
                    found ? (Action)(() => InstallFsModFor(target)) : null,
                    inst ? (Action)(() => UninstallFsModFor(target)) : null));
            }

            // Assetto Corsa: the TF4ALL CSP Bridge. Greyed when Assetto Corsa
            // with Custom Shaders Patch is not on this PC.
            bool avail = _plugin.AcCspAvailable();
            bool acIn = avail && _plugin.AcCspBridgeInstalled();
            string acStatus = !avail
                ? "Needs Assetto Corsa with Custom Shaders Patch."
                : acIn
                    ? "Installed. Restart Assetto Corsa so CSP loads it."
                    : "Not installed.";
            Add(acIn, avail, BuildModCard(
                "Assetto Corsa: TF4ALL CSP Bridge",
                "Requires Custom Shaders Patch. Unlocks the wheel's Dynamic OLED display and keeps LIGHTSYNC pattern changes from cutting the force feedback.",
                "v" + _plugin.AcCspBridgeVersionString,
                avail, acIn, acStatus,
                avail ? (Action)(() => InstallAcCspRow()) : null,
                acIn ? (Action)(() => UninstallAcCspRow()) : null));

            foreach (var c in installedCards) FsModTargetsPanel.Children.Add(c);
            foreach (var c in availableCards) FsModTargetsPanel.Children.Add(c);
            foreach (var c in otherCards) FsModTargetsPanel.Children.Add(c);

            // The bottom line is only for install/remove outcomes now; every mod
            // has its own card, so there is no "nothing found" case to report.
            if (FsModTargetsStatus != null) FsModTargetsStatus.Visibility = Visibility.Collapsed;
        }

        private static System.Windows.Media.Brush ModCardBrush(string hex)
            => new System.Windows.Media.SolidColorBrush(
                   (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        // One uniform mod card, drawn in a bordered panel: title + version across
        // the top with the Install/Remove buttons, a wrapped description, then a
        // status line. When the mod is not available on this PC (game or CSP
        // missing) the whole card is dimmed and Install is disabled.
        private UIElement BuildModCard(string title, string description, string version,
                                       bool available, bool installed, string status,
                                       Action onInstall, Action onRemove)
        {
            var card = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleWrap.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(version))
                titleWrap.Children.Add(new TextBlock
                {
                    Text = "  " + version,
                    FontSize = 11,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            Grid.SetColumn(titleWrap, 0);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(buttons, 1);
            var install = new Button
            {
                Content = installed ? "Reinstall" : "Install",
                Padding = new Thickness(12, 3, 12, 3),
                IsEnabled = available && onInstall != null,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = installed ? "Copy the mod in again, over the one that is there." : "Install this mod.",
            };
            if (install.IsEnabled)
            {
                ModalButtonTheme.Primary(install);
                install.Click += (s, ev) => onInstall?.Invoke();
            }
            buttons.Children.Add(install);
            if (onRemove != null)
            {
                var rm = new Button
                {
                    Content = "Remove",
                    Padding = new Thickness(12, 3, 12, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Remove this mod.",
                };
                ModalButtonTheme.Destructive(rm);
                rm.Click += (s, ev) => onRemove.Invoke();
                buttons.Children.Add(rm);
            }

            head.Children.Add(titleWrap);
            head.Children.Add(buttons);
            card.Children.Add(head);

            var help = TryFindResource("HelpText") as Style;
            card.Children.Add(new TextBlock
            {
                Text = description,
                Style = help,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
            if (!string.IsNullOrEmpty(status))
                card.Children.Add(new TextBlock
                {
                    Text = status,
                    FontSize = 11,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });

            var border = new Border
            {
                Background = ModCardBrush("#14FFFFFF"),
                BorderBrush = ModCardBrush("#33FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = card,
                Opacity = available ? 1.0 : 0.55,
            };
            return border;
        }

        // Mods-list Install/Remove for the Assetto Corsa bridge, with inline
        // status like the Farming Simulator rows. Remove is confirmed.
        private void InstallAcCspRow()
        {
            if (_plugin == null) return;
            string err = _plugin.InstallAndEnableAcCspBridge();
            // A failure in the status line at the foot of the section goes
            // unseen; a refusal needs a dialog the user cannot miss, and
            // "close Content Manager and try again" should be one click.
            while (err != null)
            {
                // The dialog carries a real link to the setup guide instead of
                // describing where to find it.
                var guideLink = new TextBlock { Margin = new Thickness(0, 8, 0, 0) };
                var link = new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("Open the Assetto Corsa setup guide"))
                {
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0xB4, 0xEE)),
                };
                link.Click += (s2, e2) => OpenGuides("assetto-corsa-setup");
                guideLink.Inlines.Add(link);
                bool? again = TrueforceDialog.Show(Window.GetWindow(this),
                    "Could not install the TF4ALL CSP Bridge", err,
                    DialogKind.Error, okLabel: "Retry", cancelLabel: "Cancel", goldOk: true,
                    extraContent: guideLink);
                if (again != true) { RefreshModsList(); return; }
                err = _plugin.InstallAndEnableAcCspBridge();
            }
            string outcome = "TF4ALL CSP Bridge: installed. Restart Assetto Corsa if it is running, and keep your in-game gain up.";
            RefreshModsList();
            if (FsModTargetsStatus == null) return;
            FsModTargetsStatus.Text = outcome;
            FsModTargetsStatus.Visibility = Visibility.Visible;
        }

        private void UninstallAcCspRow()
        {
            if (_plugin == null) return;
            bool? go = TrueforceDialog.Show(Window.GetWindow(this),
                "Remove the TF4ALL CSP Bridge?",
                "This deletes the bridge script from Assetto Corsa and unselects it in CSP's FFB Tweaks.\n\n"
                + "Your force feedback keeps working through the USB capture. What you lose is the wheel's "
                + "Dynamic OLED display and drop-free LIGHTSYNC pattern changes in Assetto Corsa.\n\n"
                + "It stops loading the next time Assetto Corsa starts.\n\nIf Content Manager is open, close it first, then click Remove.",
                DialogKind.Destructive, okLabel: "Remove", cancelLabel: "Keep it");
            if (go != true) return;
            string err = _plugin.UninstallAcCspBridge();
            while (err != null)
            {
                bool? again = TrueforceDialog.Show(Window.GetWindow(this),
                    "Could not remove the TF4ALL CSP Bridge", err,
                    DialogKind.Error, okLabel: "Retry", cancelLabel: "Cancel", goldOk: true);
                if (again != true) { RefreshModsList(); return; }
                err = _plugin.UninstallAcCspBridge();
            }
            string outcome = "TF4ALL CSP Bridge: removed. It stops loading the next time Assetto Corsa starts.";
            RefreshModsList();
            if (FsModTargetsStatus == null) return;
            FsModTargetsStatus.Text = outcome;
            FsModTargetsStatus.Visibility = Visibility.Visible;
        }

        /// <summary>Confirm, then remove. Confirmed because it deletes a file
        /// from another product's folder, and the confirm says the thing a user
        /// actually needs to weigh: the steering is not what they are giving
        /// up.</summary>
        private void UninstallFsModFor(TrueforcePlugin.FsModTarget t)
        {
            if (_plugin == null) return;
            bool? go = TrueforceDialog.Show(Window.GetWindow(this),
                "Remove the Farming Simulator mod?",
                "This deletes TF4ALL Enhanced Telemetry from " + t.DisplayName + "'s mods "
                + "folder.\n\n"
                + "Your force feedback keeps working. The plugin builds Farming Simulator's "
                + "steering force itself and does not need the mod. What you lose is what the "
                + "mod adds on top: ground texture through the wheel, the implement thud, and "
                + "the airborne cut.\n\n"
                + "It stops loading the next time the game starts.",
                DialogKind.Destructive, okLabel: "Remove", cancelLabel: "Keep it");
            if (go != true) return;

            string err = _plugin.UninstallFsMod(t.Game);
            string outcome = err == null
                ? t.DisplayName + ": removed. It stops loading the next time the game starts."
                : t.DisplayName + ": could not remove it. " + err + ".";
            RefreshModsList();
            if (FsModTargetsStatus == null) return;
            FsModTargetsStatus.Text = outcome;
            FsModTargetsStatus.Visibility = Visibility.Visible;
        }

        private void InstallFsModFor(TrueforcePlugin.FsModTarget t)
        {
            if (_plugin == null) return;
            string err = _plugin.InstallFsMod(t.Game);
            string outcome = err == null
                ? t.DisplayName + ": installed. It loads the next time the game starts, "
                  + "so restart it if it is running now, and tick the mod when you load your save."
                : t.DisplayName + ": install failed. " + err + ".";
            // Rebuild first so the row reads Installed / Reinstall, THEN write the
            // outcome: the rebuild owns that line and would clear it.
            RefreshModsList();
            if (FsModTargetsStatus == null) return;
            FsModTargetsStatus.Text = outcome;
            FsModTargetsStatus.Visibility = Visibility.Visible;
        }
    }
}
