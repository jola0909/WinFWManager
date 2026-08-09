using System.Globalization;
using System.Text;

namespace WinFWManager.Core.Services;

/// <summary>
/// Minimal RFC 4180 CSV writing, with one addition: values that a spreadsheet would
/// treat as a formula are neutralised.
///
/// That matters here because exported fields are not all ours. Hostnames come from
/// reverse DNS and process names from disk, so a value could arrive starting with '='
/// and be executed when the file is opened in Excel.
/// </summary>
public static class Csv
{
    private const string MustQuote = ",\"\r\n";

    /// <summary>Formats one value as a CSV field, quoting and escaping as needed.</summary>
    public static string Field(object? value)
    {
        var text = value switch
        {
            null => "",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        if (text.Length == 0)
            return "";

        // A leading =, +, - or @ makes a spreadsheet treat the cell as a formula.
        // Prefixing with an apostrophe keeps the text visible but inert.
        if (text[0] is '=' or '+' or '-' or '@')
            text = "'" + text;

        var needsQuotes = text.IndexOfAny(MustQuote.ToCharArray()) >= 0
                          || char.IsWhiteSpace(text[0])
                          || char.IsWhiteSpace(text[^1]);

        return needsQuotes ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>Joins values into one CSV line, without a trailing newline.</summary>
    public static string Row(IEnumerable<object?> values)
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var value in values)
        {
            if (!first) sb.Append(',');
            sb.Append(Field(value));
            first = false;
        }

        return sb.ToString();
    }

    public static string Row(params object?[] values) => Row((IEnumerable<object?>)values);
}
