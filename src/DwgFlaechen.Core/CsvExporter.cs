using System.Globalization;
using System.Text;

namespace DwgFlaechen.Core;

/// <summary>CSV-Export (Semikolon, deutsches Zahlenformat – direkt in Excel öffenbar).</summary>
public static class CsvExporter
{
    public static string ToCsv(IEnumerable<AreaItem> items, DrawingUnit unit, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.GetCultureInfo("de-DE");
        string u = DrawingUnits.Symbol(unit);
        bool hasM2 = DrawingUnits.ToMeters(unit) != null;
        var list = items as IList<AreaItem> ?? items.ToList();
        bool hasLabel = list.Any(i => i.Label.Length > 0 || i.LabelArea != null);
        var sb = new StringBuilder();

        sb.Append("Nr;Handle;Typ;Layer;Block;Konturen;")
          .Append($"Fläche [{u}²];Umfang [{u}]");
        if (hasM2) sb.Append(";Fläche [m²]");
        if (hasLabel) sb.Append(";Fläche lt. Beschriftung [m²];Beschriftung");
        sb.AppendLine();

        double sum = 0, sumM2 = 0;
        foreach (var i in list)
        {
            sum += i.Area;
            sb.Append(i.Nr).Append(';')
              .Append(i.Handle).Append(';')
              .Append(i.Kind).Append(';')
              .Append(Esc(i.Layer)).Append(';')
              .Append(Esc(i.Block)).Append(';')
              .Append(i.LoopCount).Append(';')
              .Append(i.Area.ToString("0.######", culture)).Append(';')
              .Append(i.Perimeter.ToString("0.######", culture));
            if (hasM2)
            {
                double m2 = DrawingUnits.AreaToSquareMeters(i.Area, unit)!.Value;
                sumM2 += m2;
                sb.Append(';').Append(m2.ToString("0.######", culture));
            }
            if (hasLabel)
                sb.Append(';').Append(i.LabelArea?.ToString("0.##", culture) ?? "")
                  .Append(';').Append(Esc(i.Label));
            sb.AppendLine();
        }

        sb.Append(";;;;Summe;;").Append(sum.ToString("0.######", culture)).Append(';');
        if (hasM2) sb.Append(';').Append(sumM2.ToString("0.######", culture));
        sb.AppendLine();
        return sb.ToString();
    }

    public static async Task WriteAsync(string path, IEnumerable<AreaItem> items, DrawingUnit unit)
    {
        // UTF-8 mit BOM, damit Excel Umlaute korrekt anzeigt
        await File.WriteAllTextAsync(path, ToCsv(items, unit), new UTF8Encoding(true));
    }

    private static string Esc(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
