using System.Globalization;
using System.Text;

namespace FlightLog.Application.Exports;

/// <summary>
/// Minimal RFC 4180 CSV writer. Cells that a spreadsheet would run as a formula (starting with = + - @ tab or CR) are
/// prefixed with an apostrophe, because exported text (log messages, comments) comes from untrusted sources.
/// Numbers are written with the invariant culture so that files read the same everywhere.
/// </summary>
public sealed class Csv
{
    private readonly StringBuilder _text = new();

    /// <summary>A metadata line before the table (<c># key: value</c>); tools such as pandas skip them with <c>comment='#'</c>.</summary>
    public Csv Meta(string key, string? value)
    {
        _text.Append("# ").Append(key).Append(": ").Append(OneLine(value ?? "")).Append("\r\n");
        return this;
    }

    public Csv Row(params object?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                _text.Append(',');
            }

            _text.Append(Cell(cells[i]));
        }

        _text.Append("\r\n");
        return this;
    }

    /// <summary>UTF-8 with a byte order mark, so that spreadsheet programs show Turkish characters correctly.</summary>
    public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_text.ToString())];

    public override string ToString() => _text.ToString();

    internal static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "",
            float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "",
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        if (text.Length > 0 && value is string && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    private static string OneLine(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
