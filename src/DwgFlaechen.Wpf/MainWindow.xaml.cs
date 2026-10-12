using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DwgFlaechen.Core;
using DwgFlaechen.Core.Geometry;
using Microsoft.Win32;

namespace DwgFlaechen.Wpf;

public partial class MainWindow : Window
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private ExtractionResult? _result;
    private string? _currentFile;
    private List<AreaRow> _allRows = new();
    private List<LayerRow> _layers = new();
    private ICollectionView? _view;
    private HashSet<string> _activeLayers = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressLayerEvents;
    private bool _loading;

    // Vergleichsmodus (A = _result, B = zweite Zeichnung)
    public static readonly RoutedUICommand CompareCommand = new("Vergleichen", nameof(CompareCommand), typeof(MainWindow));
    private CompareResult? _compare;
    private string? _compareFile;
    private List<CompareRow> _cmpRows = new();
    private List<AreaRow> _cmpShapes = new();
    private ICollectionView? _cmpView;
    private bool IsComparing => _compare != null;

    public MainWindow(string? fileToOpen = null)
    {
        InitializeComponent();
        InitUnitBox();
        Preview.ItemClicked += Preview_ItemClicked;
        Preview.CornerPicked += Preview_CornerPicked;
        Preview.PinsChanged += UpdatePointText;
        Preview.CursorMoved += Preview_CursorMoved;
        if (fileToOpen != null)
            Loaded += async (_, _) => await LoadFileAsync(fileToOpen);
    }

    // =========================================================================== Einheit / Optionen

    private void InitUnitBox()
    {
        UnitBox.Items.Add(new ComboBoxItem { Content = "Automatisch (aus DWG)", Tag = null });
        foreach (var u in new[] { DrawingUnit.Millimeter, DrawingUnit.Zentimeter, DrawingUnit.Dezimeter,
                                  DrawingUnit.Meter, DrawingUnit.Zoll, DrawingUnit.Fuß })
            UnitBox.Items.Add(new ComboBoxItem { Content = $"{u} ({DrawingUnits.Symbol(u)})", Tag = u });
        UnitBox.SelectedIndex = 0;
    }

    private DrawingUnit CurrentUnit =>
        (UnitBox.SelectedItem as ComboBoxItem)?.Tag is DrawingUnit u ? u : _result?.Unit ?? DrawingUnit.Unbekannt;

    private ExtractionOptions CurrentOptions() => new()
    {
        IncludePolylines = ChkPolylines.IsChecked,
        IncludeHatches = ChkHatches.IsChecked,
        IncludeCircles = ChkCircles.IsChecked,
        IncludeEllipses = ChkEllipses.IsChecked,
        IncludeSolids = ChkSolids.IsChecked,
        IncludeSplines = ChkSplines.IsChecked,
        IncludeStoreplannerAreas = ChkStoreplanner.IsChecked,
        IncludeBlocks = ChkBlocks.IsChecked,
        OnlyVisibleLayers = ChkVisible.IsChecked,
        TreatCoincidentEndsAsClosed = ChkCoincident.IsChecked,
    };

    // =========================================================================== Laden

    private async Task LoadFileAsync(string path)
    {
        if (_loading) return;
        _loading = true;
        Busy.Visibility = Visibility.Visible;
        Cursor = Cursors.Wait;
        HideWarning();
        FileText.Text = "Lese " + Path.GetFileName(path) + " …";
        try
        {
            _result = await DwgAreaExtractor.ExtractAsync(path, CurrentOptions());
            _currentFile = path;
            _nearWin?.Close();
            _compare = null;
            _compareFile = null;
            _nearMax = 0;
            _ = RefreshPointMarksAsync();
            Title = $"DWG Flächen – {Path.GetFileName(path)}";
            RebuildAll(resetView: true);

            if (_result.Warnings.Count > 0)
                ShowWarning(string.Join("\n", _result.Warnings.Take(8)) +
                            (_result.Warnings.Count > 8 ? $"\n… und {_result.Warnings.Count - 8} weitere" : ""));
        }
        catch (Exception ex)
        {
            FileText.Text = "Fehler beim Lesen";
            ShowWarning("Datei konnte nicht gelesen werden: " + ex.Message);
        }
        finally
        {
            _loading = false;
            Busy.Visibility = Visibility.Collapsed;
            Cursor = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Tabellen und Vorschau neu aufbauen (nach Laden oder Einheitenwechsel).</summary>
    private void RebuildAll(bool resetView)
    {
        if (_result == null) return;
        SetCompareLayout(IsComparing);
        if (IsComparing) { RebuildCompare(resetView); return; }
        var unit = CurrentUnit;
        string u = DrawingUnits.Symbol(unit);

        // Sortierung der Tabelle merken
        var sorts = _view?.SortDescriptions.ToList() ?? new List<SortDescription>();

        _allRows = _result.Items.Select(i => new AreaRow(i, unit)).ToList();

        // Layer (Häkchen-Zustand beibehalten)
        var off = _layers.Where(l => !l.IsChecked).Select(l => l.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _layers) l.PropertyChanged -= Layer_PropertyChanged;
        _layers = _result.ByLayer().Select(s =>
        {
            var color = _allRows.First(r => string.Equals(r.Layer, s.Layer, StringComparison.OrdinalIgnoreCase)).Color;
            var row = new LayerRow(s, unit, color) { IsChecked = !off.Contains(s.Layer) };
            row.PropertyChanged += Layer_PropertyChanged;
            return row;
        }).ToList();
        LayerGrid.ItemsSource = _layers;
        UpdateActiveLayers();

        _view = CollectionViewSource.GetDefaultView(_allRows);
        _view.Filter = FilterRow;
        AreaGrid.ItemsSource = _view;
        foreach (var s in sorts) _view.SortDescriptions.Add(s); // nach ItemsSource → Sortierpfeile bleiben

        ColArea.Header = $"Fläche [{u}²]";
        ColLayerArea.Header = $"Fläche [{u}²]";
        ColPerimeter.Header = $"Umfang [{u}]";
        var labelVis = _allRows.Any(r => r.Label.Length > 0 || r.LabelArea != null) ? Visibility.Visible : Visibility.Collapsed;
        ColLabel.Visibility = labelVis;
        ColLabelArea.Visibility = labelVis;
        FileText.Text = $"{Path.GetFileName(_result.FilePath)}  ·  {_result.Version}  ·  Einheit in DWG: {_result.Unit}";
        EmptyState.Visibility = _allRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Preview.UnitSymbol = u;
        if (resetView) Preview.SetItems(_allRows, _result.Bounds);
        else Preview.SetItemsKeepView(_allRows);
        UpdatePointText();
        RefreshView();
    }

    // =========================================================================== Filter / Summen

    private bool FilterRow(object o)
    {
        if (o is not AreaRow r || !_activeLayers.Contains(r.Layer)) return false;
        string f = FilterBox.Text.Trim();
        if (f.Length == 0) return true;
        return r.Layer.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Block.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Kind.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Label.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Handle.Equals(f, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<AreaRow> VisibleRows => _view?.Cast<AreaRow>() ?? Enumerable.Empty<AreaRow>();

    private void RefreshView()
    {
        if (IsComparing)
        {
            _cmpView?.Refresh();
            var vis = VisibleCompareRows.ToHashSet();
            Preview.SetVisible(_cmpShapes.Where(sh => sh.Compare != null && vis.Contains(sh.Compare)));
            UpdateTotals();
            return;
        }
        _view?.Refresh();
        Preview.SetVisible(VisibleRows);
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        if (IsComparing) { UpdateCompareTotals(); return; }
        var unit = CurrentUnit;
        string u = DrawingUnits.Symbol(unit);
        var rows = VisibleRows.ToList();
        double sum = rows.Sum(r => r.Area);
        TotalText.Text = DrawingUnits.AreaToSquareMeters(sum, unit) is double m2
            ? $"Summe: {m2.ToString("N3", De)} m²"
            : $"Summe: {sum.ToString("N3", De)} {u}²";
        CountText.Text = $"{rows.Count} von {_allRows.Count} Flächen";

        var sel = AreaGrid.SelectedItems.OfType<AreaRow>().ToList();
        if (sel.Count > 1)
        {
            double s = sel.Sum(r => r.Area);
            SelectionText.Text = $"Auswahl ({sel.Count}): " +
                (DrawingUnits.AreaToSquareMeters(s, unit) is double sm ? $"{sm.ToString("N3", De)} m²" : $"{s.ToString("N3", De)} {u}²");
        }
        else SelectionText.Text = "";
    }

    private void UpdateActiveLayers() =>
        _activeLayers = _layers.Where(l => l.IsChecked).Select(l => l.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshView();

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressLayerEvents || e.PropertyName != nameof(LayerRow.IsChecked)) return;
        UpdateActiveLayers();
        RefreshView();
    }

    private void SetAllLayers(bool on)
    {
        _suppressLayerEvents = true;
        foreach (var l in _layers) l.IsChecked = on;
        _suppressLayerEvents = false;
        UpdateActiveLayers();
        RefreshView();
    }

    private void LayersAll_Click(object sender, RoutedEventArgs e) => SetAllLayers(true);
    private void LayersNone_Click(object sender, RoutedEventArgs e) => SetAllLayers(false);

    private void UnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_result != null) RebuildAll(resetView: false);
    }

    // =========================================================================== Auswahl

    private void AreaGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Preview.SetSelected(AreaGrid.SelectedItems.OfType<AreaRow>());
        UpdateTotals();
    }

    private void AreaGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AreaGrid.SelectedItem is AreaRow r) Preview.ZoomTo(r.Item.Bounds);
    }

    private void Preview_ItemClicked(AreaRow? row, bool ctrl)
    {
        if (IsComparing) { CompareItemClicked(row?.Compare, ctrl); return; }
        if (row == null)
        {
            if (!ctrl) AreaGrid.UnselectAll();
            return;
        }
        if (ctrl)
        {
            if (AreaGrid.SelectedItems.Contains(row)) AreaGrid.SelectedItems.Remove(row);
            else AreaGrid.SelectedItems.Add(row);
        }
        else
        {
            AreaGrid.SelectedItem = row;
        }
        AreaGrid.ScrollIntoView(row);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Preview.FitAll();

    // =========================================================================== Eckpunkte / Koordinaten

    /// <summary>Genaue Koordinate bzw. Differenz (3 bis 9 Nachkommastellen).</summary>
    private string Coord(double v) => NumberFormats.ExactText(v, De);
    private string Full(double v) => NumberFormats.Full(v, De);

    private void Preview_CornerPicked(P2 pt, AreaRow row)
    {
        UpdatePointText();
        FileText.Text = $"Eckpunkt von {row.Kind} (Handle {row.Handle}, Layer {row.Layer})" +
                        (row.Source.Length > 0 ? $" aus {row.Source}" : "");
    }

    private void UpdatePointText()
    {
        var pins = Preview.Pins;
        if (pins.Count == 0)
        {
            PointText.Visibility = Visibility.Collapsed;
            return;
        }
        string u = DrawingUnits.Symbol(CurrentUnit);
        P2 last = pins[^1];
        string text = (pins.Count > 1 ? $"Punkt {pins.Count}: " : "Eckpunkt: ") +
                      $"X = {Coord(last.X)}   Y = {Coord(last.Y)} {u}";
        if (pins.Count > 1)
        {
            P2 prev = pins[^2];
            double dx = last.X - prev.X, dy = last.Y - prev.Y;
            text += $"   ·   zu Punkt {pins.Count - 1}: ΔX = {Coord(dx)}   ΔY = {Coord(dy)}   Abstand = {Coord(Math.Sqrt(dx * dx + dy * dy))} {u}";
        }
        PointRun.Text = text;
        PointText.Visibility = Visibility.Visible;
    }

    private void Preview_CursorMoved(P2? p) =>
        CursorText.Text = p is P2 w
            ? $"X {NumberFormats.Cursor(w.X, Preview.Scale, De)}   Y {NumberFormats.Cursor(w.Y, Preview.Scale, De)}"
            : "";

    private void CopyPoints_Click(object sender, RoutedEventArgs e)
    {
        if (Preview.Pins.Count == 0)
        {
            FileText.Text = "Keine Eckpunkte markiert – in der Vorschau auf eine Ecke klicken";
            return;
        }
        FileText.Text = Preview.CopyPins()
            ? $"{Preview.Pins.Count} Eckpunkt(e) in die Zwischenablage kopiert"
            : "Zwischenablage nicht verfügbar";
    }

    private void ClearPoints_Click(object sender, RoutedEventArgs e) => Preview.ClearPins();

    private void ZoomSelection_Click(object sender, RoutedEventArgs e)
    {
        if (IsComparing)
        {
            var cb = CompareGrid.SelectedItems.OfType<CompareRow>().Aggregate(Bounds2.Empty, (acc, r) => acc.Union(r.Item.Bounds));
            if (!cb.IsEmpty) Preview.ZoomTo(cb);
            return;
        }
        var b = AreaGrid.SelectedItems.OfType<AreaRow>().Aggregate(Bounds2.Empty, (acc, r) => acc.Union(r.Item.Bounds));
        if (!b.IsEmpty) Preview.ZoomTo(b);
    }

    // =========================================================================== Befehle

    private void HasResult_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _result != null && !_loading;

    private void Open_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !_loading;

    private async void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "DWG- oder DXF-Datei öffnen",
            Filter = "AutoCAD-Zeichnungen (*.dwg;*.dxf)|*.dwg;*.dxf|DWG (*.dwg)|*.dwg|DXF (*.dxf)|*.dxf|Alle Dateien (*.*)|*.*",
        };
        if (_currentFile != null) dlg.InitialDirectory = Path.GetDirectoryName(_currentFile);
        if (dlg.ShowDialog(this) == true) await LoadFileAsync(dlg.FileName);
    }

    private async void Reload_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_currentFile == null) return;
        string? compareFile = _compareFile;
        await LoadFileAsync(_currentFile);
        if (compareFile != null && _result != null) await LoadCompareAsync(compareFile);
    }

    private async void Export_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_result == null) return;
        if (IsComparing) { await ExportCompareAsync(); return; }
        var dlg = new SaveFileDialog
        {
            Title = "Flächen als CSV speichern",
            Filter = "CSV für Excel (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(_result.FilePath) + "_Flaechen.csv",
            InitialDirectory = Path.GetDirectoryName(_result.FilePath),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await CsvExporter.WriteAsync(dlg.FileName, VisibleRows.Select(r => r.Item).ToList(), CurrentUnit);
            FileText.Text = "Exportiert: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            ShowWarning("Export fehlgeschlagen: " + ex.Message);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        if (IsComparing) { CopyCompare(); return; }
        var rows = AreaGrid.SelectedItems.Count > 1 ? AreaGrid.SelectedItems.OfType<AreaRow>().ToList() : VisibleRows.ToList();
        var unit = CurrentUnit;
        string u = DrawingUnits.Symbol(unit);
        var sb = new StringBuilder();
        sb.AppendLine($"Nr\tHandle\tTyp\tLayer\tBlock\tFläche [{u}²]\tFläche [m²]\tUmfang [{u}]\tFläche lt. Beschriftung [m²]\tBeschriftung");
        foreach (var r in rows)
            sb.AppendLine($"{r.Nr}\t{r.Handle}\t{r.Kind}\t{r.Layer}\t{r.Block}\t{r.Area.ToString("0.######", De)}\t" +
                          $"{r.AreaM2?.ToString("0.######", De)}\t{r.Perimeter.ToString("0.######", De)}\t" +
                          $"{r.LabelArea?.ToString("0.##", De)}\t{r.Label}");
        double sum = rows.Sum(r => r.Area);
        sb.AppendLine($"\t\t\t\tSumme\t{sum.ToString("0.######", De)}\t{DrawingUnits.AreaToSquareMeters(sum, unit)?.ToString("0.######", De)}\t");
        try
        {
            Clipboard.SetText(sb.ToString());
            FileText.Text = $"{rows.Count} Zeilen in die Zwischenablage kopiert";
        }
        catch (Exception ex)
        {
            ShowWarning("Zwischenablage nicht verfügbar: " + ex.Message);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // =========================================================================== Hinweise

    private void ShowWarning(string text)
    {
        WarningText.Text = text;
        WarningBar.Visibility = Visibility.Visible;
    }

    private void HideWarning() => WarningBar.Visibility = Visibility.Collapsed;
    private void CloseWarning_Click(object sender, RoutedEventArgs e) => HideWarning();

    // =========================================================================== Drag & Drop

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            : null;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (DroppedFile(e) is not string f) return;
        e.Handled = true;
        await LoadFileAsync(f);
    }

    // =========================================================================== Vergleich zweier Zeichnungen

    private IEnumerable<CompareRow> VisibleCompareRows => _cmpView?.Cast<CompareRow>() ?? Enumerable.Empty<CompareRow>();

    private async void Compare_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Zweite Zeichnung (B) zum Vergleich öffnen",
            Filter = "AutoCAD-Zeichnungen (*.dwg;*.dxf)|*.dwg;*.dxf|Alle Dateien (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_compareFile ?? _result.FilePath),
        };
        if (dlg.ShowDialog(this) == true) await LoadCompareAsync(dlg.FileName);
    }

    /// <summary>Zeichnung B laden und mit der geladenen Zeichnung A vergleichen.</summary>
    private async Task LoadCompareAsync(string pathB)
    {
        if (_loading || _result == null) return;
        _loading = true;
        Busy.Visibility = Visibility.Visible;
        Cursor = Cursors.Wait;
        HideWarning();
        FileText.Text = "Lese " + Path.GetFileName(pathB) + " und vergleiche …";
        try
        {
            var b = await DwgAreaExtractor.ExtractAsync(pathB, CurrentOptions());
            _compare = await DwgComparer.CompareAsync(_result, b);
            if (_compareFile != pathB) _nearMax = 0; // neue Zeichnung B: Vorgabegrenze
            _compareFile = pathB;
            _ = RefreshPointMarksAsync();
            Title = $"DWG Flächen – Vergleich {Path.GetFileName(_result.FilePath)} ↔ {Path.GetFileName(pathB)}";
            RebuildAll(resetView: true);

            var warnings = b.Warnings.Select(w => "B: " + w).Concat(_compare.Warnings).ToList();
            if (warnings.Count > 0)
                ShowWarning(string.Join("\n", warnings.Take(8)) + (warnings.Count > 8 ? $"\n… und {warnings.Count - 8} weitere" : ""));
        }
        catch (Exception ex)
        {
            FileText.Text = "Fehler beim Vergleich";
            ShowWarning("Zweite Zeichnung konnte nicht gelesen werden: " + ex.Message);
        }
        finally
        {
            _loading = false;
            Busy.Visibility = Visibility.Collapsed;
            Cursor = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void EndCompare_Click(object sender, RoutedEventArgs e)
    {
        if (!IsComparing || _result == null) return;
        _nearWin?.Close();
        _compare = null;
        _compareFile = null;
        _nearMax = 0;
        _ = RefreshPointMarksAsync();
        Title = $"DWG Flächen – {Path.GetFileName(_result.FilePath)}";
        HideWarning();
        RebuildAll(resetView: false);
    }

    private void SetCompareLayout(bool on)
    {
        CompareBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CompareGrid.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AreaGrid.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        EndCompareMenu.IsEnabled = on;
    }

    private void RebuildCompare(bool resetView)
    {
        if (_compare == null) return;
        var unit = CurrentUnit;
        string u = DrawingUnits.Symbol(unit);
        var sorts = _cmpView?.SortDescriptions.ToList() ?? new List<SortDescription>();

        _cmpRows = _compare.Items.Select(c => new CompareRow(c, unit)).ToList();

        // Vorschau: entfallene und alte Konturen aus A, alles andere aus B
        _cmpShapes = new List<AreaRow>();
        foreach (var r in _cmpRows)
        {
            var c = r.Item;
            switch (c.Status)
            {
                case CompareStatus.Neu:
                    _cmpShapes.Add(new AreaRow(c.B!, unit, r, "B", CompareColors.New, outlineOnly: false)); break;
                case CompareStatus.Entfallen:
                    _cmpShapes.Add(new AreaRow(c.A!, unit, r, "A", CompareColors.Removed, outlineOnly: false)); break;
                case CompareStatus.Geändert:
                    _cmpShapes.Add(new AreaRow(c.B!, unit, r, "B", CompareColors.Changed, outlineOnly: false));
                    _cmpShapes.Add(new AreaRow(c.A!, unit, r, "A", CompareColors.Removed, outlineOnly: true));
                    break;
                default:
                    _cmpShapes.Add(new AreaRow(c.B!, unit, r, "B", CompareColors.Unchanged, outlineOnly: false)); break;
            }
        }

        // Layer: alle Layer beider Zeichnungen, Fläche = Stand B
        var off = _layers.Where(l => !l.IsChecked).Select(l => l.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _layers) l.PropertyChanged -= Layer_PropertyChanged;
        _layers = _cmpRows
            .GroupBy(r => r.Layer, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var sum = new LayerSummary { Layer = g.Key, Count = g.Count(), Area = g.Sum(r => r.AreaB ?? 0) };
                var row = new LayerRow(sum, unit, g.First().Color) { IsChecked = !off.Contains(g.Key) };
                row.PropertyChanged += Layer_PropertyChanged;
                return row;
            }).ToList();
        LayerGrid.ItemsSource = _layers;
        UpdateActiveLayers();

        _cmpView = CollectionViewSource.GetDefaultView(_cmpRows);
        _cmpView.Filter = FilterCompareRow;
        CompareGrid.ItemsSource = _cmpView;
        foreach (var sd in sorts) _cmpView.SortDescriptions.Add(sd);

        ColCmpA.Header = $"Fläche A [{u}²]";
        ColCmpB.Header = $"Fläche B [{u}²]";
        ColCmpDelta.Header = $"Δ [{u}²]";
        ColLayerArea.Header = $"Fläche B [{u}²]";
        CompareFilesText.Text = $"A: {Path.GetFileName(_compare.A.FilePath)}   →   B: {Path.GetFileName(_compare.B.FilePath)}";
        CompareFilesText.ToolTip = $"A: {_compare.A.FilePath}\nB: {_compare.B.FilePath}";
        CmpChangedText.Text = $"Geändert ({_compare.Count(CompareStatus.Geändert)})";
        CmpNewText.Text = $"Neu ({_compare.Count(CompareStatus.Neu)})";
        CmpRemovedText.Text = $"Entfallen ({_compare.Count(CompareStatus.Entfallen)})";
        CmpUnchangedText.Text = $"Unverändert ({_compare.Count(CompareStatus.Unverändert)})";
        FileText.Text = $"Vergleich  ·  A: {_compare.A.Items.Count} Flächen  ·  B: {_compare.B.Items.Count} Flächen  ·  Toleranz {_compare.Tolerance:G3} {u}";
        EmptyState.Visibility = _cmpShapes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Preview.UnitSymbol = u;
        if (resetView) Preview.SetItems(_cmpShapes, _compare.Bounds);
        else Preview.SetItemsKeepView(_cmpShapes);
        UpdatePointText();
        RefreshView();
    }

    private bool FilterCompareRow(object o)
    {
        if (o is not CompareRow r || !_activeLayers.Contains(r.Layer)) return false;
        bool statusOn = r.Item.Status switch
        {
            CompareStatus.Neu => ChkCmpNew.IsChecked == true,
            CompareStatus.Entfallen => ChkCmpRemoved.IsChecked == true,
            CompareStatus.Geändert => ChkCmpChanged.IsChecked == true,
            _ => ChkCmpUnchanged.IsChecked == true,
        };
        if (!statusOn) return false;
        string f = FilterBox.Text.Trim();
        if (f.Length == 0) return true;
        return r.Layer.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Kind.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Status.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Details.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               r.Label.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
               string.Equals(r.Item.A?.Handle, f, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(r.Item.B?.Handle, f, StringComparison.OrdinalIgnoreCase);
    }

    private void CompareFilter_Click(object sender, RoutedEventArgs e) => RefreshView();

    private string AreaText(double area, DrawingUnit unit, bool signed = false)
    {
        string fmt = signed ? NumberFormats.SignedArea : "N3";
        return DrawingUnits.AreaToSquareMeters(area, unit) is double m2
            ? m2.ToString(fmt, De) + " m²"
            : area.ToString(fmt, De) + " " + DrawingUnits.Symbol(unit) + "²";
    }

    private void UpdateCompareTotals()
    {
        var unit = CurrentUnit;
        var rows = VisibleCompareRows.ToList();
        double a = rows.Sum(r => r.Item.AreaA), b = rows.Sum(r => r.Item.AreaB);
        TotalText.Text = $"Δ {AreaText(b - a, unit, signed: true)}";
        CountText.Text = $"{rows.Count} von {_cmpRows.Count} Einträgen";

        var sel = CompareGrid.SelectedItems.OfType<CompareRow>().ToList();
        if (sel.Count > 0)
        {
            double sa = sel.Sum(r => r.Item.AreaA), sb = sel.Sum(r => r.Item.AreaB);
            SelectionText.Text = $"Auswahl ({sel.Count}): A {AreaText(sa, unit)} · B {AreaText(sb, unit)} · Δ {AreaText(sb - sa, unit, true)}";
        }
        else
        {
            SelectionText.Text = $"A {AreaText(a, unit)} · B {AreaText(b, unit)}";
        }
    }

    private void CompareGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = CompareGrid.SelectedItems.OfType<CompareRow>().ToHashSet();
        Preview.SetSelected(_cmpShapes.Where(sh => sh.Compare != null && sel.Contains(sh.Compare)));
        UpdateDeviationMarker();
        UpdateTotals();
    }

    /// <summary>Doppelklick: zur Stelle der größten Abweichung zoomen (ohne Abweichung: auf die ganze Fläche).</summary>
    private void CompareGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompareGrid.SelectedItem is not CompareRow r) return;
        if (r.Item.DeviationView is Bounds2 v) Preview.ZoomTo(v);
        else Preview.ZoomTo(r.Item.Bounds);
    }

    private NearPointsWindow? _nearWin;

    // ---------- Eckpunkt-Markierung im Vergleich (grün = gleich, rot = geringfügig abweichend) ----------

    /// <summary>Grenze für „geringfügig abweichend“ – dieselbe wie im Fenster „Abweichende Punkte“ (0 = Vorgabe).</summary>
    private double _nearMax;
    private PointMarks? _pointMarks;
    private int _pointMarksVersion;

    private double NearMax => _nearMax > 0 ? _nearMax : _compare != null ? NearPointFinder.SuggestMax(_compare.Tolerance) : 0;

    private void CmpPoints_Click(object sender, RoutedEventArgs e) => _ = RefreshPointMarksAsync();

    /// <summary>Grenze aus dem Fenster „Abweichende Punkte“ übernehmen.</summary>
    private void NearMaxChanged(double max)
    {
        if (max == _nearMax) return;
        _nearMax = max;
        _ = RefreshPointMarksAsync();
    }

    /// <summary>Markierung neu berechnen (im Hintergrund) bzw. entfernen, wenn kein Vergleich läuft oder das Häkchen fehlt.</summary>
    private async Task RefreshPointMarksAsync()
    {
        int version = ++_pointMarksVersion;
        if (_compare == null || ChkCmpPoints.IsChecked != true)
        {
            _pointMarks = null;
            CmpPointsText.Text = "Eckpunkte markieren";
            Preview.SetPointMarks(_pointMarks);
            return;
        }
        var (a, b, max) = (_compare.A, _compare.B, NearMax);
        CmpPointsText.Text = "Eckpunkte markieren (berechne …)";
        PointMarks marks;
        try { marks = await Task.Run(() => NearPointFinder.Classify(a, b, max)); }
        catch (Exception ex) { CmpPointsText.Text = "Eckpunkte markieren (Fehler: " + ex.Message + ")"; return; }
        if (version != _pointMarksVersion) return; // inzwischen neu angestoßen
        _pointMarks = marks;
        CmpPointsText.Text = $"Eckpunkte: {marks.Equal.Count:N0} gleich, {marks.Near.Count:N0} abweichend (≤ {NumberFormats.Full(max, De)})";
        Preview.SetPointMarks(_pointMarks);
    }

    /// <summary>Fenster mit den Eckpunkten, die in A und B nur wenig voneinander abweichen.</summary>
    private void NearPoints_Click(object sender, RoutedEventArgs e)
    {
        if (_compare == null) return;
        _nearWin?.Close();
        _nearWin = new NearPointsWindow(_compare.A, _compare.B, NearMax,
                                        DrawingUnits.Symbol(CurrentUnit), ShowNearPoint, NearMaxChanged) { Owner = this };
        _nearWin.Closed += (_, _) => _nearWin = null;
        _nearWin.Show();
    }

    /// <summary>Punktpaar aus der Liste in der Vorschau zeigen: hinzoomen und markieren (Ring = Punkt aus A).</summary>
    private void ShowNearPoint(NearPoint n)
    {
        string u = DrawingUnits.Symbol(CurrentUnit);
        Preview.ZoomTo(n.View);
        Preview.SetMarker(n.A, n.B,
            $"A: X = {Full(n.A.X)}   Y = {Full(n.A.Y)}\nB: X = {Full(n.B.X)}   Y = {Full(n.B.Y)}\nAbstand {Full(n.Distance)} {u}");
    }

    /// <summary>Bei genau einer ausgewählten geänderten Fläche die Stelle der größten Abweichung markieren.</summary>
    private void UpdateDeviationMarker()
    {
        if (CompareGrid.SelectedItems.Count == 1 && CompareGrid.SelectedItem is CompareRow r &&
            r.Item.Deviation is double d && r.Item.DeviationFrom is P2 f && r.Item.DeviationTo is P2 t)
        {
            string u = DrawingUnits.Symbol(CurrentUnit);
            Preview.SetMarker(f, t, $"max. Abweichung {Coord(d)} {u}\nEckpunkt aus {(r.Item.DeviationFromA ? "A" : "B")}");
        }
        else
        {
            Preview.ClearMarker();
        }
    }

    private void CompareItemClicked(CompareRow? row, bool ctrl)
    {
        if (row == null)
        {
            if (!ctrl) CompareGrid.UnselectAll();
            return;
        }
        if (ctrl)
        {
            if (CompareGrid.SelectedItems.Contains(row)) CompareGrid.SelectedItems.Remove(row);
            else CompareGrid.SelectedItems.Add(row);
        }
        else
        {
            CompareGrid.SelectedItem = row;
        }
        CompareGrid.ScrollIntoView(row);
    }

    private async Task ExportCompareAsync()
    {
        if (_compare == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Vergleich als CSV speichern",
            Filter = "CSV für Excel (*.csv)|*.csv",
            FileName = $"{Path.GetFileNameWithoutExtension(_compare.A.FilePath)}_vs_{Path.GetFileNameWithoutExtension(_compare.B.FilePath)}.csv",
            InitialDirectory = Path.GetDirectoryName(_compare.A.FilePath),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await DwgComparer.WriteCsvAsync(dlg.FileName, _compare, VisibleCompareRows.Select(r => r.Item).ToList(), CurrentUnit);
            FileText.Text = "Exportiert: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            ShowWarning("Export fehlgeschlagen: " + ex.Message);
        }
    }

    private void CopyCompare()
    {
        if (_compare == null) return;
        var rows = CompareGrid.SelectedItems.Count > 1 ? CompareGrid.SelectedItems.OfType<CompareRow>() : VisibleCompareRows;
        // gleiche Spalten wie der CSV-Export, aber mit Tabulator (direkt in Excel einfügbar)
        string csv = DwgComparer.ToCsv(_compare, rows.Select(r => r.Item).ToList(), CurrentUnit);
        string text = string.Join("\n", csv.Split('\n').Select(l => l.TrimEnd('\r').Replace(';', '\t')));
        try
        {
            Clipboard.SetText(text);
            FileText.Text = "Vergleich in die Zwischenablage kopiert";
        }
        catch (Exception ex)
        {
            ShowWarning("Zwischenablage nicht verfügbar: " + ex.Message);
        }
    }
}
