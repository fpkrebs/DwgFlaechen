namespace DwgFlaechen.Core.Geometry;

/// <summary>
/// Geschlossene Kontur aus Punkten mit Bulge-Werten (Bulge i gilt für das Segment Punkt i → Punkt i+1,
/// das letzte Segment schließt zum ersten Punkt). Bulge = tan(Öffnungswinkel / 4), positiv = gegen den Uhrzeigersinn.
/// Kreisbogensegmente werden bei Fläche und Umfang exakt gerechnet.
/// </summary>
public sealed class Loop
{
    public List<P2> Points { get; } = new();
    public List<double> Bulges { get; } = new();
    /// <summary>true = echter Eckpunkt (Scheitelpunkt), false = nur Stützpunkt einer Kurven-Näherung.</summary>
    public List<bool> Corners { get; } = new();

    public int Count => Points.Count;

    public void Add(P2 p, double bulge = 0, bool corner = true)
    {
        Points.Add(p);
        Bulges.Add(double.IsFinite(bulge) ? bulge : 0);
        Corners.Add(corner);
    }

    /// <summary>Die echten Eckpunkte der Kontur (ohne Stützpunkte von Kreisen, Ellipsen, Splines).</summary>
    public List<P2> CornerPoints()
    {
        var r = new List<P2>();
        for (int i = 0; i < Count; i++) if (Corners[i]) r.Add(Points[i]);
        return r;
    }

    /// <summary>Entfernt doppelte Folgepunkte und einen doppelten Schlusspunkt.</summary>
    public Loop Normalize(double tol)
    {
        var r = new Loop();
        for (int i = 0; i < Count; i++)
        {
            if (r.Count > 0 && P2.Distance(r.Points[^1], Points[i]) <= tol)
            {
                // Nullsegment – Bulge des aktuellen Punktes übernehmen
                r.Bulges[^1] = Bulges[i];
                r.Corners[^1] |= Corners[i];
                continue;
            }
            r.Add(Points[i], Bulges[i], Corners[i]);
        }
        if (r.Count > 1 && P2.Distance(r.Points[0], r.Points[^1]) <= tol)
        {
            r.Corners[0] |= r.Corners[^1];
            r.Points.RemoveAt(r.Count - 1);
            r.Bulges.RemoveAt(r.Bulges.Count - 1);
            r.Corners.RemoveAt(r.Corners.Count - 1);
        }
        return r;
    }

    /// <summary>Vorzeichenbehaftete Fläche (positiv = gegen den Uhrzeigersinn).</summary>
    public double SignedArea()
    {
        int n = Count;
        if (n < 2) return 0;
        double a = 0;
        for (int i = 0; i < n; i++)
        {
            P2 p = Points[i], q = Points[(i + 1) % n];
            a += p.X * q.Y - q.X * p.Y;
        }
        a *= 0.5;

        for (int i = 0; i < n; i++)
        {
            double b = Bulges[i];
            if (b == 0) continue;
            P2 p = Points[i], q = Points[(i + 1) % n];
            double c = P2.Distance(p, q);
            if (c < 1e-15) continue;
            double theta = 4 * Math.Atan(Math.Abs(b));          // Öffnungswinkel (0..2π)
            double r = c / (2 * Math.Sin(theta / 2));
            double segment = 0.5 * r * r * (theta - Math.Sin(theta));
            a += Math.Sign(b) * segment;
        }
        return a;
    }

    public double Area => Math.Abs(SignedArea());

    public double Perimeter()
    {
        int n = Count;
        if (n < 2) return 0;
        double len = 0;
        for (int i = 0; i < n; i++)
        {
            P2 p = Points[i], q = Points[(i + 1) % n];
            double c = P2.Distance(p, q);
            double b = Bulges[i];
            if (b == 0 || c < 1e-15) { len += c; continue; }
            double theta = 4 * Math.Atan(Math.Abs(b));
            double r = c / (2 * Math.Sin(theta / 2));
            len += r * theta;
        }
        return len;
    }

    /// <summary>Polygon-Näherung (Bögen werden in Stücke von höchstens <paramref name="maxStepRad"/> zerlegt).</summary>
    public List<P2> Tessellate(double maxStepRad = Math.PI / 90) => Tessellate(maxStepRad, null);

    /// <summary>Wie <see cref="Tessellate(double)"/>; <paramref name="corners"/> erhält je Punkt das Eckpunkt-Kennzeichen.</summary>
    public List<P2> Tessellate(double maxStepRad, List<bool>? corners)
    {
        var pts = new List<P2>(Count * 2);
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            P2 p = Points[i], q = Points[(i + 1) % n];
            pts.Add(p);
            corners?.Add(Corners[i]);
            double b = Bulges[i];
            if (b == 0) continue;
            int before = pts.Count;
            AppendArcInterior(pts, p, q, b, maxStepRad);
            if (corners != null) for (int k = before; k < pts.Count; k++) corners.Add(false);
        }
        return pts;
    }

    internal static void AppendArcInterior(List<P2> pts, P2 p, P2 q, double bulge, double maxStepRad)
    {
        P2 d = q - p;
        double c = d.Length;
        if (c < 1e-15) return;
        double theta = 4 * Math.Atan(bulge);                    // vorzeichenbehaftet
        // Mittelpunkt: Sehnenmitte + linke Normale * (c/2) * (1 - b²) / (2b)
        P2 mid = p + d * 0.5;
        P2 nrm = d.PerpLeft * (1.0 / c);
        P2 center = mid + nrm * (c / 2 * (1 - bulge * bulge) / (2 * bulge));
        double r = P2.Distance(center, p);
        double a0 = Math.Atan2(p.Y - center.Y, p.X - center.X);
        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) / maxStepRad));
        for (int k = 1; k < steps; k++)
        {
            double a = a0 + theta * k / steps;
            pts.Add(new P2(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a)));
        }
    }

    /// <summary>
    /// Transformiert die Kontur (lokale Koordinaten mit Höhe z) nach Welt-XY.
    /// Bei Ähnlichkeitsabbildungen bleiben Bögen exakt, sonst werden sie vorher in Polygone zerlegt.
    /// </summary>
    public Loop Transform(Affine3 t, double z = 0)
    {
        var r = new Loop();
        if (t.IsPlanarSimilarity(out bool mirrored, out _))
        {
            for (int i = 0; i < Count; i++)
                r.Add(t.ApplyXY(Points[i].X, Points[i].Y, z), mirrored ? -Bulges[i] : Bulges[i], Corners[i]);
        }
        else
        {
            var flags = new List<bool>();
            var pts = Tessellate(Math.PI / 720, flags);
            for (int i = 0; i < pts.Count; i++)
                r.Add(t.ApplyXY(pts[i].X, pts[i].Y, z), 0, flags[i]);
        }
        return r;
    }

    public Bounds2 GetBounds()
    {
        var b = Bounds2.Empty;
        foreach (var p in Tessellate(Math.PI / 18)) b = b.Include(p);
        return b;
    }

    /// <summary>Punkt-in-Polygon-Test (Ray Casting) auf einem bereits tessellierten Polygon.</summary>
    public static bool Contains(IReadOnlyList<P2> poly, P2 pt)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            P2 a = poly[i], b = poly[j];
            if ((a.Y > pt.Y) != (b.Y > pt.Y) &&
                pt.X < (b.X - a.X) * (pt.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
