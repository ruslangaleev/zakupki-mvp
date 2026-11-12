using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Zakupki.Indexer.Parsing;

public static partial class PurchaseObjectResolver
{
    private static readonly HtmlParser Parser = new();

    public static bool IsGenericTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return GenericTitle().IsMatch(text.Trim());
    }

    public static string? TryParseFirstPurchaseObjectName(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var document = Parser.ParseDocument(html);
        var table = document.QuerySelector("#purchaseObjectTruTable1 table.tableBlock")
            ?? document.QuerySelector("#positionKTRU table.tableBlock");
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

    public static string? ResolveObjectName(
        string? objectName,
        string? title,
        string? commonInfoHtml,
        IReadOnlyDictionary<string, string>? fields)
    {
        if (!IsGenericTitle(objectName))
        {
            return objectName!.Trim();
        }

        if (fields is not null)
        {
            foreach (var key in new[]
                     {
                         "Наименование объекта закупки",
                         "Наименование товара, работы, услуги",
                         "Наименование товара"
                     })
            {
                if (fields.TryGetValue(key, out var fromField) && !IsGenericTitle(fromField))
                {
                    return fromField.Trim();
                }
            }
        }

        var fromTable = TryParseFirstPurchaseObjectName(commonInfoHtml);
        if (!IsGenericTitle(fromTable))
        {
            return fromTable!.Trim();
        }

        if (!IsGenericTitle(title))
        {
            return title!.Trim();
        }

        return string.IsNullOrWhiteSpace(objectName) ? null : objectName.Trim();
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
}
