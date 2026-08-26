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
            GuideBrowserWindow.Show(Window.GetWindow(this), BuildGuideEntries(), initialKey);
        }

        private const string GroupSetup   = "Game setup";
        private const string GroupTrouble = "When something is wrong";
        private const string GroupAbout   = "Good to know";

        private List<GuideEntry> BuildGuideEntries()
        {
            var list = new List<GuideEntry>
            {
                new GuideEntry
                {
                    Key = "iracing-setup", Group = GroupSetup,
                    Title = "iRacing: setup guide",
                    ActionLabel = "Open the Telemetry FFB tab",
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
                    Key = "telemetry-ffb", Group = GroupSetup,
                    Title = "Telemetry Based FFB: what it does",
                    ActionLabel = "Open the Telemetry FFB tab",
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
                    Title = "Matching your wheel to the car: where that data comes from",
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
                     ? " That is one of the direct sources. It arrives as fast as the game sends "
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
                if (FsModStepsHost != null)
                {
                    FsModStepsHost.Children.Clear();
                    FsModStepsHost.Children.Add(MarkdownView.Render(
                        GuideText.Load("farming-sim", GuideContext.Panel)));
                }
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
            if (FsModExpander.IsExpanded) RefreshFsModTargets();
            else FsModExpander.IsExpanded = true;
            Dispatcher.BeginInvoke(new Action(() => FsModExpander.BringIntoView()),
                DispatcherPriority.Background);
        }

        private void FsModExpander_Expanded(object sender, RoutedEventArgs e)
            => RefreshFsModTargets();

        /// <summary>Rebuild one row per Farming Simulator title found on this PC.
        ///
        /// Read on open rather than on the refresh tick: this is disk state that
        /// only changes when someone installs something, and rebuilding buttons
        /// under the pointer several times a second is its own bug.</summary>
        private void RefreshFsModTargets()
        {
            if (_plugin == null || FsModTargetsPanel == null) return;
            FsModTargetsPanel.Children.Clear();

            int found = 0;
            foreach (var t in _plugin.FsModTargets())
            {
                // A title that isn't on this PC gets no row. Offering to install
                // into a folder that does not exist only produces a failure the
                // user cannot act on.
                if (!t.GameFound) continue;
                found++;
                FsModTargetsPanel.Children.Add(BuildFsModRow(t));
            }

            if (FsModTargetsStatus == null) return;
            if (found == 0)
            {
                // Deliberately does not name Documents\My Games. Farming Simulator
                // lets players relocate their mods folder, and TryGetFsModInfo
                // honours that override, so the folder we looked in is not always
                // the one this sentence used to name. Sending a heavy modder to
                // inspect the wrong path is worse than not naming one.
                FsModTargetsStatus.Text =
                    "No Farming Simulator mods folder was found on this PC. Start the game "
                    + "once so it creates its folders, then come back.";
                FsModTargetsStatus.Visibility = Visibility.Visible;
            }
            else
            {
                FsModTargetsStatus.Visibility = Visibility.Collapsed;
            }
        }

        private UIElement BuildFsModRow(TrueforcePlugin.FsModTarget t)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = t.DisplayName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);

            var state = new TextBlock
            {
                Text = t.Installed ? "Mod installed" : "Mod not installed",
                FontSize = 11,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(state, 1);

            // Reinstall is the repair path: same copy, over whatever is there. It
            // is also how someone picks up a newer mod after a plugin update
            // without waiting for the game-detection refresh to notice.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 2);

            var btn = new Button
            {
                Content = t.Installed ? "Reinstall" : "Install",
                Padding = new Thickness(12, 3, 12, 3),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = t.Installed
                    ? "Copy the mod in again, over the one that is there."
                    : "Copy the mod into this game's mods folder."
            };
            btn.Click += (s, ev) => InstallFsModFor(t);
            buttons.Children.Add(btn);

            // Only where there is something to remove. We put the file in that
            // folder, so taking it back out belongs here rather than in a
            // support answer telling someone to go and delete it themselves.
            if (t.Installed)
            {
                var rm = new Button
                {
                    Content = "Remove",
                    Padding = new Thickness(12, 3, 12, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Delete the mod from this game's mods folder."
                };
                rm.Click += (s, ev) => UninstallFsModFor(t);
                buttons.Children.Add(rm);
            }

            row.Children.Add(name);
            row.Children.Add(state);
            row.Children.Add(buttons);
            return row;
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
            RefreshFsModTargets();
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
            RefreshFsModTargets();
            if (FsModTargetsStatus == null) return;
            FsModTargetsStatus.Text = outcome;
            FsModTargetsStatus.Visibility = Visibility.Visible;
        }
    }
}
