using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DwgFlaechen.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DwgFlaechen.App;

/// <summary>Zeile der Flächentabelle.</summary>
public sealed class AreaRow
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public AreaRow(AreaItem item, DrawingUnit unit, bool showLabels = false)
    {
        Item = item;
        LabelColumnWidth = showLabels ? new GridLength(105) : new GridLength(0);
        LabelTextColumnWidth = showLabels ? new GridLength(16, GridUnitType.Star) : new GridLength(0);
        Color = RowColors.Display(item.R, item.G, item.B);
        Swatch = new SolidColorBrush(Color);
        AreaM2 = DrawingUnits.AreaToSquareMeters(item.Area, unit);
    }

    /// <summary>Darstellung im Vergleichsmodus: feste Farbe; <paramref name="outlineOnly"/> = nur gestrichelter Umriss.</summary>
    public AreaRow(AreaItem item, DrawingUnit unit, CompareRow compare, string source, Color color, bool outlineOnly)
        : this(item, unit)
    {
        Compare = compare;
        Source = source;
        Color = color;
        Swatch = new SolidColorBrush(color);
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
    public double? AreaM2 { get; }

    public string NrText => Item.Nr.ToString(De);
    public string KindText => Item.Kind.ToString();
    public string Layer => Item.Layer;
    public string Block => Item.Block;
    public string AreaText => Item.Area.ToString("N3", De);
    public string AreaM2Text => AreaM2?.ToString("N3", De) ?? "–";
    public string PerimeterText => Item.Perimeter.ToString("N3", De);
    /// <summary>Beschriftung (nur Storeplanner-Flächen).</summary>
    public string Label => Item.Label;
    /// <summary>Fläche laut Beschriftung in m² (nur Storeplanner-Flächen).</summary>
    public string LabelAreaText => Item.LabelArea?.ToString("N2", De) ?? "";
    /// <summary>Spaltenbreiten der Beschriftungsspalten (0, wenn die Datei keine Beschriftungen enthält).</summary>
    public GridLength LabelColumnWidth { get; }
    public GridLength LabelTextColumnWidth { get; }
    public string Tooltip =>
        $"{Item.Kind}  ·  Handle {Item.Handle}\nLayer: {Item.Layer}" +
        (string.IsNullOrEmpty(Item.Block) ? "" : $"\nBlock: {Item.Block}") +
        (Item.LoopCount > 1 ? $"\nKonturen: {Item.LoopCount} (Inseln abgezogen)" : "") +
        (string.IsNullOrEmpty(Item.Label) ? "" : $"\nBeschriftung: {Item.Label}") +
        (Item.LabelArea is double la ? $"\nFläche lt. Beschriftung: {la.ToString("N2", De)} m²" : "");
}

/// <summary>Zeile der Layer-Übersicht (mit Ein/Aus-Schalter für die Summe).</summary>
public sealed class LayerRow : INotifyPropertyChanged
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private bool _isChecked = true;

    public LayerRow(LayerSummary s, DrawingUnit unit, Color color)
    {
        Layer = s.Layer;
        Count = s.Count;
        Area = s.Area;
        AreaM2 = DrawingUnits.AreaToSquareMeters(s.Area, unit);
        Swatch = new SolidColorBrush(color);
    }

    public string Layer { get; }
    public int Count { get; }
    public double Area { get; }
    public double? AreaM2 { get; }
    public SolidColorBrush Swatch { get; }

    public string CountText => Count.ToString(De);
    public string AreaText => Area.ToString("N3", De);
    public string AreaM2Text => AreaM2?.ToString("N3", De) ?? "–";

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
        if (lum > 215) return Color.FromArgb(255, 90, 90, 90);
        return Color.FromArgb(255, r, g, b);
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>Zeile der Vergleichstabelle.</summary>
public sealed class CompareRow
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private const string Signed = NumberFormats.SignedArea;

    public CompareRow(CompareItem item, DrawingUnit unit)
    {
        Item = item;
        Color = CompareColors.For(item.Status);
        Swatch = new SolidColorBrush(Color);
        DeltaM2 = DrawingUnits.AreaToSquareMeters(item.DeltaArea, unit);
    }

    public CompareItem Item { get; }
    public Color Color { get; }
    public SolidColorBrush Swatch { get; }
    public double? DeltaM2 { get; }

    public string NrText => Item.Nr.ToString(De);
    public string Status => Item.Status.ToString();
    public string Details => Item.Details;
    public string Layer => Item.Layer;
    public string KindText => Item.Kind.ToString();
    public string Label => Item.Label;
    public string AreaAText => Item.A?.Area.ToString("N3", De) ?? "";
    public string AreaBText => Item.B?.Area.ToString("N3", De) ?? "";
    public string DeltaText => Item.DeltaArea.ToString(Signed, De);
    public string DeltaM2Text => DeltaM2?.ToString(Signed, De) ?? "–";

    public string Tooltip =>
        $"{Item.Status}" + (Item.Details.Length > 0 ? $": {Item.Details}" : "") +
        (Item.A != null ? $"\nA: {Item.A.Kind}, Handle {Item.A.Handle}, Layer {Item.A.Layer}, Fläche {Item.A.Area.ToString("N3", De)}" : "") +
        (Item.B != null ? $"\nB: {Item.B.Kind}, Handle {Item.B.Handle}, Layer {Item.B.Layer}, Fläche {Item.B.Area.ToString("N3", De)}" : "") +
        (Item.A != null && Item.B != null && Item.A.Label != Item.B.Label
            ? $"\nBeschriftung A: {Item.A.Label}\nBeschriftung B: {Item.B.Label}"
            : string.IsNullOrEmpty(Item.Label) ? "" : $"\nBeschriftung: {Item.Label}");
}

/// <summary>Farben des Vergleichsmodus (Orange bleibt der Auswahl vorbehalten) – wie in der WPF-Version.</summary>
public static class CompareColors
{
    public static readonly Color New = Color.FromArgb(255, 0, 160, 60);
    public static readonly Color Removed = Color.FromArgb(255, 220, 40, 40);
    public static readonly Color Changed = Color.FromArgb(255, 0, 110, 220);
    public static readonly Color Unchanged = Color.FromArgb(255, 150, 150, 150);

    public static Color For(CompareStatus s) => s switch
    {
        CompareStatus.Neu => New,
        CompareStatus.Entfallen => Removed,
        CompareStatus.Geändert => Changed,
        _ => Unchanged,
    };
}
