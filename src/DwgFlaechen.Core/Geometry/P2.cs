namespace DwgFlaechen.Core.Geometry;

/// <summary>Einfacher 2D-Punkt / Vektor (Weltkoordinaten, XY-Ebene).</summary>
public readonly record struct P2(double X, double Y)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
    public static P2 operator *(P2 a, double s) => new(a.X * s, a.Y * s);
    public static P2 operator *(double s, P2 a) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Cross(P2 o) => X * o.Y - Y * o.X;
    public double Dot(P2 o) => X * o.X + Y * o.Y;
    /// <summary>Linke Normale (90° gegen den Uhrzeigersinn gedreht).</summary>
    public P2 PerpLeft => new(-Y, X);

    public static double Distance(P2 a, P2 b) => (a - b).Length;

    public override string ToString() => $"({X:0.###}; {Y:0.###})";
}

/// <summary>Achsparalleles Rechteck.</summary>
public readonly record struct Bounds2(double MinX, double MinY, double MaxX, double MaxY)
{
    public static readonly Bounds2 Empty = new(double.PositiveInfinity, double.PositiveInfinity,
                                               double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;

    public Bounds2 Include(P2 p) => new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y),
                                        Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

    public Bounds2 Union(Bounds2 o) => o.IsEmpty ? this : IsEmpty ? o :
        new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
}
