namespace DwgFlaechen.Core.Geometry;

/// <summary>
/// Affine 3D-Abbildung (3x3 linear + Translation), unabhängig von den Matrix-Konventionen der CAD-Bibliothek.
/// Wird für OCS→WCS und Blockreferenzen (auch verschachtelt) verwendet.
/// </summary>
public readonly struct Affine3
{
    // Zeilen: X' = M00 x + M01 y + M02 z + T0 ...
    public readonly double M00, M01, M02, T0;
    public readonly double M10, M11, M12, T1;
    public readonly double M20, M21, M22, T2;

    public Affine3(double m00, double m01, double m02, double t0,
                   double m10, double m11, double m12, double t1,
                   double m20, double m21, double m22, double t2)
    {
        M00 = m00; M01 = m01; M02 = m02; T0 = t0;
        M10 = m10; M11 = m11; M12 = m12; T1 = t1;
        M20 = m20; M21 = m21; M22 = m22; T2 = t2;
    }

    public static readonly Affine3 Identity = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);

    public static Affine3 Translation(double x, double y, double z) => new(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z);

    public static Affine3 Scale(double sx, double sy, double sz) => new(sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0);

    public static Affine3 RotationZ(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0);
    }

    /// <summary>
    /// Objektkoordinatensystem (OCS) → Weltkoordinaten nach dem "Arbitrary Axis Algorithm" von AutoCAD.
    /// </summary>
    public static Affine3 FromOcs(double nx, double ny, double nz)
    {
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12) return Identity;
        nx /= len; ny /= len; nz /= len;
        if (Math.Abs(nx) < 1e-12 && Math.Abs(ny) < 1e-12 && nz > 0) return Identity;

        double ax, ay, az;
        const double lim = 1.0 / 64.0;
        if (Math.Abs(nx) < lim && Math.Abs(ny) < lim)
        {   // Ax = Wy × N
            ax = 1 * nz - 0 * ny; ay = 0 * nx - 0 * nz; az = 0 * ny - 1 * nx;
        }
        else
        {   // Ax = Wz × N
            ax = 0 * nz - 1 * ny; ay = 1 * nx - 0 * nz; az = 0;
        }
        double al = Math.Sqrt(ax * ax + ay * ay + az * az);
        ax /= al; ay /= al; az /= al;
        // Ay = N × Ax
        double bx = ny * az - nz * ay, by = nz * ax - nx * az, bz = nx * ay - ny * ax;

        return new(ax, bx, nx, 0,
                   ay, by, ny, 0,
                   az, bz, nz, 0);
    }

    /// <summary>Verkettung: (this ∘ inner)(p) = this(inner(p)).</summary>
    public Affine3 Then(Affine3 inner) => Multiply(this, inner);

    public static Affine3 Multiply(Affine3 a, Affine3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M00 * b.T0 + a.M01 * b.T1 + a.M02 * b.T2 + a.T0,

        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M10 * b.T0 + a.M11 * b.T1 + a.M12 * b.T2 + a.T1,

        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
        a.M20 * b.T0 + a.M21 * b.T1 + a.M22 * b.T2 + a.T2);

    /// <summary>Bildet einen Punkt (x, y, z) ab und projiziert das Ergebnis auf die XY-Ebene.</summary>
    public P2 ApplyXY(double x, double y, double z) =>
        new(M00 * x + M01 * y + M02 * z + T0,
            M10 * x + M11 * y + M12 * z + T1);

    /// <summary>
    /// Prüft, ob die Abbildung der lokalen XY-Ebene nach der Projektion eine Ähnlichkeitsabbildung ist
    /// (Kreisbögen bleiben Kreisbögen). <paramref name="mirrored"/> ist true bei Spiegelung.
    /// </summary>
    public bool IsPlanarSimilarity(out bool mirrored, out double scale)
    {
        var u = new P2(M00, M10);
        var v = new P2(M01, M11);
        double lu = u.Length, lv = v.Length;
        mirrored = u.Cross(v) < 0;
        scale = lu;
        if (lu < 1e-15 || lv < 1e-15) return false;
        return Math.Abs(lu - lv) <= 1e-9 * Math.Max(lu, lv) && Math.Abs(u.Dot(v)) <= 1e-9 * lu * lv;
    }
}
