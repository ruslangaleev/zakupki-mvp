using System.Globalization;
using System.Text.RegularExpressions;

namespace Zakupki.Indexer.Parsing;

public static partial class DateParser
{
    public static DateTimeOffset? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
        {
            return iso;
        }

        // "19.11.2026 10:00 (МСК+5)" / "11.11.2026"
        var match = RuDate().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var hour = match.Groups["hh"].Success ? int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture) : 0;
        var minute = match.Groups["mm"].Success ? int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture) : 0;

        try
        {
            // Store as UTC midnight/local wall-clock without offset math — enough for sorting.
            return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static DateTimeOffset? TryParseApplicationDeadline(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return null;
        }

        foreach (var (key, value) in fields)
        {
            if (key.Contains("окончания срока подачи", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("окончания подачи заявок", StringComparison.OrdinalIgnoreCase))
            {
                return TryParse(value);
            }
        }

        return null;
    }

    [GeneratedRegex(
        @"^(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?<y>\d{4})(?:\s+(?<hh>\d{1,2}):(?<mm>\d{2}))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuDate();
}
