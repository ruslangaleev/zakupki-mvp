using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Zakupki.Collector.Models;

namespace Zakupki.Collector.Parsing;

public sealed partial class NoticeHtmlParser
{
    private readonly HtmlParser _parser = new();

    public Dictionary<string, string> ParseFields(string html)
    {
        var document = _parser.ParseDocument(html);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var titles = document.QuerySelectorAll(".section__title");
        foreach (var titleNode in titles)
        {
            var title = Normalize(titleNode.TextContent);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var infoNode = titleNode.ParentElement?.QuerySelector(".section__info")
                ?? titleNode.NextElementSibling;
            while (infoNode is not null && !infoNode.ClassList.Contains("section__info"))
            {
                infoNode = infoNode.NextElementSibling;
            }

            var value = Normalize(infoNode?.TextContent ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!fields.ContainsKey(title))
            {
                fields[title] = value;
            }
        }

        return fields;
    }

    public IReadOnlyList<PurchasePositionDto> ParsePositions(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var document = _parser.ParseDocument(html);
        var table = document.QuerySelector("#purchaseObjectTruTable1 table.tableBlock")
            ?? document.QuerySelector("#positionKTRU table.tableBlock");
        if (table is null)
        {
            return [];
        }

        // Keep empty header cells so indices stay aligned with body <td> columns.
        var headers = table.QuerySelectorAll("thead th, thead td")
            .Select(h => Normalize(h.TextContent))
            .ToList();
        var codeIdx = FindHeader(headers, "код позиции", "код");
        var nameIdx = FindHeader(headers, "наименование товара", "наименование");
        var unitIdx = FindHeader(headers, "ед. измерения", "единица");
        var qtyIdx = FindHeader(headers, "количество");
        var unitPriceIdx = FindHeader(headers, "цена за ед");
        var amountIdx = FindHeader(headers, "стоимость");

        var positions = new List<PurchasePositionDto>();
        foreach (var row in table.QuerySelectorAll("tbody.tableBlock__body > tr.tableBlock__row"))
        {
            var cells = row.Children.Where(c => c.LocalName is "td" or "th").ToList();
            if (cells.Count < 2)
            {
                continue;
            }

            var codeCell = codeIdx >= 0 && codeIdx < cells.Count ? cells[codeIdx]
                : cells.Count > 1 ? cells[1] : cells[0];
            var nameCell = nameIdx >= 0 && nameIdx < cells.Count ? cells[nameIdx]
                : cells.Count > 2 ? cells[2] : null;
            var name = nameCell is null ? null : CleanName(nameCell);
            if (string.Equals(name, "Наименование характеристики", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (okpd, ktru) = ExtractCodes(codeCell);
            if (string.IsNullOrWhiteSpace(okpd) && string.IsNullOrWhiteSpace(ktru) && string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(okpd) && string.IsNullOrWhiteSpace(ktru) && (name?.Length ?? 0) < 3)
            {
                continue;
            }

            positions.Add(new PurchasePositionDto
            {
                Okpd2Code = okpd,
                KtruCode = ktru,
                Name = name,
                Unit = ReadCell(cells, unitIdx),
                Quantity = ReadNumber(cells, qtyIdx),
                UnitPrice = ReadNumber(cells, unitPriceIdx),
                Amount = ReadNumber(cells, amountIdx),
                IsPrimary = positions.Count == 0
            });
        }

        return positions;
    }

    /// <summary>
    /// First row of the purchase-object / KTRU table ("Наименование товара, работы, услуги").
    /// Used when the notice has no "Наименование объекта закупки" (typical for ст. 93 templates).
    /// </summary>
    public string? TryParseFirstPurchaseObjectName(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var document = _parser.ParseDocument(html);
        var table = document.QuerySelector("#purchaseObjectTruTable1 table.tableBlock")
            ?? document.QuerySelector("#positionKTRU table.tableBlock")
            ?? document.QuerySelector("table.tableBlock");
        if (table is null)
        {
            return null;
        }

        foreach (var row in table.QuerySelectorAll("tbody.tableBlock__body > tr.tableBlock__row"))
        {
            var cells = row.Children.Where(c => c.LocalName is "td" or "th").ToList();
            if (cells.Count < 3)
            {
                continue;
            }

            var nameCell = cells[2];
            foreach (var nested in nameCell.QuerySelectorAll(".section__title, .help-icon, script, style").ToList())
            {
                nested.Remove();
            }

            var name = Normalize(nameCell.TextContent);
            if (!string.IsNullOrWhiteSpace(name) && name.Length >= 3)
            {
                return name;
            }
        }

        return null;
    }

    public static bool IsGenericProcurementTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim();
        return GenericTitle().IsMatch(value);
    }

    private static string? CleanName(IElement nameCell)
    {
        foreach (var nested in nameCell.QuerySelectorAll(".section__title, .help-icon, script, style").ToList())
        {
            nested.Remove();
        }

        var name = Normalize(nameCell.TextContent);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static (string? Okpd, string? Ktru) ExtractCodes(IElement cell)
    {
        string? ktru = null;
        foreach (var href in cell.QuerySelectorAll("a[href*='itemId=']")
                     .Select(a => a.GetAttribute("href"))
                     .Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var m = ItemId().Match(href!);
            if (m.Success)
            {
                ktru = m.Groups["id"].Value;
                break;
            }
        }

        var text = Normalize(cell.TextContent);
        string? okpd = null;
        foreach (Match m in OkpdCode().Matches(text))
        {
            var candidate = m.Value;
            if (ktru is not null && ktru.StartsWith(candidate, StringComparison.Ordinal))
            {
                okpd = candidate;
                break;
            }

            okpd ??= candidate;
        }

        if (okpd is null && ktru is not null)
        {
            var dash = ktru.IndexOf('-');
            okpd = dash > 0 ? ktru[..dash] : null;
        }

        return (okpd, ktru);
    }

    private static string? ReadCell(IReadOnlyList<IElement> cells, int index)
    {
        if (index < 0 || index >= cells.Count)
        {
            return null;
        }

        var value = Normalize(cells[index].TextContent);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static double? ReadNumber(IReadOnlyList<IElement> cells, int index)
    {
        var raw = ReadCell(cells, index);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace("\u00A0", "").Replace(" ", "").Replace(",", ".");
        return double.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int FindHeader(IReadOnlyList<string> headers, params string[] needles)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i].ToLowerInvariant();
            if (needles.Any(n => h.Contains(n, StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Normalize(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        return string.Join(' ', decoded.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(
        @"осуществляем\w*\s+в\s+соответствии\s+с\s+частью\s+\d+\s+статьи\s+93|частью\s+\d+\s+статьи\s+93|ч\.\s*\d+\s*ст\.\s*93|^закупка\s+у\s+единственного\s+поставщика\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericTitle();

    [GeneratedRegex(@"itemId=(?<id>[0-9.\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemId();

    [GeneratedRegex(@"\b\d{2}\.\d{2}(?:\.\d{2}(?:\.\d{1,3})?)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex OkpdCode();
}
