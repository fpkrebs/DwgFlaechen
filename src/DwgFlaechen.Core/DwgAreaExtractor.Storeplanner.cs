using System.Globalization;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Classes;
using ACadSharp.Entities;
using ACadSharp.Entities.ProxyGraphics;
using ACadSharp.Tables;
using CSMath;
using DwgFlaechen.Core.Geometry;
using AcadColor = ACadSharp.Color;

namespace DwgFlaechen.Core;

/// <summary>
/// Storeplanner-Flächen (DFStoreplanner / PundC).
/// <para>
/// Die Fläche ist das C++-Objekt <c>GIN_DbArea</c> (DXF-Name <c>GIN_DBAREA</c>, Anwendung GIN_STOREPLANNER,
/// abgeleitet von <c>OdDbCurve</c>). Ohne das Storeplanner-Modul zeigt AutoCAD es als Proxy-Objekt an.
/// Die eigentlichen Objektdaten sind binär und nicht dokumentiert; ausgewertet wird deshalb die
/// <b>Proxy-Grafik</b>, die beim Speichern mit in die DWG geschrieben wird:
/// </para>
/// <list type="bullet">
///   <item>ein gefülltes, trianguliertes Netz (Shell) = die Fläche einschließlich ausgesparter Inseln;
///         die Summe der Dreiecke ergibt die Fläche (identisch mit dem Wert in der Beschriftung)</item>
///   <item>einzelne Randlinien (Polyline, Kreisbogen) = Außenkontur und Inseln</item>
/// </list>
/// Die Beschriftung (Name, Sortiment, Marke, Nummer, "84.34 qm") ist ein eigenes Objekt
/// <c>GIN_DBENTIYREF</c>, das direkt nach der Fläche gespeichert wird (Handle der Fläche + 1).
/// Gangflächen (Layer SP_AREAS_CORRIDOR) haben keine Füllung; ihre Fläche wird aus den Randlinien berechnet.
/// </summary>
public sealed partial class DwgAreaExtractor
{
    private const string SpAreaDxfName = "GIN_DBAREA";
    private const string SpLabelDxfName = "GIN_DBENTIYREF"; // Schreibweise so im Storeplanner

    /// <summary>Beschriftungen je Handle (aus GIN_DBENTIYREF).</summary>
    private readonly Dictionary<ulong, SpLabel> _spLabels = new();

    /// <summary>Handles aller Storeplanner-Flächen (um die zugehörigen Beschriftungen zu erkennen).</summary>
    private readonly HashSet<ulong> _spAreaHandles = new();

    private sealed record SpLabel(ulong Handle, List<string> Lines, P2? Position);

    private static string? DxfNameOf(Entity e) => e switch
    {
        UnknownEntity u => u.DxfClass?.DxfName,
        ProxyEntity p => p.DxfClass?.DxfName,
        _ => null,
    };

    private static bool IsStoreplannerArea(Entity e) =>
        string.Equals(DxfNameOf(e), SpAreaDxfName, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------ Beschriftungen

    private void CollectStoreplannerLabels(CadDocument doc)
    {
        foreach (var br in doc.BlockRecords)
        foreach (var e in br.Entities)
            if (IsStoreplannerArea(e)) _spAreaHandles.Add(e.Handle);

        foreach (var br in doc.BlockRecords)
        foreach (var e in br.Entities)
        {
            if (!string.Equals(DxfNameOf(e), SpLabelDxfName, StringComparison.OrdinalIgnoreCase)) continue;
            var lines = new List<string>();
            P2? pos = null;
            var stack = new Stack<Affine3>();
            var cur = Affine3.Identity;
            foreach (var g in e.ProxyGeometries)
            {
                switch (g)
                {
                    case ProxyPushModelTransform pm: stack.Push(cur); cur = cur.Then(ToAffine(pm.TransformationMatrix)); break;
                    case ProxyPushModelTransform2 pm2: stack.Push(cur); cur = cur.Then(ToAffine(pm2.TransformationMatrix)); break;
                    case ProxyPopModelTransform: if (stack.Count > 0) cur = stack.Pop(); break;
                    case ProxyUnicodeText2 t2: Add(t2.Text, t2.StartPoint); break;
                    case ProxyUnicodeText t1: Add(t1.Text, t1.StartPoint); break;
                    case ProxyText2 t3: Add(t3.Text, t3.StartPoint); break;
                    case ProxyText t4: Add(t4.Text, t4.StartPoint); break;
                }
            }
            _spLabels[e.Handle] = new SpLabel(e.Handle, lines, pos);

            void Add(string? text, XYZ start)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                lines.Add(text);
                pos ??= cur.ApplyXY(start.X, start.Y, start.Z);
            }
        }
    }

    private static readonly Regex QmRegex = new(@"(-?\d+(?:[.,]\d+)?)\s*(?:qm|m²|m2)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Zerlegt die Beschriftungszeilen in Text und Flächenwert ("84.34 qm").</summary>
    private static (string Text, double? Area) ParseLabel(SpLabel? label)
    {
        if (label == null) return ("", null);
        double? area = null;
        var parts = new List<string>();
        foreach (string raw in label.Lines)
        {
            bool underlined = raw.Contains("%%U", StringComparison.OrdinalIgnoreCase);
            string s = Regex.Replace(raw, "%%[UOuo]", "");
            // Reste von MText-Steuerzeichen abschneiden (z. B. 'Ruhezone\"" \Ruhezone')
            int bs = s.IndexOf('\\');
            if (bs > 0) s = s[..bs];
            s = s.Trim().Trim('"').Trim();
            if (s.Length == 0) continue;

            var m = QmRegex.Match(s);
            if (m.Success && m.Index + m.Length >= s.Length - 1)
            {
                if (double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    area = v;
                s = s[..m.Index].Trim();
                if (s.Length == 0) continue;
            }
            if (underlined) continue; // unterstrichene Zeile wiederholt nur den Namen
            if (!parts.Contains(s)) parts.Add(s);
        }
        return (string.Join(" · ", parts), area);
    }

    private SpLabel? FindLabel(Entity area, IReadOnlyList<List<P2>> outerPolys)
    {
        // Normalfall: Beschriftung wird direkt nach der Fläche angelegt (Handle + 1).
        // Ist sie leer, hat die Fläche keine Beschriftung (z. B. Abzugs- oder Umlageflächen).
        if (_spLabels.TryGetValue(area.Handle + 1, out var next))
        {
            _spLabels.Remove(next.Handle);
            return next.Lines.Count > 0 ? next : null;
        }
        // Sonst: freie Beschriftung (keiner Fläche zugeordnet), deren Textposition in der Fläche liegt
        foreach (var l in _spLabels.Values)
        {
            if (l.Position is not P2 p || l.Lines.Count == 0) continue;
            if (_spAreaHandles.Contains(l.Handle - 1)) continue; // gehört zu einer anderen Fläche
            if (outerPolys.Any(poly => Loop.Contains(poly, p)))
            {
                _spLabels.Remove(l.Handle);
                return l;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------ Fläche

    private void HandleStoreplannerArea(Entity e, Layer layer, AcadColor color, Context ctx)
    {
        if (e.ProxyGeometries.Count == 0)
        {
            Skip("Storeplanner-Fläche ohne Proxy-Grafik (PROXYGRAPHICS = 0 beim Speichern?)");
            return;
        }

        var segments = new List<List<P2>>();  // Randlinien in Welt-XY
        var segCorner = new List<List<bool>>();
        double fillArea = 0;
        var fillEdges = new List<(P2 A, P2 B)>();
        bool hasFill = false;
        bool fillColorSet = false;
        AcadColor fillColor = color;

        var stack = new Stack<Affine3>();
        var cur = ctx.Transform;
        AcadColor? subColor = null;

        foreach (var g in e.ProxyGeometries)
        {
            switch (g)
            {
                case ProxyPushModelTransform pm:
                    stack.Push(cur); cur = cur.Then(ToAffine(pm.TransformationMatrix)); break;
                case ProxyPushModelTransform2 pm2:
                    stack.Push(cur); cur = cur.Then(ToAffine(pm2.TransformationMatrix)); break;
                case ProxyPopModelTransform:
                    if (stack.Count > 0) cur = stack.Pop(); break;

                case ProxySubentTrueColor tc:
                    subColor = tc.Color; break;
                case ProxySubentColor sc when subColor is null || !subColor.Value.IsTrueColor:
                    subColor = new AcadColor((short)sc.ColorIndex); break;

                case ProxyShell shell:
                {
                    hasFill = true;
                    if (!fillColorSet && subColor is AcadColor fc && !fc.IsByLayer && !fc.IsByBlock)
                    {
                        fillColor = fc; fillColorSet = true;
                    }
                    var v = shell.Vertices.Select(p => cur.ApplyXY(p.X, p.Y, p.Z)).ToList();
                    foreach (var face in shell.Faces)
                    {
                        if (face == null || face.Length < 3) continue;
                        var idx = face.Select(Math.Abs).ToArray();
                        if (idx.Any(i => i >= v.Count)) continue;
                        double a = 0;
                        for (int i = 0; i < idx.Length; i++)
                            a += v[idx[i]].Cross(v[idx[(i + 1) % idx.Length]]);
                        fillArea += Math.Abs(a) / 2;
                        for (int i = 0; i < idx.Length; i++)
                            fillEdges.Add((v[idx[i]], v[idx[(i + 1) % idx.Length]]));
                    }
                    break;
                }

                case ProxyPolyline pl when pl.Points.Count >= 2:
                    AddSeg(pl.Points.Select(p => cur.ApplyXY(p.X, p.Y, p.Z)).ToList(), null); break;
                case ProxyPolygon pg when pg.Points != null && pg.Points.Count >= 3:
                {
                    var pts = pg.Points.Select(p => cur.ApplyXY(p.X, p.Y, p.Z)).ToList();
                    pts.Add(pts[0]);
                    AddSeg(pts, null);
                    break;
                }
                case ProxyLwPolyine lwp when lwp.Entity != null && lwp.Entity.Vertices.Count >= 2:
                {
                    var loop = new Loop();
                    foreach (var vx in lwp.Entity.Vertices) loop.Add(new P2(vx.Location.X, vx.Location.Y), vx.Bulge);
                    if (!lwp.Entity.IsClosed) loop.Bulges[^1] = 0; // kein Schlussbogen bei offener Polylinie
                    var flags = new List<bool>();
                    var t = cur.Then(Affine3.FromOcs(lwp.Entity.Normal.X, lwp.Entity.Normal.Y, lwp.Entity.Normal.Z));
                    var pts = loop.Transform(t, lwp.Entity.Elevation).Tessellate(Math.PI / 180, flags);
                    if (lwp.Entity.IsClosed) { pts.Add(pts[0]); flags.Add(flags[0]); }
                    AddSeg(pts, flags);
                    break;
                }
                case ProxyCircularArc arc when arc.Radius > 0:
                    AddSeg(ArcPoints(arc.Center, arc.Normal, arc.StartVectorDirection, arc.Radius, arc.SweepAngle, cur, out var arcFlags), arcFlags);
                    break;
                case ProxyCircle circle when circle.Radius > 0:
                {
                    var n = circle.Normal;
                    var ocs = Affine3.FromOcs(n.X, n.Y, n.Z);
                    var start = new XYZ(ocs.M00, ocs.M10, ocs.M20); // X-Achse des OCS
                    var pts = ArcPoints(circle.Center, n, start, circle.Radius, 2 * Math.PI, cur, out var fl);
                    for (int i = 0; i < fl.Count; i++) fl[i] = false;
                    AddSeg(pts, fl);
                    break;
                }
            }
        }

        // Randlinien zu geschlossenen Konturen verketten
        var bounds = Bounds2.Empty;
        foreach (var s in segments) foreach (var p in s) bounds = bounds.Include(p);
        foreach (var (a, b) in fillEdges) { bounds = bounds.Include(a); bounds = bounds.Include(b); }
        double diag = Math.Max(1e-9, Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height));
        double tol = diag * 1e-7;

        var loops = ChainSegments(segments, segCorner, tol, out int open);

        // Keine verwertbaren Randlinien: Kontur aus dem Rand des Füllnetzes bilden
        bool loopsFromFill = false;
        if (loops.Count == 0 && fillEdges.Count > 0)
        {
            loops = BoundaryOfMesh(fillEdges, tol);
            loopsFromFill = true;
        }

        if (loops.Count == 0)
        {
            Skip("Storeplanner-Fläche ohne geschlossene Kontur");
            return;
        }

        // Fläche: aus den Konturen (Bögen fein unterteilt, genauer als das Füllnetz).
        // Das Füllnetz dient als Gegenprobe und als Rückfall, wenn Randlinien fehlen.
        var polys = loops.Select(l => l.Tessellate()).ToList();
        double loopArea = NestedArea(loops, polys);
        double? areaOverride = null;
        if (hasFill && fillArea > 0)
        {
            bool mismatch = Math.Abs(loopArea - fillArea) > 0.005 * fillArea;
            if (open > 0 || loopsFromFill || mismatch)
            {
                areaOverride = fillArea;
                if (mismatch && !loopsFromFill)
                    _warnings.Add($"Storeplanner-Fläche [{e.Handle:X}]: Kontur ({loopArea:0.###}) und Füllung ({fillArea:0.###}) " +
                                  "weichen ab – Fläche der Füllung verwendet.");
            }
        }
        else if (open > 0)
        {
            _warnings.Add($"Storeplanner-Fläche [{e.Handle:X}]: {open} Randlinie(n) ließen sich nicht schließen.");
        }

        // Beschriftung zuordnen
        var (labelText, labelArea) = ParseLabel(FindLabel(e, polys));

        AddItem(e, AreaKind.Storeplanner, layer, hasFill ? fillColor : color, ctx, loops,
            areaOverride: areaOverride, label: labelText, labelArea: labelArea);

        void AddSeg(List<P2> pts, List<bool>? corners)
        {
            if (pts.Count < 2) return;
            segments.Add(pts);
            segCorner.Add(corners ?? Enumerable.Repeat(true, pts.Count).ToList());
        }
    }

    /// <summary>Punkte eines Kreisbogens (Proxy-Grafik) in Welt-XY.</summary>
    private static List<P2> ArcPoints(XYZ center, XYZ normal, XYZ startDir, double radius, double sweep,
                                      Affine3 t, out List<bool> corners)
    {
        // lokales System: u = Startrichtung, v = Normal × u
        double nl = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
        XYZ n = nl < 1e-12 ? XYZ.AxisZ : new XYZ(normal.X / nl, normal.Y / nl, normal.Z / nl);
        double ul = Math.Sqrt(startDir.X * startDir.X + startDir.Y * startDir.Y + startDir.Z * startDir.Z);
        XYZ u = ul < 1e-12 ? XYZ.AxisX : new XYZ(startDir.X / ul, startDir.Y / ul, startDir.Z / ul);
        XYZ v = XYZ.Cross(n, u);
        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 720))); // 0,25°
        var pts = new List<P2>(steps + 1);
        corners = new List<bool>(steps + 1);
        for (int k = 0; k <= steps; k++)
        {
            double a = sweep * k / steps;
            double c = Math.Cos(a) * radius, s = Math.Sin(a) * radius;
            pts.Add(t.ApplyXY(center.X + c * u.X + s * v.X, center.Y + c * u.Y + s * v.Y, center.Z + c * u.Z + s * v.Z));
            corners.Add(k == 0 || k == steps);
        }
        return pts;
    }

    /// <summary>Fläche mehrerer Konturen: gerade Verschachtelungstiefe zählt, ungerade (Insel) wird abgezogen.</summary>
    private static double NestedArea(List<Loop> loops, List<List<P2>> polys)
    {
        double a = 0;
        for (int i = 0; i < loops.Count; i++)
        {
            P2 probe = InteriorProbe(polys[i]);
            int depth = 0;
            for (int j = 0; j < loops.Count; j++)
                if (i != j && loops[j].Area > loops[i].Area && Loop.Contains(polys[j], probe)) depth++;
            a += depth % 2 == 1 ? -loops[i].Area : loops[i].Area;
        }
        return Math.Abs(a);
    }

    /// <summary>
    /// Verkettet offene Linienzüge (je Randlinie einer) über gemeinsame Endpunkte zu geschlossenen Konturen.
    /// </summary>
    private static List<Loop> ChainSegments(List<List<P2>> segs, List<List<bool>> corners, double tol, out int openCount)
    {
        var result = new List<Loop>();
        openCount = 0;
        int n = segs.Count;
        var used = new bool[n];
        var grid = new Dictionary<(long, long), List<(int Seg, bool AtStart)>>();
        double cell = tol * 4;

        (long, long) Key(P2 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
        void Put(P2 p, int s, bool atStart)
        {
            var k = Key(p);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new();
            list.Add((s, atStart));
        }
        for (int i = 0; i < n; i++)
        {
            // geschlossene Linienzüge direkt übernehmen
            if (segs[i].Count >= 4 && P2.Distance(segs[i][0], segs[i][^1]) <= tol)
            {
                used[i] = true;
                result.Add(MakeLoop(segs[i], corners[i], tol));
                continue;
            }
            Put(segs[i][0], i, true);
            Put(segs[i][^1], i, false);
        }

        (int Seg, bool AtStart)? FindNext(P2 p)
        {
            var (kx, ky) = Key(p);
            (int, bool)? best = null;
            double bestD = double.MaxValue;
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy), out var list)) continue;
                foreach (var (s, atStart) in list)
                {
                    if (used[s]) continue;
                    double d = P2.Distance(p, atStart ? segs[s][0] : segs[s][^1]);
                    if (d <= tol && d < bestD) { bestD = d; best = (s, atStart); }
                }
            }
            return best;
        }

        for (int i = 0; i < n; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var pts = new List<P2>(segs[i]);
            var cor = new List<bool>(corners[i]);
            bool closed = false;
            while (true)
            {
                if (pts.Count >= 4 && P2.Distance(pts[0], pts[^1]) <= tol) { closed = true; break; }
                var next = FindNext(pts[^1]);
                if (next is not (int s, bool atStart)) break;
                used[s] = true;
                var sp = segs[s]; var sc = corners[s];
                if (atStart)
                    for (int k = 1; k < sp.Count; k++) { pts.Add(sp[k]); cor.Add(sc[k]); }
                else
                    for (int k = sp.Count - 2; k >= 0; k--) { pts.Add(sp[k]); cor.Add(sc[k]); }
            }
            if (closed) result.Add(MakeLoop(pts, cor, tol));
            else openCount++;
        }
        return result.Where(l => l.Count >= 3).ToList();
    }

    private static Loop MakeLoop(List<P2> pts, List<bool> corners, double tol)
    {
        var loop = new Loop();
        for (int i = 0; i < pts.Count; i++) loop.Add(pts[i], 0, i < corners.Count && corners[i]);
        loop = loop.Normalize(tol);
        // Zwischenpunkte auf gerader Linie sind keine Ecken
        for (int i = 0; i < loop.Count; i++)
        {
            if (!loop.Corners[i]) continue;
            P2 a = loop.Points[(i - 1 + loop.Count) % loop.Count], b = loop.Points[i], c = loop.Points[(i + 1) % loop.Count];
            P2 d1 = b - a, d2 = c - b;
            double l1 = d1.Length, l2 = d2.Length;
            if (l1 > 0 && l2 > 0 && Math.Abs(d1.Cross(d2)) <= 1e-9 * l1 * l2 && d1.Dot(d2) > 0)
                loop.Corners[i] = false;
        }
        return loop;
    }

    /// <summary>Außen- und Inselkonturen eines Dreiecksnetzes (Kanten, die nur einmal vorkommen).</summary>
    private static List<Loop> BoundaryOfMesh(List<(P2 A, P2 B)> edges, double tol)
    {
        double cell = tol * 4;
        (long, long) K(P2 p) => ((long)Math.Round(p.X / cell), (long)Math.Round(p.Y / cell));
        var count = new Dictionary<((long, long), (long, long)), int>();
        foreach (var (a, b) in edges)
        {
            var ka = K(a); var kb = K(b);
            if (ka == kb) continue;
            var key = ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka);
            count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
        }
        var segs = new List<List<P2>>(); var cors = new List<List<bool>>();
        foreach (var (a, b) in edges)
        {
            var ka = K(a); var kb = K(b);
            if (ka == kb) continue;
            var key = ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka);
            if (count[key] != 1) continue;
            segs.Add([a, b]); cors.Add([true, true]);
        }
        return ChainSegments(segs, cors, tol, out _);
    }

    /// <summary>
    /// $INSUNITS ist in Storeplanner-Zeichnungen oft falsch gesetzt (z. B. Zoll, obwohl in Metern gezeichnet).
    /// Die Beschriftungen nennen die Fläche in m² ("qm"); stimmt das Verhältnis zu einer Einheit, wird diese verwendet.
    /// </summary>
    private DrawingUnit ResolveUnit(DrawingUnit fromHeader)
    {
        var ratios = _items.Where(i => i.LabelArea is > 0 && i.Area > 0)
                           .Select(i => i.Area / i.LabelArea!.Value).OrderBy(r => r).ToList();
        if (ratios.Count == 0) return fromHeader;
        double median = ratios[ratios.Count / 2];
        foreach (var u in new[] { DrawingUnit.Meter, DrawingUnit.Millimeter, DrawingUnit.Zentimeter, DrawingUnit.Dezimeter })
        {
            double f = DrawingUnits.ToMeters(u)!.Value;
            double expected = 1 / (f * f); // Zeichnungseinheiten² je m²
            if (Math.Abs(median / expected - 1) < 0.01)
            {
                if (u != fromHeader)
                    _warnings.Add($"Einheit aus den Storeplanner-Beschriftungen abgeleitet: {u} " +
                                  $"($INSUNITS der Zeichnung: {fromHeader}).");
                return u;
            }
        }
        return fromHeader;
    }

    /// <summary>4×4-Matrix der Proxy-Grafik (Zeile/Spalte, Translation in Spalte 3) → Affine3.</summary>
    private static Affine3 ToAffine(Matrix4 m) => new(
        m.M00, m.M01, m.M02, m.M03,
        m.M10, m.M11, m.M12, m.M13,
        m.M20, m.M21, m.M22, m.M23);
}
