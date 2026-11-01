using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Game.Tools.Reports;

/// <summary>A titled table of cells (string, integer, double or null) that renders as text, CSV or JSON.</summary>
internal sealed class ReportTable
{
    public ReportTable(string title, params string[] columns)
    {
        Title = title;
        Columns = columns;
    }

    public string Title { get; }

    public string[] Columns { get; }

    public List<object?[]> Rows { get; } = new();

    public ReportTable Add(params object?[] cells)
    {
        if (cells.Length != Columns.Length) throw new ArgumentException("Row has " + cells.Length + " cells, table has " + Columns.Length + " columns.");
        Rows.Add(cells);
        return this;
    }

    private static string Cell(object? value) => value switch
    {
        null => "-",
        double d => Math.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static string ToText(IReadOnlyList<ReportTable> tables)
    {
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            sb.Append("== ").Append(table.Title).Append(" ==\n");
            var widths = table.Columns.Select(c => c.Length).ToArray();
            foreach (var row in table.Rows)
                for (var i = 0; i < row.Length; i++) widths[i] = Math.Max(widths[i], Cell(row[i]).Length);
            AppendLine(sb, table.Columns, widths);
            sb.Append(string.Join("  ", widths.Select(w => new string('-', w)))).Append('\n');
            foreach (var row in table.Rows) AppendLine(sb, row.Select(Cell).ToArray(), widths, row);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendLine(StringBuilder sb, string[] cells, int[] widths, object?[]? raw = null)
    {
        var start = sb.Length;
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append("  ");
            var numeric = raw != null && raw[i] is int or long or double;
            sb.Append(numeric ? cells[i].PadLeft(widths[i]) : cells[i].PadRight(widths[i]));
        }
        while (sb.Length > start && sb[sb.Length - 1] == ' ') sb.Length--;
        sb.Append('\n');
    }

    public static string ToCsv(IReadOnlyList<ReportTable> tables)
    {
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            sb.Append("# ").Append(table.Title).Append('\n');
            sb.Append(string.Join(",", table.Columns)).Append('\n');
            foreach (var row in table.Rows)
                sb.Append(string.Join(",", row.Select(c => c == null ? "" : Quote(Cell(c))))).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Quote(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    public static string ToJson(IReadOnlyList<ReportTable> tables)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            foreach (var table in tables)
            {
                w.WriteStartArray(table.Title);
                foreach (var row in table.Rows)
                {
                    w.WriteStartObject();
                    for (var i = 0; i < row.Length; i++)
                    {
                        switch (row[i])
                        {
                            case null: w.WriteNull(table.Columns[i]); break;
                            case int n: w.WriteNumber(table.Columns[i], n); break;
                            case long n: w.WriteNumber(table.Columns[i], n); break;
                            case double d: w.WriteNumber(table.Columns[i], Math.Round(d, 4)); break;
                            default: w.WriteString(table.Columns[i], row[i]!.ToString()); break;
                        }
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Render(IReadOnlyList<ReportTable> tables, string format) => format switch
    {
        "json" => ToJson(tables),
        "csv" => ToCsv(tables),
        _ => ToText(tables),
    };
}
