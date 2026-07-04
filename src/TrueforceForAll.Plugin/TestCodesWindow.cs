// Scrollable browser for the dev/test access codes, with an inline input so a
// code can be run without closing it. Replaces the old single-shot info dialog
// (TrueforceDialog) that overflowed the screen once the catalog grew past a
// screenful. Styled to match the TrueforceDialog family (dark panels, gold
// header).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class TestCodesWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush FieldBg  = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush GoldFg   = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));

        // Runs the entered code and returns a short status to show, or null.
        private readonly Func<string, string> _runCode;
        private readonly TextBox _input;
        private readonly TextBlock _status;

        public TestCodesWindow(string catalog, Func<string, string> runCode)
        {
            _runCode = runCode;

            Title = "Trueforce For All: test codes";
            Width = 580;
            // Cap height to the display so a long catalog scrolls instead of
            // running off the bottom of the screen.
            Height = Math.Min(620, Math.Max(360, SystemParameters.WorkArea.Height - 80));
            MinWidth = 420; MinHeight = 320;
            Background = WindowBg;
            Foreground = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;

            var root = new Grid { Margin = new Thickness(16, 14, 16, 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 header
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 1 codes (scroll)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 input
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 3 status
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4 close
            Content = root;

            // Header + hint.
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            head.Children.Add(new TextBlock
            {
                Text = "Test codes",
                Foreground = GoldFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 3),
            });
            head.Children.Add(new TextBlock
            {
                Text = "Type a code below and press Run (or Enter). This window stays open so you can run several.",
                Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // Scrollable, read-only catalog (monospace so the code column lines up;
            // selectable so codes can be copied).
            var codes = new TextBox
            {
                Text = catalog ?? "",
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = FieldBg, Foreground = TextFg,
                BorderBrush = BorderFg, BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Padding = new Thickness(8),
            };
            Grid.SetRow(codes, 1);
            root.Children.Add(codes);

            // Input row: text box + Run.
            var inputRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _input = new TextBox
            {
                Background = FieldBg, Foreground = TextFg,
                BorderBrush = BorderFg, BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4), FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; RunInput(); } };
            Grid.SetColumn(_input, 0);
            inputRow.Children.Add(_input);
            var runBtn = new Button
            {
                Content = "Run", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0),
                Foreground = TextFg, Background = PanelBg,
            };
            runBtn.Click += (s, e) => RunInput();
            Grid.SetColumn(runBtn, 1);
            inputRow.Children.Add(runBtn);
            Grid.SetRow(inputRow, 2);
            root.Children.Add(inputRow);

            // Status line.
            _status = new TextBlock
            {
                Text = "", Foreground = MutedFg, FontSize = 11.5,
                Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(_status, 3);
            root.Children.Add(_status);

            // Close.
            var closeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var close = new Button
            {
                Content = "Close", Padding = new Thickness(16, 5, 16, 5),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            close.Click += (s, e) => Close();
            closeRow.Children.Add(close);
            Grid.SetRow(closeRow, 4);
            root.Children.Add(closeRow);

            Loaded += (s, e) => _input.Focus();
        }

        private void RunInput()
        {
            string code = (_input.Text ?? string.Empty).Trim();
            if (code.Length == 0) return;
            string status = null;
            try { status = _runCode?.Invoke(code); }
            catch (Exception ex) { status = "Error: " + ex.Message; }
            _input.Clear();
            _input.Focus();
            _status.Text = string.IsNullOrEmpty(status) ? ("Ran " + code + ".") : status;
        }
    }
}
