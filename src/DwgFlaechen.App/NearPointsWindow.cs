using System.Globalization;
using System.Text;
using DwgFlaechen.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DwgFlaechen.App;

/// <summary>
/// Liste der Eckpunkte, die in A und B nur wenig voneinander abweichen (Vergleichsmodus).
/// Klick auf eine Zeile zoomt in der Vorschau des Hauptfensters zum Punktpaar.
/// Ohne XAML aufgebaut; angezeigt werden höchstens <see cref="MaxShown"/> Zeilen (Kopieren/CSV enthalten alle).
/// </summary>
public sealed class NearPointsWindow : Window
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private const int MaxShown = 2000;

    private static readonly (string Header, double Width, bool Right, string Key)[] Columns =
    {
        ("Nr", 50, true, "Nr"),
        ("X A", 190, true, "XA"), ("Y A", 190, true, "YA"),
        ("X B", 190, true, "XB"), ("Y B", 190, true, "YB"),
        ("ΔX", 170, true, "DX"), ("ΔY", 170, true, "DY"), ("Abstand", 170, true, "D"),
        ("Layer A", 110, false, "LA"), ("Handle A", 70, false, "HA"),
        ("Layer B", 110, false, "LB"), ("Handle B", 70, false, "HB"),
    };

    private readonly ExtractionResult _a, _b;
    private readonly string _unit;
    private readonly Action<NearPoint> _show;
    private readonly Action<double>? _maxChanged;
    private readonly TextBox _to = new() { Width = 120 };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _search = new() { Content = "Suchen" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
    private List<NearPoint> _items = new();
    private string _sortKey = "D";
    private bool _sortDesc = true;

    public NearPointsWindow(ExtractionResult a, ExtractionResult b, double defaultMax, string unitSymbol, Action<NearPoint> show,
                            Action<double>? maxChanged = null)
    {
        _a = a; _b = b; _unit = unitSymbol; _show = show; _maxChanged = maxChanged;
        Title = $"Abweichende Punkte – {Path.GetFileName(a.FilePath)} / {Path.GetFileName(b.FilePath)}";
        AppWindow.Resize(new SizeInt32(1720, 640));

        _to.Text = NumberFormats.ExactText(defaultMax, De);
        string u = unitSymbol.Length > 0 ? " " + unitSymbol : "";

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 12, 12, 8) };
        top.Children.Add(Label("Abstand höchstens"));
        top.Children.Add(_to);
        top.Children.Add(Label(u));
        top.Children.Add(_search);
        top.Children.Add(_info);
        _search.Click += async (_, _) => await SearchAsync();

        // Spaltenköpfe (Klick = sortieren)
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12 + 16, 0, 12, 2) };
        foreach (var c in Columns)
        {
            var h = new Button
            {
                Content = c.Header + (c.Key is "XA" or "YA" or "XB" or "YB" or "DX" or "DY" or "D" ? u : ""),
                Width = c.Width, Padding = new Thickness(4, 2, 4, 2), Tag = c.Key,
                HorizontalContentAlignment = c.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            };
            h.Click += (s, _) =>
            {
                string key = (string)((Button)s).Tag;
                if (_sortKey == key) _sortDesc = !_sortDesc;
                else { _sortKey = key; _sortDesc = key == "D"; }
                Fill();
            };
            header.Children.Add(h);
        }
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItem is FrameworkElement { Tag: NearPoint n }) _show(n);
        };

        var copy = new Button { Content = "Kopieren" };
        copy.Click += (_, _) => Copy();
        var csv = new Button { Content = "Als CSV speichern …" };
        csv.Click += async (_, _) => await SaveCsvAsync();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 8, 12, 12) };
        bottom.Children.Add(new TextBlock
        {
            Text = "Klick auf eine Zeile zoomt in der Vorschau zum Punktpaar. Kopieren: markierte Zeilen bzw. alle.",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Opacity = 0.7,
        });
        bottom.Children.Add(copy);
        bottom.Children.Add(csv);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 1);
        Grid.SetRow(_list, 2);
        Grid.SetRow(bottom, 3);
        root.Children.Add(top);
        root.Children.Add(header);
        root.Children.Add(_list);
        root.Children.Add(bottom);
        Content = root;

        root.Loaded += async (_, _) => await SearchAsync();
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

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
            Fill();
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

    private IEnumerable<NearPoint> Sorted()
    {
        Func<NearPoint, object> key = _sortKey switch
        {
            "Nr" => n => n.Nr,
            "XA" => n => n.A.X, "YA" => n => n.A.Y, "XB" => n => n.B.X, "YB" => n => n.B.Y,
            "DX" => n => n.DX, "DY" => n => n.DY,
            "LA" => n => n.ItemA.Layer, "HA" => n => n.ItemA.Handle,
            "LB" => n => n.ItemB.Layer, "HB" => n => n.ItemB.Handle,
            _ => n => n.Distance,
        };
        return _sortDesc ? _items.OrderByDescending(key) : _items.OrderBy(key);
    }

    private void Fill()
    {
        _list.Items.Clear();
        foreach (var n in Sorted().Take(MaxShown)) _list.Items.Add(Row(n));
        if (_items.Count == 0)
        {
            _info.Text = "Keine Punkte in diesem Bereich.";
            return;
        }
        double maxD = _items.Max(n => n.Distance);
        _info.Text = $"{_items.Count:N0} Punktpaare" +
                     (_items.Count > MaxShown ? $" (angezeigt: {MaxShown:N0})" : "") +
                     (_items.Count >= NearPointFinder.MaxResults ? " – Höchstzahl erreicht, Grenze verkleinern" : "") +
                     $", größter Abstand {NumberFormats.Full(maxD, De)} {_unit}";
    }

    private static FrameworkElement Row(NearPoint n)
    {
        string[] texts =
        {
            n.Nr.ToString(De),
            NumberFormats.Full(n.A.X, De), NumberFormats.Full(n.A.Y, De), NumberFormats.Full(n.B.X, De), NumberFormats.Full(n.B.Y, De),
            NumberFormats.Full(n.DX, De, signed: true), NumberFormats.Full(n.DY, De, signed: true),
            NumberFormats.Full(n.Distance, De),
            n.ItemA.Layer, n.ItemA.Handle, n.ItemB.Layer, n.ItemB.Handle,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = n };
        for (int i = 0; i < Columns.Length; i++)
            row.Children.Add(new TextBlock
            {
                Text = texts[i], Width = Columns[i].Width, Padding = new Thickness(4, 0, 4, 0),
                TextAlignment = Columns[i].Right ? TextAlignment.Right : TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        return row;
    }

    private IEnumerable<NearPoint> RowsForExport() =>
        _list.SelectedItems.Count > 1
            ? _list.SelectedItems.OfType<FrameworkElement>().Select(e => e.Tag).OfType<NearPoint>()
            : Sorted();

    private void Copy()
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(NearPointFinder.ToTabText(RowsForExport()));
            Clipboard.SetContent(dp);
            Clipboard.Flush();
            _info.Text = "In die Zwischenablage kopiert.";
        }
        catch (Exception ex)
        {
            _info.Text = "Kopieren fehlgeschlagen: " + ex.Message;
        }
    }

    private async Task SaveCsvAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_a.FilePath)}_vs_{Path.GetFileNameWithoutExtension(_b.FilePath)}_Punkte",
        };
        picker.FileTypeChoices.Add("CSV (Excel, Semikolon)", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, NearPointFinder.ToCsv(Sorted(), _unit), new UTF8Encoding(true));
            _info.Text = "Gespeichert: " + file.Path;
        }
        catch (Exception ex)
        {
            _info.Text = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }
}
