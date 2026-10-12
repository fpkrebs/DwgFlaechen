using DwgFlaechen.Core.Geometry;

namespace DwgFlaechen.Core;

public enum AreaKind
{
    Polylinie,
    Schraffur,
    Kreis,
    Ellipse,
    Solid,
    Spline,
    /// <summary>Storeplanner-Fläche (GIN_DbArea, in AutoCAD als Proxy-Objekt dargestellt).</summary>
    Storeplanner,
}

/// <summary>Kontur eines Flächenobjekts in Weltkoordinaten (für Vorschau).</summary>
public sealed record AreaOutline(IReadOnlyList<P2> Points, bool IsHole)
{
    /// <summary>Echte Eckpunkte der Kontur in Weltkoordinaten (leer bei Kreis, Ellipse, Spline).</summary>
    public IReadOnlyList<P2> Corners { get; init; } = Array.Empty<P2>();
}

/// <summary>Eine gefundene Fläche.</summary>
public sealed class AreaItem
{
    public int Nr { get; init; }
    public required string Handle { get; init; }
    public AreaKind Kind { get; init; }
    public required string Layer { get; init; }
    /// <summary>Blockpfad bei Objekten aus Blockreferenzen, z. B. "Raum > Tisch"; leer im Modellbereich.</summary>
    public string Block { get; init; } = "";
    /// <summary>Fläche in Zeichnungseinheiten².</summary>
    public double Area { get; init; }
    /// <summary>Umfang (Summe aller Konturen) in Zeichnungseinheiten.</summary>
    public double Perimeter { get; init; }
    public int LoopCount { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public required IReadOnlyList<AreaOutline> Outlines { get; init; }
    public Bounds2 Bounds { get; init; }
    /// <summary>Beschriftung der Fläche (nur Storeplanner-Flächen), Zeilen mit " · " getrennt.</summary>
    public string Label { get; init; } = "";
    /// <summary>In der Beschriftung angezeigte Fläche in m² ("… qm"), sonst null.</summary>
    public double? LabelArea { get; init; }
}

public sealed class LayerSummary
{
    public required string Layer { get; init; }
    public int Count { get; init; }
    public double Area { get; init; }
}

public sealed class ExtractionOptions
{
    public bool IncludePolylines { get; set; } = true;
    public bool IncludeHatches { get; set; } = true;
    public bool IncludeCircles { get; set; } = true;
    public bool IncludeEllipses { get; set; } = true;
    public bool IncludeSolids { get; set; } = true;
    public bool IncludeSplines { get; set; } = true;
    /// <summary>Storeplanner-Flächen (Proxy-Objekte GIN_DBAREA) aus ihrer Proxy-Grafik auswerten.</summary>
    public bool IncludeStoreplannerAreas { get; set; } = true;
    /// <summary>Blockreferenzen (INSERT) auflösen, inkl. verschachtelter Blöcke.</summary>
    public bool IncludeBlocks { get; set; } = true;
    /// <summary>Objekte auf ausgeschalteten oder gefrorenen Layern überspringen.</summary>
    public bool OnlyVisibleLayers { get; set; } = true;
    /// <summary>Offene Polylinien, deren Start- und Endpunkt zusammenfallen, als geschlossen werten.</summary>
    public bool TreatCoincidentEndsAsClosed { get; set; } = true;
    /// <summary>Flächen kleiner als dieser Wert (Zeichnungseinheiten²) werden ignoriert.</summary>
    public double MinArea { get; set; } = 1e-9;
}

public sealed class ExtractionResult
{
    public required string FilePath { get; init; }
    public required string Version { get; init; }
    public DrawingUnit Unit { get; init; }
    public required IReadOnlyList<AreaItem> Items { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public Bounds2 Bounds { get; init; }

    public double TotalArea => Items.Sum(i => i.Area);

    public IReadOnlyList<LayerSummary> ByLayer() => Items
        .GroupBy(i => i.Layer, StringComparer.OrdinalIgnoreCase)
        .Select(g => new LayerSummary { Layer = g.Key, Count = g.Count(), Area = g.Sum(i => i.Area) })
        .OrderBy(s => s.Layer, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}
