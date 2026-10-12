using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DwgFlaechen.Core;
using Microsoft.Win32;

namespace DwgFlaechen.Wpf;

/// <summary>
/// Liste der Eckpunkte, die in A und B nur wenig voneinander abweichen (Vergleichsmodus).
/// Klick auf eine Zeile zoomt in der Vorschau des Hauptfensters zum Punktpaar.
/// </summary>
public sealed class NearPointsWindow : Window
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly ExtractionResult _a, _b;
    private readonly string _unit;
    private readonly Action<NearPoint> _show;
    private readonly Action<double>? _maxChanged;
    private readonly TextBox _to = new() { Width = 110, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly DataGrid _grid;
    private readonly Button _search = new() { Content = "Suchen", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
    private List<NearPoint> _items = new();

    public NearPointsWindow(ExtractionResult a, ExtractionResult b, double defaultMax, string unitSymbol, Action<NearPoint> show,
                            Action<double>? maxChanged = null)
    {
        _a = a; _b = b; _unit = unitSymbol; _show = show; _maxChanged = maxChanged;
        Title = $"Abweichende Punkte – {Path.GetFileName(a.FilePath)} / {Path.GetFileName(b.FilePath)}";
        Width = 1500; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _to.Text = NumberFormats.ExactText(defaultMax, De);
        string u = unitSymbol.Length > 0 ? " " + unitSymbol : "";

        // Kopfzeile: Grenzen und Suchen
        var top = new WrapPanel { Margin = new Thickness(10, 10, 10, 6) };
        top.Children.Add(new TextBlock { Text = "Abstand höchstens", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_to);
        top.Children.Add(new TextBlock { Text = u, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        top.Children.Add(_search);
        top.Children.Add(_info);
        _search.Click += async (_, _) => await SearchAsync();

        // Tabelle (Sortierung über Spaltenköpfe)
        _grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Margin = new Thickness(10, 0, 10, 0),
        };
        _grid.Columns.Add(Col("Nr", "Nr", null, 50));
        _grid.Columns.Add(Col("X A" + u, "A.X", FullConverter.Plain, 190));
        _grid.Columns.Add(Col("Y A" + u, "A.Y", FullConverter.Plain, 190));
        _grid.Columns.Add(Col("X B" + u, "B.X", FullConverter.Plain, 190));
        _grid.Columns.Add(Col("Y B" + u, "B.Y", FullConverter.Plain, 190));
        _grid.Columns.Add(Col("ΔX", "DX", FullConverter.Signed, 170));
        _grid.Columns.Add(Col("ΔY", "DY", FullConverter.Signed, 170));
        _grid.Columns.Add(Col("Abstand", "Distance", FullConverter.Plain, 170));
        _grid.Columns.Add(Col("Layer A", "ItemA.Layer", null, 110));
        _grid.Columns.Add(Col("Handle A", "ItemA.Handle", null, 70));
        _grid.Columns.Add(Col("Layer B", "ItemB.Layer", null, 110));
        _grid.Columns.Add(Col("Handle B", "ItemB.Handle", null, 70));
        _grid.SelectionChanged += (_, _) => { if (_grid.SelectedItem is NearPoint n) _show(n); };

        // Fußzeile
        var copy = new Button { Content = "Kopieren", Padding = new Thickness(12, 2, 12, 2) };
        copy.Click += (_, _) => Copy();
        var csv = new Button { Content = "Als CSV speichern …", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
        csv.Click += async (_, _) => await SaveCsvAsync();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 10) };
        bottom.Children.Add(new TextBlock
        {
            Text = "Klick auf eine Zeile zoomt in der Vorschau zum Punktpaar. Kopieren: markierte Zeilen bzw. alle.",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Foreground = System.Windows.Media.Brushes.Gray,
        });
        bottom.Children.Add(copy);
        bottom.Children.Add(csv);

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(bottom);
        dock.Children.Add(_grid);
        Content = dock;

        Loaded += async (_, _) => await SearchAsync();
    }

    private static DataGridTextColumn Col(string header, string path, IValueConverter? format, double width)
    {
        var b = new Binding(path);
        if (format != null) b.Converter = format;
        var col = new DataGridTextColumn { Header = header, Binding = b, Width = width, SortMemberPath = path };
        if (format != null)
            col.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
        return col;
    }

    private static bool TryParse(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, De, out v) ||
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private async Task SearchAsync()
    {
        if (!TryParse(_to.Text, out double max) || max <= 0)
        {
            _info.Text = "Bitte einen Abstand größer als 0 eingeben.";
            return;
        }
        _search.IsEnabled = false;
        _info.Text = "Suche …";
        _maxChanged?.Invoke(max); // gleiche Grenze für die Eckpunkt-Markierung im Hauptfenster
        try
        {
            _items = await Task.Run(() => NearPointFinder.Find(_a, _b, max));
            _grid.ItemsSource = _items;
            _info.Text = _items.Count == 0
                ? "Keine Punkte in diesem Bereich."
                : $"{_items.Count:N0} Punktpaare" + (_items.Count >= NearPointFinder.MaxResults ? " (Höchstzahl erreicht – Grenze verkleinern)" : "") +
                  $", größter Abstand {NumberFormats.Full(_items[0].Distance, De)} {_unit}";
        }
        catch (Exception ex)
        {
            _info.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            _search.IsEnabled = true;
        }
    }

    private IEnumerable<NearPoint> RowsForExport() =>
        _grid.SelectedItems.Count > 1 ? _grid.SelectedItems.OfType<NearPoint>() : _grid.Items.OfType<NearPoint>();

    private void Copy()
    {
        try { Clipboard.SetText(NearPointFinder.ToTabText(RowsForExport())); _info.Text = "In die Zwischenablage kopiert."; }
        catch (Exception ex) { _info.Text = "Kopieren fehlgeschlagen: " + ex.Message; }
    }

    private async Task SaveCsvAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Abweichende Punkte als CSV speichern",
            Filter = "CSV für Excel (*.csv)|*.csv",
            FileName = $"{Path.GetFileNameWithoutExtension(_a.FilePath)}_vs_{Path.GetFileNameWithoutExtension(_b.FilePath)}_Punkte.csv",
            InitialDirectory = Path.GetDirectoryName(_a.FilePath),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, NearPointFinder.ToCsv(_grid.Items.OfType<NearPoint>(), _unit), new UTF8Encoding(true));
            _info.Text = "Gespeichert: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            _info.Text = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }
}

/// <summary>Zahl mit voller Genauigkeit anzeigen (siehe <see cref="NumberFormats.Full"/>).</summary>
internal sealed class FullConverter : IValueConverter
{
    public static readonly FullConverter Plain = new(false), Signed = new(true);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly bool _signed;
    private FullConverter(bool signed) => _signed = signed;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? NumberFormats.Full(d, De, _signed) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
