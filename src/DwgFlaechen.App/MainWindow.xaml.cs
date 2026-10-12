using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using DwgFlaechen.Core;
using DwgFlaechen.Core.Geometry;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using XEllipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using XPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DwgFlaechen.App;

public sealed partial class MainWindow : Window
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Color SelFill = Color.FromArgb(255, 255, 140, 0);
    private static readonly Color SelStroke = Color.FromArgb(255, 255, 69, 0);
    private static readonly Color HoverColor = Color.FromArgb(255, 0, 120, 215);
    private static readonly Color PinColor = Color.FromArgb(255, 200, 0, 0);

    /// <summary>Fangradius für Eckpunkte in Bildschirmpixeln.</summary>
    private const double SnapPx = 7;

    private ObservableCollection<AreaRow> _rows = new();
    private readonly ObservableCollection<LayerRow> _layers = new();
    private readonly Dictionary<AreaItem, XPath> _paths = new();
    /// <summary>Vorberechnete Geometrie je Pfad (Koordinaten relativ zu _origin) für die normale Ansicht.</summary>
    private readonly Dictionary<XPath, PathGeometry> _localGeo = new();
    /// <summary>Starke Vergrößerung: Pfade liegen in Bildschirmkoordinaten (siehe ScreenClip).</summary>
    private bool _precise;
    /// <summary>Markierung der größten Abweichung im Vergleich.</summary>
    private (P2 From, P2 To, string Text)? _marker;
    private static readonly Color MarkerColor = Color.FromArgb(255, 200, 0, 200);
    private static readonly Color EqualColor = Color.FromArgb(255, 0, 160, 60);
    private static readonly Color NearColor = Color.FromArgb(255, 220, 40, 40);
    private const int MaxPointMarks = 5_000;
    private Bounds2 _sceneBounds = Bounds2.Empty;

    private ExtractionResult? _result;
    private List<AreaRow> _allRows = new();
    private string? _currentFile;
    private string _sortKey = "Nr";
    private bool _sortDesc;
    private bool _suppressLayerEvents;
    private bool _loading;

    // Eckpunkte: markierte Punkte (Weltkoordinaten, Klickreihenfolge) und Punkt unter dem Mauszeiger
    private readonly List<P2> _pins = new();
    private P2? _hoverCorner;

    // Vergleichsmodus (A = _result, B = zweite Zeichnung)
    private CompareResult? _compare;
    private string? _compareFile;
    private List<CompareRow> _cmpAll = new();
    private ObservableCollection<CompareRow> _cmpRows = new();
    private List<AreaRow> _cmpShapes = new();
    private List<AreaRow> _cmpVisibleShapes = new();
    private string _cmpSortKey = "Nr";
    private bool _cmpSortDesc;
    private bool IsComparing => _compare != null;

    /// <summary>Flächen, die in der Vorschau angeklickt werden können (Normal: sichtbare Zeilen, Vergleich: sichtbare Formen).</summary>
    private IEnumerable<AreaRow> PickRows => IsComparing ? _cmpVisibleShapes : _rows;

    // Vorschau: Bildschirm = Scale * (Welt − Origin) mit gespiegelter Y-Achse + Translation
    private P2 _origin;
    private double _scale = 1, _tx, _ty;
    private bool _panning, _leftPress;
    private Point _panStart, _pressPoint;
    private double _panTx, _panTy;

    public MainWindow(string? fileToOpen = null)
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1500, 920));
        Closed += (_, _) => _nearWin?.Close(); // Punktliste nicht offen lassen

        AreaList.ItemsSource = _rows;
        LayerList.ItemsSource = _layers;
        InitUnitBox();

        // Esc / Strg+C auch dann, wenn ein Steuerelement die Taste schon verarbeitet hat
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), handledEventsToo: true);

        if (fileToOpen != null)
            RootGrid.Loaded += async (_, _) => await LoadFileAsync(fileToOpen);
    }

    // =========================================================================== Laden

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
        IncludePolylines = ChkPolylines.IsChecked == true,
        IncludeHatches = ChkHatches.IsChecked == true,
        IncludeCircles = ChkCircles.IsChecked == true,
        IncludeEllipses = ChkEllipses.IsChecked == true,
        IncludeSolids = ChkSolids.IsChecked == true,
        IncludeSplines = ChkSplines.IsChecked == true,
        IncludeStoreplannerAreas = ChkStoreplanner.IsChecked == true,
        IncludeBlocks = ChkBlocks.IsChecked == true,
        OnlyVisibleLayers = ChkVisible.IsChecked == true,
        TreatCoincidentEndsAsClosed = ChkCoincident.IsChecked == true,
    };

    private async Task LoadFileAsync(string path)
    {
        if (_loading) return;
        _loading = true;
        Busy.IsActive = true;
        WarningBar.IsOpen = false;
        WarningBar.Severity = InfoBarSeverity.Warning;
        WarningBar.Title = "Hinweise";
        FileText.Text = "Lese " + Path.GetFileName(path) + " …";
        try
        {
            var opt = CurrentOptions();
            _result = await DwgAreaExtractor.ExtractAsync(path, opt);
            _currentFile = path;
            _nearWin?.Close();
            _compare = null;
            _compareFile = null;
            _nearMax = 0;
            _ = RefreshPointMarksAsync();
            Title = $"DWG Flächen – {Path.GetFileName(path)}";
            ReloadButton.IsEnabled = ExportButton.IsEnabled = CopyButton.IsEnabled = CompareButton.IsEnabled = true;
            ClearPins();
            RebuildAll(fit: true);

            if (_result.Warnings.Count > 0)
            {
                WarningBar.Message = string.Join("\n", _result.Warnings.Take(8)) +
                                     (_result.Warnings.Count > 8 ? $"\n… und {_result.Warnings.Count - 8} weitere" : "");
                WarningBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            FileText.Text = "Fehler beim Lesen";
            WarningBar.Severity = InfoBarSeverity.Error;
            WarningBar.Title = "Datei konnte nicht gelesen werden";
            WarningBar.Message = ex.Message;
            WarningBar.IsOpen = true;
            return;
        }
        finally
        {
            Busy.IsActive = false;
            _loading = false;
        }
    }

    /// <summary>Tabelle, Layerliste und Vorschau neu aufbauen (z. B. nach Laden oder Einheitenwechsel).</summary>
    private void RebuildAll(bool fit)
    {
        if (_result == null) return;
        SetCompareLayout(IsComparing);
        if (IsComparing) { RebuildCompare(fit); return; }
        var unit = CurrentUnit;
        bool showLabels = _result.Items.Any(i => i.Label.Length > 0 || i.LabelArea != null);
        _allRows = _result.Items.Select(i => new AreaRow(i, unit, showLabels)).ToList();
        HeaderLabelAreaCol.Width = showLabels ? new GridLength(105) : new GridLength(0);
        HeaderLabelCol.Width = showLabels ? new GridLength(16, GridUnitType.Star) : new GridLength(0);

        // Layerliste (Häkchen-Zustand beibehalten)
        var unchecked_ = _layers.Where(l => !l.IsChecked).Select(l => l.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _layers) l.PropertyChanged -= Layer_PropertyChanged;
        _layers.Clear();
        foreach (var s in _result.ByLayer())
        {
            var first = _allRows.First(r => string.Equals(r.Layer, s.Layer, StringComparison.OrdinalIgnoreCase));
            var row = new LayerRow(s, unit, first.Color) { IsChecked = !unchecked_.Contains(s.Layer) };
            row.PropertyChanged += Layer_PropertyChanged;
            _layers.Add(row);
        }

        string u = DrawingUnits.Symbol(unit);
        AreaHeader.Text = $"Fläche [{u}²]";
        PerimeterHeader.Text = $"Umfang [{u}]";
        FileText.Text = $"{Path.GetFileName(_result.FilePath)}  ·  {_result.Version}  ·  Einheit in DWG: {_result.Unit}";

        BuildScene(_allRows);
        ApplyFilter();
        if (fit) FitView();
        UpdatePointText();
        UpdateOverlay();
    }

    // =========================================================================== Filter / Sortierung / Summen

    private void ApplyFilter()
    {
        if (IsComparing) { ApplyCompareFilter(); return; }
        var active = _layers.Where(l => l.IsChecked).Select(l => l.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string f = FilterBox.Text.Trim();

        IEnumerable<AreaRow> q = _allRows.Where(r => active.Contains(r.Layer));
        if (f.Length > 0)
            q = q.Where(r => r.Layer.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Block.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.KindText.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Label.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Item.Handle.Equals(f, StringComparison.OrdinalIgnoreCase));

        q = _sortKey switch
        {
            "Kind" => _sortDesc ? q.OrderByDescending(r => r.KindText) : q.OrderBy(r => r.KindText),
            "Layer" => _sortDesc ? q.OrderByDescending(r => r.Layer, StringComparer.CurrentCultureIgnoreCase) : q.OrderBy(r => r.Layer, StringComparer.CurrentCultureIgnoreCase),
            "Block" => _sortDesc ? q.OrderByDescending(r => r.Block, StringComparer.CurrentCultureIgnoreCase) : q.OrderBy(r => r.Block, StringComparer.CurrentCultureIgnoreCase),
            "Area" => _sortDesc ? q.OrderByDescending(r => r.Item.Area) : q.OrderBy(r => r.Item.Area),
            "Perimeter" => _sortDesc ? q.OrderByDescending(r => r.Item.Perimeter) : q.OrderBy(r => r.Item.Perimeter),
            "LabelArea" => _sortDesc ? q.OrderByDescending(r => r.Item.LabelArea ?? double.MinValue) : q.OrderBy(r => r.Item.LabelArea ?? double.MaxValue),
            "Label" => _sortDesc ? q.OrderByDescending(r => r.Label, StringComparer.CurrentCultureIgnoreCase) : q.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase),
            _ => _sortDesc ? q.OrderByDescending(r => r.Item.Nr) : q.OrderBy(r => r.Item.Nr),
        };

        var list = q.ToList();
        foreach (var r in AreaList.SelectedItems.OfType<AreaRow>().ToList()) Highlight(r, false);
        _rows = new ObservableCollection<AreaRow>(list);
        AreaList.ItemsSource = _rows;

        // Vorschau: nur sichtbare Einträge zeichnen
        var visible = list.Select(r => r.Item).ToHashSet();
        foreach (var (item, path) in _paths)
            path.Visibility = visible.Contains(item) ? Visibility.Visible : Visibility.Collapsed;

        UpdateTotals();
        if (_hoverCorner != null) { _hoverCorner = null; UpdateOverlay(); }
    }

    private void UpdateTotals()
    {
        if (IsComparing) { UpdateCompareTotals(); return; }
        var unit = CurrentUnit;
        string u = DrawingUnits.Symbol(unit);
        double sum = _rows.Sum(r => r.Item.Area);
        double? m2 = DrawingUnits.AreaToSquareMeters(sum, unit);
        TotalText.Text = m2 is double v
            ? $"Summe: {v.ToString("N3", De)} m²"
            : $"Summe: {sum.ToString("N3", De)} {u}²";
        CountText.Text = $"{_rows.Count} von {_allRows.Count} Flächen";

        var sel = AreaList.SelectedItems.OfType<AreaRow>().ToList();
        if (sel.Count > 1)
        {
            double s = sel.Sum(r => r.Item.Area);
            SelectionText.Text = $"Auswahl ({sel.Count}): " +
                (DrawingUnits.AreaToSquareMeters(s, unit) is double sm ? $"{sm.ToString("N3", De)} m²" : $"{s.ToString("N3", De)} {u}²");
        }
        else SelectionText.Text = "";
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Header_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key is "Area" or "Perimeter" or "LabelArea"; }
        ApplyFilter();
    }

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressLayerEvents && e.PropertyName == nameof(LayerRow.IsChecked)) ApplyFilter();
    }

    private void SetAllLayers(bool on)
    {
        _suppressLayerEvents = true;
        foreach (var l in _layers) l.IsChecked = on;
        _suppressLayerEvents = false;
        ApplyFilter();
    }

    private void LayersAll_Click(object sender, RoutedEventArgs e) => SetAllLayers(true);
    private void LayersNone_Click(object sender, RoutedEventArgs e) => SetAllLayers(false);

    private void UnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildAll(fit: false);

    // =========================================================================== Auswahl

    private void AreaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var r in e.RemovedItems.OfType<AreaRow>()) Highlight(r, false);
        foreach (var r in e.AddedItems.OfType<AreaRow>()) Highlight(r, true);
        UpdateTotals();
    }

    private void AreaList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (AreaList.SelectedItem is AreaRow r) ZoomTo(r.Item.Bounds);
    }

    // =========================================================================== Befehle

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".dwg");
        picker.FileTypeFilter.Add(".dxf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null) await LoadFileAsync(file.Path);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void ApplyOptions_Click(object sender, RoutedEventArgs e)
    {
        OptionsFlyout.Hide();
        await ReloadAsync();
    }

    /// <summary>A neu laden; im Vergleichsmodus auch B neu laden und neu vergleichen.</summary>
    private async Task ReloadAsync()
    {
        if (_currentFile == null) return;
        string? compareFile = _compareFile;
        await LoadFileAsync(_currentFile);
        if (compareFile != null && _result != null) await LoadCompareAsync(compareFile);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        if (IsComparing) { await ExportCompareAsync(); return; }
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_result.FilePath) + "_Flaechen",
        };
        picker.FileTypeChoices.Add("CSV (Excel, Semikolon)", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await CsvExporter.WriteAsync(file.Path, _rows.Select(r => r.Item), CurrentUnit);
            FileText.Text = "Exportiert: " + file.Path;
        }
        catch (Exception ex)
        {
            WarningBar.Severity = InfoBarSeverity.Error;
            WarningBar.Title = "Export fehlgeschlagen";
            WarningBar.Message = ex.Message;
            WarningBar.IsOpen = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (IsComparing) { CopyCompare(); return; }
        var rows = AreaList.SelectedItems.Count > 1 ? AreaList.SelectedItems.OfType<AreaRow>().ToList() : _rows.ToList();
        var unit = CurrentUnit;
        string u = DrawingUnits.Symbol(unit);
        var sb = new StringBuilder();
        sb.AppendLine($"Nr\tHandle\tTyp\tLayer\tBlock\tFläche [{u}²]\tFläche [m²]\tUmfang [{u}]\tFläche lt. Beschriftung [m²]\tBeschriftung");
        foreach (var r in rows)
            sb.AppendLine($"{r.NrText}\t{r.Item.Handle}\t{r.KindText}\t{r.Layer}\t{r.Block}\t" +
                          $"{r.Item.Area.ToString("0.######", De)}\t{r.AreaM2?.ToString("0.######", De)}\t{r.Item.Perimeter.ToString("0.######", De)}\t" +
                          $"{r.Item.LabelArea?.ToString("0.##", De)}\t{r.Label}");
        double sum = rows.Sum(r => r.Item.Area);
        sb.AppendLine($"\t\t\t\tSumme\t{sum.ToString("0.######", De)}\t{DrawingUnits.AreaToSquareMeters(sum, unit)?.ToString("0.######", De)}\t");

        var dp = new DataPackage();
        dp.SetText(sb.ToString());
        Clipboard.SetContent(dp);
        Clipboard.Flush();
        FileText.Text = $"{rows.Count} Zeilen in die Zwischenablage kopiert";
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitView();

    private void ZoomSelection_Click(object sender, RoutedEventArgs e)
    {
        if (IsComparing)
        {
            var cb = CompareList.SelectedItems.OfType<CompareRow>().Aggregate(Bounds2.Empty, (acc, r) => acc.Union(r.Item.Bounds));
            if (!cb.IsEmpty) ZoomTo(cb);
            return;
        }
        var b = AreaList.SelectedItems.OfType<AreaRow>().Aggregate(Bounds2.Empty, (acc, r) => acc.Union(r.Item.Bounds));
        if (!b.IsEmpty) ZoomTo(b);
    }

    // =========================================================================== Drag & Drop

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "DWG öffnen";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        string? path = null;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            path = items.OfType<StorageFile>().FirstOrDefault(f =>
                f.FileType.Equals(".dwg", StringComparison.OrdinalIgnoreCase) ||
                f.FileType.Equals(".dxf", StringComparison.OrdinalIgnoreCase))?.Path;
        }
        finally
        {
            deferral.Complete();
        }
        if (path != null) await LoadFileAsync(path);
    }

    // =========================================================================== Vorschau

    private void BuildScene(IReadOnlyList<AreaRow> shapes)
    {
        Scene.Children.Clear();
        _paths.Clear();
        _localGeo.Clear();
        _precise = false;
        _marker = null;
        _sceneBounds = Bounds2.Empty;
        EmptyState.Visibility = shapes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var bounds = _compare?.Bounds ?? _result?.Bounds ?? Bounds2.Empty;
        if (bounds.IsEmpty) return;

        // Koordinaten relativ zum Zeichnungsursprung (vermeidet Float-Ungenauigkeit bei großen Koordinaten)
        _origin = new P2(bounds.MinX, bounds.MinY);
        _sceneBounds = bounds;

        // Große Flächen zuerst zeichnen, damit kleine obenauf liegen; reine Umrisse zuletzt (obenauf)
        foreach (var row in shapes.OrderBy(r => r.OutlineOnly).ThenByDescending(r => r.Item.Area))
        {
            var geo = new PathGeometry { FillRule = FillRule.EvenOdd };
            foreach (var o in row.Item.Outlines)
            {
                if (o.Points.Count < 2) continue;
                var fig = new PathFigure { StartPoint = ToLocal(o.Points[0]), IsClosed = true, IsFilled = true };
                var seg = new PolyLineSegment();
                for (int i = 1; i < o.Points.Count; i++) seg.Points.Add(ToLocal(o.Points[i]));
                fig.Segments.Add(seg);
                geo.Figures.Add(fig);
            }
            var path = new XPath
            {
                Data = geo,
                Fill = row.OutlineOnly ? null : new SolidColorBrush(RowColors.WithAlpha(row.Color, 0x40)),
                Stroke = new SolidColorBrush(row.Color),
                IsHitTestVisible = false,
            };
            if (row.OutlineOnly) path.StrokeDashArray = new DoubleCollection { 4, 3 };
            _paths[row.Item] = path;
            _localGeo[path] = geo;
            Scene.Children.Add(path);
        }
        ApplyTransform();
        UpdateStrokes();
    }

    private Point ToLocal(P2 p) => new(p.X - _origin.X, p.Y - _origin.Y);

    private void Highlight(AreaRow row, bool on)
    {
        if (!_paths.TryGetValue(row.Item, out var path)) return;
        path.Fill = row.OutlineOnly ? null : new SolidColorBrush(RowColors.WithAlpha(on ? SelFill : row.Color, on ? (byte)0x90 : (byte)0x40));
        path.Stroke = new SolidColorBrush(on ? SelStroke : row.Color);
        path.Tag = on ? "sel" : null;
        // ausgewählte Fläche nach oben holen
        if (on) { Scene.Children.Remove(path); Scene.Children.Add(path); }
        UpdateStroke(path);
    }

    private void UpdateStrokes()
    {
        foreach (var p in _paths.Values) UpdateStroke(p);
    }

    private void UpdateStroke(XPath p) =>
        p.StrokeThickness = (p.Tag is "sel" ? 2.5 : p.StrokeDashArray?.Count > 0 ? 1.5 : 1.0) / (_precise ? 1.0 : _scale);

    private void ApplyTransform()
    {
        // Starke Vergrößerung: WinUI zeichnet mit float → Konturen in double in Bildschirmkoordinaten
        // umrechnen und zuschneiden, statt eine riesige Transformation zu übergeben.
        bool precise = ScreenClip.NeedsPrecise(_scale, _sceneBounds);
        if (precise)
        {
            SceneTransform.ScaleX = 1;
            SceneTransform.ScaleY = 1;
            SceneTransform.TranslateX = 0;
            SceneTransform.TranslateY = 0;
            var view = new ViewTransform(_scale, _tx, _ty, _origin);
            var clip = ScreenClip.ClipRect(PreviewHost.ActualWidth, PreviewHost.ActualHeight);
            foreach (var (item, path) in _paths)
                path.Data = ScreenGeometry(item, view, clip);
        }
        else
        {
            if (_precise)
                foreach (var (path, geo) in _localGeo) path.Data = geo;
            SceneTransform.ScaleX = _scale;
            SceneTransform.ScaleY = -_scale;
            SceneTransform.TranslateX = _tx;
            SceneTransform.TranslateY = _ty;
        }
        if (precise != _precise)
        {
            _precise = precise;
            UpdateStrokes();
        }
        UpdateOverlay();
    }

    /// <summary>Kontur in Bildschirmkoordinaten, auf den sichtbaren Bereich zugeschnitten (null = nicht sichtbar).</summary>
    private static PathGeometry? ScreenGeometry(AreaItem item, ViewTransform view, Bounds2 clip)
    {
        var sb = view.ToScreen(item.Bounds);
        if (sb.MaxX < clip.MinX || sb.MinX > clip.MaxX || sb.MaxY < clip.MinY || sb.MinY > clip.MaxY) return null;
        var loops = ScreenClip.Clip(item.Outlines.Select(o => o.Points), view, clip);
        if (loops.Count == 0) return null;
        var geo = new PathGeometry { FillRule = FillRule.EvenOdd };
        foreach (var l in loops)
        {
            var fig = new PathFigure { StartPoint = new Point(l[0].X, l[0].Y), IsClosed = true, IsFilled = true };
            var seg = new PolyLineSegment();
            for (int i = 1; i < l.Count; i++) seg.Points.Add(new Point(l[i].X, l[i].Y));
            fig.Segments.Add(seg);
            geo.Figures.Add(fig);
        }
        return geo;
    }

    private void FitView()
    {
        if (_result == null || _result.Bounds.IsEmpty) return;
        var all = _compare?.Bounds ?? _result.Bounds;
        var pick = PickRows.ToList();
        var visible = pick.Count > 0 ? pick.Select(r => r.Item.Bounds).Aggregate(Bounds2.Empty, (a, b) => a.Union(b)) : all;
        ZoomTo(visible);
    }

    private void ZoomTo(Bounds2 b)
    {
        double w = PreviewHost.ActualWidth, h = PreviewHost.ActualHeight;
        if (w < 10 || h < 10 || b.IsEmpty) return;
        double bw = Math.Max(b.Width, 1e-9), bh = Math.Max(b.Height, 1e-9);
        _scale = Math.Min(w / bw, h / bh) * 0.9;
        double cx = (b.MinX + b.MaxX) / 2 - _origin.X, cy = (b.MinY + b.MaxY) / 2 - _origin.Y;
        _tx = w / 2 - _scale * cx;
        _ty = h / 2 + _scale * cy;
        ApplyTransform();
        UpdateStrokes();
    }

    private P2 ScreenToWorld(Point p) => new((p.X - _tx) / _scale + _origin.X, -(p.Y - _ty) / _scale + _origin.Y);

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PreviewHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (e.PreviousSize.Width < 10) FitView();
        else if (_precise) ApplyTransform(); // Zuschneiderahmen an neue Größe anpassen
    }

    private void PreviewHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(PreviewHost);
        double f = pt.Properties.MouseWheelDelta > 0 ? 1.2 : 1 / 1.2;
        double newScale = Math.Clamp(_scale * f, 1e-9, 1e9);
        f = newScale / _scale;
        // Zoom um die Mausposition
        _tx = pt.Position.X - (pt.Position.X - _tx) * f;
        _ty = pt.Position.Y - (pt.Position.Y - _ty) * f;
        _scale *= f;
        ApplyTransform();
        UpdateStrokes();
        ShowCursor(pt.Position);
        e.Handled = true;
    }

    private void PreviewHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(PreviewHost);
        if (!pt.Properties.IsLeftButtonPressed && !pt.Properties.IsMiddleButtonPressed) return;
        _panning = true;
        _leftPress = pt.Properties.IsLeftButtonPressed;
        _panStart = _pressPoint = pt.Position;
        _panTx = _tx; _panTy = _ty;
        PreviewHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PreviewHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(PreviewHost).Position;
        ShowCursor(p);
        if (!_panning)
        {
            P2? corner = FindCorner(p)?.Point;
            if (corner != _hoverCorner)
            {
                _hoverCorner = corner;
                UpdateOverlay();
            }
            return;
        }
        _tx = _panTx + (p.X - _panStart.X);
        _ty = _panTy + (p.Y - _panStart.Y);
        ApplyTransform();
    }

    private void PreviewHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        PreviewHost.ReleasePointerCapture(e.Pointer);
        var p = e.GetCurrentPoint(PreviewHost).Position;
        bool isClick = Math.Abs(p.X - _pressPoint.X) < 4 && Math.Abs(p.Y - _pressPoint.Y) < 4;
        if (!isClick || !_leftPress) return;

        // Eckpunkt hat Vorrang vor der Flächenauswahl
        if (FindCorner(p) is { } c)
        {
            if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)) _pins.Clear();
            _pins.Remove(c.Point);
            _pins.Add(c.Point);
            UpdateOverlay();
            UpdatePointText();
            FileText.Text = $"Eckpunkt von {c.Row.KindText} (Handle {c.Row.Item.Handle}, Layer {c.Row.Layer})" +
                            (c.Row.Source.Length > 0 ? $" aus {c.Row.Source}" : "");
            return;
        }
        if (e.KeyModifiers == VirtualKeyModifiers.None && SelectAtHit(p) == null) ClearPins();
        SelectAt(p, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
    }

    private void PreviewHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        CursorText.Text = "";
        if (_hoverCorner != null && !_panning)
        {
            _hoverCorner = null;
            UpdateOverlay();
        }
    }

    private void PreviewHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _panning = false;

    private void PreviewHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => FitView();

    /// <summary>Kleinste sichtbare Fläche unter dem Punkt (Bildschirmkoordinaten).</summary>
    private AreaRow? SelectAtHit(Point screen)
    {
        P2 w = ScreenToWorld(screen);
        AreaRow? hit = null;
        foreach (var r in PickRows)
        {
            var b = r.Item.Bounds;
            if (w.X < b.MinX || w.X > b.MaxX || w.Y < b.MinY || w.Y > b.MaxY) continue;
            // Even-Odd über alle Konturen (Inseln sind Löcher)
            int inside = r.Item.Outlines.Count(o => Loop.Contains(o.Points, w));
            if (inside % 2 == 0) continue;
            if (hit == null || r.Item.Area < hit.Item.Area) hit = r;
        }
        return hit;
    }

    /// <summary>Kleinste sichtbare Fläche unter dem Mauszeiger auswählen.</summary>
    private void SelectAt(Point screen, bool addToSelection)
    {
        AreaRow? hit = SelectAtHit(screen);
        if (IsComparing) { CompareItemClicked(hit?.Compare, addToSelection); return; }

        if (hit == null)
        {
            if (!addToSelection) AreaList.SelectedItems.Clear();
            return;
        }
        if (addToSelection)
        {
            if (AreaList.SelectedItems.Contains(hit)) AreaList.SelectedItems.Remove(hit);
            else AreaList.SelectedItems.Add(hit);
        }
        else
        {
            AreaList.SelectedItem = hit;
        }
        AreaList.ScrollIntoView(hit);
    }

    // =========================================================================== Eckpunkte / Koordinaten

    /// <summary>Genaue Koordinate bzw. Differenz (3 bis 9 Nachkommastellen).</summary>
    private string Coord(double v) => NumberFormats.ExactText(v, De);
    private string Full(double v) => NumberFormats.Full(v, De);

    private void ShowCursor(Point p)
    {
        if (_paths.Count == 0) return;
        P2 w = ScreenToWorld(p);
        CursorText.Text = $"X {NumberFormats.Cursor(w.X, _scale, De)}   Y {NumberFormats.Cursor(w.Y, _scale, De)}";
    }

    private Point ToScreen(P2 w) => new(_scale * (w.X - _origin.X) + _tx, -_scale * (w.Y - _origin.Y) + _ty);

    /// <summary>Nächster echter Eckpunkt einer sichtbaren Fläche innerhalb des Fangradius.</summary>
    private (P2 Point, AreaRow Row)? FindCorner(Point screen)
    {
        if (_scale <= 0) return null;
        P2 w = ScreenToWorld(screen);
        double tol = SnapPx / _scale;
        double best = SnapPx * SnapPx;
        (P2 Point, AreaRow Row)? hit = null;
        foreach (var r in PickRows)
        {
            var b = r.Item.Bounds;
            if (w.X < b.MinX - tol || w.X > b.MaxX + tol || w.Y < b.MinY - tol || w.Y > b.MaxY + tol) continue;
            foreach (var o in r.Item.Outlines)
            foreach (var c in o.Corners)
            {
                double dx = (c.X - w.X) * _scale, dy = (c.Y - w.Y) * _scale;
                double d2 = dx * dx + dy * dy;
                // gleicher Punkt in mehreren Flächen → kleinste Fläche melden
                bool same = hit != null && c == hit.Value.Point;
                if (d2 < best || (same && r.Item.Area < hit!.Value.Row.Item.Area))
                {
                    best = d2;
                    hit = (c, r);
                }
            }
        }
        return hit;
    }

    /// <summary>Markierungen und Koordinaten-Beschriftungen in Bildschirmkoordinaten neu zeichnen.</summary>
    private void UpdateOverlay()
    {
        Overlay.Children.Clear();
        if (_hoverCorner is P2 h && !_pins.Contains(h))
            AddDot(ToScreen(h), 10, HoverColor, null);

        string u = DrawingUnits.Symbol(CurrentUnit);
        double maxX = PreviewHost.ActualWidth;

        // Eckpunkt-Markierung im Vergleich (grün = gleich, rot = geringfügig abweichend)
        if (_pointMarks is { } pm)
        {
            AddPointMarks(pm.Equal, EqualColor, 3.5);
            AddPointMarks(pm.Near, NearColor, 4.5);
        }

        // größte Abweichung (Vergleich)
        if (_marker is { } m)
        {
            Point mf = ToScreen(m.From), mt = ToScreen(m.To);
            if (Math.Abs(mf.X) < 1e6 && Math.Abs(mf.Y) < 1e6 && Math.Abs(mt.X) < 1e6 && Math.Abs(mt.Y) < 1e6)
            {
                Overlay.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = mf.X, Y1 = mf.Y, X2 = mt.X, Y2 = mt.Y,
                    Stroke = new SolidColorBrush(MarkerColor), StrokeThickness = 2,
                });
                AddDot(mf, 20, MarkerColor, null);
                AddDot(mt, 5, MarkerColor, MarkerColor);
                AddLabel(mf, m.Text, maxX);
            }
        }

        for (int i = 0; i < _pins.Count; i++)
        {
            Point sp = ToScreen(_pins[i]);
            AddDot(sp, 8, PinColor, Color.FromArgb(255, 255, 255, 255));
            string head = _pins.Count > 1 ? $"Punkt {i + 1}\n" : "";
            AddLabel(sp, $"{head}X = {Coord(_pins[i].X)} {u}\nY = {Coord(_pins[i].Y)} {u}", maxX);
        }
    }

    /// <summary>Kleine Kreise an den sichtbaren Punkten, zusammengefasst in einem Path (je Farbe ein Element).</summary>
    private void AddPointMarks(List<P2> points, Color color, double r)
    {
        if (points.Count == 0) return;
        double w = PreviewHost.ActualWidth, h = PreviewHost.ActualHeight;
        var group = new GeometryGroup();
        foreach (var p in points)
        {
            Point s = ToScreen(p);
            if (s.X < -r || s.Y < -r || s.X > w + r || s.Y > h + r) continue;
            group.Children.Add(new EllipseGeometry { Center = s, RadiusX = r, RadiusY = r });
            if (group.Children.Count >= MaxPointMarks) break;
        }
        if (group.Children.Count == 0) return;
        Overlay.Children.Add(new XPath
        {
            Data = group,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B)),
            IsHitTestVisible = false,
        });
    }

    private void AddDot(Point center, double size, Color stroke, Color? fill)
    {
        var dot = new XEllipse
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 2,
            Fill = fill is Color f ? new SolidColorBrush(f) : null,
        };
        Canvas.SetLeft(dot, center.X - size / 2);
        Canvas.SetTop(dot, center.Y - size / 2);
        Overlay.Children.Add(dot);
    }

    private void AddLabel(Point anchor, string text, double maxX)
    {
        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30)),
            },
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = label.DesiredSize.Width, h = label.DesiredSize.Height;
        // Standard: rechts oberhalb; am Rand auf die andere Seite klappen
        double x = anchor.X + 10, y = anchor.Y - 10 - h;
        if (x + w > maxX - 2) x = anchor.X - 10 - w;
        if (y < 2) y = anchor.Y + 10;
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        Overlay.Children.Add(label);
    }

    private void ClearPins()
    {
        if (_pins.Count == 0) return;
        _pins.Clear();
        UpdateOverlay();
        UpdatePointText();
    }

    private void UpdatePointText()
    {
        if (_pins.Count == 0)
        {
            PointPanel.Visibility = Visibility.Collapsed;
            return;
        }
        string u = DrawingUnits.Symbol(CurrentUnit);
        P2 last = _pins[^1];
        string text = (_pins.Count > 1 ? $"Punkt {_pins.Count}: " : "Eckpunkt: ") +
                      $"X = {Coord(last.X)}   Y = {Coord(last.Y)} {u}";
        if (_pins.Count > 1)
        {
            P2 prev = _pins[^2];
            double dx = last.X - prev.X, dy = last.Y - prev.Y;
            text += $"   ·   zu Punkt {_pins.Count - 1}: ΔX = {Coord(dx)}   ΔY = {Coord(dy)}   Abstand = {Coord(Math.Sqrt(dx * dx + dy * dy))} {u}";
        }
        PointText.Text = text;
        PointPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Markierte Eckpunkte als Text (X Tab Y je Zeile) in die Zwischenablage – zum Einfügen in Excel.</summary>
    private bool CopyPins()
    {
        if (_pins.Count == 0) return false;
        var sb = new StringBuilder();
        foreach (var pt in _pins)
            sb.Append(pt.X.ToString("0.######", De)).Append('\t').Append(pt.Y.ToString("0.######", De)).AppendLine();
        try
        {
            var dp = new DataPackage();
            dp.SetText(sb.ToString());
            Clipboard.SetContent(dp);
            Clipboard.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CopyPoints_Click(object sender, RoutedEventArgs e)
    {
        if (_pins.Count == 0)
        {
            FileText.Text = "Keine Eckpunkte markiert – in der Vorschau auf eine Ecke klicken";
            return;
        }
        FileText.Text = CopyPins()
            ? $"{_pins.Count} Eckpunkt(e) in die Zwischenablage kopiert"
            : "Zwischenablage nicht verfügbar";
    }

    private void ClearPoints_Click(object sender, RoutedEventArgs e) => ClearPins();

    /// <summary>Esc: Markierungen löschen · Strg+C: Eckpunkte kopieren (nicht in Textfeldern).</summary>
    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _pins.Count > 0)
        {
            ClearPins();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.C && _pins.Count > 0 &&
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) &&
            FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not TextBox)
        {
            CopyPoints_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // =========================================================================== Vergleich zweier Zeichnungen

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || _loading) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".dwg");
        picker.FileTypeFilter.Add(".dxf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null) await LoadCompareAsync(file.Path);
    }

    /// <summary>Zeichnung B laden und mit der geladenen Zeichnung A vergleichen.</summary>
    private async Task LoadCompareAsync(string pathB)
    {
        if (_loading || _result == null) return;
        _loading = true;
        Busy.IsActive = true;
        WarningBar.IsOpen = false;
        WarningBar.Severity = InfoBarSeverity.Warning;
        WarningBar.Title = "Hinweise";
        FileText.Text = "Lese " + Path.GetFileName(pathB) + " und vergleiche …";
        try
        {
            var b = await DwgAreaExtractor.ExtractAsync(pathB, CurrentOptions());
            _compare = await DwgComparer.CompareAsync(_result, b);
            if (_compareFile != pathB) _nearMax = 0; // neue Zeichnung B: Vorgabegrenze
            _compareFile = pathB;
            _ = RefreshPointMarksAsync();
            Title = $"DWG Flächen – Vergleich {Path.GetFileName(_result.FilePath)} ↔ {Path.GetFileName(pathB)}";
            ClearPins();
            RebuildAll(fit: true);

            var warnings = b.Warnings.Select(w => "B: " + w).Concat(_compare.Warnings).ToList();
            if (warnings.Count > 0)
            {
                WarningBar.Message = string.Join("\n", warnings.Take(8)) + (warnings.Count > 8 ? $"\n… und {warnings.Count - 8} weitere" : "");
                WarningBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            FileText.Text = "Fehler beim Vergleich";
            WarningBar.Severity = InfoBarSeverity.Error;
            WarningBar.Title = "Zweite Zeichnung konnte nicht gelesen werden";
            WarningBar.Message = ex.Message;
            WarningBar.IsOpen = true;
        }
        finally
        {
            Busy.IsActive = false;
            _loading = false;
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
        WarningBar.IsOpen = false;
        ClearPins();
        RebuildAll(fit: false);
    }

    private void SetCompareLayout(bool on)
    {
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        var normal = on ? Visibility.Collapsed : Visibility.Visible;
        CompareBar.Visibility = vis;
        CompareHeaderGrid.Visibility = vis;
        CompareList.Visibility = vis;
        HeaderGrid.Visibility = normal;
        AreaList.Visibility = normal;
    }

    private void RebuildCompare(bool fit)
    {
        if (_compare == null) return;
        var unit = CurrentUnit;
        string u = DrawingUnits.Symbol(unit);

        _cmpAll = _compare.Items.Select(c => new CompareRow(c, unit)).ToList();

        // Vorschau: entfallene und alte Konturen aus A, alles andere aus B
        _cmpShapes = new List<AreaRow>();
        foreach (var r in _cmpAll)
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
        var unchecked_ = _layers.Where(l => !l.IsChecked).Select(l => l.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _layers) l.PropertyChanged -= Layer_PropertyChanged;
        _layers.Clear();
        foreach (var g in _cmpAll.GroupBy(r => r.Layer, StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var sum = new LayerSummary { Layer = g.Key, Count = g.Count(), Area = g.Sum(r => r.Item.AreaB) };
            var row = new LayerRow(sum, unit, g.First().Color) { IsChecked = !unchecked_.Contains(g.Key) };
            row.PropertyChanged += Layer_PropertyChanged;
            _layers.Add(row);
        }

        CmpAHeader.Text = $"Fläche A [{u}²]";
        CmpBHeader.Text = $"Fläche B [{u}²]";
        CmpDeltaHeader.Text = $"Δ [{u}²]";
        CompareFilesText.Text = $"A: {Path.GetFileName(_compare.A.FilePath)}   →   B: {Path.GetFileName(_compare.B.FilePath)}";
        ToolTipService.SetToolTip(CompareFilesText, $"A: {_compare.A.FilePath}\nB: {_compare.B.FilePath}");
        CmpChangedText.Text = $"Geändert ({_compare.Count(CompareStatus.Geändert)})";
        CmpNewText.Text = $"Neu ({_compare.Count(CompareStatus.Neu)})";
        CmpRemovedText.Text = $"Entfallen ({_compare.Count(CompareStatus.Entfallen)})";
        CmpUnchangedText.Text = $"Unverändert ({_compare.Count(CompareStatus.Unverändert)})";
        FileText.Text = $"Vergleich  ·  A: {_compare.A.Items.Count} Flächen  ·  B: {_compare.B.Items.Count} Flächen  ·  Toleranz {_compare.Tolerance:G3} {u}";

        BuildScene(_cmpShapes);
        ApplyCompareFilter();
        if (fit) FitView();
        UpdatePointText();
        UpdateOverlay();
    }

    private void ApplyCompareFilter()
    {
        var active = _layers.Where(l => l.IsChecked).Select(l => l.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string f = FilterBox.Text.Trim();

        bool StatusOn(CompareStatus s) => s switch
        {
            CompareStatus.Neu => ChkCmpNew.IsChecked == true,
            CompareStatus.Entfallen => ChkCmpRemoved.IsChecked == true,
            CompareStatus.Geändert => ChkCmpChanged.IsChecked == true,
            _ => ChkCmpUnchanged.IsChecked == true,
        };

        IEnumerable<CompareRow> q = _cmpAll.Where(r => active.Contains(r.Layer) && StatusOn(r.Item.Status));
        if (f.Length > 0)
            q = q.Where(r => r.Layer.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.KindText.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Status.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Details.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             r.Label.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             string.Equals(r.Item.A?.Handle, f, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(r.Item.B?.Handle, f, StringComparison.OrdinalIgnoreCase));

        q = _cmpSortKey switch
        {
            "Status" => _cmpSortDesc ? q.OrderByDescending(r => r.Status) : q.OrderBy(r => r.Status),
            "Details" => _cmpSortDesc ? q.OrderByDescending(r => r.Details) : q.OrderBy(r => r.Details),
            "Layer" => _cmpSortDesc ? q.OrderByDescending(r => r.Layer, StringComparer.CurrentCultureIgnoreCase) : q.OrderBy(r => r.Layer, StringComparer.CurrentCultureIgnoreCase),
            "A" => _cmpSortDesc ? q.OrderByDescending(r => r.Item.AreaA) : q.OrderBy(r => r.Item.AreaA),
            "B" => _cmpSortDesc ? q.OrderByDescending(r => r.Item.AreaB) : q.OrderBy(r => r.Item.AreaB),
            "Delta" => _cmpSortDesc ? q.OrderByDescending(r => Math.Abs(r.Item.DeltaArea)) : q.OrderBy(r => Math.Abs(r.Item.DeltaArea)),
            "Label" => _cmpSortDesc ? q.OrderByDescending(r => r.Label, StringComparer.CurrentCultureIgnoreCase) : q.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase),
            _ => _cmpSortDesc ? q.OrderByDescending(r => r.Item.Nr) : q.OrderBy(r => r.Item.Nr),
        };

        var list = q.ToList();
        foreach (var sh in _cmpShapes) Highlight(sh, false);
        _cmpRows = new ObservableCollection<CompareRow>(list);
        CompareList.ItemsSource = _cmpRows;

        // Vorschau: nur sichtbare Einträge zeichnen
        var visible = list.ToHashSet();
        _cmpVisibleShapes = _cmpShapes.Where(sh => sh.Compare != null && visible.Contains(sh.Compare)).ToList();
        var visibleItems = _cmpVisibleShapes.Select(sh => sh.Item).ToHashSet();
        foreach (var (item, path) in _paths)
            path.Visibility = visibleItems.Contains(item) ? Visibility.Visible : Visibility.Collapsed;

        UpdateCompareTotals();
        if (_hoverCorner != null) { _hoverCorner = null; UpdateOverlay(); }
    }

    private void CompareFilter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void CompareHeader_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        if (_cmpSortKey == key) _cmpSortDesc = !_cmpSortDesc;
        else { _cmpSortKey = key; _cmpSortDesc = key is "A" or "B" or "Delta"; }
        ApplyCompareFilter();
    }

    private string AreaValueText(double area, DrawingUnit unit, bool signed = false)
    {
        string fmt = signed ? NumberFormats.SignedArea : "N3";
        return DrawingUnits.AreaToSquareMeters(area, unit) is double m2
            ? m2.ToString(fmt, De) + " m²"
            : area.ToString(fmt, De) + " " + DrawingUnits.Symbol(unit) + "²";
    }

    private void UpdateCompareTotals()
    {
        var unit = CurrentUnit;
        double a = _cmpRows.Sum(r => r.Item.AreaA), b = _cmpRows.Sum(r => r.Item.AreaB);
        TotalText.Text = $"Δ {AreaValueText(b - a, unit, signed: true)}";
        CountText.Text = $"{_cmpRows.Count} von {_cmpAll.Count} Einträgen";

        var sel = CompareList.SelectedItems.OfType<CompareRow>().ToList();
        if (sel.Count > 0)
        {
            double sa = sel.Sum(r => r.Item.AreaA), sb = sel.Sum(r => r.Item.AreaB);
            SelectionText.Text = $"Auswahl ({sel.Count}): A {AreaValueText(sa, unit)} · B {AreaValueText(sb, unit)} · Δ {AreaValueText(sb - sa, unit, true)}";
        }
        else
        {
            SelectionText.Text = $"A {AreaValueText(a, unit)} · B {AreaValueText(b, unit)}";
        }
    }

    private void CompareList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var removed = e.RemovedItems.OfType<CompareRow>().ToHashSet();
        var added = e.AddedItems.OfType<CompareRow>().ToHashSet();
        foreach (var sh in _cmpShapes)
        {
            if (sh.Compare == null) continue;
            if (removed.Contains(sh.Compare)) Highlight(sh, false);
            if (added.Contains(sh.Compare)) Highlight(sh, true);
        }
        UpdateDeviationMarker();
        UpdateCompareTotals();
    }

    /// <summary>Doppelklick: zur Stelle der größten Abweichung zoomen (ohne Abweichung: auf die ganze Fläche).</summary>
    private void CompareList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (CompareList.SelectedItem is not CompareRow r) return;
        if (r.Item.DeviationView is Bounds2 v) ZoomTo(v);
        else ZoomTo(r.Item.Bounds);
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
            UpdateOverlay();
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
        UpdateOverlay();
    }

    /// <summary>Fenster mit den Eckpunkten, die in A und B nur wenig voneinander abweichen.</summary>
    private void NearPoints_Click(object sender, RoutedEventArgs e)
    {
        if (_compare == null) return;
        _nearWin?.Close();
        var win = new NearPointsWindow(_compare.A, _compare.B, NearMax,
                                       DrawingUnits.Symbol(CurrentUnit), ShowNearPoint, NearMaxChanged);
        win.Closed += (_, _) => { if (_nearWin == win) _nearWin = null; };
        _nearWin = win;
        win.Activate();
    }

    /// <summary>Punktpaar aus der Liste in der Vorschau zeigen: hinzoomen und markieren (Ring = Punkt aus A).</summary>
    private void ShowNearPoint(NearPoint n)
    {
        string u = DrawingUnits.Symbol(CurrentUnit);
        ZoomTo(n.View);
        _marker = (n.A, n.B,
            $"A: X = {Full(n.A.X)}   Y = {Full(n.A.Y)}\nB: X = {Full(n.B.X)}   Y = {Full(n.B.Y)}\nAbstand {Full(n.Distance)} {u}");
        UpdateOverlay();
    }

    /// <summary>Bei genau einer ausgewählten geänderten Fläche die Stelle der größten Abweichung markieren.</summary>
    private void UpdateDeviationMarker()
    {
        if (CompareList.SelectedItems.Count == 1 && CompareList.SelectedItem is CompareRow r &&
            r.Item.Deviation is double d && r.Item.DeviationFrom is P2 f && r.Item.DeviationTo is P2 t)
        {
            string u = DrawingUnits.Symbol(CurrentUnit);
            _marker = (f, t, $"max. Abweichung {Coord(d)} {u}\nEckpunkt aus {(r.Item.DeviationFromA ? "A" : "B")}");
        }
        else
        {
            _marker = null;
        }
        UpdateOverlay();
    }

    private void CompareItemClicked(CompareRow? row, bool addToSelection)
    {
        if (row == null)
        {
            if (!addToSelection) CompareList.SelectedItems.Clear();
            return;
        }
        if (addToSelection)
        {
            if (CompareList.SelectedItems.Contains(row)) CompareList.SelectedItems.Remove(row);
            else CompareList.SelectedItems.Add(row);
        }
        else
        {
            CompareList.SelectedItem = row;
        }
        CompareList.ScrollIntoView(row);
    }

    private async Task ExportCompareAsync()
    {
        if (_compare == null) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_compare.A.FilePath)}_vs_{Path.GetFileNameWithoutExtension(_compare.B.FilePath)}",
        };
        picker.FileTypeChoices.Add("CSV (Excel, Semikolon)", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await DwgComparer.WriteCsvAsync(file.Path, _compare, _cmpRows.Select(r => r.Item).ToList(), CurrentUnit);
            FileText.Text = "Exportiert: " + file.Path;
        }
        catch (Exception ex)
        {
            WarningBar.Severity = InfoBarSeverity.Error;
            WarningBar.Title = "Export fehlgeschlagen";
            WarningBar.Message = ex.Message;
            WarningBar.IsOpen = true;
        }
    }

    private void CopyCompare()
    {
        if (_compare == null) return;
        IEnumerable<CompareRow> rows = CompareList.SelectedItems.Count > 1 ? CompareList.SelectedItems.OfType<CompareRow>() : _cmpRows;
        // gleiche Spalten wie der CSV-Export, aber mit Tabulator (direkt in Excel einfügbar)
        string csv = DwgComparer.ToCsv(_compare, rows.Select(r => r.Item).ToList(), CurrentUnit);
        string text = string.Join("\n", csv.Split('\n').Select(l => l.TrimEnd('\r').Replace(';', '\t')));
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        Clipboard.Flush();
        FileText.Text = "Vergleich in die Zwischenablage kopiert";
    }
}
