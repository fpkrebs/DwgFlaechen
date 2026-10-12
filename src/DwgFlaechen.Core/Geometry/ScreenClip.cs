namespace DwgFlaechen.Core.Geometry;

/// <summary>
/// Ansicht der Vorschau: Bildschirm = (s·(x−ox) + tx, −s·(y−oy) + ty).
/// Alle Rechnungen in double – erst das Ergebnis geht an WPF/WinUI (die intern mit float zeichnen).
/// </summary>
public readonly record struct ViewTransform(double Scale, double Tx, double Ty, P2 Origin)
{
    public P2 ToScreen(P2 w) => new(Scale * (w.X - Origin.X) + Tx, -Scale * (w.Y - Origin.Y) + Ty);

    public Bounds2 ToScreen(Bounds2 w) => w.IsEmpty ? w
        : new Bounds2(Scale * (w.MinX - Origin.X) + Tx, -Scale * (w.MaxY - Origin.Y) + Ty,
                      Scale * (w.MaxX - Origin.X) + Tx, -Scale * (w.MinY - Origin.Y) + Ty);
}

/// <summary>
/// Genaues Zeichnen bei starker Vergrößerung.
/// WPF und WinUI rechnen beim Zeichnen mit float (≈ 7 Stellen). Liegt die Zeichnung im Bildschirmmaßstab
/// bei Millionen Pixeln, verwackeln Kanten oder verschwinden. Dann werden die Konturen hier in double in
/// Bildschirmkoordinaten umgerechnet und auf einen Rahmen knapp außerhalb des sichtbaren Bereichs
/// zugeschnitten, sodass die Oberfläche nur noch kleine Zahlen sieht.
/// </summary>
public static class ScreenClip
{
    /// <summary>Ab dieser Ausdehnung der Zeichnung in Pixeln wird im Bildschirmraum gezeichnet.</summary>
    public const double PreciseFromPixels = 20_000;

    /// <summary>Rand um den sichtbaren Bereich (px), damit Schnittkanten und Linienenden unsichtbar bleiben.</summary>
    public const double Margin = 64;

    /// <summary>Muss im Bildschirmraum gezeichnet werden?</summary>
    public static bool NeedsPrecise(double scale, Bounds2 drawing) =>
        !drawing.IsEmpty && scale * Math.Max(drawing.Width, drawing.Height) > PreciseFromPixels;

    /// <summary>Zuschneiderahmen für einen Ausschnitt der Größe w × h.</summary>
    public static Bounds2 ClipRect(double width, double height) =>
        new(-Margin, -Margin, width + Margin, height + Margin);

    /// <summary>
    /// Konturen in Bildschirmkoordinaten, auf <paramref name="clip"/> zugeschnitten (Sutherland-Hodgman je Kontur).
    /// Even-Odd-Füllung bleibt richtig: (A∩R) xor (B∩R) = (A xor B)∩R.
    /// Liefert eine leere Liste, wenn nichts im Rahmen liegt.
    /// </summary>
    public static List<List<P2>> Clip(IEnumerable<IReadOnlyList<P2>> loops, ViewTransform view, Bounds2 clip)
    {
        var result = new List<List<P2>>();
        foreach (var loop in loops)
        {
            if (loop.Count < 2) continue;
            var pts = new List<P2>(loop.Count);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var w in loop)
            {
                var s = view.ToScreen(w);
                pts.Add(s);
                if (s.X < minX) minX = s.X;
                if (s.X > maxX) maxX = s.X;
                if (s.Y < minY) minY = s.Y;
                if (s.Y > maxY) maxY = s.Y;
            }
            // ganz außerhalb
            if (maxX < clip.MinX || minX > clip.MaxX || maxY < clip.MinY || minY > clip.MaxY) continue;
            // ganz innerhalb: nichts zu schneiden
            if (minX >= clip.MinX && maxX <= clip.MaxX && minY >= clip.MinY && maxY <= clip.MaxY)
            {
                result.Add(pts);
                continue;
            }
            pts = ClipEdge(pts, p => p.X >= clip.MinX, (a, b) => AtX(a, b, clip.MinX));
            pts = ClipEdge(pts, p => p.X <= clip.MaxX, (a, b) => AtX(a, b, clip.MaxX));
            pts = ClipEdge(pts, p => p.Y >= clip.MinY, (a, b) => AtY(a, b, clip.MinY));
            pts = ClipEdge(pts, p => p.Y <= clip.MaxY, (a, b) => AtY(a, b, clip.MaxY));
            if (pts.Count >= 2) result.Add(pts);
        }
        return result;
    }

    private static List<P2> ClipEdge(List<P2> input, Func<P2, bool> inside, Func<P2, P2, P2> cut)
    {
        var output = new List<P2>(input.Count + 4);
        if (input.Count == 0) return output;
        P2 prev = input[^1];
        bool prevIn = inside(prev);
        foreach (var cur in input)
        {
            bool curIn = inside(cur);
            if (curIn)
            {
                if (!prevIn) output.Add(cut(prev, cur));
                output.Add(cur);
            }
            else if (prevIn)
            {
                output.Add(cut(prev, cur));
            }
            prev = cur;
            prevIn = curIn;
        }
        return output;
    }

    private static P2 AtX(P2 a, P2 b, double x)
    {
        double t = (x - a.X) / (b.X - a.X);
        return new P2(x, a.Y + t * (b.Y - a.Y));
    }

    private static P2 AtY(P2 a, P2 b, double y)
    {
        double t = (y - a.Y) / (b.Y - a.Y);
        return new P2(a.X + t * (b.X - a.X), y);
    }
}
