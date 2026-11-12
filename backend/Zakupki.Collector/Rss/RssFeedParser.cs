using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Zakupki.Collector.Models;

namespace Zakupki.Collector.Rss;

public sealed partial class RssFeedParser
{
    public IReadOnlyList<RssOrderItem> Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var items = new List<RssOrderItem>();

        foreach (var item in document.Descendants("item"))
        {
            var title = item.Element("title")?.Value?.Trim() ?? string.Empty;
            var link = item.Element("link")?.Value?.Trim() ?? string.Empty;
            var description = item.Element("description")?.Value ?? string.Empty;
            var author = item.Element("author")?.Value?.Trim();
            var pubDateRaw = item.Element("pubDate")?.Value?.Trim();

            var regNumber = ExtractRegNumber(link) ?? ExtractRegNumber(title);
            if (string.IsNullOrWhiteSpace(regNumber) || string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            DateTimeOffset? pubDate = null;
            if (!string.IsNullOrWhiteSpace(pubDateRaw)
                && DateTimeOffset.TryParse(pubDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                pubDate = parsed;
            }

            var decodedDescription = WebUtility.HtmlDecode(description);

            items.Add(new RssOrderItem
            {
                RegNumber = regNumber,
                Title = title,
                Url = link,
                Customer = author ?? ExtractStrongValue(decodedDescription, "Наименование Заказчика"),
                ObjectName = ExtractStrongValue(decodedDescription, "Наименование объекта закупки"),
                Price = ExtractStrongValue(decodedDescription, "Начальная цена контракта"),
                Currency = ExtractStrongValue(decodedDescription, "Валюта"),
                Stage = ExtractStrongValue(decodedDescription, "Этап размещения")
                    ?? ExtractStrongValue(decodedDescription, "Этап закупки"),
                Law = ExtractStrongValue(decodedDescription, "Размещение выполняется по")
                    ?? ExtractStrongValue(decodedDescription, "Закупки по"),
                Ikz = ExtractStrongValue(decodedDescription, "Идентификационный код закупки (ИКЗ)"),
                PlacedAt = ExtractStrongValue(decodedDescription, "Размещено"),
                UpdatedAt = ExtractStrongValue(decodedDescription, "Обновлено"),
                PubDate = pubDate,
                DescriptionHtml = description
            });
        }

        return items;
    }

    private static string? ExtractRegNumber(string value)
    {
        var match = RegNumberRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static string? ExtractStrongValue(string html, string label)
    {
        var pattern = $@"<strong>\s*{Regex.Escape(label)}\s*:?\s*</strong>\s*(.*?)(?:<br\s*/?>|</p>|<strong>|$)";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var text = Regex.Replace(match.Groups[1].Value, "<.*?>", string.Empty);
        text = WebUtility.HtmlDecode(text).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [GeneratedRegex(@"regNumber=(\d{18,19})|№\s*(\d{18,19})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RegNumberRegex();
}
