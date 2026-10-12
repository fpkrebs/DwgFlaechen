namespace DwgFlaechen.Core;

/// <summary>Zeichnungseinheit (entspricht den gängigen Werten von $INSUNITS).</summary>
public enum DrawingUnit
{
    Unbekannt = 0,
    Zoll = 1,
    Fuß = 2,
    Millimeter = 4,
    Zentimeter = 5,
    Meter = 6,
    Kilometer = 7,
    Dezimeter = 14,
}

public static class DrawingUnits
{
    /// <summary>Länge einer Zeichnungseinheit in Metern (null = unbekannt).</summary>
    public static double? ToMeters(DrawingUnit u) => u switch
    {
        DrawingUnit.Millimeter => 0.001,
        DrawingUnit.Zentimeter => 0.01,
        DrawingUnit.Dezimeter => 0.1,
        DrawingUnit.Meter => 1.0,
        DrawingUnit.Kilometer => 1000.0,
        DrawingUnit.Zoll => 0.0254,
        DrawingUnit.Fuß => 0.3048,
        _ => null,
    };

    public static string Symbol(DrawingUnit u) => u switch
    {
        DrawingUnit.Millimeter => "mm",
        DrawingUnit.Zentimeter => "cm",
        DrawingUnit.Dezimeter => "dm",
        DrawingUnit.Meter => "m",
        DrawingUnit.Kilometer => "km",
        DrawingUnit.Zoll => "in",
        DrawingUnit.Fuß => "ft",
        _ => "E",
    };

    /// <summary>Rechnet eine Fläche in Zeichnungseinheiten² in m² um (null, wenn Einheit unbekannt).</summary>
    public static double? AreaToSquareMeters(double area, DrawingUnit u) =>
        ToMeters(u) is double f ? area * f * f : null;

    public static DrawingUnit FromInsUnits(int insUnits) =>
        Enum.IsDefined(typeof(DrawingUnit), insUnits) ? (DrawingUnit)insUnits : DrawingUnit.Unbekannt;
}
