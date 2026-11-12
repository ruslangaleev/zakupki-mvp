using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zakupki.Categorization.Advance;

/// <summary>
/// Precision-first advance detector. Prefers structured EIS field, then explicit % patterns.
/// </summary>
public sealed partial class RuleBasedAdvanceDetector : IAdvanceDetector
{
    public const string MethodName = "advance-rules-v1";

    public string Method => MethodName;

    public AdvanceDetectionResult Detect(AdvanceDetectionInput input)
    {
        foreach (var (name, value) in input.Fields)
        {
            if (!IsAdvanceSizeField(name))
            {
                continue;
            }

            if (TryParsePercent(value, out var percent) && percent > 0)
            {
                return Hit(
                    percent,
                    $"Аванс {FormatPercent(percent)}%",
                    $"{name}: {value}",
                    "field",
                    confidence: 0.97);
            }

            // Explicit zero / empty size → no advance.
            return Miss();
        }

        foreach (var part in input.Texts)
        {
            if (string.IsNullOrWhiteSpace(part.Text))
            {
                continue;
            }

            var normalized = NormalizeWhitespace(part.Text);
            if (ContainsToponymNoise(normalized) && !SectionPercentRegex().IsMatch(normalized))
            {
                // Place-name only, e.g. «Аванское лесничество».
                continue;
            }

            var section = SectionPercentRegex().Match(normalized);
            if (section.Success &&
                TryParsePercent(section.Groups["pct"].Value, out var sectionPct) &&
                sectionPct > 0)
            {
                return Hit(
                    sectionPct,
                    $"Аванс {FormatPercent(sectionPct)}%",
                    Truncate(section.Value, 220),
                    SourceKind(part.Source),
                    confidence: 0.93);
            }

            foreach (Match match in ExplicitPercentRegex().Matches(normalized))
            {
                if (IsBoilerplateContext(normalized, match.Index))
                {
                    continue;
                }

                if (!TryParsePercent(match.Groups["pct"].Value, out var pct) || pct <= 0)
                {
                    continue;
                }

                return Hit(
                    pct,
                    $"Аванс {FormatPercent(pct)}%",
                    Truncate(ContextWindow(normalized, match.Index, match.Length), 220),
                    SourceKind(part.Source),
                    confidence: 0.85);
            }
        }

        return Miss();
    }

    private AdvanceDetectionResult Hit(
        double percent,
        string label,
        string snippet,
        string source,
        double confidence) =>
        new()
        {
            HasAdvance = true,
            Percent = percent,
            Label = label,
            Snippet = snippet,
            Source = source,
            Method = MethodName,
            Confidence = confidence,
            DetectedAt = DateTimeOffset.UtcNow
        };

    private AdvanceDetectionResult Miss() =>
        new()
        {
            HasAdvance = false,
            Method = MethodName,
            Confidence = 0.9,
            DetectedAt = DateTimeOffset.UtcNow
        };

    private static bool IsAdvanceSizeField(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("размер аванса", StringComparison.Ordinal)
               || n.Contains("аванс, %", StringComparison.Ordinal)
               || n.Contains("аванса, %", StringComparison.Ordinal);
    }

    private static string SourceKind(string source)
    {
        if (source.StartsWith("pages/", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("common-info", StringComparison.OrdinalIgnoreCase))
        {
            return "page";
        }

        if (source.StartsWith("documents/", StringComparison.OrdinalIgnoreCase))
        {
            return "document";
        }

        return "document";
    }

    private static bool ContainsToponymNoise(string text) =>
        text.Contains("аванск", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoilerplateContext(string text, int matchIndex)
    {
        var start = Math.Max(0, matchIndex - 120);
        var len = Math.Min(text.Length - start, 280);
        var window = text.Substring(start, len).ToLowerInvariant();
        return window.Contains("не менее суммы аванса", StringComparison.Ordinal)
               || window.Contains("не менее размера аванса", StringComparison.Ordinal)
               || window.Contains("если контрактом предусмотрена выплата аванса", StringComparison.Ordinal)
               || window.Contains("если предусмотрена выплата аванса", StringComparison.Ordinal);
    }

    private static bool TryParsePercent(string raw, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var cleaned = raw.Trim()
            .Replace('\u00A0', ' ')
            .Replace("%", "", StringComparison.Ordinal)
            .Trim();

        // Keep first number-like token.
        var match = NumberRegex().Match(cleaned);
        if (!match.Success)
        {
            return false;
        }

        var token = match.Value.Replace(',', '.');
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
    }

    private static string FormatPercent(double percent) =>
        percent is >= 0 and <= 1000
            ? percent.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"))
            : percent.ToString("0.##", CultureInfo.InvariantCulture);

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ContextWindow(string text, int index, int length)
    {
        var start = Math.Max(0, index - 60);
        var end = Math.Min(text.Length, index + length + 80);
        return text[start..end];
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(
        @"размер\s+аванса\s*,?\s*%\s*(?:</[^>]+>\s*)*(?:<[^>]+>\s*)*(?<pct>\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SectionPercentRegex();

    [GeneratedRegex(
        @"аванс(?:а|ом|у|е)?[^\d%]{0,40}(?<pct>\d+(?:[.,]\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPercentRegex();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
}
