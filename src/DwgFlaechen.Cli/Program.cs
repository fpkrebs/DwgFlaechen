using System.Globalization;
using DwgFlaechen.Core;

// Kommandozeilen-Variante: DwgFlaechen.Cli <datei.dwg> [--csv ausgabe.csv] [--keine-bloecke] [--einheit mm|cm|m]
//                                                [--vergleich andere.dwg]
if (args.Length == 0)
{
    Console.WriteLine("Aufruf: DwgFlaechen.Cli <datei.dwg|dxf> [--csv ausgabe.csv] [--keine-bloecke] [--einheit mm|cm|dm|m] [--vergleich b.dwg]");
    return 1;
}

var de = CultureInfo.GetCultureInfo("de-DE");
string file = args[0];
string? csv = null;
string? compareWith = null;
DrawingUnit? unitOverride = null;
var opt = new ExtractionOptions();

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--csv" when i + 1 < args.Length: csv = args[++i]; break;
        case "--keine-bloecke": opt.IncludeBlocks = false; break;
        case "--vergleich" when i + 1 < args.Length: compareWith = args[++i]; break;
        case "--einheit" when i + 1 < args.Length:
            unitOverride = args[++i].ToLowerInvariant() switch
            {
                "mm" => DrawingUnit.Millimeter,
                "cm" => DrawingUnit.Zentimeter,
                "dm" => DrawingUnit.Dezimeter,
                "m" => DrawingUnit.Meter,
                _ => DrawingUnit.Unbekannt,
            };
            break;
    }
}

var res = DwgAreaExtractor.Extract(file, opt);
var unit = unitOverride ?? res.Unit;
string u = DrawingUnits.Symbol(unit);

if (compareWith != null)
{
    var resB = DwgAreaExtractor.Extract(compareWith, opt);
    var cmp = DwgComparer.Compare(res, resB);
    Console.WriteLine($"A: {res.FilePath}  ({res.Items.Count} Flächen)");
    Console.WriteLine($"B: {resB.FilePath}  ({resB.Items.Count} Flächen)   Einheit: {unit}   Toleranz: {cmp.Tolerance:G3} {u}");
    Console.WriteLine();
    Console.WriteLine($"{"Nr",4} {"Status",-12} {"Layer",-22} {"Hdl A",-6} {"Hdl B",-6} {"A [" + u + "²]",12} {"B [" + u + "²]",12} {"Δ",10}  Änderung");
    foreach (var c in cmp.Items.Where(c => c.Status != CompareStatus.Unverändert))
        Console.WriteLine($"{c.Nr,4} {c.Status,-12} {Trim(c.Layer, 22),-22} {c.A?.Handle,-6} {c.B?.Handle,-6} " +
                          $"{c.A?.Area.ToString("N3", de),12} {c.B?.Area.ToString("N3", de),12} {c.DeltaArea.ToString("+0.000;-0.000;0", de),10}  {c.Details}");
    Console.WriteLine();
    Console.WriteLine($"Neu {cmp.Count(CompareStatus.Neu)} · Entfallen {cmp.Count(CompareStatus.Entfallen)} · " +
                      $"Geändert {cmp.Count(CompareStatus.Geändert)} · Unverändert {cmp.Count(CompareStatus.Unverändert)}");
    Console.WriteLine($"Summe A {cmp.TotalA.ToString("N3", de)} · Summe B {cmp.TotalB.ToString("N3", de)} · Δ {cmp.TotalDelta.ToString("+0.000;-0.000;0", de)} {u}²");
    foreach (var w in res.Warnings.Concat(resB.Warnings).Concat(cmp.Warnings)) Console.WriteLine("Hinweis: " + w);
    if (csv != null)
    {
        await DwgComparer.WriteCsvAsync(csv, cmp, cmp.Items, unit);
        Console.WriteLine($"CSV geschrieben: {csv}");
    }
    return 0;
}

Console.WriteLine($"Datei:   {res.FilePath}");
Console.WriteLine($"Version: {res.Version}   Einheit: {unit}");
Console.WriteLine();
bool anyLabel = res.Items.Any(i => i.Label.Length > 0 || i.LabelArea != null);
Console.WriteLine($"{"Nr",4} {"Handle",-8} {"Typ",-12} {"Layer",-24} {"Block",-20} {"Fläche [" + u + "²]",18} {"m²",12}" +
                  (anyLabel ? $" {"lt. Beschr.",11}  Beschriftung" : ""));
foreach (var i in res.Items)
{
    string m2 = DrawingUnits.AreaToSquareMeters(i.Area, unit)?.ToString("N3", de) ?? "-";
    string lbl = anyLabel ? $" {i.LabelArea?.ToString("N2", de) ?? "",11}  {Trim(i.Label, 60)}" : "";
    Console.WriteLine($"{i.Nr,4} {i.Handle,-8} {i.Kind,-12} {Trim(i.Layer, 24),-24} {Trim(i.Block, 20),-20} {i.Area.ToString("N4", de),18} {m2,12}{lbl}");
}
Console.WriteLine();
Console.WriteLine("Summe je Layer:");
foreach (var s in res.ByLayer())
{
    string m2 = DrawingUnits.AreaToSquareMeters(s.Area, unit)?.ToString("N3", de) + " m²";
    Console.WriteLine($"  {Trim(s.Layer, 30),-30} {s.Count,5} Obj.  {s.Area.ToString("N4", de),18} {u}²  {m2}");
}
Console.WriteLine();
Console.WriteLine($"GESAMT: {res.Items.Count} Flächen, {res.TotalArea.ToString("N4", de)} {u}²" +
                  (DrawingUnits.AreaToSquareMeters(res.TotalArea, unit) is double t ? $" = {t.ToString("N3", de)} m²" : ""));
foreach (var w in res.Warnings) Console.WriteLine("Hinweis: " + w);

if (csv != null)
{
    await CsvExporter.WriteAsync(csv, res.Items, unit);
    Console.WriteLine($"CSV geschrieben: {csv}");
}
return 0;

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
