using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DwgFlaechen.Core;

namespace DwgFlaechen.Wpf;

/// <summary>Zeile der Flächentabelle (numerische Properties → DataGrid sortiert korrekt).</summary>
public sealed class AreaRow
{
    public AreaRow(AreaItem item, DrawingUnit unit)
    {
        Item = item;
        Color = RowColors.Display(item.R, item.G, item.B);
        Swatch = RowColors.Brush(Color);
        AreaM2 = DrawingUnits.AreaToSquareMeters(item.Area, unit);
    }

    /// <summary>Darstellung im Vergleichsmodus: feste Farbe; <paramref name="outlineOnly"/> = nur gestrichelter Umriss.</summary>
    public AreaRow(AreaItem item, DrawingUnit unit, CompareRow compare, string source, Color color, bool outlineOnly)
        : this(item, unit)
    {
        Compare = compare;
        Source = source;
        Color = color;
        Swatch = RowColors.Brush(color);
        OutlineOnly = outlineOnly;
    }

    public AreaItem Item { get; }
    /// <summary>Zugehörige Vergleichszeile (nur im Vergleichsmodus).</summary>
    public CompareRow? Compare { get; }
    /// <summary>"A" oder "B" im Vergleichsmodus, sonst leer.</summary>
    public string Source { get; } = "";
    /// <summary>Nur Umriss (gestrichelt), keine Füllung.</summary>
    public bool OutlineOnly { get; }
    public Color Color { get; }
    public SolidColorBrush Swatch { get; }

    public int Nr => Item.Nr;
    public string Handle => Item.Handle;
    public string Kind => Item.Kind.ToString();
    public string Layer => Item.Layer;
    public string Block => Item.Block;
    public double Area => Item.Area;
    public double? AreaM2 { get; }
    public double Perimeter => Item.Perimeter;
    public int LoopCount => Item.LoopCount;
    /// <summary>Beschriftung (nur Storeplanner-Flächen).</summary>
    public string Label => Item.Label;
    /// <summary>Fläche laut Beschriftung in m² (nur Storeplanner-Flächen).</summary>
    public double? LabelArea => Item.LabelArea;

    public string Tooltip =>
        $"{Item.Kind}  ·  Handle {Item.Handle}\nLayer: {Item.Layer}" +
        (string.IsNullOrEmpty(Item.Block) ? "" : $"\nBlock: {Item.Block}") +
        (Item.LoopCount > 1 ? $"\nKonturen: {Item.LoopCount} (Inseln abgezogen)" : "") +
        (string.IsNullOrEmpty(Item.Label) ? "" : $"\nBeschriftung: {Item.Label}") +
        (Item.LabelArea is double la ? $"\nFläche lt. Beschriftung: {la:N2} m²" : "");
}

/// <summary>Zeile der Layer-Übersicht mit Häkchen „in Summe einbeziehen“.</summary>
public sealed class LayerRow : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public LayerRow(LayerSummary s, DrawingUnit unit, Color color)
    {
        Layer = s.Layer;
        Count = s.Count;
        Area = s.Area;
        AreaM2 = DrawingUnits.AreaToSquareMeters(s.Area, unit);
        Swatch = RowColors.Brush(color);
    }

    public string Layer { get; }
    public int Count { get; }
    public double Area { get; }
    public double? AreaM2 { get; }
    public SolidColorBrush Swatch { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

internal static class RowColors
{
    /// <summary>Sehr helle Farben (z. B. ACI 7 = weiß) auf hellem Hintergrund sichtbar machen.</summary>
    public static Color Display(byte r, byte g, byte b)
    {
        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return lum > 215 ? Color.FromRgb(90, 90, 90) : Color.FromRgb(r, g, b);
    }

    public static SolidColorBrush Brush(Color c, byte alpha = 255)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// <summary>Zeile der Vergleichstabelle.</summary>
public sealed class CompareRow
{
    public CompareRow(CompareItem item, DrawingUnit unit)
    {
        Item = item;
        Color = CompareColors.For(item.Status);
        Swatch = RowColors.Brush(Color);
        DeltaM2 = DrawingUnits.AreaToSquareMeters(item.DeltaArea, unit);
    }

    public CompareItem Item { get; }
    public Color Color { get; }
    public SolidColorBrush Swatch { get; }

    public int Nr => Item.Nr;
    public string Status => Item.Status.ToString();
    public string Details => Item.Details;
    public string Layer => Item.Layer;
    public string Kind => Item.Kind.ToString();
    public double? AreaA => Item.A?.Area;
    public double? AreaB => Item.B?.Area;
    public double Delta => Item.DeltaArea;
    public double? DeltaM2 { get; }
    public string Label => Item.Label;

    public string Tooltip =>
        $"{Item.Status}" + (Item.Details.Length > 0 ? $": {Item.Details}" : "") +
        (Item.A != null ? $"\nA: {Item.A.Kind}, Handle {Item.A.Handle}, Layer {Item.A.Layer}, Fläche {Item.A.Area:N3}" : "") +
        (Item.B != null ? $"\nB: {Item.B.Kind}, Handle {Item.B.Handle}, Layer {Item.B.Layer}, Fläche {Item.B.Area:N3}" : "") +
        (Item.A != null && Item.B != null && Item.A.Label != Item.B.Label
            ? $"\nBeschriftung A: {Item.A.Label}\nBeschriftung B: {Item.B.Label}"
            : string.IsNullOrEmpty(Item.Label) ? "" : $"\nBeschriftung: {Item.Label}");
}

/// <summary>Farben des Vergleichsmodus (Orange bleibt der Auswahl vorbehalten).</summary>
public static class CompareColors
{
    public static readonly Color New = Color.FromRgb(0, 160, 60);
    public static readonly Color Removed = Color.FromRgb(220, 40, 40);
    public static readonly Color Changed = Color.FromRgb(0, 110, 220);
    public static readonly Color Unchanged = Color.FromRgb(150, 150, 150);

    public static Color For(CompareStatus s) => s switch
    {
        CompareStatus.Neu => New,
        CompareStatus.Entfallen => Removed,
        CompareStatus.Geändert => Changed,
        _ => Unchanged,
    };
}
