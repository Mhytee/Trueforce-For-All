// The light-pattern maker on the LIGHTSYNC tab.
//
// Deliberately its own partial class: SettingsControl.xaml.cs is already past
// 13,000 lines and adding another feature to it makes the file harder to work
// in for everyone.
//
// The ten rectangles ARE the strip, left to right. Index 0 is the leftmost LED,
// confirmed on hardware, so what the user arranges here is what lights there
// with no mental flip. Preview writes the pattern to the slot they lent us and
// sweeps the bar, because a static picture cannot show which way it fills.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        private LightPatternStore _patternStore;
        private LightPatternLibrary _patternLib;
        private LightPattern _editing;
        private int _selectedLed;
        private Border[] _ledCells;
        private bool _patternUiLoading;

        // Enough range to build a real rev pattern without a colour-wheel dialog,
        // which WPF does not provide and which would be overkill for ten LEDs.
        private static readonly string[] Palette =
        {
            "FF0000", "FF4000", "FF8000", "FFC000", "FFFF00", "C0FF00",
            "00FF00", "00FF80", "00FFFF", "0080FF", "0000FF", "8000FF",
            "FF00FF", "FF0080", "FFFFFF", "808080", "202020", "000000",
        };

        private static readonly (string Label, byte Wire)[] Directions =
        {
            ("Left to right",          3),
            ("Right to left",          4),
            ("Outward from the middle", 1),
            ("Inward from the ends",    2),
        };

        private void EnsurePatternUi()
        {
            if (_patternStore != null) return;

            // The plugin owns the library so a bound button can cycle it with no
            // window open; the editor works on that same instance rather than a
            // second copy that would drift.
            _patternStore = _plugin.LightPatternStoreInstance;
            _patternLib = _plugin.LightPatterns;

            // Opening this tab means the user is about to work on lighting, so
            // bring the channel up now rather than letting the first click be the
            // one that quietly fails. Also lets the slot names and contents below
            // be read at all.
            _plugin.EnsureLedChannelOpen();

            BuildLedCells();
            BuildPalette();
            RefreshSlotNames();

            // Work out which pattern is in which slot by looking at the wheel.
            // A slot whose colours we already have is ASSIGNED to that pattern
            // rather than copied in, which is what keeps one pattern one entry.
            // New contents are added only the first time, so deleting an imported
            // pattern does not bring it back next launch.
            // The first time, put the list in the order the WHEEL is already in,
            // so the top five are the five the user can see on the rim. After
            // that, position is what drives the wheel rather than the reverse.
            if (!_patternLib.ImportedFromWheel)
            {
                LightPatternStore.AdoptWheelOrder(_patternLib, sl => _plugin?.ReadSlot(sl),
                                                  WheelLedChannel.CustomSlotCount, allowAdd: true,
                                                  wireHex: p => _plugin != null
                                                      ? _plugin.ToWireHex(p)
                                                      : LightSlotBackupStore.ToHex(p.Rgb()));
                _patternLib.ImportedFromWheel = true;
            }
            LightPatternStore.NormalizeSlots(_patternLib);
            _patternStore.Save(_patternLib);

            // Make the wheel agree with the list. On a first run this writes no
            // colours (the order was just adopted FROM the wheel) but it does put
            // the pattern names onto the slots, so the base's own menu reads as
            // the patterns rather than whatever they were called before.
            string syncMsg;
            _plugin.SyncSlotsToWheel(out syncMsg);
            RefreshSlotNames();

            if (PatternDirectionCombo != null && PatternDirectionCombo.Items.Count == 0)
                foreach (var d in Directions) PatternDirectionCombo.Items.Add(d.Label);

            // Point the library at whatever the wheel is showing right now, so
            // the editor opens on the pattern the user is actually looking at
            // rather than an arbitrary first entry. Writes nothing: the colours
            // are already on the wheel.
            if (string.IsNullOrEmpty(_patternLib.CurrentId))
                AdoptSlotContentsAsPattern(_plugin.StageSlot());

            RefreshPatternList();
            RefreshBrightness();
            RefreshLedTrim();
        }

        /// <summary>Show the wheel's current brightness, or hide the control on a
        /// wheel that does not offer it. Reads only: the value is whatever the
        /// user already had, and nothing here writes it back.</summary>
        private void RefreshBrightness()
        {
            if (LedBrightnessRow == null || LedBrightnessSlider == null) return;
            int pct = _plugin?.ReadLedBrightness() ?? -1;
            if (pct < 0)
            {
                LedBrightnessRow.Visibility = Visibility.Collapsed;
                return;
            }
            _patternUiLoading = true;
            try
            {
                LedBrightnessSlider.Value = pct;
                if (LedBrightnessText != null) LedBrightnessText.Text = pct + "%";
                LedBrightnessRow.Visibility = Visibility.Visible;
            }
            finally { _patternUiLoading = false; }
        }

        private void LedBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_patternUiLoading || _plugin == null) return;
            int pct = (int)Math.Round(LedBrightnessSlider.Value);
            if (LedBrightnessText != null) LedBrightnessText.Text = pct + "%";
            // Throttled like the colour edits: dragging a slider would otherwise
            // fire a write per pixel down the pipe the wheel shares with force.
            _pendingBrightness = pct;
            if (_brightnessTimer == null)
            {
                _brightnessTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(250) };
                _brightnessTimer.Tick += (s, ev) =>
                {
                    _brightnessTimer.Stop();
                    if (!_plugin.WriteLedBrightness(_pendingBrightness))
                        SetPatternStatus("The wheel would not accept a brightness change.");
                };
            }
            _brightnessTimer.Stop();
            _brightnessTimer.Start();
        }

        private int _pendingBrightness = -1;
        private System.Windows.Threading.DispatcherTimer _brightnessTimer;

        // ---------------- colour trim ----------------

        /// <summary>Put the stored trim on the three sliders. Percentages, because
        /// "green at 59%" is a thing a person can hold in their head where 0.588
        /// is not.</summary>
        private void RefreshLedTrim()
        {
            if (LedTrimRSlider == null || _plugin?.Settings == null) return;
            _patternUiLoading = true;
            try
            {
                // Shows what is IN FORCE, which for anyone who has never touched
                // these is the shipped tuning rather than 100 across the board.
                float r, g, b; _plugin.EffectiveLedTrim(out r, out g, out b);
                LedTrimRSlider.Value = r * 100.0;
                LedTrimGSlider.Value = g * 100.0;
                LedTrimBSlider.Value = b * 100.0;
                UpdateLedTrimText();

                // Stored trim is never in force without visible UI. Anyone who
                // has ever moved a slider finds the section already open rather
                // than a live correction hidden behind a collapsed header. Null
                // is the default and only a deliberate drag makes it concrete,
                // so this fires for almost nobody.
                if (LedTrimExpander != null)
                    LedTrimExpander.IsExpanded = _plugin.Settings.LedTrimR.HasValue
                                              || _plugin.Settings.LedTrimG.HasValue
                                              || _plugin.Settings.LedTrimB.HasValue;
            }
            finally { _patternUiLoading = false; }
        }

        private void UpdateLedTrimText()
        {
            if (LedTrimRText != null) LedTrimRText.Text = (int)Math.Round(LedTrimRSlider.Value) + "%";
            if (LedTrimGText != null) LedTrimGText.Text = (int)Math.Round(LedTrimGSlider.Value) + "%";
            if (LedTrimBText != null) LedTrimBText.Text = (int)Math.Round(LedTrimBSlider.Value) + "%";
        }

        private void LedTrim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_patternUiLoading || _plugin?.Settings == null) return;
            UpdateLedTrimText();

            // Moving a slider is a deliberate choice, so all three become
            // concrete and no future change to the shipped tuning moves them.
            _plugin.Settings.LedTrimR = (float)(LedTrimRSlider.Value / 100.0);
            _plugin.Settings.LedTrimG = (float)(LedTrimGSlider.Value / 100.0);
            _plugin.Settings.LedTrimB = (float)(LedTrimBSlider.Value / 100.0);

            // Same throttle as the brightness and colour edits: a slider drag
            // would otherwise fire a 64-byte upload per pixel down the pipe the
            // wheel also uses for force.
            if (_trimTimer == null)
            {
                _trimTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(250) };
                _trimTimer.Tick += (s, ev) =>
                {
                    _trimTimer.Stop();
                    _plugin.PersistSettings();
                    // Re-show whatever is on the rim so the trim is judged live.
                    // A test colour wins, because that is what they are looking
                    // at while dragging.
                    if (_trimTestColour != null)
                        ShowFlatTestColour(_trimTestColour[0], _trimTestColour[1], _trimTestColour[2], null);
                    else
                    {
                        ShowOnWheelNow();
                        // An exempt pattern does not move when the trim moves,
                        // which reads as the sliders being broken.
                        if (_editing?.TrimExempt == true)
                            SetPatternStatus("This pattern goes to the wheel unchanged, so the trim does not move it. "
                                           + "Use Show yellow or Show white to judge the trim.");
                    }
                };
            }
            _trimTimer.Stop();
            _trimTimer.Start();
        }

        private void LedTrimYellow_Click(object sender, RoutedEventArgs e)
            => ShowFlatTestColour(0xFF, 0xFF, 0x00, "Plain yellow on all ten. Bring green down until it stops looking lime.");

        private void LedTrimWhite_Click(object sender, RoutedEventArgs e)
            => ShowFlatTestColour(0xFF, 0xFF, 0xFF, "Plain white on all ten. Hold a sheet of paper beside it and trim until they match.");

        private void LedTrimReset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null) return;
            // Back to the shipped tuning, NOT to no correction at all. Clearing
            // to null is what makes this button safe to press by accident: it
            // returns the wheel to how it arrives out of the box rather than
            // stranding it on uncorrected colours.
            _plugin.Settings.LedTrimR = null;
            _plugin.Settings.LedTrimG = null;
            _plugin.Settings.LedTrimB = null;
            _plugin.PersistSettings();
            RefreshLedTrim();
            _trimTestColour = null;
            ShowOnWheelNow();
            SetPatternStatus("Back to the tuning this wheel ships with.");
        }

        /// <summary>Light every LED one flat colour, sent as INTENT so it passes
        /// through the trim on the way out. That is the whole point: the user
        /// drags until the trimmed result looks like the colour they asked
        /// for.</summary>
        private void ShowFlatTestColour(byte r, byte g, byte b, string status)
        {
            if (_plugin == null) return;
            _trimTestColour = new[] { r, g, b };

            var rgb = new byte[WheelLedChannel.LedCount * 3];
            for (int i = 0; i < WheelLedChannel.LedCount; i++)
            {
                rgb[i * 3 + 0] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }

            string msg;
            bool ok = _plugin.BorrowSlot(new WheelLedChannel.WheelLedSlot
            {
                Slot = (byte)_plugin.StageSlot(),
                DirectionWire = 3,
                Rgb = rgb,
            }, out msg, displayLevel: WheelLedChannel.LedCount);

            if (!ok) { _trimTestColour = null; SetPatternStatus(msg); }
            else if (status != null) SetPatternStatus(status);
        }

        // Which flat colour the trim sliders are being judged against, or null
        // when the rim is back to showing a real pattern.
        private byte[] _trimTestColour;
        private System.Windows.Threading.DispatcherTimer _trimTimer;

        private void BuildLedCells()
        {
            if (PatternLedStrip == null) return;
            _ledCells = new Border[WheelLedChannel.LedCount];
            PatternLedStrip.Items.Clear();
            for (int i = 0; i < _ledCells.Length; i++)
            {
                var cell = new Border
                {
                    Width = 34, Height = 46, Margin = new Thickness(2, 0, 2, 0),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Background = Brushes.Black,
                    Cursor = Cursors.Hand,
                    Tag = i,
                    // Numbered so a user can say "LED 7 is wrong" and be understood.
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 2),
                        FontSize = 10, Opacity = 0.75, Foreground = Brushes.White,
                    },
                };
                cell.MouseLeftButtonUp += LedCell_Click;
                _ledCells[i] = cell;
                PatternLedStrip.Items.Add(cell);
            }
        }

        private void BuildPalette()
        {
            if (PatternPalette == null) return;
            PatternPalette.Items.Clear();
            foreach (string hex in Palette)
            {
                var sw = new Border
                {
                    Width = 26, Height = 26, Margin = new Thickness(0, 0, 4, 4),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
                    Background = BrushFromHex(hex),
                    Cursor = Cursors.Hand,
                    Tag = hex,
                    ToolTip = "#" + hex,
                };
                sw.MouseLeftButtonUp += Swatch_Click;
                PatternPalette.Items.Add(sw);
            }
        }

        private static SolidColorBrush BrushFromHex(string hex)
        {
            byte[] b = LightSlotBackupStore.FromHex(hex);
            return b != null && b.Length >= 3
                ? new SolidColorBrush(Color.FromRgb(b[0], b[1], b[2]))
                : Brushes.Black;
        }

        /// <summary>The wheel's own four sweeps, in device effect order (1..4).
        /// Shown for context: they are firmware patterns we cannot edit, but a
        /// user picking a pattern is choosing among ALL nine things the wheel can
        /// show, not just ours.</summary>
        private static readonly string[] WheelBuiltinEffects =
        { "Inside out", "Outside in", "Right to left", "Left to right" };

        /// <summary>Device direction value for built-in sweep <paramref name="index"/>
        /// (0-based, effect index + 1). The effect IS the direction, but the two
        /// lists are numbered differently, which is why this is written out rather
        /// than computed: effect 1 is inside-out (direction 1), 2 outside-in (2),
        /// 3 right-to-left (4), 4 left-to-right (3).</summary>
        private static byte BuiltinEffectDirection(int index)
        {
            switch (index)
            {
                case 0: return 1;   // Inside out
                case 1: return 2;   // Outside in
                case 2: return 4;   // Right to left
                default: return 3;  // Left to right
            }
        }

        /// <summary>Whether the thing being edited is something the user can
        /// actually change. Built-in sweeps are firmware, so their controls are
        /// shown (so the direction is visible) but disabled.</summary>
        private bool EditingIsReadOnly => _editing?.Origin == "builtin-effect";

        /// <summary>One row of the picker. The list deliberately mirrors what the
        /// WHEEL offers, top to bottom: its four fixed sweeps, then its five
        /// stored slots, then the patterns inside the slot we borrow, which is the
        /// only section that is ours to reorder.</summary>
        private sealed class PatternRow
        {
            public string Text;
            public string Kind;          // header | note | wheelEffect | emptySlot | library
            public LightPattern Pattern; // library rows only
            public int Slot = -1;        // which slot the row sits at, -1 when none
            /// <summary>Physically stored on the wheel, so it works with SimHub
            /// closed. Drawn with a bracket down the left edge.</summary>
            public bool OnWheel;
            /// <summary>The bare name, without the indentation and markers the
            /// display text carries. What inline rename edits.</summary>
            public string EditableName;
            public override string ToString() => Text;
        }

        /// <summary>Which slot is being edited directly, or -1 when the editor is
        /// on a library pattern. Editing a wheel slot writes straight to that slot
        /// (with a backup first), because the user is changing THEIR slot rather
        /// than a pattern we swap through ours.</summary>
        /// <summary>Which slot the pattern being edited lives in, or -1 when it
        /// only exists in the plugin. Derived rather than tracked: it used to be
        /// a field set alongside _editing, which meant two things had to be
        /// updated together and a missed one edited the wrong target.</summary>
        private int _editingSlot => _editing?.Slot ?? -1;

        /// <summary>Scratch pattern used while editing a wheel slot directly, so
        /// the editor code has a single object to work against either way. Never
        /// stored in the library.</summary>
        private LightPattern _slotScratch;

        private System.Collections.Generic.List<PatternRow> _rows =
            new System.Collections.Generic.List<PatternRow>();

        private void RefreshPatternList()
        {
            if (PatternList == null) return;
            _patternUiLoading = true;
            try
            {
                string keepId = _editing?.Id;
                int lent = _plugin?.StageSlot() ?? -1;

                _rows.Clear();
                // Everything from here to the end of the slots physically lives on
                // the wheel and keeps working with SimHub closed. The bracket down
                // the left edge is what says so.
                _rows.Add(new PatternRow { Kind = "header", Text = "On the wheel", OnWheel = true });
                _rows.Add(new PatternRow { Kind = "note", Text = "these keep working with SimHub closed", OnWheel = true });
                for (int i = 0; i < WheelBuiltinEffects.Length; i++)
                    _rows.Add(new PatternRow
                    {
                        Kind = "wheelEffect",
                        Slot = i,                 // index into the four sweeps, effect = i + 1
                        OnWheel = true,
                        Text = "   " + WheelBuiltinEffects[i],
                    });
                // The patterns that are stored in a slot, in slot order, still
                // inside the bracket: they are on the wheel in the literal sense
                // that they survive SimHub closing.
                for (int i = 0; i < WheelLedChannel.CustomSlotCount; i++)
                {
                    var inSlot = _patternLib.Patterns.FirstOrDefault(p => p != null && p.Slot == i);
                    _rows.Add(new PatternRow
                    {
                        Kind = inSlot != null ? "library" : "emptySlot",
                        Pattern = inSlot,
                        Slot = i,
                        OnWheel = true,
                        EditableName = inSlot?.Name,
                        Text = "   CUSTOM " + (i + 1) + ": "
                             + (inSlot != null ? inSlot.Name : "empty")
                             + (i == lent ? "   ← lent" : "")
                             + (inSlot != null && inSlot.Id == _patternLib.CurrentId ? "   ●" : ""),
                    });
                }

                _rows.Add(new PatternRow { Kind = "header", Text = "In the plugin" });
                _rows.Add(new PatternRow
                {
                    Kind = "note",
                    Text = "these need SimHub running; save one to a slot to change that",
                });
                foreach (var p in _patternLib.Patterns)
                {
                    if (p == null || p.OnWheel) continue;    // already listed above
                    _rows.Add(new PatternRow
                    {
                        Kind = "library",
                        Pattern = p,
                        Slot = -1,
                        EditableName = p.Name,
                        Text = "   " + p.Name + (p.Id == _patternLib.CurrentId ? "   ●" : ""),
                    });
                }

                PatternList.Items.Clear();
                var bracket = new SolidColorBrush(Color.FromRgb(0x5E, 0x9C, 0xD8));
                foreach (var row in _rows)
                {
                    var item = new ListBoxItem { Content = row.Text, Tag = row, Padding = new Thickness(4, 2, 4, 2) };

                    // A continuous bar down the left of every wheel-resident row
                    // reads as one bracket around the group, which is the point:
                    // it shows at a glance which patterns survive SimHub closing.
                    if (row.OnWheel)
                    {
                        item.BorderThickness = new Thickness(3, 0, 0, 0);
                        item.BorderBrush = bracket;
                    }

                    if (row.Kind == "header")
                    {
                        // Not a destination: a header is a label, and letting it
                        // be selected would leave the editor showing nothing.
                        item.IsEnabled = false;
                        item.FontWeight = FontWeights.SemiBold;
                        item.Opacity = 0.9;
                    }
                    else if (row.Kind == "note")
                    {
                        item.IsEnabled = false;
                        item.FontSize = 10;
                        item.Opacity = 0.6;
                        item.FontStyle = FontStyles.Italic;
                    }
                    else if (row.Kind == "emptySlot")
                    {
                        // A slot with nothing in it is a destination, not a thing
                        // to look at, so it is shown but cannot be selected.
                        item.IsEnabled = false;
                        item.Opacity = 0.5;
                        item.FontStyle = FontStyles.Italic;
                    }
                    else if (row.Kind == "wheelEffect")
                    {
                        // Selectable (picking one shows it on the wheel) but dimmed,
                        // because unlike everything else here they cannot be edited.
                        item.Opacity = 0.75;
                    }
                    PatternList.Items.Add(item);
                }

                int idx = -1;
                if (keepId != null)
                    idx = _rows.FindIndex(r => r.Kind == "library" && r.Pattern?.Id == keepId);
                if (idx < 0) idx = _rows.FindIndex(r => r.Kind == "library");
                if (idx >= 0) PatternList.SelectedIndex = idx;
            }
            finally { _patternUiLoading = false; }
            LoadSelectedPattern();
            AbsorbOwnLightSelectionChange();
        }

        /// <summary>Mark the wheel's current light selection as already seen.
        ///
        /// The tick behind FollowExternalLightSelection exists to follow a RIM
        /// BUTTON changing the pattern behind this tab's back, and following it
        /// reassigns _editing and rebuilds the list. This tab's own writes bump
        /// the very same counter: ReleaseBorrowedSlot alone does it. So without
        /// this, moving a pattern was immediately followed by the list jumping
        /// to whatever sits in the wheel's selected slot, and the selection was
        /// lost after every single click of Up, which made moving anything more
        /// than one place a matter of reselecting between clicks.
        ///
        /// Called once the list has been rebuilt, which is the point at which
        /// this tab is authoritative about what is showing. A rim press in the
        /// moment between our write and this call would be swallowed; the next
        /// press is caught as normal.</summary>
        private void AbsorbOwnLightSelectionChange()
        {
            if (_plugin != null) _seenLightSelectionGen = _plugin.LightSelectionGeneration;
        }

        /// <summary>The library pattern behind the selected row, or null when the
        /// selection is on a header or one of the wheel's own entries.</summary>
        private LightPattern SelectedLibraryPattern()
        {
            int i = PatternList?.SelectedIndex ?? -1;
            if (i < 0 || i >= _rows.Count) return null;
            return _rows[i].Kind == "library" ? _rows[i].Pattern : null;
        }

        /// <summary>Point the list and the editor at whatever the wheel is showing
        /// now, after something outside this tab changed it. Rebuilds rather than
        /// just moving the selection, because the row text carries the marker that
        /// says which pattern is current.</summary>
        private void SyncPatternListToCurrent()
        {
            if (PatternList == null || _patternStore == null || _plugin == null) return;

            // The pattern showing is either one of ours standing in the lent slot,
            // or whichever one lives in the slot the wheel has selected.
            int sel = _plugin.GetWheelPatternSelection();
            LightPattern now = null;
            if (_plugin.BorrowedSlot >= 0)
                now = _patternLib.Patterns.FirstOrDefault(p => p != null && p.Id == _patternLib.CurrentId);
            else if (sel >= WheelSlotFirstEffect && sel <= 9)
                now = _patternLib.Patterns.FirstOrDefault(p => p != null && p.Slot == sel - WheelSlotFirstEffect);

            // A firmware sweep is showing, which is not one of ours. Leave the
            // editor where it is rather than yanking it somewhere arbitrary.
            if (now == null) return;
            if (_editing != null && _editing.Id == now.Id) return;   // already there

            _editing = now;
            RefreshPatternList();
        }

        private PatternRow SelectedRow()
        {
            int i = PatternList?.SelectedIndex ?? -1;
            return (i >= 0 && i < _rows.Count) ? _rows[i] : null;
        }

        /// <summary>Slot names as the wheel reports them, refreshed when the tab
        /// opens. Null until read; a wheel that is not connected simply shows the
        /// plain CUSTOM numbering.</summary>
        private string[] _slotNames;

        private void RefreshSlotNames()
        {
            try
            {
                var names = new string[WheelLedChannel.CustomSlotCount];
                for (int i = 0; i < names.Length; i++) names[i] = _plugin?.ReadSlotName(i);
                _slotNames = names;
            }
            catch { /* a wheel we cannot ask just shows CUSTOM 1..5 */ }
        }

        private void LoadSelectedPattern()
        {
            var row = SelectedRow();
            _slotScratch = null;

            if (row != null && row.Kind == "wheelEffect")
            {
                // A firmware sweep. Its DIRECTION we know exactly, because the
                // effect number is the direction. Its colours we cannot read: they
                // are fixed in the wheel and there is no way to ask for them, so
                // the strip is shown blank rather than inventing a guess and
                // passing it off as what the wheel does.
                _slotScratch = new LightPattern
                {
                    Id = "builtin" + row.Slot,
                    Name = WheelBuiltinEffects[row.Slot],
                    DirectionWire = BuiltinEffectDirection(row.Slot),
                    RgbHex = new string('0', WheelLedChannel.LedCount * 6),
                    Origin = "builtin-effect",
                };
                _editing = _slotScratch;
                SetPatternStatus("This one is built into the wheel: it fills "
                               + Directions.FirstOrDefault(d => d.Wire == _editing.DirectionWire).Label.ToLowerInvariant()
                               + ", and its colours are fixed in the wheel's own firmware, so they cannot be read or changed here.");
            }
            else
            {
                _editing = SelectedLibraryPattern();
                if (_editing != null)
                    SetPatternStatus(_editing.OnWheel
                        ? "Living in CUSTOM " + (_editing.Slot + 1) + " on the wheel, so it works with "
                          + "SimHub closed. Edits are saved straight to that slot."
                        : "Living in the plugin. Save it to a slot to keep it on the wheel itself.");
            }

            _patternUiLoading = true;
            try
            {
                if (PatternNameBox != null) PatternNameBox.Text = _editing?.Name ?? string.Empty;
                // A checkbox does not clear itself when the selection changes,
                // so without this it would show the previous pattern's state and
                // the next tick would write the exemption onto the wrong one.
                // Stored as the exemption, shown as the positive. Absent in the
                // file means trimmed, which is what keeps every pattern already
                // on disk behaving as it did.
                if (PatternApplyTrimBox != null) PatternApplyTrimBox.IsChecked = _editing != null && !_editing.TrimExempt;
                if (PatternDirectionCombo != null)
                {
                    int d = _editing == null ? 0
                        : Array.FindIndex(Directions, x => x.Wire == _editing.DirectionWire);
                    PatternDirectionCombo.SelectedIndex = d < 0 ? 0 : d;
                }
            }
            finally { _patternUiLoading = false; }

            PaintLedCells();
            SelectLed(_selectedLed);
            ApplyEditorEnabledState();
        }

        /// <summary>Grey the editing controls for anything that cannot be changed.
        /// Shown rather than hidden, so a built-in sweep still reveals which way
        /// it fills; it just cannot be edited.</summary>
        private void ApplyEditorEnabledState()
        {
            bool editable = _editing != null && !EditingIsReadOnly;

            if (PatternNameBox != null) PatternNameBox.IsEnabled = editable;
            if (PatternDirectionCombo != null) PatternDirectionCombo.IsEnabled = editable;
            if (PatternApplyTrimBox != null) PatternApplyTrimBox.IsEnabled = editable;
            if (PatternHexBox != null) PatternHexBox.IsEnabled = editable;
            if (PatternRSlider != null) PatternRSlider.IsEnabled = editable;
            if (PatternGSlider != null) PatternGSlider.IsEnabled = editable;
            if (PatternBSlider != null) PatternBSlider.IsEnabled = editable;
            if (PatternFillAllBtn != null) PatternFillAllBtn.IsEnabled = editable;
            if (PatternMirrorBtn != null) PatternMirrorBtn.IsEnabled = editable;
            if (PatternPalette != null) PatternPalette.IsEnabled = editable;
            if (PatternLedStrip != null) PatternLedStrip.IsEnabled = editable;
            if (PatternLedStrip != null) PatternLedStrip.Opacity = editable ? 1.0 : 0.45;

            // Deleting or copying a firmware sweep means nothing either, and
            // `editable` already covers that: a sweep is a _slotScratch object
            // whose Origin is "builtin-effect", so EditingIsReadOnly catches it.
            //
            // These used to carry `&& _editingSlot < 0` as well, aimed at the
            // same sweeps. It never reached them, because a scratch object never
            // assigns Slot and so inherits the -1 default. All it actually did
            // was switch Delete and Reset off for the five patterns sitting in
            // the wheel's slots, which are exactly the ones a user is most
            // likely to have edited. Reverting one already works: ShowOnWheelNow
            // routes a slot resident to WriteOwnSlot, the same call Reset ends
            // on, so the plumbing was there the whole time behind a dead guard.
            if (PatternDeleteBtn != null) PatternDeleteBtn.IsEnabled = editable;
            if (PatternCopyBtn != null) PatternCopyBtn.IsEnabled = editable;
            if (PatternResetBtn != null) PatternResetBtn.IsEnabled = editable;

            // Each direction only makes sense from one side of the bracket: a
            // pattern of ours can be saved ONTO the wheel, one of the wheel's
            // slots can be copied OFF it.
            var row = SelectedRow();
            bool isPattern = row != null && row.Kind == "library" && row.Pattern != null;
            // Up and Down are how a pattern moves on and off the wheel, so they
            // are only meaningful on a pattern.
            if (PatternUpBtn != null)   PatternUpBtn.IsEnabled = isPattern;
            if (PatternDownBtn != null) PatternDownBtn.IsEnabled = isPattern;
        }

        private void PaintLedCells()
        {
            if (_ledCells == null) return;
            byte[] rgb = _editing?.Rgb();
            for (int i = 0; i < _ledCells.Length; i++)
            {
                Color c = (rgb != null && rgb.Length >= (i + 1) * 3)
                    ? Color.FromRgb(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2])
                    : Colors.Black;
                _ledCells[i].Background = new SolidColorBrush(c);
                // Label in whichever of black/white stays readable on the colour.
                if (_ledCells[i].Child is TextBlock t)
                    t.Foreground = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 140
                        ? Brushes.Black : Brushes.White;
            }
        }

        private void SelectLed(int index)
        {
            if (_ledCells == null || _ledCells.Length == 0) return;
            _selectedLed = Math.Max(0, Math.Min(_ledCells.Length - 1, index));
            for (int i = 0; i < _ledCells.Length; i++)
                _ledCells[i].BorderBrush = i == _selectedLed
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40))
                    : Brushes.Transparent;

            if (PatternSelectedLedText != null) PatternSelectedLedText.Text = "LED " + (_selectedLed + 1);

            byte[] rgb = _editing?.Rgb();
            if (rgb == null || rgb.Length < (_selectedLed + 1) * 3) return;
            byte r = rgb[_selectedLed * 3], g = rgb[_selectedLed * 3 + 1], b = rgb[_selectedLed * 3 + 2];

            _patternUiLoading = true;
            try
            {
                if (PatternHexBox != null) PatternHexBox.Text = string.Format("{0:X2}{1:X2}{2:X2}", r, g, b);
                if (PatternRSlider != null) PatternRSlider.Value = r;
                if (PatternGSlider != null) PatternGSlider.Value = g;
                if (PatternBSlider != null) PatternBSlider.Value = b;
                UpdateColorReadouts(r, g, b);
            }
            finally { _patternUiLoading = false; }
        }

        private void UpdateColorReadouts(byte r, byte g, byte b)
        {
            if (PatternRText != null) PatternRText.Text = r.ToString();
            if (PatternGText != null) PatternGText.Text = g.ToString();
            if (PatternBText != null) PatternBText.Text = b.ToString();
            if (PatternCurrentSwatch != null)
                PatternCurrentSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        /// <summary>Write one LED's colour into the pattern being edited and save.
        /// Saving on every touch keeps the library and the screen in step; there
        /// is no separate commit for the user to forget.</summary>
        private void SetLed(int index, byte r, byte g, byte b, bool alsoMirror = false)
        {
            if (_editing == null) return;
            byte[] rgb = _editing.Rgb() ?? new byte[WheelLedChannel.LedCount * 3];
            if (rgb.Length < WheelLedChannel.LedCount * 3) Array.Resize(ref rgb, WheelLedChannel.LedCount * 3);
            if (index < 0 || index >= WheelLedChannel.LedCount) return;

            rgb[index * 3] = r; rgb[index * 3 + 1] = g; rgb[index * 3 + 2] = b;
            if (alsoMirror)
            {
                int j = WheelLedChannel.LedCount - 1 - index;
                rgb[j * 3] = r; rgb[j * 3 + 1] = g; rgb[j * 3 + 2] = b;
            }
            _editing.RgbHex = LightSlotBackupStore.ToHex(rgb);
            // A library pattern persists to our file; a wheel slot lives on the
            // wheel, so there is nothing of ours to save for it.
            if (_editingSlot < 0) _patternStore.Save(_patternLib);
            PaintLedCells();
            ShowOnWheelThrottled();
        }

        /// <summary>Put what is on screen onto the wheel, lit, so editing is done
        /// by looking at the rim rather than at a swatch.
        ///
        /// Throttled because every call is a full slot write (an effect select, a
        /// 64-byte colour upload and a commit), and dragging a slider would
        /// otherwise fire dozens a second down a pipe the wheel also uses for
        /// force. A trailing timer makes sure the LAST value still lands, so the
        /// rim never ends up showing something the screen does not.</summary>
        private void ShowOnWheelThrottled()
        {
            if (_editing == null || _plugin == null) return;
            // A wheel slot being edited directly is its own target; otherwise we
            // need a lent slot to show anything.
            // Automatic always resolves to a real slot, so there is always somewhere to show it.

            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            if (now - _lastWheelShowMs >= WheelShowMinMs)
            {
                _lastWheelShowMs = now;
                ShowOnWheelNow();
                return;
            }

            if (_wheelShowTimer == null)
            {
                _wheelShowTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(WheelShowMinMs) };
                _wheelShowTimer.Tick += (s, e) =>
                {
                    _wheelShowTimer.Stop();
                    _lastWheelShowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                    ShowOnWheelNow();
                };
            }
            _wheelShowTimer.Stop();
            _wheelShowTimer.Start();
        }

        private const int WheelShowMinMs = 220;
        private long _lastWheelShowMs;
        private System.Windows.Threading.DispatcherTimer _wheelShowTimer;

        /// <summary>Write the pattern and light the whole bar, WITHOUT sweeping.
        /// The static full bar is what you want while placing colours; the sweep
        /// is a separate, deliberate action.</summary>
        /// <param name="pattern">The pattern to show. Passed EXPLICITLY by the
        /// click handler rather than read from _editing, so what lands on the
        /// wheel is what was clicked. Reading shared editor state here meant any
        /// desync wrote a different pattern than the one selected, silently.
        /// Null falls back to whatever is being edited, for the colour edits that
        /// genuinely mean "the thing in front of me".</param>
        private void ShowOnWheelNow(LightPattern pattern = null, bool sweep = false)
        {
            var target = pattern ?? _editing;
            if (target == null || _plugin == null) return;

            // Showing a real pattern ends any flat colour the trim sliders were
            // being judged against, so a later trim drag re-shows this rather
            // than jumping back to the test swatch.
            _trimTestColour = null;

            string msg;
            // Editing one of the wheel's OWN slots writes to THAT slot. Showing a
            // library pattern goes through the same call the cycle uses, so the
            // two cannot drift apart again: they already had, one lighting the
            // full bar and the other keeping a rev level that is 0 when parked.
            bool ok = _editingSlot >= 0
                ? _plugin.WriteOwnSlot(_editingSlot, target, out msg, sweep)
                : _plugin.ApplyLightPattern(target, out msg, userChose: false, sweep: sweep);

            if (!ok) SetPatternStatus(msg);
        }

        // ---------------- handlers ----------------

        /// <summary>Take whatever is stored in a slot into the library and make it
        /// the one showing. Returns the pattern's name, or null if the slot could
        /// not be read. Writes nothing to the wheel: the colours are already
        /// there, which is the entire point.</summary>
        private string AdoptSlotContentsAsPattern(int slot)
        {
            var onWheel = _plugin?.ReadSlot(slot);
            if (onWheel?.Rgb == null || onWheel.Rgb.Length < WheelLedChannel.LedCount * 3) return null;

            string hex = LightSlotBackupStore.ToHex(onWheel.Rgb);
            // Match against each pattern's TRIMMED hex, because that is what we
            // would have written. Comparing raw would find nothing on a
            // calibrated wheel, and the library would then acquire an
            // already-trimmed copy of a pattern it already has, which gets
            // trimmed a second time on the next show.
            var existing = _patternLib.Patterns.FirstOrDefault(p =>
                string.Equals(_plugin != null ? _plugin.ToWireHex(p) : LightSlotBackupStore.ToHex(p.Rgb()), hex,
                              StringComparison.OrdinalIgnoreCase)
                && p.DirectionWire == onWheel.DirectionWire);

            // Bytes that match nothing were authored by the user or by G HUB,
            // so they are already in the wheel's own space. Store them raw and
            // never un-trim them.
            if (existing == null)
                existing = LightPatternStore.Add(_patternLib, "CUSTOM " + (slot + 1) + " (yours)",
                                                 onWheel.DirectionWire, onWheel.Rgb, "wheel",
                                                 trimExempt: true);

            _patternLib.CurrentId = existing.Id;
            _patternStore.Save(_patternLib);
            _editing = existing;
            RefreshPatternList();
            return existing.Name;
        }

        private void PatternRgbSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_patternUiLoading || _editing == null) return;
            byte r = (byte)(PatternRSlider?.Value ?? 0);
            byte g = (byte)(PatternGSlider?.Value ?? 0);
            byte b = (byte)(PatternBSlider?.Value ?? 0);

            _patternUiLoading = true;
            try { if (PatternHexBox != null) PatternHexBox.Text = string.Format("{0:X2}{1:X2}{2:X2}", r, g, b); }
            finally { _patternUiLoading = false; }

            UpdateColorReadouts(r, g, b);
            SetLed(_selectedLed, r, g, b);
        }

        /// <summary>Put a shipped pattern back the way it came. Only meaningful
        /// for built-ins, since only they have an original to return to.</summary>
        private void PatternReset_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            var shipped = LightPatternStore.Builtins
                .FirstOrDefault(b => string.Equals(b.Name, _editing.Name, StringComparison.OrdinalIgnoreCase));
            if (shipped.Name == null)
            {
                SetPatternStatus("This is your own pattern, so there is no shipped version to go back to. "
                               + "Use Copy on a shipped one if you want a starting point you can always reset.");
                return;
            }

            _editing.DirectionWire = shipped.Direction;
            _editing.RgbHex = shipped.Hex;
            // Shipped patterns are sRGB intent, so going back to how it shipped
            // means going back through the trim too.
            _editing.TrimExempt = false;
            _patternStore.Save(_patternLib);
            LoadSelectedPattern();
            SetPatternStatus("\"" + shipped.Name + "\" is back to how it shipped.");
            ShowOnWheelNow();
        }

        /// <summary>The name the WHEEL reports for a slot, or null when it has
        /// none (or could not be read).</summary>
        private string SlotRealName(int slot)
        {
            string name = (_slotNames != null && slot >= 0 && slot < _slotNames.Length)
                        ? _slotNames[slot] : null;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        private string SlotDisplayName(int slot) => SlotRealName(slot) ?? "unnamed";

        private void PatternCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            // The copy inherits the exemption. One that silently reverted to
            // intent would look wrong the instant it was shown, which is the
            // whole bug this flag exists to fix.
            var made = LightPatternStore.Add(_patternLib, _editing.Name + " copy",
                                             _editing.DirectionWire, _editing.Rgb(), "user",
                                             trimExempt: _editing.TrimExempt);
            _patternStore.Save(_patternLib);
            _editing = made;
            RefreshPatternList();
            SetPatternStatus("Copied. Edit this one freely; the original stays as it was.");
        }

        private void PatternList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // _patternUiLoading is set while the list is being rebuilt, so this
            // only ever fires on a real click. Rebuilds must NOT write to the
            // wheel, or every edit would trigger a second write.
            if (_patternUiLoading) return;
            LoadSelectedPattern();
            ApplySelectionToWheel();
        }

        /// <summary>Clicking a pattern shows it on the wheel, because a picker
        /// that does not pick is just a list.
        ///
        /// The two kinds of row mean different things, so they do different
        /// things. A pattern of OURS has to be written into the lent slot to be
        /// seen at all. One of the wheel's OWN slots is already stored there, so
        /// it only needs selecting: rewriting it would be destructive for no
        /// reason, when all the user asked for was to look at it.</summary>
        private void ApplySelectionToWheel()
        {
            var row = SelectedRow();
            if (row == null || _plugin == null) return;

            // Without this the select is merely STAGED when the channel is closed
            // (parked, no game), and the click looks like it did nothing.
            _plugin.EnsureLedChannelOpen();

            // Names both what was CLICKED and what the editor is holding. If they
            // ever disagree, the selection is not reaching the editor, and the log
            // says so outright instead of leaving it to be inferred from which
            // pattern landed on the wheel.
            SimHub.Logging.Current.Info(
                $"[TF4ALL] pattern clicked: '{row.Pattern?.Name}' (id {row.Pattern?.Id}); "
                + $"editor holds '{_editing?.Name}' (id {_editing?.Id})");

            if (row.Kind == "wheelEffect")
            {
                // A firmware sweep, which genuinely lives on the wheel, so give
                // back any slot on loan: a loan exists only to show patterns the
                // wheel does not hold, and this is not one of those.
                _plugin.ReleaseBorrowedSlot();
                // Selecting an effect changes WHICH pattern the strip would draw,
                // but parked the level is 0 and the strip is dark, so nothing
                // appears to happen. Sweep it once the selection has landed.
                _plugin.PickRevLightPattern(row.Slot + 1, previewAfter: true);
                SetPatternStatus("Showing one of the wheel's built-in sweeps. These cannot be edited.");
            }
            else if (row.Kind == "library" && row.Pattern != null)
            {
                if (row.Pattern.OnWheel)
                {
                    // Already stored in a slot, so this is only a selection.
                    // Rewriting it would be destructive for no reason when all the
                    // user asked for was to look at it.
                    _plugin.ReleaseBorrowedSlot();
                    _plugin.PickRevLightPattern(WheelSlotFirstEffect + row.Pattern.Slot, previewAfter: true);
                    SetPatternStatus("Showing CUSTOM " + (row.Pattern.Slot + 1)
                                   + ". Edits here are saved straight to that slot.");
                }
                else
                {
                    // Lives only in the plugin, so it has to be put INTO a slot to
                    // be seen at all. The slot on loan holds it while it is up, and
                    // its own pattern comes back afterwards.
                    int target = _plugin.StageSlot();
                    ShowOnWheelNow(row.Pattern, sweep: true);
                    SetPatternStatus("Showing " + row.Pattern.Name + " through CUSTOM " + (target + 1)
                                   + ". That slot's own pattern is saved and comes back.");
                }
            }
        }

        /// <summary>Device effect number of CUSTOM 1. Effects 1-4 are the firmware
        /// sweeps and 5-9 are the five custom slots.</summary>
        private const int WheelSlotFirstEffect = 5;

        // ---------------- rename in place ----------------

        private bool _renaming;

        /// <summary>Double-click a row to rename it where it sits. Renaming is the
        /// commonest edit once a library grows, and making the user reach for a
        /// separate field to do it is friction for no reason.</summary>
        private void PatternList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = SelectedRow();
            if (row == null || _renaming) return;
            if (row.Kind != "library" || row.Pattern == null) return;

            int index = PatternList.SelectedIndex;
            var item = PatternList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            if (item == null) return;

            _renaming = true;
            var box = new TextBox
            {
                Text = row.EditableName ?? string.Empty,
                // The wheel's own name field is eight characters; the library has
                // no such limit, so only cap where the hardware does.
                // The wheel's own name field is eight characters, so a pattern
                // that lives in a slot is capped where the hardware caps it.
                MaxLength = row.Pattern.OnWheel ? WheelLedChannel.SlotNameMaxLength : 40,
                Margin = new Thickness(2, 0, 2, 0),
            };

            bool committed = false;
            Action<bool> finish = commit =>
            {
                if (committed) return;
                committed = true;
                _renaming = false;
                string typed = box.Text;
                item.Content = row.Text;          // put the label back either way
                if (commit) CommitInlineRename(row, typed);
            };

            box.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter) { ev.Handled = true; finish(true); }
                else if (ev.Key == Key.Escape) { ev.Handled = true; finish(false); }
            };
            box.LostFocus += (s, ev) => finish(true);

            item.Content = box;
            box.Focus();
            box.SelectAll();
        }

        private void CommitInlineRename(PatternRow row, string typed)
        {
            string name = (typed ?? string.Empty).Trim();
            if (name.Length == 0 || string.Equals(name, row.EditableName, StringComparison.Ordinal)) return;

            if (row.Pattern == null) return;

            if (LightPatternStore.Rename(_patternLib, row.Pattern.Id, name))
            {
                _patternStore.Save(_patternLib);
                // A pattern that lives in a slot is named in BOTH places, or the
                // base's own menu would keep showing the old name for something
                // the plugin now calls something else.
                if (row.Pattern.OnWheel)
                {
                    bool ok = _plugin.WriteSlotName(row.Pattern.Slot, name);
                    RefreshSlotNames();
                    SetPatternStatus(ok
                        ? "Renamed, on the wheel too."
                        : "Renamed here, but the wheel would not accept the name for CUSTOM "
                          + (row.Pattern.Slot + 1) + ".");
                }
                else SetPatternStatus("Renamed.");
            }
            RefreshPatternList();
        }

        private void LedCell_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as Border)?.Tag is int i) SelectLed(i);
        }

        private void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            string hex = (sender as Border)?.Tag as string;
            byte[] c = LightSlotBackupStore.FromHex(hex);
            if (c == null || c.Length < 3) return;
            SetLed(_selectedLed, c[0], c[1], c[2]);
            SelectLed(_selectedLed);   // refresh the hex box
        }

        private void PatternHex_Committed(object sender, RoutedEventArgs e)
        {
            if (_patternUiLoading || _editing == null) return;
            string t = (PatternHexBox?.Text ?? string.Empty).Trim().TrimStart('#');
            byte[] c = LightSlotBackupStore.FromHex(t);
            if (c == null || c.Length < 3) { SelectLed(_selectedLed); return; }   // reject, restore
            SetLed(_selectedLed, c[0], c[1], c[2]);
        }

        private void PatternHex_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) PatternHex_Committed(sender, null);
        }

        private void PatternFillAll_Click(object sender, RoutedEventArgs e)
        {
            byte[] c = LightSlotBackupStore.FromHex((PatternHexBox?.Text ?? "").Trim().TrimStart('#'));
            if (c == null || c.Length < 3 || _editing == null) return;
            for (int i = 0; i < WheelLedChannel.LedCount; i++) SetLed(i, c[0], c[1], c[2]);
        }

        /// <summary>Copy the left half onto the right. The two mirrored fills drive
        /// the strip in pairs, so a pattern for them has to be symmetric or the
        /// halves disagree.</summary>
        private void PatternMirror_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            byte[] rgb = _editing.Rgb();
            if (rgb == null || rgb.Length < WheelLedChannel.LedCount * 3) return;
            int n = WheelLedChannel.LedCount;
            for (int i = 0; i < n / 2; i++)
            {
                int j = n - 1 - i;
                rgb[j * 3] = rgb[i * 3]; rgb[j * 3 + 1] = rgb[i * 3 + 1]; rgb[j * 3 + 2] = rgb[i * 3 + 2];
            }
            _editing.RgbHex = LightSlotBackupStore.ToHex(rgb);
            _patternStore.Save(_patternLib);
            PaintLedCells();
        }

        private void PatternDirection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_patternUiLoading || _editing == null) return;
            int i = PatternDirectionCombo?.SelectedIndex ?? -1;
            if (i < 0 || i >= Directions.Length) return;
            _editing.DirectionWire = Directions[i].Wire;
            _patternStore.Save(_patternLib);
            // Which way it fills is exactly what a still bar cannot show, so this
            // is one of the few changes worth playing rather than just lighting.
            PreviewCurrentPattern();
        }

        private void PatternApplyTrim_Changed(object sender, RoutedEventArgs e)
        {
            if (_patternUiLoading || _editing == null) return;
            _editing.TrimExempt = PatternApplyTrimBox?.IsChecked != true;
            // Saved unconditionally. This flag has no representation on the
            // wheel to fall back on, so it cannot rely on a later incidental
            // save the way a colour edit in a live slot can.
            _patternStore.Save(_patternLib);
            SetPatternStatus(_editing.TrimExempt
                ? "Colours now go to the wheel exactly as stored, so judge this one on the rim rather than on screen."
                : "Colours now go through the trim on the way out.");
            // Only the colours changed, not the fill, so light it rather than
            // running a sweep.
            ShowOnWheelNow();
        }

        private void PatternName_Committed(object sender, RoutedEventArgs e)
        {
            if (_patternUiLoading || _editing == null) return;
            string name = (PatternNameBox?.Text ?? string.Empty).Trim();
            if (name.Length == 0) { PatternNameBox.Text = _editing.Name; return; }

            // Naming a WHEEL slot writes the name onto the wheel, where its own
            // menu shows it. The wire only carries eight characters, so say so
            // rather than silently truncating.
            if (_editingSlot >= 0)
            {
                bool ok = _plugin.WriteSlotName(_editingSlot, name);
                RefreshSlotNames();
                RefreshPatternList();
                SetPatternStatus(ok
                    ? (name.Length > WheelLedChannel.SlotNameMaxLength
                        ? "Named on the wheel as \"" + name.Substring(0, WheelLedChannel.SlotNameMaxLength)
                          + "\"; the wheel only stores " + WheelLedChannel.SlotNameMaxLength + " characters."
                        : "Named on the wheel.")
                    : "The wheel would not accept that name.");
                return;
            }

            if (LightPatternStore.Rename(_patternLib, _editing.Id, name))
            {
                _patternStore.Save(_patternLib);
                RefreshPatternList();
            }
        }

        private void PatternName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) PatternName_Committed(sender, null);
        }

        private void PatternMove_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            int step = ((sender as FrameworkElement)?.Tag as string) == "-1" ? -1 : 1;
            int i = _patternLib.Patterns.FindIndex(p => p.Id == _editing.Id);
            if (i < 0) return;
            bool wasOnWheel = _editing.OnWheel;
            if (!LightPatternStore.Move(_patternLib, _editing.Id, i + step)) return;

            _patternStore.Save(_patternLib);

            // Position IS residency, so a move can put this pattern on the wheel
            // or take it off, and may shift someone else across the line too.
            // The loan was showing something from below the line, and the line
            // has just moved, so hand it back before pushing the new order down.
            _plugin.ReleaseBorrowedSlot();
            string msg;
            _plugin.SyncSlotsToWheel(out msg);

            RefreshPatternList();
            RefreshSlotNames();

            bool nowOnWheel = _editing.OnWheel;
            SetPatternStatus(
                nowOnWheel && !wasOnWheel
                    ? _editing.Name + " is now in CUSTOM " + (_editing.Slot + 1)
                      + " on the wheel, so it works with SimHub closed."
              : !nowOnWheel && wasOnWheel
                    ? _editing.Name + " has come off the wheel and lives in the plugin now."
              : nowOnWheel
                    ? "Moved to CUSTOM " + (_editing.Slot + 1) + ". " + msg
                    : "Moved. This order is the order a bound button cycles through.");
        }

        private void PatternNew_Click(object sender, RoutedEventArgs e)
        {
            var rgb = new byte[WheelLedChannel.LedCount * 3];
            for (int i = 0; i < WheelLedChannel.LedCount; i++) { rgb[i * 3] = 0x20; rgb[i * 3 + 1] = 0x20; rgb[i * 3 + 2] = 0x20; }
            var made = LightPatternStore.Add(_patternLib, "My pattern", 3, rgb, "user");
            _patternStore.Save(_patternLib);
            _editing = made;
            RefreshPatternList();
            SetPatternStatus("Click an LED, then a colour.");
        }

        private void PatternDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            var answer = TrueforceDialog.Show(Window.GetWindow(this), "Delete pattern",
                $"Delete \"{_editing.Name}\"? This cannot be undone.", DialogKind.Destructive);
            if (answer != true) return;

            // Deleting one that lives on the wheel shifts everything below it up,
            // so a different pattern lands in that slot. Without pushing the new
            // order down, the rim would keep showing the deleted pattern's
            // colours while the list named its replacement, until the next move
            // or restart. Same release-then-sync that PatternMove_Click does,
            // and for the same reason: position IS residency.
            bool wasOnWheel = _editing.OnWheel;
            string name = _editing.Name;

            LightPatternStore.Remove(_patternLib, _editing.Id);
            _patternStore.Save(_patternLib);
            _editing = null;

            if (wasOnWheel && _plugin != null)
            {
                _plugin.ReleaseBorrowedSlot();
                string syncMsg;
                _plugin.SyncSlotsToWheel(out syncMsg);
                RefreshSlotNames();
            }

            RefreshPatternList();
            SetPatternStatus(wasOnWheel
                ? "\"" + name + "\" is gone, and the wheel has taken the next pattern into that slot."
                : "\"" + name + "\" is gone.");
        }

        private void PatternPreview_Click(object sender, RoutedEventArgs e) => PreviewCurrentPattern();

        /// <summary>Put the pattern on the wheel and sweep it. Goes through the
        /// same borrow path as everything else, so the slot's original contents
        /// are backed up before the first write and can be given back.</summary>
        private void PreviewCurrentPattern()
        {
            if (_editing == null || _plugin == null) return;

            byte[] rgb = _editing.Rgb();
            if (rgb == null || rgb.Length < WheelLedChannel.LedCount * 3)
            {
                SetPatternStatus("This pattern's colours are unreadable.");
                return;
            }

            string msg;
            bool ok = _plugin.BorrowSlot(new WheelLedChannel.WheelLedSlot
            {
                Slot = (byte)_plugin.StageSlot(),
                DirectionWire = _editing.DirectionWire,
                Rgb = rgb,
            }, out msg,
               displayLevel: WheelLedChannel.LedCount,
               slotName: _editing.Name,
               rawColors: _editing.TrimExempt);

            if (ok)
            {
                _patternLib.CurrentId = _editing.Id;
                // Pressing Play is the user choosing this pattern, so it becomes
                // the one that comes back on cars with nothing of their own.
                _patternLib.StickyId = _editing.Id;
                _patternStore.Save(_patternLib);
                _plugin.TestRpmLeds();   // sweep, so the fill direction is visible
                SetPatternStatus("Playing on the wheel.");
            }
            else SetPatternStatus(msg);
        }

        private void SetPatternStatus(string text)
        {
            if (PatternStatusText != null) PatternStatusText.Text = text ?? string.Empty;
        }
    }
}
