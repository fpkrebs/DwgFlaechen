using System.Globalization;
using System.Text;
using DwgFlaechen.Core.Geometry;

namespace DwgFlaechen.Core;

/// <summary>Ein Eckpunkt aus A und der nächstgelegene Eckpunkt aus B, die fast, aber nicht genau übereinstimmen.</summary>
public sealed class NearPoint
{
    public int Nr { get; init; }
    public P2 A { get; init; }
    public P2 B { get; init; }
    public required AreaItem ItemA { get; init; }
    public required AreaItem ItemB { get; init; }

    public double DX => B.X - A.X;
    public double DY => B.Y - A.Y;
    public double Distance => Math.Sqrt(DX * DX + DY * DY);

    /// <summary>Ausschnitt um das Punktpaar (Abstand ≈ 1/12 der Breite).</summary>
    public Bounds2 View
    {
        get
        {
            double half = Math.Max(Distance * 6, 1e-6);
            double mx = (A.X + B.X) / 2, my = (A.Y + B.Y) / 2;
            return new Bounds2(mx - half, my - half, mx + half, my + half);
        }
    }
}

/// <summary>Eckpunkte für die Markierung in der Vorschau (siehe <see cref="NearPointFinder.Classify"/>).</summary>
public sealed class PointMarks
{
    /// <summary>In A und B genau gleich (grün).</summary>
    public List<P2> Equal { get; } = new();

    /// <summary>Geringfügig abweichend, 0 &lt; Abstand ≤ Grenze (rot).</summary>
    public List<P2> Near { get; } = new();
}

/// <summary>
/// Sucht Eckpunkte, die in A und B nur wenig voneinander abweichen – typisch für versehentlich verschobene
/// Punkte, die unterhalb der Vergleichstoleranz bleiben oder in einer „geänderten“ Fläche untergehen.
/// </summary>
public static class NearPointFinder
{
    /// <summary>Mehr Treffer werden nicht geliefert (die Liste wäre ohnehin nicht mehr lesbar).</summary>
    public const int MaxResults = 100_000;

    /// <summary>
    /// Alle Punktpaare mit 0 &lt; Abstand ≤ <paramref name="maxDistance"/> (genau gleiche Punkte zählen nicht).
    /// Ein Punkt zählt nur, wenn sein <b>nächster</b> Punkt in der anderen Zeichnung in diesem Bereich liegt –
    /// hat er dort ein genaues Gegenstück, ist er nicht abweichend. Gesucht wird in beide Richtungen.
    /// Sortiert nach Abstand, größter zuerst.
    /// </summary>
    public static List<NearPoint> Find(ExtractionResult a, ExtractionResult b, double maxDistance)
        => Find(a, b, 0, maxDistance);

    /// <summary>Wie oben, zusätzlich mit Untergrenze (Abstand &gt; <paramref name="minDistance"/>).</summary>
    public static List<NearPoint> Find(ExtractionResult a, ExtractionResult b, double minDistance, double maxDistance)
    {
        var result = new List<NearPoint>();
        if (maxDistance <= 0 || maxDistance <= minDistance) return result;

        var pa = UniquePoints(a);
        var pb = UniquePoints(b);
        var gridA = new PointGrid(pa, maxDistance);
        var gridB = new PointGrid(pb, maxDistance);
        var seen = new HashSet<(P2, P2)>();

        void Scan(List<(P2 P, AreaItem Item)> from, PointGrid to, bool fromIsA)
        {
            foreach (var (p, item) in from)
            {
                if (result.Count >= MaxResults) return;
                if (to.Nearest(p, maxDistance) is not { } hit) continue;
                double d = P2.Distance(p, hit.P);
                if (d <= minDistance || d > maxDistance) continue;
                var (ptA, ptB, itA, itB) = fromIsA ? (p, hit.P, item, hit.Item) : (hit.P, p, hit.Item, item);
                if (!seen.Add((ptA, ptB))) continue;
                result.Add(new NearPoint { A = ptA, B = ptB, ItemA = itA, ItemB = itB });
            }
        }

        Scan(pa, gridB, true);
        Scan(pb, gridA, false);

        return result
            .OrderByDescending(n => n.Distance)
            .Select((n, i) => new NearPoint { Nr = i + 1, A = n.A, B = n.B, ItemA = n.ItemA, ItemB = n.ItemB })
            .ToList();
    }

    /// <summary>
    /// Eckpunkte für die Markierung in der Vorschau einteilen:
    /// <see cref="PointMarks.Equal"/> = Punkt aus A mit genau gleichem Punkt in B (Abstand 0),
    /// <see cref="PointMarks.Near"/> = Punkte aus A und aus B, deren nächster Punkt in der anderen Zeichnung
    /// um mehr als 0 und höchstens <paramref name="maxDistance"/> abweicht (beide Punkte eines Paars).
    /// Punkte ohne Gegenstück in diesem Bereich sind in keiner Liste.
    /// </summary>
    public static PointMarks Classify(ExtractionResult a, ExtractionResult b, double maxDistance)
    {
        var marks = new PointMarks();
        if (maxDistance <= 0) return marks;
        var pa = UniquePoints(a);
        var pb = UniquePoints(b);
        var gridA = new PointGrid(pa, maxDistance);
        var gridB = new PointGrid(pb, maxDistance);

        foreach (var (p, _) in pa)
        {
            if (gridB.Nearest(p, maxDistance) is not { } hit) continue;
            if (hit.P == p) marks.Equal.Add(p);
            else marks.Near.Add(p);
        }
        foreach (var (p, _) in pb)
        {
            if (gridA.Nearest(p, maxDistance) is not { } hit) continue;
            if (hit.P != p) marks.Near.Add(p); // gleiche Punkte stehen schon in Equal
        }
        return marks;
    }

    /// <summary>Echte Eckpunkte aller Flächen, jeder Punkt nur einmal (gemeinsame Ecken: kleinste Fläche).</summary>
    private static List<(P2 P, AreaItem Item)> UniquePoints(ExtractionResult r)
    {
        var map = new Dictionary<P2, AreaItem>();
        foreach (var item in r.Items.OrderBy(i => i.Area))
        foreach (var o in item.Outlines)
        foreach (var p in o.Corners.Count > 0 ? o.Corners : o.Points)
            map.TryAdd(p, item);
        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Raster mit Zellgröße = Suchradius: der nächste Punkt liegt in der eigenen oder einer Nachbarzelle.</summary>
    private sealed class PointGrid
    {
        private readonly Dictionary<(long, long), List<(P2 P, AreaItem Item)>> _cells = new();
        private readonly double _cell;

        public PointGrid(List<(P2 P, AreaItem Item)> points, double cell)
        {
            _cell = cell;
            foreach (var e in points)
            {
                var k = Key(e.P);
                if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new();
                l.Add(e);
            }
        }

        private (long, long) Key(P2 p) => ((long)Math.Floor(p.X / _cell), (long)Math.Floor(p.Y / _cell));

        public (P2 P, AreaItem Item)? Nearest(P2 p, double maxDistance)
        {
            var (kx, ky) = Key(p);
            double best = maxDistance * maxDistance;
            (P2 P, AreaItem Item)? hit = null;
            for (long x = kx - 1; x <= kx + 1; x++)
            for (long y = ky - 1; y <= ky + 1; y++)
            {
                if (!_cells.TryGetValue((x, y), out var l)) continue;
                foreach (var e in l)
                {
                    double dx = e.P.X - p.X, dy = e.P.Y - p.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 <= best) { best = d2; hit = e; }
                }
            }
            return hit;
        }
    }

    /// <summary>Vorschlag für die obere Grenze: 20 × Vergleichstoleranz, auf 1-2-5 gerundet.</summary>
    public static double SuggestMax(double tolerance)
    {
        double v = Math.Max(tolerance * 20, 1e-6);
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
    }

    /// <summary>Liste als CSV (Semikolon, deutsches Zahlenformat).</summary>
    public static string ToCsv(IEnumerable<NearPoint> points, string unitSymbol, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.GetCultureInfo("de-DE");
        string u = unitSymbol.Length > 0 ? $" [{unitSymbol}]" : "";
        var sb = new StringBuilder();
        sb.AppendLine($"Nr;X A{u};Y A{u};X B{u};Y B{u};ΔX{u};ΔY{u};Abstand{u};Layer A;Handle A;Layer B;Handle B");
        foreach (var n in points)
        {
            sb.Append(n.Nr).Append(';')
              .Append(F(n.A.X)).Append(';').Append(F(n.A.Y)).Append(';')
              .Append(F(n.B.X)).Append(';').Append(F(n.B.Y)).Append(';')
              .Append(F(n.DX)).Append(';').Append(F(n.DY)).Append(';').Append(F(n.Distance)).Append(';')
              .Append(Esc(n.ItemA.Layer)).Append(';').Append(n.ItemA.Handle).Append(';')
              .Append(Esc(n.ItemB.Layer)).Append(';').Append(n.ItemB.Handle).AppendLine();
        }
        return sb.ToString();

        string F(double v) => NumberFormats.Full(v, culture).Replace(culture.NumberFormat.NumberGroupSeparator, "");
        static string Esc(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Liste als Text mit Tabulatoren (zum Einfügen in Excel).</summary>
    public static string ToTabText(IEnumerable<NearPoint> points, CultureInfo? culture = null) =>
        ToCsv(points, "", culture).Replace(';', '\t');
}
