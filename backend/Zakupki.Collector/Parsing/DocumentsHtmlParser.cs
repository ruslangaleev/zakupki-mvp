using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Zakupki.Collector.Models;

namespace Zakupki.Collector.Parsing;

public sealed partial class DocumentsHtmlParser
{
    private readonly HtmlParser _parser = new();

    public IReadOnlyList<OrderDocumentInfo> Parse(string html)
    {
        var document = _parser.ParseDocument(html);
        var results = new List<OrderDocumentInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in document.QuerySelectorAll("a[href*='filestore'][href*='uid=']"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                href = "https://zakupki.gov.ru" + (href.StartsWith('/') ? href : "/" + href);
            }

            var uidMatch = UidRegex().Match(href);
            if (!uidMatch.Success)
            {
                continue;
            }

            var uid = uidMatch.Groups[1].Value;
            if (!seen.Add(uid))
            {
                continue;
            }

            var fileName = anchor.GetAttribute("title");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = null;
            }

            var title = Normalize(anchor.TextContent);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = fileName;
            }

            results.Add(new OrderDocumentInfo
            {
                Uid = uid,
                DownloadUrl = href,
                FileName = fileName,
                Title = title
            });
        }

        return results;
    }

    private static string Normalize(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        return string.Join(' ', decoded.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"[?&]uid=([A-Fa-f0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UidRegex();
}
