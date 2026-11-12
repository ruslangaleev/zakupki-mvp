using System.Globalization;
using System.Text.RegularExpressions;

namespace Zakupki.Indexer.Parsing;

public static partial class PriceParser
{
    public static double? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Trim()
            .Replace('\u00A0', ' ')
            .Replace("₽", string.Empty, StringComparison.Ordinal)
            .Replace("руб.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("руб", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        cleaned = NonNumericJunk().Replace(cleaned, string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        // 1 234 567,89 or 1.234.567,89 → 1234567.89
        if (cleaned.Contains(',') && (cleaned.Contains(' ') || cleaned.Contains('.')))
        {
            cleaned = cleaned.Replace(" ", string.Empty).Replace(".", string.Empty).Replace(',', '.');
        }
        else if (cleaned.Contains(','))
        {
            cleaned = cleaned.Replace(" ", string.Empty).Replace(',', '.');
        }
        else
        {
            cleaned = cleaned.Replace(" ", string.Empty);
        }

        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            value >= 0 &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value))
        {
            return value;
        }

        return null;
    }

    [GeneratedRegex(@"[^\d\s.,]")]
    private static partial Regex NonNumericJunk();
}
