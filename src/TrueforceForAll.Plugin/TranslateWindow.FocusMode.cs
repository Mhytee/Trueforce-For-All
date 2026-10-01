// One string at a time, in the order that gets a language usable soonest.
//
// The list is the right tool for finding a particular string and the wrong one for
// working through 2,974 of them: it shows what is left, which is discouraging, and it
// gives no sense of having finished anything. This view shows one English string, a box
// under it, and Next. Enter saves and moves on.
//
// The order is the point. Translating in key order means a language can be 300 strings
// in and still look untouched, because the strings a person actually sees are scattered
// through the file. The ranking below puts the main screen first, the controls before
// the tooltips that describe them, and the short labels before the long dialog bodies,
// so a few dozen strings make the plugin feel translated (owner, 2026-09-30: "we can
// first show the highest impact strings in this mode so the most important stuff
// translates first").
//
// Both halves are labelled, with the same two strings the list's column headers use. The
// first cut of this screen had no labels at all: the list's headers do that work and
// this view dropped them without replacing them, so a translator was handed a sentence
// and a box with nothing saying which was which (owner, 2026-09-30: "hard to see what is
// being translated"). The English also sits in its own block with an accent edge, so the
// thing being translated reads as a quotation rather than as a second field.
//
// Everything here shares the list's state: the same Row objects, the same save, the same
// publish queue. Switching views is a visibility change, not a second copy of the data.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    internal sealed partial class TranslateWindow
    {
        private static readonly Brush SourceBg = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36));

        private StackPanel _focusPanel;
        private TextBlock _focusArea;
        private TextBlock _focusEnglish;
        private TextBox _focusBox;
        private TextBlock _focusNote;
        private TextBlock _focusProgress;
        private Button _focusPrev;
        private Button _viewListButton;
        private Button _viewFocusButton;
        private bool _focusMode;

        /// <summary>The rows this view walks, worst first by impact. Built when the view
        /// is entered and kept until it is left, so finishing a row does not reshuffle
        /// the queue under the person working through it.</summary>
        private List<Row> _focusQueue = new List<Row>();
        private int _focusAt;

        /// <summary>Lower comes first. Three signals, in order of how much they say about
        /// whether a reader will ever see the string.</summary>
        private static long ImpactRank(Row r)
        {
            // The area, using the order the list already groups by: Header, Effects,
            // TelemetryFfb and so on, which is the order these surfaces are met in.
            long area = r.Area == null ? 999 : r.Area.Rank;
            // A tooltip only appears on hover, so it is worth less than the control it
            // describes. This is also what keeps a tip from arriving before its label.
            long tip = r.Key != null && r.Key.EndsWith("_Tip", StringComparison.Ordinal) ? 1 : 0;
            // Short before long: buttons and labels are what is read at a glance, while
            // the long strings are dialog bodies and numbered setup steps.
            long length = Math.Min(999, (r.English ?? "").Length);
            return ((area * 2 + tip) * 1000) + length;
        }

        /// <summary>Untranslated rows, in impact order. Translated ones are left out: the
        /// view exists to make progress, and a row already done is not work.</summary>
        private void BuildFocusQueue()
        {
            _focusQueue = _rows
                .Where(r => string.IsNullOrWhiteSpace(r.Text))
                .OrderBy(ImpactRank)
                .ThenBy(r => r.Key, StringComparer.Ordinal)
                .ToList();
            _focusAt = 0;
        }

        /// <summary>A caption over a block, naming what the block is. Small and muted: it
        /// has to be readable and must not compete with the string itself.</summary>
        private static TextBlock FocusCaption(string text)
            => new TextBlock
            {
                Text = text, Foreground = MutedFg, FontSize = 11,
                Margin = new Thickness(2, 0, 0, 3),
            };

        private UIElement BuildFocusPanel(string tag)
        {
            // Held to a reading measure rather than stretched across the window. A line
            // the full 1040 pixels wide is hard to read, and a box that wide reads as an
            // accident rather than as a field.
            _focusPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
            };

            // Where this string lives, and how far through the queue it is. The area is
            // the useful half: it says which screen these words show up on.
            var context = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 10) };
            _focusArea = new TextBlock
            {
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            context.Children.Add(_focusArea);
            _focusProgress = new TextBlock
            {
                Foreground = MutedFg, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            context.Children.Add(_focusProgress);
            _focusPanel.Children.Add(context);

            _focusPanel.Children.Add(FocusCaption(Loc.T("Translate_ColumnEnglish")));
            _focusEnglish = new TextBlock
            {
                Foreground = TextFg, FontSize = 16, TextWrapping = TextWrapping.Wrap,
            };
            _focusPanel.Children.Add(new Border
            {
                Background = SourceBg,
                // The accent down the left edge is what makes this read as the source
                // rather than as another field.
                BorderBrush = HeaderFg, BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 14),
                Child = _focusEnglish,
            });

            _focusPanel.Children.Add(FocusCaption(Loc.F("Translate_ColumnYours_Fmt", tag)));
            _focusBox = new TextBox
            {
                Foreground = TextFg, Background = InputBg, BorderBrush = BorderFg,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6), FontSize = 15,
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                MinHeight = 76, MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 6),
            };
            // Enter finishes the row, which is the whole gesture of this view. The strings
            // that carry a line break still need one typed, so Shift+Enter is the newline
            // and the hint below says so.
            _focusBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
                e.Handled = true;
                FocusNext();
            };
            _focusPanel.Children.Add(_focusBox);

            _focusNote = new TextBlock
            {
                Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 0, 12),
            };
            _focusPanel.Children.Add(_focusNote);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _focusPrev = MakeButton(Loc.T("Translate_FocusBack"), (s, e) => FocusBack());
            _focusPrev.Margin = new Thickness(0);
            buttons.Children.Add(_focusPrev);
            var skip = MakeButton(Loc.T("Translate_FocusSkip"), (s, e) => FocusSkip());
            skip.ToolTip = Loc.T("Translate_FocusSkip_Tip");
            buttons.Children.Add(skip);
            // Next is the one being reached for, so it is the one that looks reachable.
            var next = MakeButton(Loc.T("Translate_FocusNext"), (s, e) => FocusNext());
            next.Margin = new Thickness(16, 0, 0, 0);
            ModalButtonTheme.Primary(next);
            buttons.Children.Add(next);
            _focusPanel.Children.Add(buttons);

            _focusPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("Translate_FocusHint"),
                Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 0, 0),
            });
            return _focusPanel;
        }

        /// <summary>Two segments, List and one at a time, with the one in use looking like
        /// it. This was a single toggle labelled "One at a time", which swapped the whole
        /// window and left nothing on screen saying a second view existed or how to get
        /// back (owner, 2026-09-30: "the one at a time button is unclear and is a whole
        /// other mode"). Two named segments make it a view switch.</summary>
        private UIElement BuildViewSwitch()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _viewListButton = MakeButton(Loc.T("Translate_ViewList"), (s, e) => SetFocusMode(false));
            _viewFocusButton = MakeButton(Loc.T("Translate_FocusToggle"), (s, e) => SetFocusMode(true));
            _viewFocusButton.ToolTip = Loc.T("Translate_FocusToggle_Tip");
            foreach (var b in new[] { _viewListButton, _viewFocusButton })
            {
                b.Margin = new Thickness(0, 0, 1, 0);
                b.MinWidth = 108;
            }
            row.Children.Add(_viewListButton);
            row.Children.Add(_viewFocusButton);
            PaintViewSwitch();
            return row;
        }

        /// <summary>Gold on the view in use, plain on the other. Through ModalButtonTheme
        /// rather than by setting Background: WPF's own button template paints system
        /// chrome over a brush set by hand, which is why every button in this window
        /// looked unstyled (owner, 2026-09-30: "none of the buttons on the translate pages
        /// are styled?").</summary>
        private void PaintViewSwitch()
        {
            if (_viewListButton == null || _viewFocusButton == null) return;
            ModalButtonTheme.Primary(_focusMode ? _viewFocusButton : _viewListButton);
            ModalButtonTheme.Secondary(_focusMode ? _viewListButton : _viewFocusButton);
        }

        private void SetFocusMode(bool on)
        {
            if (_focusPanel == null || _grid == null) return;
            if (_focusMode == on) return;
            _focusMode = on;
            PaintViewSwitch();
            if (!on)
            {
                // Whatever is in the box belongs to the row before the list comes back.
                CommitFocusBox();
                _focusPanel.Visibility = Visibility.Collapsed;
                _grid.Visibility = Visibility.Visible;
                ApplyFilter();
                return;
            }
            BuildFocusQueue();
            _grid.Visibility = Visibility.Collapsed;
            _focusPanel.Visibility = Visibility.Visible;
            ShowFocusRow();
        }

        private void ShowFocusRow()
        {
            if (_focusQueue.Count == 0 || _focusAt >= _focusQueue.Count)
            {
                _focusArea.Text = "";
                _focusEnglish.Text = Loc.T("Translate_FocusDone");
                _focusNote.Text = "";
                _focusBox.Text = "";
                _focusBox.IsEnabled = false;
                _focusProgress.Text = "";
                _focusPrev.IsEnabled = _focusAt > 0;
                return;
            }
            var row = _focusQueue[_focusAt];
            _focusArea.Text = row.Area == null ? "" : row.Area.Label;
            _focusEnglish.Text = row.English ?? "";
            _focusBox.IsEnabled = true;
            _focusBox.Text = row.Text ?? "";
            _focusNote.Text = row.Note ?? "";
            _focusProgress.Text = Loc.F("Translate_FocusProgress_Fmt",
                                        _focusAt + 1, _focusQueue.Count);
            _focusPrev.IsEnabled = _focusAt > 0;
            _focusBox.Focus();
            _focusBox.CaretIndex = _focusBox.Text.Length;
        }

        /// <summary>Put the box's text on the row and save, the same way leaving a box in
        /// the list does. Returns false when there was nothing to commit.</summary>
        private bool CommitFocusBox()
        {
            if (_focusQueue.Count == 0 || _focusAt >= _focusQueue.Count) return false;
            var row = _focusQueue[_focusAt];
            if (string.Equals(row.Text ?? "", _focusBox.Text ?? "", StringComparison.Ordinal)) return false;
            row.Text = _focusBox.Text;
            _refused.Remove(row.Key);
            if (IsMine(row, null)) _pending.Add(row.Key);
            Dispatcher.BeginInvoke(new Action(() => Save(quiet: true)));
            return true;
        }

        private void FocusNext()
        {
            CommitFocusBox();
            if (_focusAt < _focusQueue.Count) _focusAt++;
            ShowFocusRow();
        }

        /// <summary>Past this one without writing anything. The row keeps its place in the
        /// queue, so Back reaches it again, and it is still untranslated the next time the
        /// view is entered.</summary>
        private void FocusSkip()
        {
            if (_focusAt < _focusQueue.Count) _focusAt++;
            ShowFocusRow();
        }

        private void FocusBack()
        {
            CommitFocusBox();
            if (_focusAt > 0) _focusAt--;
            ShowFocusRow();
        }
    }
}
