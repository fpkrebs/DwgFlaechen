using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using DwgFlaechen.Core.Geometry;
using AcadColor = ACadSharp.Color;

namespace DwgFlaechen.Core;

/// <summary>
/// Liest eine DWG- oder DXF-Datei mit ACadSharp und ermittelt alle Flächenobjekte des Modellbereichs:
/// geschlossene Polylinien, Schraffuren (Inseln werden abgezogen), Kreise, Ellipsen, 2D-Solids und
/// geschlossene Splines – auf Wunsch auch innerhalb von Blockreferenzen.
/// </summary>
public sealed partial class DwgAreaExtractor
{
    private const int MaxBlockDepth = 32;
    private const double Tol = 1e-9;

    private readonly ExtractionOptions _opt;
    private readonly List<AreaItem> _items = new();
    private readonly List<string> _warnings = new();
    private readonly Dictionary<string, int> _skipped = new();
    private int _nr;

    private DwgAreaExtractor(ExtractionOptions options) => _opt = options;

    public static ExtractionResult Extract(string path, ExtractionOptions? options = null)
    {
        var ex = new DwgAreaExtractor(options ?? new ExtractionOptions());
        return ex.Run(path);
    }

    public static Task<ExtractionResult> ExtractAsync(string path, ExtractionOptions? options = null,
        CancellationToken ct = default) => Task.Run(() => Extract(path, options), ct);

    private ExtractionResult Run(string path)
    {
        CadDocument doc = ReadDocument(path);
        if (_opt.IncludeStoreplannerAreas) CollectStoreplannerLabels(doc);

        var ctx = new Context(Affine3.Identity, null, AcadColor.ByLayer, "", 0);
        foreach (Entity e in doc.Entities)
            ProcessEntity(e, ctx);

        foreach (var kv in _skipped.OrderBy(k => k.Key))
            _warnings.Add($"{kv.Value} × {kv.Key} nicht auswertbar (übersprungen).");

        var bounds = Bounds2.Empty;
        foreach (var i in _items) bounds = bounds.Union(i.Bounds);

        return new ExtractionResult
        {
            FilePath = path,
            Version = doc.Header.Version.ToString(),
            Unit = ResolveUnit(DrawingUnits.FromInsUnits((int)doc.Header.InsUnits)),
            Items = _items,
            Warnings = _warnings,
            Bounds = bounds,
        };
    }

    private CadDocument ReadDocument(string path)
    {
        void Notify(object? sender, NotificationEventArgs e)
        {
            if (e.NotificationType is NotificationType.Error && _warnings.Count < 50)
                _warnings.Add("Lesefehler: " + e.Message);
        }

        if (string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase))
            return DxfReader.Read(path, Notify);

        // Unbekannte Objekte behalten und ihre Proxy-Grafik lesen – daraus werden die
        // Storeplanner-Flächen (GIN_DBAREA) gewonnen.
        if (_opt.IncludeStoreplannerAreas)
        {
            try
            {
                var config = new DwgReaderConfiguration { KeepUnknownEntities = true, IgnoreProxyGraphics = false };
                return DwgReader.Read(path, config, Notify);
            }
            catch (Exception ex)
            {
                // Defekte Proxy-Grafik bricht das Lesen in ACadSharp ab → ohne Proxy-Grafik erneut lesen
                _warnings.Add("Proxy-Grafik nicht lesbar, Storeplanner-Flächen werden übersprungen: " + ex.Message);
            }
        }
        return DwgReader.Read(path, Notify);
    }

    /// <summary>Kontext beim Durchlaufen (verschachtelter) Blöcke.</summary>
    private sealed record Context(Affine3 Transform, Layer? ParentLayer, AcadColor ParentColor, string BlockPath, int Depth);

    // ------------------------------------------------------------------------------------------

    private void ProcessEntity(Entity e, Context ctx)
    {
        if (e.IsInvisible) return;

        // Objekte auf Layer "0" in Blöcken übernehmen den Layer der Blockreferenz
        Layer layer = e.Layer;
        if (ctx.ParentLayer != null && string.Equals(layer?.Name, Layer.DefaultName, StringComparison.Ordinal))
            layer = ctx.ParentLayer;
        layer ??= Layer.Default;

        if (_opt.OnlyVisibleLayers && (!layer.IsOn || layer.Flags.HasFlag(LayerFlags.Frozen)))
            return;

        AcadColor color = e.Color;
        if (color.IsByLayer) color = layer.Color;
        else if (color.IsByBlock) color = ctx.ParentColor.IsByLayer || ctx.ParentColor.IsByBlock ? layer.Color : ctx.ParentColor;

        try
        {
            if (IsStoreplannerArea(e))
            {
                if (_opt.IncludeStoreplannerAreas) HandleStoreplannerArea(e, layer, color, ctx);
                return;
            }

            switch (e)
            {
                case LwPolyline lw when _opt.IncludePolylines:
                    HandleLwPolyline(lw, layer, color, ctx);
                    break;
                case Polyline2D pl when _opt.IncludePolylines:
                    HandlePolyline2D(pl, layer, color, ctx);
                    break;
                case Hatch h when _opt.IncludeHatches:
                    HandleHatch(h, layer, color, ctx);
                    break;
                case Circle c when c is not Arc && _opt.IncludeCircles:
                    HandleCircle(c, layer, color, ctx);
                    break;
                case ACadSharp.Entities.Ellipse el when _opt.IncludeEllipses:
                    HandleEllipse(el, layer, color, ctx);
                    break;
                case Solid s when _opt.IncludeSolids:
                    HandleSolid(s, layer, color, ctx);
                    break;
                case Spline sp when _opt.IncludeSplines:
                    HandleSpline(sp, layer, color, ctx);
                    break;
                case Insert ins when _opt.IncludeBlocks:
                    HandleInsert(ins, layer, color, ctx);
                    break;
                case Region:
                    Skip("Region (ACIS-Geometrie)");
                    break;
            }
        }
        catch (Exception ex)
        {
            _warnings.Add($"{e.ObjectName} [{e.Handle:X}]: {ex.Message}");
        }
    }

    private void Skip(string what) => _skipped[what] = _skipped.TryGetValue(what, out int n) ? n + 1 : 1;

    // ------------------------------------------------------------------------------------------ Entities

    private void HandleLwPolyline(LwPolyline lw, Layer layer, AcadColor color, Context ctx)
    {
        if (lw.Vertices.Count < 2) return;
        var loop = new Loop();
        foreach (var v in lw.Vertices) loop.Add(new P2(v.Location.X, v.Location.Y), v.Bulge);

        if (!IsClosedOrCoincident(lw.IsClosed, loop)) return;

        var t = ctx.Transform.Then(Affine3.FromOcs(lw.Normal.X, lw.Normal.Y, lw.Normal.Z));
        AddItem(lw, AreaKind.Polylinie, layer, color, ctx, [loop.Transform(t, lw.Elevation)]);
    }

    private void HandlePolyline2D(Polyline2D pl, Layer layer, AcadColor color, Context ctx)
    {
        var loop = new Loop();
        foreach (var v in pl.Vertices)
        {
            if (v.Flags.HasFlag(VertexFlags.SplineFrameControlPoint)) continue;
            loop.Add(new P2(v.Location.X, v.Location.Y), v.Bulge);
        }
        if (loop.Count < 2 || !IsClosedOrCoincident(pl.IsClosed, loop)) return;

        var t = ctx.Transform.Then(Affine3.FromOcs(pl.Normal.X, pl.Normal.Y, pl.Normal.Z));
        AddItem(pl, AreaKind.Polylinie, layer, color, ctx, [loop.Transform(t, pl.Elevation)]);
    }

    private bool IsClosedOrCoincident(bool isClosed, Loop loop)
    {
        if (isClosed) return true;
        return _opt.TreatCoincidentEndsAsClosed && loop.Count >= 3 &&
               P2.Distance(loop.Points[0], loop.Points[^1]) <= Tol * Math.Max(1, loop.Points[0].Length);
    }

    private void HandleCircle(Circle c, Layer layer, AcadColor color, Context ctx)
    {
        if (c.Radius <= 0) return;
        // Kreis in OCS: Mittelpunkt ist in OCS gespeichert – zwei Halbkreise mit Bulge 1 → exakte Fläche πr²
        var ocs = Affine3.FromOcs(c.Normal.X, c.Normal.Y, c.Normal.Z);
        var loop = new Loop();
        loop.Add(new P2(c.Center.X + c.Radius, c.Center.Y), 1, corner: false);
        loop.Add(new P2(c.Center.X - c.Radius, c.Center.Y), 1, corner: false);
        AddItem(c, AreaKind.Kreis, layer, color, ctx, [loop.Transform(ctx.Transform.Then(ocs), c.Center.Z)]);
    }

    private void HandleEllipse(ACadSharp.Entities.Ellipse el, Layer layer, AcadColor color, Context ctx)
    {
        double span = el.EndParameter - el.StartParameter;
        span = NormalizeSpan(span);
        if (Math.Abs(span - 2 * Math.PI) > 1e-6) return; // Ellipsenbogen – keine Fläche

        // Ellipse ist in WCS gespeichert
        XYZ ctr = el.Center, maj = el.MajorAxisEndPoint, n = el.Normal;
        double nl = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        if (nl < 1e-12) { n = XYZ.AxisZ; nl = 1; }
        XYZ min = XYZ.Cross(new XYZ(n.X / nl, n.Y / nl, n.Z / nl), maj) * el.RadiusRatio;
        const int steps = 1440;
        var loop = new Loop();
        for (int k = 0; k < steps; k++)
        {
            double a = 2 * Math.PI * k / steps;
            double ca = Math.Cos(a), sa = Math.Sin(a);
            loop.Add(ctx.Transform.ApplyXY(ctr.X + ca * maj.X + sa * min.X,
                                           ctr.Y + ca * maj.Y + sa * min.Y,
                                           ctr.Z + ca * maj.Z + sa * min.Z), 0, corner: false);
        }
        AddItem(el, AreaKind.Ellipse, layer, color, ctx, [loop]);
    }

    private void HandleSolid(Solid s, Layer layer, AcadColor color, Context ctx)
    {
        // SOLID-Eckpunktreihenfolge: 1-2-4-3
        var loop = new Loop();
        loop.Add(new P2(s.FirstCorner.X, s.FirstCorner.Y));
        loop.Add(new P2(s.SecondCorner.X, s.SecondCorner.Y));
        loop.Add(new P2(s.FourthCorner.X, s.FourthCorner.Y));
        loop.Add(new P2(s.ThirdCorner.X, s.ThirdCorner.Y));
        loop = loop.Normalize(Tol);
        if (loop.Count < 3) return;
        var t = ctx.Transform.Then(Affine3.FromOcs(s.Normal.X, s.Normal.Y, s.Normal.Z));
        AddItem(s, AreaKind.Solid, layer, color, ctx, [loop.Transform(t, s.FirstCorner.Z)]);
    }

    private void HandleSpline(Spline sp, Layer layer, AcadColor color, Context ctx)
    {
        if (!sp.IsClosed && !sp.IsPeriodic)
        {
            // manche Splines sind geometrisch geschlossen ohne Flag
            if (!(_opt.TreatCoincidentEndsAsClosed && sp.ControlPoints.Count > 2 &&
                  Dist(sp.ControlPoints[0], sp.ControlPoints[^1]) <= Tol))
                return;
        }
        if (!sp.TryPolygonalVertexes(1024, out List<XYZ> pts) || pts.Count < 3) return;
        var loop = new Loop();
        foreach (var p in pts) loop.Add(ctx.Transform.ApplyXY(p.X, p.Y, p.Z), 0, corner: false);
        AddItem(sp, AreaKind.Spline, layer, color, ctx, [loop]);
    }

    private void HandleInsert(Insert ins, Layer layer, AcadColor color, Context ctx)
    {
        if (ins.Block == null) return;
        if (ctx.Depth >= MaxBlockDepth)
        {
            _warnings.Add($"Block '{ins.Block.Name}': maximale Verschachtelungstiefe erreicht.");
            return;
        }
        // Anonyme Bemaßungs- (*D…) und Tabellenblöcke (*T…) enthalten keine echten Flächen
        if (ins.Block.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase) ||
            ins.Block.Name.StartsWith("*T", StringComparison.OrdinalIgnoreCase)) return;

        XYZ basePt = ins.Block.BlockEntity?.BasePoint ?? XYZ.Zero;
        var ocs = Affine3.FromOcs(ins.Normal.X, ins.Normal.Y, ins.Normal.Z);
        var rot = Affine3.RotationZ(ins.Rotation);
        var scale = Affine3.Scale(ins.XScale, ins.YScale, ins.ZScale);
        var toBase = Affine3.Translation(-basePt.X, -basePt.Y, -basePt.Z);

        string path = string.IsNullOrEmpty(ctx.BlockPath) ? ins.Block.Name : ctx.BlockPath + " > " + ins.Block.Name;
        int rows = Math.Max((int)ins.RowCount, 1), cols = Math.Max((int)ins.ColumnCount, 1);

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            // Welt = OCS · T(Einfügepunkt) · R · T(Array-Versatz) · S · T(−Basispunkt)
            var arr = Affine3.Translation(c * ins.ColumnSpacing, r * ins.RowSpacing, 0);
            var local = Affine3.Translation(ins.InsertPoint.X, ins.InsertPoint.Y, ins.InsertPoint.Z)
                .Then(rot).Then(arr).Then(scale).Then(toBase);
            var t = ctx.Transform.Then(ocs).Then(local);

            var child = new Context(t, layer, color, path, ctx.Depth + 1);
            foreach (Entity e in ins.Block.Entities)
                ProcessEntity(e, child);
        }
    }

    // ------------------------------------------------------------------------------------------ Hatch

    private void HandleHatch(Hatch h, Layer layer, AcadColor color, Context ctx)
    {
        var t = ctx.Transform.Then(Affine3.FromOcs(h.Normal.X, h.Normal.Y, h.Normal.Z));
        var loops = new List<Loop>();
        foreach (var path in h.Paths)
        {
            var local = BuildHatchLoop(path);
            if (local == null) continue;
            local = local.Normalize(Tol);
            if (local.Count < 2) continue;
            loops.Add(local.Transform(t, h.Elevation));
        }
        if (loops.Count == 0) return;
        AddItem(h, AreaKind.Schraffur, layer, color, ctx, loops, h.Style);
    }

    /// <summary>Setzt eine Schraffur-Umgrenzung aus ihren Kanten zu einer geschlossenen Kontur zusammen.</summary>
    private static Loop? BuildHatchLoop(Hatch.BoundaryPath path)
    {
        // Polylinien-Umgrenzung
        var pl = path.Edges.OfType<Hatch.BoundaryPath.Polyline>().FirstOrDefault();
        if (pl != null)
        {
            var loop = new Loop();
            foreach (var v in pl.Vertices) loop.Add(new P2(v.X, v.Y), v.Z); // Z = Bulge
            return loop;
        }

        // Kanten-Umgrenzung: jede Kante als Punktfolge mit Bulges; Richtung wird bei Bedarf umgedreht
        var result = new Loop();
        P2? lastEnd = null;
        foreach (var edge in path.Edges)
        {
            var seg = EdgeToChain(edge);
            if (seg == null || seg.Points.Count < 2) continue;

            if (lastEnd is P2 le)
            {
                double dStart = P2.Distance(le, seg.Points[0]);
                double dEnd = P2.Distance(le, seg.Points[^1]);
                if (dEnd < dStart) seg.Reverse();
            }
            else if (result.Count == 0 && path.Edges.Count > 1)
            {
                // Richtung der ersten Kante anhand der zweiten prüfen
                var next = EdgeToChain(path.Edges[1]);
                if (next != null && next.Points.Count > 1)
                {
                    double best = Math.Min(P2.Distance(seg.Points[^1], next.Points[0]), P2.Distance(seg.Points[^1], next.Points[^1]));
                    double bestRev = Math.Min(P2.Distance(seg.Points[0], next.Points[0]), P2.Distance(seg.Points[0], next.Points[^1]));
                    if (bestRev < best) seg.Reverse();
                }
            }

            // Punkte der Kante ohne Endpunkt anhängen (der Endpunkt ist Startpunkt der nächsten Kante)
            for (int i = 0; i < seg.Points.Count - 1; i++)
                result.Add(seg.Points[i], seg.Bulges[i], seg.Corners[i]);
            lastEnd = seg.Points[^1];
        }
        if (lastEnd is P2 end && result.Count > 0 && P2.Distance(end, result.Points[0]) > 1e-6)
            result.Add(end); // nicht exakt geschlossene Kontur: letzten Punkt behalten
        return result.Count >= 2 ? result : null;
    }

    /// <summary>Offene Kette (Punkte + Bulges pro Segment) einer Umgrenzungskante.</summary>
    private sealed class Chain
    {
        public List<P2> Points { get; } = new();
        public List<double> Bulges { get; } = new(); // Bulges[i] gilt für Points[i] → Points[i+1]
        /// <summary>Eckpunkt-Kennzeichen je Punkt; fehlt es, gelten nur Anfang und Ende als Ecke.</summary>
        public List<bool> Corners { get; } = new();

        public void Reverse()
        {
            Points.Reverse();
            Bulges.Reverse();
            Corners.Reverse();
            for (int i = 0; i < Bulges.Count; i++) Bulges[i] = -Bulges[i];
        }
    }

    private static Chain? EdgeToChain(Hatch.BoundaryPath.Edge edge)
    {
        var ch = new Chain();
        switch (edge)
        {
            case Hatch.BoundaryPath.Line ln:
                ch.Points.Add(new P2(ln.Start.X, ln.Start.Y));
                ch.Points.Add(new P2(ln.End.X, ln.End.Y));
                ch.Bulges.Add(0);
                ch.Corners.Add(true); ch.Corners.Add(true);
                return ch;

            case Hatch.BoundaryPath.Arc arc:
            {
                double span = NormalizeSpan(arc.EndAngle - arc.StartAngle);
                // Im Uhrzeigersinn: Winkel sind gespiegelt gespeichert
                double start = arc.CounterClockWise ? arc.StartAngle : -arc.StartAngle;
                double sweep = arc.CounterClockWise ? span : -span;
                int parts = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2 + 1e-9)));
                double step = sweep / parts;
                for (int k = 0; k <= parts; k++)
                {
                    double a = start + step * k;
                    ch.Points.Add(new P2(arc.Center.X + arc.Radius * Math.Cos(a), arc.Center.Y + arc.Radius * Math.Sin(a)));
                    if (k < parts) ch.Bulges.Add(Math.Tan(step / 4));
                    // Bogen-Teilungspunkte sind keine Ecken; beim Vollkreis auch Anfang/Ende nicht
                    ch.Corners.Add((k == 0 || k == parts) && !IsFullTurn(span));
                }
                return ch;
            }

            case Hatch.BoundaryPath.Ellipse el:
            {
                double span = NormalizeSpan(el.EndAngle - el.StartAngle);
                double start = el.CounterClockWise ? el.StartAngle : -el.StartAngle;
                double sweep = el.CounterClockWise ? span : -span;
                P2 c = new(el.Center.X, el.Center.Y);
                P2 maj = new(el.MajorAxisEndPoint.X, el.MajorAxisEndPoint.Y);
                P2 min = maj.PerpLeft * el.RadiusRatio;
                int steps = Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 720)));
                for (int k = 0; k <= steps; k++)
                {
                    double a = start + sweep * k / steps;
                    ch.Points.Add(c + maj * Math.Cos(a) + min * Math.Sin(a));
                    if (k < steps) ch.Bulges.Add(0);
                    ch.Corners.Add((k == 0 || k == steps) && !IsFullTurn(span));
                }
                return ch;
            }

            case Hatch.BoundaryPath.Spline sp:
            {
                List<XYZ> pts;
                try { pts = sp.PolygonalVertexes(512); }
                catch
                {
                    pts = sp.FitPoints.Count > 1
                        ? sp.FitPoints.Select(p => new XYZ(p.X, p.Y, 0)).ToList()
                        : sp.ControlPoints.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
                }
                foreach (var p in pts) { ch.Points.Add(new P2(p.X, p.Y)); }
                for (int i = 0; i < ch.Points.Count - 1; i++) ch.Bulges.Add(0);
                for (int i = 0; i < ch.Points.Count; i++) ch.Corners.Add(i == 0 || i == ch.Points.Count - 1);
                return ch;
            }

            case Hatch.BoundaryPath.Polyline pl:
            {
                foreach (var v in pl.Vertices) ch.Points.Add(new P2(v.X, v.Y));
                var bul = pl.Vertices.Select(v => v.Z).ToList();
                if (pl.IsClosed && ch.Points.Count > 0) { ch.Points.Add(ch.Points[0]); }
                for (int i = 0; i < ch.Points.Count - 1; i++) ch.Bulges.Add(i < bul.Count ? bul[i] : 0);
                for (int i = 0; i < ch.Points.Count; i++) ch.Corners.Add(true);
                return ch;
            }
        }
        return null;
    }

    private static double Dist(XYZ a, XYZ b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static bool IsFullTurn(double span) => Math.Abs(span - 2 * Math.PI) < 1e-6;

    private static double NormalizeSpan(double span)
    {
        const double twoPi = 2 * Math.PI;
        if (span <= 1e-12) span += twoPi * Math.Ceiling((-span + 1e-12) / twoPi);
        if (span > twoPi + 1e-9) span = span % twoPi;
        if (span < 1e-12) span = twoPi;
        return span;
    }

    // ------------------------------------------------------------------------------------------ Ergebnis

    private void AddItem(Entity e, AreaKind kind, Layer layer, AcadColor color, Context ctx,
                         List<Loop> loops, HatchStyleType style = HatchStyleType.Normal,
                         double? areaOverride = null, string label = "", double? labelArea = null)
    {
        loops = loops.Select(l => l.Normalize(Tol)).Where(l => l.Count >= 2).ToList();
        if (loops.Count == 0) return;

        var polys = loops.Select(l => l.Tessellate()).ToList();
        double area;
        var holes = new bool[loops.Count];
        var include = new bool[loops.Count];

        if (loops.Count == 1)
        {
            area = loops[0].Area;
            include[0] = true;
        }
        else
        {
            // Verschachtelungstiefe jeder Kontur bestimmen (gerade = Fläche, ungerade = Insel)
            area = 0;
            for (int i = 0; i < loops.Count; i++)
            {
                int depth = 0;
                P2 probe = InteriorProbe(polys[i]);
                for (int j = 0; j < loops.Count; j++)
                {
                    if (i == j) continue;
                    if (Loop.Contains(polys[j], probe) && loops[j].Area > loops[i].Area) depth++;
                }
                bool counts = style switch
                {
                    HatchStyleType.Outer => depth <= 1,
                    HatchStyleType.Normal => true,
                    _ => depth == 0, // Ignore: nur äußerste Umgrenzung
                };
                if (!counts) continue;
                include[i] = true;
                holes[i] = depth % 2 == 1;
                area += holes[i] ? -loops[i].Area : loops[i].Area;
            }
            area = Math.Abs(area);
        }

        if (areaOverride is double ao) area = ao;
        if (area < _opt.MinArea) return;

        var outlines = new List<AreaOutline>();
        var bounds = Bounds2.Empty;
        double perimeter = 0;
        for (int i = 0; i < loops.Count; i++)
        {
            if (!include[i]) continue;
            outlines.Add(new AreaOutline(polys[i], holes[i]) { Corners = loops[i].CornerPoints() });
            foreach (var p in polys[i]) bounds = bounds.Include(p);
            perimeter += loops[i].Perimeter();
        }

        byte[] rgb = SafeRgb(color);
        _items.Add(new AreaItem
        {
            Nr = ++_nr,
            Handle = e.Handle.ToString("X"),
            Kind = kind,
            Layer = layer.Name,
            Block = ctx.BlockPath,
            Area = area,
            Perimeter = perimeter,
            LoopCount = outlines.Count,
            R = rgb[0], G = rgb[1], B = rgb[2],
            Outlines = outlines,
            Bounds = bounds,
            Label = label,
            LabelArea = labelArea,
        });
    }

    /// <summary>Ein Punkt knapp innerhalb der Kontur (neben der Mitte der ersten brauchbaren Kante).</summary>
    private static P2 InteriorProbe(List<P2> poly)
    {
        double signed = 0;
        for (int i = 0; i < poly.Count; i++) signed += poly[i].Cross(poly[(i + 1) % poly.Count]);
        for (int i = 0; i < poly.Count; i++)
        {
            P2 a = poly[i], b = poly[(i + 1) % poly.Count];
            P2 d = b - a;
            double len = d.Length;
            if (len < 1e-12) continue;
            P2 inward = d.PerpLeft * (1.0 / len) * (signed >= 0 ? 1 : -1);
            P2 probe = a + d * 0.5 + inward * (len * 1e-4);
            if (Loop.Contains(poly, probe)) return probe;
        }
        return poly[0];
    }

    private static byte[] SafeRgb(AcadColor c)
    {
        try
        {
            if (c.IsByLayer || c.IsByBlock) return [128, 128, 128];
            return [c.R, c.G, c.B];
        }
        catch
        {
            return [128, 128, 128];
        }
    }
}
