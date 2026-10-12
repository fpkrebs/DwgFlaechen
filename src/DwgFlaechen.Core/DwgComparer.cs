using System.Globalization;
using System.Text;
using DwgFlaechen.Core.Geometry;

namespace DwgFlaechen.Core;

/// <summary>Ergebnis des Abgleichs einer Fläche zwischen zwei Zeichnungen.</summary>
public enum CompareStatus
{
    /// <summary>Nur in B vorhanden.</summary>
    Neu,
    /// <summary>Nur in A vorhanden.</summary>
    Entfallen,
    /// <summary>In beiden vorhanden, aber Fläche, Kontur, Layer oder Beschriftung unterschiedlich.</summary>
    Geändert,
    /// <summary>In beiden gleich.</summary>
    Unverändert,
}

/// <summary>Eine Zeile des Vergleichs: Fläche aus A und/oder B.</summary>
public sealed class CompareItem
{
    public int Nr { get; init; }
    public CompareStatus Status { get; init; }
    public AreaItem? A { get; init; }
    public AreaItem? B { get; init; }

    /// <summary>Was sich geändert hat, z. B. "Kontur (max. 0,0004), Fläche, Beschriftung".</summary>
    public string Details { get; init; } = "";

    /// <summary>
    /// Größte Konturabweichung zwischen A und B (Zeichnungseinheiten), nur bei geänderter Kontur.
    /// Gemessen von jedem Eckpunkt zur nächstgelegenen Kante der anderen Zeichnung, in beide Richtungen.
    /// </summary>
    public double? Deviation { get; init; }

    /// <summary>Eckpunkt mit der größten Abweichung.</summary>
    public P2? DeviationFrom { get; init; }

    /// <summary>Nächstgelegener Punkt auf der Kontur der anderen Zeichnung.</summary>
    public P2? DeviationTo { get; init; }

    /// <summary>true: der Eckpunkt <see cref="DeviationFrom"/> stammt aus A, sonst aus B.</summary>
    public bool DeviationFromA { get; init; }

    /// <summary>Ausschnitt um die größte Abweichung (die Abweichung nimmt etwa 1/12 der Breite ein), sonst null.</summary>
    public Bounds2? DeviationView
    {
        get
        {
            if (Deviation is not double d || DeviationFrom is not P2 f || DeviationTo is not P2 t) return null;
            double half = Math.Max(d * 6, 1e-6);
            P2 m = new((f.X + t.X) / 2, (f.Y + t.Y) / 2);
            return new Bounds2(m.X - half, m.Y - half, m.X + half, m.Y + half);
        }
    }

    /// <summary>Maßgebliches Objekt (B, sonst A).</summary>
    public AreaItem Current => B ?? A!;
    public string Layer => Current.Layer;
    public AreaKind Kind => Current.Kind;
    public string Label => Current.Label;

    public double AreaA => A?.Area ?? 0;
    public double AreaB => B?.Area ?? 0;
    /// <summary>Flächenänderung B − A in Zeichnungseinheiten².</summary>
    public double DeltaArea => AreaB - AreaA;

    public Bounds2 Bounds => (A?.Bounds ?? Bounds2.Empty).Union(B?.Bounds ?? Bounds2.Empty);
}

/// <summary>Ergebnis eines Vergleichs zweier Zeichnungen.</summary>
public sealed class CompareResult
{
    public required ExtractionResult A { get; init; }
    public required ExtractionResult B { get; init; }
    public required IReadOnlyList<CompareItem> Items { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Längentoleranz (Zeichnungseinheiten), mit der Konturen als gleich gelten.</summary>
    public double Tolerance { get; init; }

    public int Count(CompareStatus s) => Items.Count(i => i.Status == s);
    public double TotalA => A.TotalArea;
    public double TotalB => B.TotalArea;
    public double TotalDelta => TotalB - TotalA;
    public Bounds2 Bounds => A.Bounds.Union(B.Bounds);
}

/// <summary>
/// Vergleicht die Flächen zweier Zeichnungen im selben Koordinatensystem (z. B. zwei Planstände eines Geschosses).
/// <para>
/// Zuordnung: Zuerst über gleiches Handle (Planstände derselben DWG behalten ihre Handles), sofern die Geometrie
/// ähnlich ist; danach geometrisch über Überdeckung der Umrisse und Flächenverhältnis (beste Paare zuerst).
/// Gleich ist ein Paar, wenn alle Eckpunkte, Fläche, Layer und Beschriftung innerhalb der Toleranz übereinstimmen.
/// </para>
/// </summary>
public static class DwgComparer
{
    /// <summary>Mindest-Ähnlichkeit (0..1) für eine geometrische Zuordnung.</summary>
    private const double MinScore = 0.5;
    /// <summary>Bei gleichem Handle genügt eine geringere Ähnlichkeit.</summary>
    private const double MinScoreSameHandle = 0.2;

    public static CompareResult Compare(ExtractionResult a, ExtractionResult b, double? tolerance = null)
    {
        var warnings = new List<string>();
        if (a.Unit != b.Unit && a.Unit != DrawingUnit.Unbekannt && b.Unit != DrawingUnit.Unbekannt)
            warnings.Add($"Die Zeichnungen haben unterschiedliche Einheiten (A: {a.Unit}, B: {b.Unit}). Verglichen wird in Zeichnungseinheiten.");

        var all = a.Bounds.Union(b.Bounds);
        double diag = all.IsEmpty ? 1 : Math.Sqrt(all.Width * all.Width + all.Height * all.Height);
        double tol = tolerance ?? Math.Max(diag * 1e-6, 1e-9);

        if (!a.Bounds.IsEmpty && !b.Bounds.IsEmpty && Overlap(a.Bounds, b.Bounds) < 0.05)
            warnings.Add("Die Zeichnungen überdecken sich kaum – liegen sie wirklich im selben Koordinatensystem?");

        var matchOfA = new AreaItem?[a.Items.Count];
        var usedB = new bool[b.Items.Count];

        // 1. Gleiches Handle (und gleiche Objektart) mit ähnlicher Geometrie
        var bByHandle = new Dictionary<(string, AreaKind), List<int>>();
        for (int j = 0; j < b.Items.Count; j++)
        {
            var key = (b.Items[j].Handle, b.Items[j].Kind);
            if (!bByHandle.TryGetValue(key, out var list)) bByHandle[key] = list = new();
            list.Add(j);
        }
        for (int i = 0; i < a.Items.Count; i++)
        {
            var ai = a.Items[i];
            if (!bByHandle.TryGetValue((ai.Handle, ai.Kind), out var cands)) continue;
            int best = -1; double bestScore = MinScoreSameHandle;
            foreach (int j in cands)
            {
                if (usedB[j] || !string.Equals(ai.Block, b.Items[j].Block, StringComparison.Ordinal)) continue;
                double s = Score(ai, b.Items[j]);
                if (s >= bestScore) { bestScore = s; best = j; }
            }
            if (best >= 0) { matchOfA[i] = b.Items[best]; usedB[best] = true; }
        }

        // 2. Geometrisch: alle Kandidatenpaare mit überlappenden Umrissen, beste zuerst
        var pairs = new List<(double Score, int I, int J)>();
        var grid = new SpatialGrid(b.Items, all);
        for (int i = 0; i < a.Items.Count; i++)
        {
            if (matchOfA[i] != null) continue;
            var ai = a.Items[i];
            foreach (int j in grid.Query(ai.Bounds))
            {
                if (usedB[j]) continue;
                double s = Score(ai, b.Items[j]);
                if (s >= MinScore) pairs.Add((s, i, j));
            }
        }
        foreach (var (_, i, j) in pairs.OrderByDescending(p => p.Score))
        {
            if (matchOfA[i] != null || usedB[j]) continue;
            matchOfA[i] = b.Items[j];
            usedB[j] = true;
        }

        // 3. Ergebnis
        var items = new List<CompareItem>();
        for (int i = 0; i < a.Items.Count; i++)
        {
            var ai = a.Items[i];
            if (matchOfA[i] is AreaItem bi)
            {
                string details = Differences(ai, bi, tol, out var dev);
                items.Add(new CompareItem
                {
                    A = ai, B = bi, Details = details,
                    Status = details.Length == 0 ? CompareStatus.Unverändert : CompareStatus.Geändert,
                    Deviation = dev?.Distance, DeviationFrom = dev?.From, DeviationTo = dev?.To, DeviationFromA = dev?.FromA ?? false,
                });
            }
            else
            {
                items.Add(new CompareItem { A = ai, Status = CompareStatus.Entfallen });
            }
        }
        for (int j = 0; j < b.Items.Count; j++)
            if (!usedB[j]) items.Add(new CompareItem { B = b.Items[j], Status = CompareStatus.Neu });

        // sortiert: Änderungen zuerst, dann nach Layer und Lage
        var sorted = items
            .OrderBy(c => c.Status switch
            {
                CompareStatus.Geändert => 0, CompareStatus.Neu => 1, CompareStatus.Entfallen => 2, _ => 3,
            })
            .ThenBy(c => c.Layer, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Bounds.MinY).ThenBy(c => c.Bounds.MinX)
            .Select((c, k) => new CompareItem
            {
                Nr = k + 1, A = c.A, B = c.B, Status = c.Status, Details = c.Details,
                Deviation = c.Deviation, DeviationFrom = c.DeviationFrom, DeviationTo = c.DeviationTo, DeviationFromA = c.DeviationFromA,
            })
            .ToList();

        return new CompareResult { A = a, B = b, Items = sorted, Warnings = warnings, Tolerance = tol };
    }

    public static Task<CompareResult> CompareAsync(ExtractionResult a, ExtractionResult b, CancellationToken ct = default) =>
        Task.Run(() => Compare(a, b), ct);

    // ------------------------------------------------------------------------------------------ Ähnlichkeit

    /// <summary>Ähnlichkeit 0..1: Überdeckung der Umrissrechtecke × Flächenverhältnis.</summary>
    private static double Score(AreaItem a, AreaItem b)
    {
        double iou = Overlap(a.Bounds, b.Bounds);
        if (iou <= 0) return 0;
        double ratio = Math.Min(a.Area, b.Area) / Math.Max(Math.Max(a.Area, b.Area), 1e-300);
        double s = iou * ratio;
        // gleiche Objektart und gleicher Layer bevorzugen
        if (a.Kind != b.Kind) s *= 0.8;
        if (!string.Equals(a.Layer, b.Layer, StringComparison.OrdinalIgnoreCase)) s *= 0.9;
        return s;
    }

    /// <summary>Schnittfläche / Vereinigungsfläche zweier Rechtecke (Intersection over Union).</summary>
    private static double Overlap(Bounds2 a, Bounds2 b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
        double w = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        double h = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        if (w < 0 || h < 0) return 0;
        // entartete Rechtecke (Linienbreite 0) über Längen vergleichen
        double aw = Math.Max(a.Width, 1e-12), ah = Math.Max(a.Height, 1e-12);
        double bw = Math.Max(b.Width, 1e-12), bh = Math.Max(b.Height, 1e-12);
        w = Math.Max(w, 1e-12); h = Math.Max(h, 1e-12);
        double inter = w * h;
        return inter / (aw * ah + bw * bh - inter);
    }

    /// <summary>Liste der Unterschiede eines zugeordneten Paares (leer = gleich).</summary>
    private static string Differences(AreaItem a, AreaItem b, double tol, out Deviation? dev)
    {
        var d = new List<string>();
        dev = null;
        bool contourDiff = !SameContour(a, b, tol);
        if (contourDiff)
        {
            dev = MaxDeviation(a, b);
            d.Add(dev is { } x && x.Distance > 0
                ? $"Kontur (max. {NumberFormats.ExactText(x.Distance, De)})"
                : "Kontur");
        }
        double areaTol = Math.Max(tol * Math.Max(a.Perimeter, b.Perimeter), 1e-9 * Math.Max(a.Area, b.Area));
        if (Math.Abs(a.Area - b.Area) > areaTol) d.Add("Fläche");
        if (!string.Equals(a.Layer, b.Layer, StringComparison.OrdinalIgnoreCase)) d.Add("Layer");
        if (a.Kind != b.Kind) d.Add("Objektart");
        if (!string.Equals(a.Label, b.Label, StringComparison.Ordinal) ||
            !Nullable.Equals(a.LabelArea, b.LabelArea)) d.Add("Beschriftung");
        if (!string.Equals(a.Block, b.Block, StringComparison.Ordinal)) d.Add("Block");
        return string.Join(", ", d);
    }

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Größte Abweichung eines Paares: Eckpunkt, nächster Punkt der anderen Kontur, Abstand.</summary>
    public readonly record struct Deviation(double Distance, P2 From, P2 To, bool FromA);

    /// <summary>
    /// Größte Abweichung zwischen den Konturen (diskrete Hausdorff-Distanz über die Eckpunkte):
    /// je Eckpunkt der Abstand zur nächsten Kante der anderen Zeichnung, das Maximum in beide Richtungen.
    /// </summary>
    public static Deviation? MaxDeviation(AreaItem a, AreaItem b)
    {
        var ab = OneWay(a, b);
        var ba = OneWay(b, a);
        if (ab is null) return ba is { } y ? y with { FromA = false } : null;
        if (ba is null || ab.Value.Distance >= ba.Value.Distance) return ab.Value with { FromA = true };
        return ba.Value with { FromA = false };
    }

    private static Deviation? OneWay(AreaItem from, AreaItem to)
    {
        // Kanten der Zielkontur einmal sammeln (geschlossene Konturen)
        var segs = new List<(P2 P, P2 Q)>();
        foreach (var o in to.Outlines)
        {
            int n = o.Points.Count;
            if (n == 0) continue;
            if (n == 1) { segs.Add((o.Points[0], o.Points[0])); continue; }
            for (int i = 0; i < n; i++) segs.Add((o.Points[i], o.Points[(i + 1) % n]));
        }
        if (segs.Count == 0) return null;

        var grid = new SegmentGrid(segs);
        Deviation? worst = null;
        foreach (var o in from.Outlines)
        foreach (var p in o.Points)
        {
            var (dist, q) = grid.Nearest(p);
            if (worst is null || dist > worst.Value.Distance) worst = new Deviation(dist, p, q, true);
        }
        return worst;
    }

    /// <summary>Raster über Kanten für die Suche „nächste Kante zu einem Punkt“ (statt alle Kanten zu prüfen).</summary>
    private sealed class SegmentGrid
    {
        private readonly List<(P2 P, P2 Q)> _segs;
        private readonly Dictionary<(long, long), List<int>> _cells = new();
        private readonly double _cell;
        private readonly long _minX, _minY, _maxX, _maxY;

        public SegmentGrid(List<(P2 P, P2 Q)> segs)
        {
            _segs = segs;
            var b = Bounds2.Empty;
            foreach (var (p, q) in segs) b = b.Include(p).Include(q);
            double size = Math.Max(Math.Max(b.Width, b.Height), 1e-12);
            _cell = size / Math.Clamp(Math.Sqrt(segs.Count), 1, 256);
            _minX = _minY = long.MaxValue; _maxX = _maxY = long.MinValue;
            for (int i = 0; i < segs.Count; i++)
            {
                var (p, q) = segs[i];
                long x0 = Key(Math.Min(p.X, q.X)), x1 = Key(Math.Max(p.X, q.X));
                long y0 = Key(Math.Min(p.Y, q.Y)), y1 = Key(Math.Max(p.Y, q.Y));
                // sehr lange schräge Kanten belegen viele Zellen – begrenzt durch die Rastergröße (≤ 256²)
                for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                {
                    if (!_cells.TryGetValue((x, y), out var l)) _cells[(x, y)] = l = new();
                    l.Add(i);
                }
                _minX = Math.Min(_minX, x0); _maxX = Math.Max(_maxX, x1);
                _minY = Math.Min(_minY, y0); _maxY = Math.Max(_maxY, y1);
            }
        }

        private long Key(double v) => (long)Math.Floor(v / _cell);

        public (double Dist, P2 At) Nearest(P2 p)
        {
            long cx = Key(p.X), cy = Key(p.Y);
            double best = double.MaxValue;
            P2 at = p;
            var seen = new HashSet<int>();
            // Ringe um die Zelle des Punkts, bis kein näherer Treffer mehr möglich ist
            long maxR = Math.Max(Math.Max(Math.Abs(cx - _minX), Math.Abs(_maxX - cx)), Math.Max(Math.Abs(cy - _minY), Math.Abs(_maxY - cy)));
            for (long r = 0; r <= maxR; r++)
            {
                if (best < double.MaxValue)
                {
                    double ring = (r - 1) * _cell; // Mindestabstand aller Zellen im Ring r
                    if (ring > 0 && ring * ring >= best) break;
                }
                for (long x = cx - r; x <= cx + r; x++)
                for (long y = cy - r; y <= cy + r; y++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != r) continue; // nur der Rand des Rings
                    if (x < _minX || x > _maxX || y < _minY || y > _maxY) continue;
                    if (!_cells.TryGetValue((x, y), out var l)) continue;
                    foreach (int i in l)
                    {
                        if (!seen.Add(i)) continue;
                        var q = ClosestOnSegment(p, _segs[i].P, _segs[i].Q);
                        double d2 = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                        if (d2 < best) { best = d2; at = q; }
                    }
                }
            }
            return (Math.Sqrt(best), at);
        }
    }

    private static P2 ClosestOnSegment(P2 p, P2 s, P2 e)
    {
        var d = e - s;
        double len2 = d.Dot(d);
        if (len2 <= 0) return s;
        double t = Math.Clamp((p - s).Dot(d) / len2, 0, 1);
        return s + d * t;
    }

    /// <summary>Gleiche Kontur: gleiche Anzahl Konturen und jeder Konturpunkt hat ein Gegenstück in der anderen.</summary>
    private static bool SameContour(AreaItem a, AreaItem b, double tol)
    {
        if (a.Outlines.Count != b.Outlines.Count) return false;
        if (Math.Abs(a.Bounds.MinX - b.Bounds.MinX) > tol || Math.Abs(a.Bounds.MinY - b.Bounds.MinY) > tol ||
            Math.Abs(a.Bounds.MaxX - b.Bounds.MaxX) > tol || Math.Abs(a.Bounds.MaxY - b.Bounds.MaxY) > tol)
            return false;
        return AllPointsCovered(a, b, tol) && AllPointsCovered(b, a, tol);
    }

    private static bool AllPointsCovered(AreaItem from, AreaItem to, double tol)
    {
        double cell = tol * 2;
        var set = new HashSet<(long, long)>();
        foreach (var o in to.Outlines)
        foreach (var p in o.Points)
            set.Add(((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell)));
        foreach (var o in from.Outlines)
        foreach (var p in o.Points)
        {
            long kx = (long)Math.Floor(p.X / cell), ky = (long)Math.Floor(p.Y / cell);
            bool found = false;
            for (long dx = -1; dx <= 1 && !found; dx++)
            for (long dy = -1; dy <= 1 && !found; dy++)
                found = set.Contains((kx + dx, ky + dy));
            if (!found) return false;
        }
        return true;
    }

    /// <summary>Einfaches Raster zur schnellen Suche überlappender Umrissrechtecke.</summary>
    private sealed class SpatialGrid
    {
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        private readonly Bounds2 _all;
        private readonly double _cw, _ch;
        private const int N = 64;

        public SpatialGrid(IReadOnlyList<AreaItem> items, Bounds2 all)
        {
            _all = all.IsEmpty ? new Bounds2(0, 0, 1, 1) : all;
            _cw = Math.Max(_all.Width / N, 1e-12);
            _ch = Math.Max(_all.Height / N, 1e-12);
            for (int i = 0; i < items.Count; i++)
                foreach (var c in Cells(items[i].Bounds))
                {
                    if (!_cells.TryGetValue(c, out var l)) _cells[c] = l = new();
                    l.Add(i);
                }
        }

        private IEnumerable<(int, int)> Cells(Bounds2 b)
        {
            if (b.IsEmpty) yield break;
            int x0 = Clamp((b.MinX - _all.MinX) / _cw), x1 = Clamp((b.MaxX - _all.MinX) / _cw);
            int y0 = Clamp((b.MinY - _all.MinY) / _ch), y1 = Clamp((b.MaxY - _all.MinY) / _ch);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                yield return (x, y);
        }

        private static int Clamp(double v) => (int)Math.Clamp(Math.Floor(v), 0, N - 1);

        public IEnumerable<int> Query(Bounds2 b)
        {
            var seen = new HashSet<int>();
            foreach (var c in Cells(b))
                if (_cells.TryGetValue(c, out var l))
                    foreach (int i in l)
                        if (seen.Add(i)) yield return i;
        }
    }

    // ------------------------------------------------------------------------------------------ Export

    /// <summary>Vergleich als CSV (Semikolon, deutsches Zahlenformat).</summary>
    public static string ToCsv(CompareResult r, IEnumerable<CompareItem> items, DrawingUnit unit, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.GetCultureInfo("de-DE");
        string u = DrawingUnits.Symbol(unit);
        bool hasM2 = DrawingUnits.ToMeters(unit) != null;
        var sb = new StringBuilder();
        sb.AppendLine($"A;{Esc(r.A.FilePath)}");
        sb.AppendLine($"B;{Esc(r.B.FilePath)}");
        sb.Append("Nr;Status;Änderung;Layer;Typ;Handle A;Handle B;")
          .Append($"Fläche A [{u}²];Fläche B [{u}²];Δ [{u}²]");
        if (hasM2) sb.Append(";Fläche A [m²];Fläche B [m²];Δ [m²]");
        sb.AppendLine(";Beschriftung A;Beschriftung B");

        var list = items.ToList();
        foreach (var c in list)
        {
            sb.Append(c.Nr).Append(';').Append(c.Status).Append(';').Append(Esc(c.Details)).Append(';')
              .Append(Esc(c.Layer)).Append(';').Append(c.Kind).Append(';')
              .Append(c.A?.Handle).Append(';').Append(c.B?.Handle).Append(';')
              .Append(c.A?.Area.ToString("0.######", culture)).Append(';')
              .Append(c.B?.Area.ToString("0.######", culture)).Append(';')
              .Append(c.DeltaArea.ToString("0.######", culture));
            if (hasM2)
                sb.Append(';').Append(M2(c.A?.Area, unit, culture))
                  .Append(';').Append(M2(c.B?.Area, unit, culture))
                  .Append(';').Append(M2(c.DeltaArea, unit, culture));
            sb.Append(';').Append(Esc(c.A?.Label ?? "")).Append(';').Append(Esc(c.B?.Label ?? ""));
            sb.AppendLine();
        }
        double sa = list.Sum(c => c.AreaA), sbb = list.Sum(c => c.AreaB);
        sb.Append(";;;;;;Summe;").Append(sa.ToString("0.######", culture)).Append(';')
          .Append(sbb.ToString("0.######", culture)).Append(';').Append((sbb - sa).ToString("0.######", culture));
        if (hasM2)
            sb.Append(';').Append(M2(sa, unit, culture)).Append(';').Append(M2(sbb, unit, culture))
              .Append(';').Append(M2(sbb - sa, unit, culture));
        sb.AppendLine();
        return sb.ToString();
    }

    public static async Task WriteCsvAsync(string path, CompareResult r, IEnumerable<CompareItem> items, DrawingUnit unit) =>
        await File.WriteAllTextAsync(path, ToCsv(r, items, unit), new UTF8Encoding(true));

    private static string M2(double? area, DrawingUnit unit, CultureInfo culture) =>
        area is double a && DrawingUnits.AreaToSquareMeters(a, unit) is double m ? m.ToString("0.######", culture) : "";

    private static string Esc(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
