using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DwgFlaechen.Core;
using DwgFlaechen.Core.Geometry;

namespace DwgFlaechen.Wpf;

/// <summary>
/// Grafische Vorschau der Flächen. Zeichnet direkt per OnRender (schnell auch bei vielen Objekten).
/// Mausrad = Zoom um Cursor, Ziehen = Verschieben, Klick = Auswahl, Doppelklick = Einpassen.
/// Klick auf einen Eckpunkt = Koordinaten anzeigen (Umschalt: weitere Punkte, Esc: löschen, Strg+C: kopieren).
/// </summary>
public sealed class PreviewCanvas : FrameworkElement
{
    private sealed class Shape
    {
        public required AreaRow Row { get; init; }
        public required StreamGeometry Geometry { get; init; }
        public required Brush? Fill { get; init; }
        public required Color Color { get; init; }
        /// <summary>Gestrichelter Umriss ohne Füllung (alte Kontur im Vergleichsmodus).</summary>
        public bool Dashed { get; init; }
    }

    private static readonly Brush SelFill = RowColors.Brush(Color.FromRgb(255, 140, 0), 0x90);
    private static readonly Color SelStroke = Color.FromRgb(255, 69, 0);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Fangradius für Eckpunkte in Bildschirmpixeln.</summary>
    private const double SnapPx = 7;

    private static readonly Pen HoverPen = Frozen(new Pen(RowColors.Brush(Color.FromRgb(0, 120, 215)), 2));
    private static readonly Pen PinPen = Frozen(new Pen(RowColors.Brush(Color.FromRgb(200, 0, 0)), 2));
    private static readonly Pen LabelBorder = Frozen(new Pen(RowColors.Brush(Color.FromRgb(160, 160, 160)), 1));
    private static readonly Brush LabelBack = RowColors.Brush(Color.FromRgb(255, 255, 255), 0xEE);
    private static readonly Brush LabelText = RowColors.Brush(Color.FromRgb(30, 30, 30));
    private static readonly Typeface LabelFont = new("Segoe UI");

    private static readonly Pen MarkerPen = Frozen(new Pen(RowColors.Brush(Color.FromRgb(200, 0, 200)), 2));

    private static Pen Frozen(Pen p) { p.Freeze(); return p; }

    // Eckpunkt-Markierung im Vergleich: grün = genau gleich, rot = geringfügig abweichend
    private static readonly Pen EqualPen = Frozen(new Pen(RowColors.Brush(Color.FromRgb(0, 160, 60)), 1.5));
    private static readonly Brush EqualFill = RowColors.Brush(Color.FromRgb(0, 160, 60), 0x50);
    private static readonly Pen NearPen = Frozen(new Pen(RowColors.Brush(Color.FromRgb(220, 40, 40)), 1.5));
    private static readonly Brush NearFill = RowColors.Brush(Color.FromRgb(220, 40, 40), 0x60);
    private const int MaxPointMarks = 20_000;

    private List<Shape> _shapes = new();
    private HashSet<AreaRow> _visible = new();
    private HashSet<AreaRow> _selected = new();
    private Bounds2 _bounds = Bounds2.Empty;
    private P2 _origin;
    private P2? _hoverCorner;
    private readonly List<P2> _pins = new();
    private (P2 From, P2 To, string Text)? _marker;
    private PointMarks? _pointMarks;

    // Bildschirm = (s·(x−ox) + tx, −s·(y−oy) + ty)
    private double _scale = 1, _tx, _ty;
    private bool _panning, _hasView;
    private Point _panStart, _pressPoint;
    private double _panTx, _panTy;

    /// <summary>Klick auf eine Fläche (null = ins Leere). Zweiter Parameter: Strg gedrückt.</summary>
    public event Action<AreaRow?, bool>? ItemClicked;

    /// <summary>Eckpunkt angeklickt: Punkt in Weltkoordinaten und die (kleinste) Fläche, zu der er gehört.</summary>
    public event Action<P2, AreaRow>? CornerPicked;

    /// <summary>Die markierten Eckpunkte wurden geändert (z. B. gelöscht).</summary>
    public event Action? PinsChanged;

    /// <summary>Mauszeiger bewegt: Position in Weltkoordinaten (null = außerhalb).</summary>
    public event Action<P2?>? CursorMoved;

    /// <summary>Einheitenzeichen für die Koordinatenbeschriftung, z. B. "m".</summary>
    public string UnitSymbol { get; set; } = "";

    /// <summary>Aktueller Maßstab (Pixel je Zeichnungseinheit) – für die Nachkommastellen der Mauskoordinate.</summary>
    public double Scale => _scale;

    /// <summary>Die aktuell markierten Eckpunkte (Weltkoordinaten), in Klickreihenfolge.</summary>
    public IReadOnlyList<P2> Pins => _pins;

    /// <summary>Markierung der größten Abweichung im Vergleich (Linie Eckpunkt → andere Kontur, Ring, Beschriftung).</summary>
    public void SetMarker(P2 from, P2 to, string text)
    {
        _marker = (from, to, text);
        InvalidateVisual();
    }

    /// <summary>Eckpunkte im Vergleich markieren (null = keine Markierung).</summary>
    public void SetPointMarks(PointMarks? marks)
    {
        _pointMarks = marks;
        InvalidateVisual();
    }

    public void ClearMarker()
    {
        if (_marker == null) return;
        _marker = null;
        InvalidateVisual();
    }

    public void ClearPins()
    {
        if (_pins.Count == 0) return;
        _pins.Clear();
        InvalidateVisual();
        PinsChanged?.Invoke();
    }

    public PreviewCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public void SetItems(IReadOnlyList<AreaRow> rows, Bounds2 bounds) => Build(rows, bounds, keepView: false);

    /// <summary>Neu aufbauen, aber Zoom/Ausschnitt beibehalten (z. B. nach Einheitenwechsel).</summary>
    public void SetItemsKeepView(IReadOnlyList<AreaRow> rows) => Build(rows, _bounds, keepView: true);

    private void Build(IReadOnlyList<AreaRow> rows, Bounds2 bounds, bool keepView)
    {
        _bounds = bounds;
        _origin = bounds.IsEmpty ? new P2(0, 0) : new P2(bounds.MinX, bounds.MinY);
        _shapes = new List<Shape>(rows.Count);

        // Große Flächen zuerst, damit kleine obenauf liegen; reine Umrisse zuletzt (obenauf)
        foreach (var row in rows.OrderBy(r => r.OutlineOnly).ThenByDescending(r => r.Area))
        {
            var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = g.Open())
            {
                foreach (var o in row.Item.Outlines)
                {
                    if (o.Points.Count < 2) continue;
                    ctx.BeginFigure(ToLocal(o.Points[0]), isFilled: true, isClosed: true);
                    ctx.PolyLineTo(o.Points.Skip(1).Select(ToLocal).ToList(), isStroked: true, isSmoothJoin: false);
                }
            }
            g.Freeze();
            _shapes.Add(new Shape
            {
                Row = row, Geometry = g, Color = row.Color, Dashed = row.OutlineOnly,
                Fill = row.OutlineOnly ? null : RowColors.Brush(row.Color, 0x40),
            });
        }
        _visible = rows.ToHashSet();
        _selected.Clear();
        _hoverCorner = null;
        _marker = null;
        if (!keepView) ClearPins();
        if (keepView && _hasView) { InvalidateVisual(); return; }
        _hasView = false;
        FitAll();
    }

    public void SetVisible(IEnumerable<AreaRow> rows)
    {
        _visible = rows.ToHashSet();
        InvalidateVisual();
    }

    public void SetSelected(IEnumerable<AreaRow> rows)
    {
        _selected = rows.ToHashSet();
        InvalidateVisual();
    }

    private Point ToLocal(P2 p) => new(p.X - _origin.X, p.Y - _origin.Y);

    // ------------------------------------------------------------------ Ansicht

    public void FitAll()
    {
        var b = Bounds2.Empty;
        foreach (var s in _shapes)
            if (_visible.Contains(s.Row)) b = b.Union(s.Row.Item.Bounds);
        ZoomTo(b.IsEmpty ? _bounds : b);
    }

    public void ZoomTo(Bounds2 b)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10 || b.IsEmpty) { InvalidateVisual(); return; }
        double bw = Math.Max(b.Width, 1e-9), bh = Math.Max(b.Height, 1e-9);
        _scale = Math.Min(w / bw, h / bh) * 0.9;
        double cx = (b.MinX + b.MaxX) / 2 - _origin.X, cy = (b.MinY + b.MaxY) / 2 - _origin.Y;
        _tx = w / 2 - _scale * cx;
        _ty = h / 2 + _scale * cy;
        _hasView = true;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_hasView) FitAll();
    }

    // ------------------------------------------------------------------ Zeichnen

    protected override void OnRender(DrawingContext dc)
    {
        // Transparenter Hintergrund → Mausereignisse auf der ganzen Fläche
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_shapes.Count == 0) return;

        // Starke Vergrößerung: WPF rechnet beim Zeichnen mit float → Konturen in double in Bildschirm-
        // koordinaten umrechnen und zuschneiden, statt eine riesige Transformation zu übergeben.
        bool precise = ScreenClip.NeedsPrecise(_scale, _bounds);
        var view = new ViewTransform(_scale, _tx, _ty, _origin);
        var clip = ScreenClip.ClipRect(ActualWidth, ActualHeight);
        Geometry? GeoFor(Shape s)
        {
            if (!precise) return s.Geometry;
            var sb = view.ToScreen(s.Row.Item.Bounds);
            if (sb.MaxX < clip.MinX || sb.MinX > clip.MaxX || sb.MaxY < clip.MinY || sb.MinY > clip.MaxY) return null;
            var loops = ScreenClip.Clip(s.Row.Item.Outlines.Select(o => o.Points), view, clip);
            if (loops.Count == 0) return null;
            var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = g.Open())
                foreach (var l in loops)
                {
                    ctx.BeginFigure(new Point(l[0].X, l[0].Y), isFilled: true, isClosed: true);
                    ctx.PolyLineTo(l.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), isStroked: true, isSmoothJoin: false);
                }
            g.Freeze();
            return g;
        }

        if (!precise) dc.PushTransform(new MatrixTransform(_scale, 0, 0, -_scale, _tx, _ty));
        double px = precise ? 1.0 : 1.0 / _scale;
        var pens = new Dictionary<(Color, bool), Pen>();
        Pen PenFor(Color c, bool dashed)
        {
            if (!pens.TryGetValue((c, dashed), out var p))
            {
                p = new Pen(RowColors.Brush(c), dashed ? 1.5 * px : px) { LineJoin = PenLineJoin.Round };
                if (dashed) p.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
                p.Freeze();
                pens[(c, dashed)] = p;
            }
            return p;
        }

        foreach (var s in _shapes)
        {
            if (!_visible.Contains(s.Row) || _selected.Contains(s.Row)) continue;
            if (GeoFor(s) is { } g) dc.DrawGeometry(s.Fill, PenFor(s.Color, s.Dashed), g);
        }

        // Auswahl zuletzt (obenauf)
        if (_selected.Count > 0)
        {
            var selPen = new Pen(RowColors.Brush(SelStroke), 2.5 * px) { LineJoin = PenLineJoin.Round };
            selPen.Freeze();
            foreach (var s in _shapes)
                if (_visible.Contains(s.Row) && _selected.Contains(s.Row) && GeoFor(s) is { } g)
                    dc.DrawGeometry(s.Dashed ? null : SelFill, selPen, g);
        }
        if (!precise) dc.Pop();

        // Eckpunkt-Markierung im Vergleich (feste Größe, nur sichtbare Punkte)
        if (_pointMarks is { } pm)
        {
            DrawPointMarks(dc, pm.Equal, EqualPen, EqualFill, 3.5);
            DrawPointMarks(dc, pm.Near, NearPen, NearFill, 4.5);
        }

        // Eckpunkte in Bildschirmkoordinaten (feste Größe unabhängig vom Zoom)
        if (_hoverCorner is P2 h && !_pins.Contains(h))
            dc.DrawEllipse(null, HoverPen, ToScreen(h), 5, 5);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // größte Abweichung (Vergleich)
        if (_marker is { } m)
        {
            Point mf = ToScreen(m.From), mt = ToScreen(m.To);
            if (Math.Abs(mf.X) < 1e6 && Math.Abs(mf.Y) < 1e6 && Math.Abs(mt.X) < 1e6 && Math.Abs(mt.Y) < 1e6)
            {
                dc.DrawLine(MarkerPen, mf, mt);
                dc.DrawEllipse(null, MarkerPen, mf, 10, 10);
                dc.DrawEllipse(MarkerPen.Brush, null, mt, 2.5, 2.5);
                DrawLabel(dc, mf, m.Text, dpi);
            }
        }
        for (int i = 0; i < _pins.Count; i++)
        {
            Point sp = ToScreen(_pins[i]);
            dc.DrawEllipse(Brushes.White, PinPen, sp, 4, 4);
            string head = _pins.Count > 1 ? $"Punkt {i + 1}\n" : "";
            DrawLabel(dc, sp, $"{head}X = {Fmt(_pins[i].X)}\nY = {Fmt(_pins[i].Y)}", dpi);
        }
    }

    /// <summary>Kleine Kreise an den sichtbaren Punkten, alle in einer Geometrie (schnell auch bei vielen Punkten).</summary>
    private void DrawPointMarks(DrawingContext dc, List<P2> points, Pen pen, Brush fill, double r)
    {
        if (points.Count == 0) return;
        double w = ActualWidth, h = ActualHeight;
        var g = new StreamGeometry();
        int n = 0;
        using (var ctx = g.Open())
            foreach (var p in points)
            {
                Point s = ToScreen(p);
                if (s.X < -r || s.Y < -r || s.X > w + r || s.Y > h + r) continue;
                ctx.BeginFigure(new Point(s.X + r, s.Y), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(s.X - r, s.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.ArcTo(new Point(s.X + r, s.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                if (++n >= MaxPointMarks) break;
            }
        if (n == 0) return;
        g.Freeze();
        dc.DrawGeometry(fill, pen, g);
    }

    private string Fmt(double v) => NumberFormats.ExactText(v, De) + (UnitSymbol.Length > 0 ? " " + UnitSymbol : "");

    private void DrawLabel(DrawingContext dc, Point anchor, string text, double dpi)
    {
        var ft = new FormattedText(text, De, FlowDirection.LeftToRight, LabelFont, 12, LabelText, dpi);
        double w = ft.Width + 12, h = ft.Height + 8;
        // Standard: rechts oberhalb; am Rand auf die andere Seite klappen
        double x = anchor.X + 10, y = anchor.Y - 10 - h;
        if (x + w > ActualWidth - 2) x = anchor.X - 10 - w;
        if (y < 2) y = anchor.Y + 10;
        var r = new Rect(x, y, w, h);
        dc.DrawRoundedRectangle(LabelBack, LabelBorder, r, 4, 4);
        dc.DrawText(ft, new Point(x + 6, y + 4));
    }

    private Point ToScreen(P2 w) => new(_scale * (w.X - _origin.X) + _tx, -_scale * (w.Y - _origin.Y) + _ty);

    private P2 ToWorld(Point s) => new((s.X - _tx) / _scale + _origin.X, -(s.Y - _ty) / _scale + _origin.Y);

    // ------------------------------------------------------------------ Maus

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Point p = e.GetPosition(this);
        double f = e.Delta > 0 ? 1.2 : 1 / 1.2;
        double newScale = Math.Clamp(_scale * f, 1e-9, 1e9);
        f = newScale / _scale;
        _tx = p.X - (p.X - _tx) * f;
        _ty = p.Y - (p.Y - _ty) * f;
        _scale = newScale;
        _hasView = true;
        InvalidateVisual();
        CursorMoved?.Invoke(ToWorld(p)); // Nachkommastellen der Mauskoordinate passen sich dem Zoom an
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            FitAll();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton is MouseButton.Left or MouseButton.Middle)
        {
            _panning = true;
            _panStart = _pressPoint = e.GetPosition(this);
            _panTx = _tx; _panTy = _ty;
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point p = e.GetPosition(this);
        if (_shapes.Count > 0) CursorMoved?.Invoke(ToWorld(p));
        if (!_panning)
        {
            var hit = FindCorner(p);
            P2? corner = hit?.Point;
            if (corner != _hoverCorner)
            {
                _hoverCorner = corner;
                Cursor = corner != null ? Cursors.Hand : Cursors.Cross;
                InvalidateVisual();
            }
            return;
        }
        _tx = _panTx + (p.X - _panStart.X);
        _ty = _panTy + (p.Y - _panStart.Y);
        _hasView = true;
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ReleaseMouseCapture();
        Point p = e.GetPosition(this);
        bool isClick = Math.Abs(p.X - _pressPoint.X) < 4 && Math.Abs(p.Y - _pressPoint.Y) < 4;
        if (!isClick || e.ChangedButton != MouseButton.Left) return;

        // Eckpunkt hat Vorrang vor der Flächenauswahl
        if (FindCorner(p) is { } c)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _pins.Clear();
            _pins.Remove(c.Point);
            _pins.Add(c.Point);
            InvalidateVisual();
            CornerPicked?.Invoke(c.Point, c.Row);
            return;
        }
        var hitRow = HitTest(p);
        if (hitRow == null && Keyboard.Modifiers == ModifierKeys.None) ClearPins();
        ItemClicked?.Invoke(hitRow, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        CursorMoved?.Invoke(null);
        if (_hoverCorner != null && !_panning)
        {
            _hoverCorner = null;
            Cursor = Cursors.Cross;
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _pins.Count > 0)
        {
            ClearPins();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _pins.Count > 0)
        {
            CopyPins();
            e.Handled = true;
        }
    }

    /// <summary>Markierte Eckpunkte als Text (X Tab Y je Zeile) in die Zwischenablage – zum Einfügen in Excel.</summary>
    public bool CopyPins()
    {
        if (_pins.Count == 0) return false;
        var sb = new System.Text.StringBuilder();
        foreach (var pt in _pins)
            sb.Append(pt.X.ToString("0.######", De)).Append('\t').Append(pt.Y.ToString("0.######", De)).AppendLine();
        try { Clipboard.SetText(sb.ToString()); return true; }
        catch { return false; }
    }

    /// <summary>Nächster echter Eckpunkt einer sichtbaren Fläche innerhalb des Fangradius.</summary>
    private (P2 Point, AreaRow Row)? FindCorner(Point screen)
    {
        if (_shapes.Count == 0 || _scale <= 0) return null;
        P2 w = ToWorld(screen);
        double tol = SnapPx / _scale;
        double best = SnapPx * SnapPx;
        (P2 Point, AreaRow Row)? hit = null;
        foreach (var s in _shapes)
        {
            if (!_visible.Contains(s.Row)) continue;
            var b = s.Row.Item.Bounds;
            if (w.X < b.MinX - tol || w.X > b.MaxX + tol || w.Y < b.MinY - tol || w.Y > b.MaxY + tol) continue;
            foreach (var o in s.Row.Item.Outlines)
            foreach (var c in o.Corners)
            {
                double dx = (c.X - w.X) * _scale, dy = (c.Y - w.Y) * _scale;
                double d2 = dx * dx + dy * dy;
                // gleicher Punkt in mehreren Flächen → kleinste Fläche melden
                bool same = hit != null && c == hit.Value.Point;
                if (d2 < best || (same && s.Row.Area < hit!.Value.Row.Area))
                {
                    best = d2;
                    hit = (c, s.Row);
                }
            }
        }
        return hit;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panning = false;
    }

    /// <summary>Kleinste sichtbare Fläche unter dem Punkt.</summary>
    private AreaRow? HitTest(Point screen)
    {
        var w = ToWorld(screen);
        AreaRow? hit = null;
        foreach (var s in _shapes)
        {
            if (!_visible.Contains(s.Row)) continue;
            var b = s.Row.Item.Bounds;
            if (w.X < b.MinX || w.X > b.MaxX || w.Y < b.MinY || w.Y > b.MaxY) continue;
            int inside = s.Row.Item.Outlines.Count(o => Loop.Contains(o.Points, w)); // Even-Odd: Inseln = Löcher
            if (inside % 2 == 0) continue;
            if (hit == null || s.Row.Area < hit.Area) hit = s.Row;
        }
        return hit;
    }
}
