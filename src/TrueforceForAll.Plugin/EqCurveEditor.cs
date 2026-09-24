// The Trueforce EQ curve editor: a log-frequency by dB canvas showing the
// summed response of the band list with one draggable handle per band,
// over a live spectrum of the mix. A bare FrameworkElement drawn in
// OnRender (no template), so SimHub's theme cannot restyle it and there is
// nothing to bind: the host hands it the live band list, it edits the
// EqBand objects in place and raises events; the host pushes the list to
// the engine, keeps the undo stack and persists.
//
// Interaction (mirrored in the host's tooltip and help text):
//   drag a handle         frequency (x, log) and gain (y); on a cut, y is Q
//   Alt + drag            Q only (bells and shelves), frequency held
//   scroll on a handle    Q, one notch = 12 %
//   arrows                nudge the selected band (Shift = bigger steps)
//   Delete                remove the selected band
//   Ctrl+Z                undo (raised to the host)
//   double-click space    add a bell there
//   right-click a handle  remove that band
//   click space           deselect

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    internal sealed class EqCurveEditor : FrameworkElement
    {
        // Plot range. Frequency spans the whole usable band of the 4 kHz
        // stream; dB spans the band gain range (deep cuts, modest boosts).
        public const double MinHz = 10.0;
        public const double MaxHz = 2000.0;
        public const double MinDb = ParametricEq.MinGainDb;
        public const double MaxDb = ParametricEq.MaxGainDb;

        // Spectrum display range: 0 dB = a full-scale sine.
        public const double SpecMinDb = -60.0;
        public const double SpecMaxDb = 0.0;

        private const double PadL = 36, PadR = 12, PadT = 12, PadB = 22;
        private const double HandleR = 7.0;
        private const double HitR = 12.0;
        // Cut handles sit on the 0 dB line (a cut has no gain), so their
        // vertical drag is relative and changes Q instead: up sharpens the
        // corner, down softens it. Alt + drag does the same on a bell. One
        // doubling of Q per QDragPixels.
        private const double QDragPixels = 60.0;

        private static readonly Brush BackBrush     = Frozen(0x1B, 0x1B, 0x1B);
        private static readonly Brush PlotBrush     = Frozen(0x21, 0x21, 0x21);
        private static readonly Brush LabelBrush    = Frozen(0x9A, 0x9A, 0x9A);
        private static readonly Brush ReadoutBrush  = Frozen(0xCC, 0xCC, 0xCC);
        private static readonly Brush HandleFill    = Frozen(0x2A, 0x2A, 0x2A);
        private static readonly Brush HandleOffFill = Frozen(0x3A, 0x3A, 0x3A);
        private static readonly Brush Gold          = Frozen(0xE5, 0xC0, 0x4A);
        private static readonly Brush GoldFill      = Frozen(0xE5, 0xC0, 0x4A, 0x22);
        private static readonly Brush GoldFaint     = Frozen(0xE5, 0xC0, 0x4A, 0x66);
        private static readonly Brush Grey          = Frozen(0x88, 0x88, 0x88);
        private static readonly Brush GreyFill      = Frozen(0x88, 0x88, 0x88, 0x1A);
        private static readonly Brush SpecFill      = Frozen(0x3A, 0x6B, 0xBE, 0x5A);
        private static readonly Brush HandleNumOn   = Frozen(0xE8, 0xE8, 0xE8);
        private static readonly Brush HandleNumSel  = Frozen(0x1E, 0x1E, 0x1E);
        private static readonly Pen BorderPen    = FrozenPen(Frozen(0x44, 0x44, 0x44), 1.0);
        private static readonly Pen FocusPen     = FrozenPen(Frozen(0xE5, 0xC0, 0x4A, 0x80), 1.0);
        private static readonly Pen GridPen      = FrozenPen(Frozen(0x33, 0x33, 0x33), 1.0);
        private static readonly Pen ZeroPen      = FrozenPen(Frozen(0x6A, 0x6A, 0x6A), 1.0);
        private static readonly Pen CurvePen     = FrozenPen(Gold, 2.0);
        private static readonly Pen CurveOffPen  = FrozenPen(Grey, 2.0);
        private static readonly Pen BandPen      = FrozenPen(GoldFaint, 1.0);
        private static readonly Pen HandlePen    = FrozenPen(Gold, 1.5);
        private static readonly Pen HandleOffPen = FrozenPen(Frozen(0x77, 0x77, 0x77), 1.5);
        private static readonly Pen SpecPrePen   = FrozenPen(Frozen(0x9F, 0xBF, 0xEE, 0x90), 1.0);
        private static readonly Pen HoverPen     = FrozenPen(Frozen(0xFF, 0xFF, 0xFF, 0x30), 1.0);
        private static readonly Typeface Face = new Typeface("Segoe UI");

        private static readonly double[] GridHz  = { 10, 20, 30, 50, 100, 200, 300, 500, 1000, 2000 };
        private static readonly double[] LabelHz = { 10, 20, 50, 100, 200, 500, 1000, 2000 };

        private static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        private static Pen FrozenPen(Brush b, double w)
        {
            var p = new Pen(b, w);
            p.Freeze();
            return p;
        }

        private IList<EqBand> _bands;
        private bool _eqActive = true;
        private int _selected = -1;
        private int _hover = -1;
        private Point _hoverPt;
        private bool _hoverInside;

        // Drag state. Frequency and gain follow the cursor with the offset
        // the user grabbed the handle at, so a handle never jumps under the
        // cursor; Q drags are relative to the grab point.
        private int _drag = -1;
        private bool _dragMoved;
        private double _dragStartY;
        private double _dragStartQ;
        private double _dragHzRatio = 1.0;
        private double _dragGainOffsetDb;
        private bool _dragAlt;

        /// <summary>Live spectrum, one entry per bin, in dB (0 = full-scale
        /// sine). Set by the host from its analysis timer; may be null.</summary>
        public double[] SpectrumHz;
        public float[] SpectrumPostDb;
        public float[] SpectrumPreDb;

        public EqCurveEditor()
        {
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            MinHeight = 160;
        }

        /// <summary>The live band list. Edited in place by drags and keys.</summary>
        public IList<EqBand> Bands
        {
            get => _bands;
            set
            {
                _bands = value;
                int n = value?.Count ?? 0;
                if (_selected >= n) _selected = -1;
                if (_hover >= n) _hover = -1;
                if (_drag >= n) { _drag = -1; if (IsMouseCaptured) ReleaseMouseCapture(); }
                InvalidateVisual();
            }
        }

        /// <summary>False greys the curve (the EQ is bypassed); the handles
        /// stay editable so the user can prepare a curve before enabling.</summary>
        public bool EqActive
        {
            get => _eqActive;
            set { _eqActive = value; InvalidateVisual(); }
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                int n = _bands?.Count ?? 0;
                if (value < 0 || value >= n) value = -1;
                if (value == _selected) return;
                _selected = value;
                InvalidateVisual();
                SelectionChanged?.Invoke();
            }
        }

        /// <summary>About to change this band by a gesture (drag, scroll,
        /// key). The host snapshots for undo. Raised BEFORE the change.</summary>
        public event Action<int> EditStarted;
        /// <summary>A band's values moved (live, many times per drag).</summary>
        public event Action<int> BandChanged;
        /// <summary>A drag, scroll or key edit finished on this band.</summary>
        public event Action<int> BandCommitted;
        /// <summary>Double-click on empty space: a new bell at that point.</summary>
        public event Action<EqBand> BandAddRequested;
        /// <summary>Right-click on a handle, or Delete on the selection.</summary>
        public event Action<int> BandRemoveRequested;
        /// <summary>Ctrl+Z in the graph.</summary>
        public event Action UndoRequested;
        public event Action SelectionChanged;

        // ---------- mapping ----------

        private double PlotW => Math.Max(1.0, ActualWidth - PadL - PadR);
        private double PlotH => Math.Max(1.0, ActualHeight - PadT - PadB);
        private Rect PlotRect => new Rect(PadL, PadT, PlotW, PlotH);

        private double XOf(double hz)
        {
            if (hz < MinHz) hz = MinHz;
            if (hz > MaxHz) hz = MaxHz;
            return PadL + Math.Log(hz / MinHz) / Math.Log(MaxHz / MinHz) * PlotW;
        }

        private double HzOf(double x)
        {
            double t = (x - PadL) / PlotW;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return MinHz * Math.Exp(t * Math.Log(MaxHz / MinHz));
        }

        private double YOf(double db)
        {
            if (db < MinDb) db = MinDb;
            if (db > MaxDb) db = MaxDb;
            return PadT + (MaxDb - db) / (MaxDb - MinDb) * PlotH;
        }

        private double DbOf(double y)
        {
            double t = (y - PadT) / PlotH;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return MaxDb - t * (MaxDb - MinDb);
        }

        private double YOfSpec(double db)
        {
            double t = (db - SpecMinDb) / (SpecMaxDb - SpecMinDb);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return PadT + PlotH - t * PlotH;
        }

        private static bool IsCut(EqBand b) => b.IsCut;

        private Point HandleCenter(EqBand b) => new Point(XOf(b.FrequencyHz), YOf(IsCut(b) ? 0.0 : b.GainDb));

        private int HitTest(Point p)
        {
            var bands = _bands;
            if (bands == null) return -1;
            int best = -1;
            double bestD = HitR * HitR;
            // Later bands are drawn on top, so prefer them on overlap.
            for (int i = bands.Count - 1; i >= 0; i--)
            {
                var b = bands[i];
                if (b == null) continue;
                var c = HandleCenter(b);
                double dx = p.X - c.X, dy = p.Y - c.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // Rounds to a friendly value so a drag leaves numbers a person would type.
        private static double RoundHz(double hz)
        {
            if (hz < 100) return Math.Round(hz, 1);
            if (hz < 1000) return Math.Round(hz);
            return Math.Round(hz / 5) * 5;
        }

        private static bool AltDown => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        private static bool ShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        private EqBand SelectedBand()
        {
            var bands = _bands;
            return bands != null && _selected >= 0 && _selected < bands.Count ? bands[_selected] : null;
        }

        // ---------- input ----------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            var bands = _bands;
            if (bands == null) return;
            var p = e.GetPosition(this);
            int hit = HitTest(p);

            if (e.ClickCount == 2)
            {
                if (hit < 0 && bands.Count < ParametricEq.MaxBands)
                {
                    var nb = new EqBand
                    {
                        FrequencyHz = RoundHz(HzOf(p.X)),
                        GainDb      = Math.Round(DbOf(p.Y), 1),
                        Q           = ParametricEq.DefaultQ,
                        Type        = EqBandType.Peak,
                    };
                    ParametricEq.Clamp(nb);
                    BandAddRequested?.Invoke(nb);
                }
                e.Handled = true;
                return;
            }

            if (hit >= 0)
            {
                var b = bands[hit];
                SelectedIndex = hit;
                _drag = hit;
                _dragMoved = false;
                _dragStartY = p.Y;
                _dragStartQ = b?.Q ?? ParametricEq.DefaultQ;
                _dragHzRatio = b != null ? b.FrequencyHz / HzOf(p.X) : 1.0;
                _dragGainOffsetDb = b != null ? b.GainDb - DbOf(p.Y) : 0.0;
                _dragAlt = AltDown;
                EditStarted?.Invoke(hit);
                CaptureMouse();
                e.Handled = true;
            }
            else
            {
                SelectedIndex = -1;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var bands = _bands;
            var p = e.GetPosition(this);

            if (_drag >= 0 && IsMouseCaptured)
            {
                if (bands == null || _drag >= bands.Count) { _drag = -1; ReleaseMouseCapture(); return; }
                var b = bands[_drag];
                if (b == null) return;

                bool alt = AltDown;
                if (alt != _dragAlt)
                {
                    // Modifier flipped mid-drag: re-anchor so nothing jumps.
                    _dragAlt = alt;
                    _dragStartY = p.Y;
                    _dragStartQ = b.Q;
                    _dragGainOffsetDb = b.GainDb - DbOf(p.Y);
                    _dragHzRatio = b.FrequencyHz / HzOf(p.X);
                }

                bool qDrag = IsCut(b) || alt;
                if (!alt) b.FrequencyHz = RoundHz(HzOf(p.X) * _dragHzRatio);
                if (qDrag)
                    b.Q = Math.Round(_dragStartQ * Math.Pow(2.0, (_dragStartY - p.Y) / QDragPixels), 2);
                else
                    b.GainDb = Math.Round(DbOf(p.Y) + _dragGainOffsetDb, 1);
                ParametricEq.Clamp(b);
                _dragMoved = true;
                InvalidateVisual();
                BandChanged?.Invoke(_drag);
                return;
            }

            int hit = HitTest(p);
            bool inside = PlotRect.Contains(p);
            bool moved = Math.Abs(p.X - _hoverPt.X) >= 0.5 || Math.Abs(p.Y - _hoverPt.Y) >= 0.5;
            _hoverPt = p;
            if (hit != _hover || inside != _hoverInside || (inside && moved))
            {
                _hover = hit;
                _hoverInside = inside;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Arrow;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_drag < 0) return;
            int idx = _drag;
            bool moved = _dragMoved;
            _drag = -1;
            _dragMoved = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
            if (moved) BandCommitted?.Invoke(idx);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_drag < 0) return;
            int idx = _drag;
            bool moved = _dragMoved;
            _drag = -1;
            _dragMoved = false;
            if (moved) BandCommitted?.Invoke(idx);
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            int hit = HitTest(e.GetPosition(this));
            if (hit < 0) return;
            e.Handled = true;
            BandRemoveRequested?.Invoke(hit);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            var bands = _bands;
            if (bands == null) return;
            int hit = HitTest(e.GetPosition(this));
            if (hit < 0) return;   // off a handle the page scrolls as usual
            var b = bands[hit];
            if (b == null) return;
            EditStarted?.Invoke(hit);
            double notches = e.Delta / 120.0;
            b.Q = Math.Round(b.Q * Math.Pow(1.12, notches), 2);
            ParametricEq.Clamp(b);
            SelectedIndex = hit;
            InvalidateVisual();
            e.Handled = true;
            BandChanged?.Invoke(hit);
            BandCommitted?.Invoke(hit);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            bool redraw = _hover != -1 || _hoverInside;
            _hover = -1;
            _hoverInside = false;
            Cursor = Cursors.Arrow;
            if (redraw) InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                UndoRequested?.Invoke();
                e.Handled = true;
                return;
            }

            var b = SelectedBand();
            if (b == null) return;
            int idx = _selected;

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                BandRemoveRequested?.Invoke(idx);
                e.Handled = true;
                return;
            }

            bool shift = ShiftDown;
            bool qKeys = IsCut(b) || AltDown;
            double hzStep = shift ? Math.Pow(2.0, 1.0 / 3.0) : Math.Pow(2.0, 1.0 / 12.0);
            double dbStep = shift ? 2.0 : 0.5;
            double qStep  = shift ? 1.5 : 1.12;

            switch (e.Key)
            {
                case Key.Left:  EditStarted?.Invoke(idx); b.FrequencyHz = RoundHz(b.FrequencyHz / hzStep); break;
                case Key.Right: EditStarted?.Invoke(idx); b.FrequencyHz = RoundHz(b.FrequencyHz * hzStep); break;
                case Key.Up:
                    EditStarted?.Invoke(idx);
                    if (qKeys) b.Q = Math.Round(b.Q * qStep, 2); else b.GainDb = Math.Round(b.GainDb + dbStep, 1);
                    break;
                case Key.Down:
                    EditStarted?.Invoke(idx);
                    if (qKeys) b.Q = Math.Round(b.Q / qStep, 2); else b.GainDb = Math.Round(b.GainDb - dbStep, 1);
                    break;
                default:
                    return;
            }
            ParametricEq.Clamp(b);
            InvalidateVisual();
            e.Handled = true;
            BandChanged?.Invoke(idx);
            BandCommitted?.Invoke(idx);
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            InvalidateVisual();
        }

        // ---------- drawing ----------

        private FormattedText Text(string s, double size, Brush brush)
        {
            double ppd = 1.0;
            try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, ppd);
        }

        private static string HzLabel(double hz) => hz >= 1000 ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : hz.ToString("0", CultureInfo.InvariantCulture);

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;

            dc.DrawRectangle(BackBrush, IsKeyboardFocused ? FocusPen : BorderPen, new Rect(0.5, 0.5, w - 1, h - 1));
            var plot = PlotRect;
            dc.DrawRectangle(PlotBrush, null, plot);

            // Frequency grid + labels.
            foreach (double hz in GridHz)
            {
                double x = Math.Round(XOf(hz)) + 0.5;
                dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
            foreach (double hz in LabelHz)
            {
                var t = Text(HzLabel(hz), 10, LabelBrush);
                double x = XOf(hz) - t.Width / 2;
                if (x < 2) x = 2;
                if (x + t.Width > w - 2) x = w - 2 - t.Width;
                dc.DrawText(t, new Point(x, plot.Bottom + 4));
            }

            // dB grid + labels every 6 dB; the 0 dB line stands out.
            for (double db = Math.Ceiling(MinDb / 6) * 6; db <= MaxDb; db += 6)
            {
                double y = Math.Round(YOf(db)) + 0.5;
                dc.DrawLine(Math.Abs(db) < 0.01 ? ZeroPen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var t = Text(db.ToString("+0;-0;0", CultureInfo.InvariantCulture), 10, LabelBrush);
                dc.DrawText(t, new Point(PadL - 6 - t.Width, y - t.Height / 2));
            }

            DrawSpectrum(dc, plot);

            var bands = _bands;
            double y0 = YOf(0);

            // The selected band's own response, faint, so its width reads.
            if (bands != null && _selected >= 0 && _selected < bands.Count && bands[_selected] != null && !bands[_selected].IsBypass)
            {
                var single = new List<EqBand>(1) { bands[_selected] };
                dc.DrawGeometry(null, BandPen, CurveGeometry(single, plot, closeToZero: false, y0));
            }

            // Summed response, filled to the 0 dB line.
            if (bands != null && bands.Count > 0)
            {
                bool active = _eqActive;
                dc.DrawGeometry(active ? GoldFill : GreyFill, null, CurveGeometry(bands, plot, closeToZero: true, y0));
                dc.DrawGeometry(null, active ? CurvePen : CurveOffPen, CurveGeometry(bands, plot, closeToZero: false, y0));
            }

            // Hover crosshair + readout (top left), when not on a handle.
            if (_hoverInside && _hover < 0 && _drag < 0)
            {
                double x = Math.Round(_hoverPt.X) + 0.5;
                dc.DrawLine(HoverPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                double hz = HzOf(_hoverPt.X);
                double db = bands != null ? ParametricEq.ResponseDb(bands, hz) : 0.0;
                var t = Text(string.Format(CultureInfo.InvariantCulture, "{0} Hz  {1:+0.0;-0.0;0.0} dB", FormatHz(hz), db), 11, ReadoutBrush);
                dc.DrawText(t, new Point(plot.Left + 6, plot.Top + 4));
            }

            // Handles, numbered to match the rows below.
            if (bands != null)
            {
                for (int i = 0; i < bands.Count; i++)
                {
                    var b = bands[i];
                    if (b == null) continue;
                    var c = HandleCenter(b);
                    bool sel = i == _selected;
                    bool hov = i == _hover || i == _drag;
                    double r = HandleR + (hov ? 1.5 : 0);
                    Brush fill = !b.Enabled ? HandleOffFill : sel ? Gold : HandleFill;
                    Pen pen = b.Enabled ? HandlePen : HandleOffPen;
                    dc.DrawEllipse(fill, pen, c, r, r);
                    var t = Text((i + 1).ToString(CultureInfo.InvariantCulture), 9, sel && b.Enabled ? HandleNumSel : HandleNumOn);
                    dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
                }
            }

            // Readout for the selected band, top right of the plot.
            var sb = SelectedBand();
            if (sb != null)
            {
                string s = IsCut(sb)
                    ? string.Format(CultureInfo.InvariantCulture, "{0}: {1} Hz  {2} dB/oct  Q {3:0.00}", TypeLabel(sb.Type), FormatHz(sb.FrequencyHz), ParametricEq.SnapSlope(sb.SlopeDbPerOct), sb.Q)
                    : string.Format(CultureInfo.InvariantCulture, "{0}: {1} Hz  {2:+0.0;-0.0;0.0} dB  Q {3:0.00}", TypeLabel(sb.Type), FormatHz(sb.FrequencyHz), sb.GainDb, sb.Q);
                var t = Text(s, 11, ReadoutBrush);
                dc.DrawText(t, new Point(plot.Right - t.Width - 6, plot.Top + 4));
            }
            else if (!_eqActive)
            {
                var t = Text("EQ off (bypassed)", 11, ReadoutBrush);
                dc.DrawText(t, new Point(plot.Right - t.Width - 6, plot.Top + 4));
            }
        }

        private void DrawSpectrum(DrawingContext dc, Rect plot)
        {
            var hz = SpectrumHz;
            var post = SpectrumPostDb;
            if (hz == null || post == null || hz.Length != post.Length || hz.Length < 2) return;
            int n = hz.Length;

            // Bars: each bin spans halfway to its neighbours in log x.
            for (int k = 0; k < n; k++)
            {
                double db = post[k];
                if (db <= SpecMinDb + 0.5) continue;
                double xc = XOf(hz[k]);
                double xl = k > 0 ? (XOf(hz[k - 1]) + xc) / 2 : plot.Left;
                double xr = k < n - 1 ? (xc + XOf(hz[k + 1])) / 2 : plot.Right;
                double y = YOfSpec(db);
                if (xr - xl < 1) continue;
                dc.DrawRectangle(SpecFill, null, new Rect(xl + 0.5, y, xr - xl - 1, plot.Bottom - y));
            }

            // Pre-EQ outline: where the energy was before the curve acted.
            var pre = SpectrumPreDb;
            if (!_eqActive || pre == null || pre.Length != n) return;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                bool started = false;
                for (int k = 0; k < n; k++)
                {
                    var pt = new Point(XOf(hz[k]), YOfSpec(pre[k]));
                    if (!started) { ctx.BeginFigure(pt, false, false); started = true; }
                    else ctx.LineTo(pt, true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, SpecPrePen, g);
        }

        private StreamGeometry CurveGeometry(IList<EqBand> bands, Rect plot, bool closeToZero, double y0)
        {
            var g = new StreamGeometry();
            int n = Math.Max(16, (int)(plot.Width / 2));
            using (var ctx = g.Open())
            {
                for (int i = 0; i <= n; i++)
                {
                    double x = plot.Left + plot.Width * i / n;
                    double hz = MinHz * Math.Exp((double)i / n * Math.Log(MaxHz / MinHz));
                    double db = ParametricEq.ResponseDb(bands, hz);
                    double y = YOf(db);
                    if (i == 0) ctx.BeginFigure(new Point(x, closeToZero ? y0 : y), closeToZero, closeToZero);
                    if (i == 0 && closeToZero) ctx.LineTo(new Point(x, y), false, false);
                    else ctx.LineTo(new Point(x, y), true, false);
                }
                if (closeToZero) ctx.LineTo(new Point(plot.Right, y0), false, false);
            }
            g.Freeze();
            return g;
        }

        public static string FormatHz(double hz)
            => hz < 100 ? hz.ToString("0.#", CultureInfo.InvariantCulture) : hz.ToString("0", CultureInfo.InvariantCulture);

        public static string TypeLabel(EqBandType t)
        {
            switch (t)
            {
                case EqBandType.LowShelf:  return "Low shelf";
                case EqBandType.HighShelf: return "High shelf";
                case EqBandType.LowCut:    return "Low cut";
                case EqBandType.HighCut:   return "High cut";
                default:                   return "Bell";
            }
        }
    }
}
