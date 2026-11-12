namespace Zakupki.Collector.Parsing;

public static class NoticeUrlHelper
{
    public static string ToDocumentsUrl(string noticeUrl)
    {
        if (string.IsNullOrWhiteSpace(noticeUrl))
        {
            throw new ArgumentException("Notice URL is empty.", nameof(noticeUrl));
        }

        var uri = new Uri(noticeUrl);
        var path = uri.AbsolutePath;

        if (path.Contains("common-info.html", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Replace("common-info.html", "documents.html", StringComparison.OrdinalIgnoreCase);
        }
        else if (!path.Contains("documents.html", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback: keep query and swap last segment when possible.
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (segments.Count > 0)
            {
                segments[^1] = "documents.html";
                path = "/" + string.Join('/', segments);
            }
        }

        return $"{uri.Scheme}://{uri.Authority}{path}{uri.Query}";
    }

    public static string ToCommonInfoUrl(string noticeUrl)
    {
        if (string.IsNullOrWhiteSpace(noticeUrl))
        {
            throw new ArgumentException("Notice URL is empty.", nameof(noticeUrl));
        }

        var uri = new Uri(noticeUrl);
        var path = uri.AbsolutePath;

        if (path.Contains("documents.html", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Replace("documents.html", "common-info.html", StringComparison.OrdinalIgnoreCase);
        }

        return $"{uri.Scheme}://{uri.Authority}{path}{uri.Query}";
    }
}
