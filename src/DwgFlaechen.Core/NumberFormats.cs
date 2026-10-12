using System.Globalization;

namespace DwgFlaechen.Core;

/// <summary>Zahlenformate für Koordinaten und Differenzen (beide Oberflächen gleich).</summary>
public static class NumberFormats
{
    /// <summary>
    /// Genaue Werte (Eckpunkte, ΔX/ΔY, Abstand, Abweichung): mindestens 3, höchstens 6 Nachkommastellen.
    /// Mehr wäre bei großen Koordinaten (z. B. Gauß-Krüger) nur Rundungsrauschen von double.
    /// </summary>
    public const string Exact = "#,##0.000###";

    /// <summary>Flächendifferenz mit Vorzeichen, bis 6 Nachkommastellen (kleinste Abweichungen bleiben sichtbar).</summary>
    public const string SignedArea = "+#,##0.000###;-#,##0.000###;0";

    /// <summary>Nachkommastellen für die Mauskoordinate: so viele, dass ein Bildschirmpixel noch auflöst (3…6).</summary>
    public static int CursorDecimals(double scale) =>
        scale <= 0 || double.IsNaN(scale) ? 3 : Math.Clamp((int)Math.Ceiling(Math.Log10(scale)), 3, 6);

    public static string Cursor(double value, double scale, CultureInfo culture) =>
        value.ToString("N" + CursorDecimals(scale).ToString(CultureInfo.InvariantCulture), culture);

    public static string ExactText(double value, CultureInfo culture) => value.ToString(Exact, culture);

    /// <summary>
    /// Volle Genauigkeit: so viele Nachkommastellen, wie der double-Wert wirklich trägt
    /// (kürzeste Darstellung, die beim Einlesen wieder exakt denselben Wert ergibt), mindestens <paramref name="minDecimals"/>.
    /// Ohne Exponentenschreibweise, mit Tausenderpunkten. <paramref name="signed"/>: „+“ vor positiven Werten.
    /// </summary>
    public static string Full(double value, CultureInfo culture, bool signed = false, int minDecimals = 3)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return value.ToString(culture);
        if (value == 0) return signed ? "0" : 0.0.ToString("N" + minDecimals.ToString(CultureInfo.InvariantCulture), culture);

        string r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture); // z. B. "1.4551915228366852E-11"
        int exp = 0, e = r.IndexOfAny(new[] { 'E', 'e' });
        string mant = r;
        if (e >= 0) { exp = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture); mant = r[..e]; }
        int dot = mant.IndexOf('.');
        int frac = dot < 0 ? 0 : mant.Length - dot - 1;
        int dec = Math.Clamp(frac - exp, minDecimals, 350);

        string s = value.ToString("N" + dec.ToString(CultureInfo.InvariantCulture), culture);
        return signed && value > 0 ? "+" + s : s;
    }
}
