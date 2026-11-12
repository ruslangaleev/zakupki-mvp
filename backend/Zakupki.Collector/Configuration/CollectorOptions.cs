namespace Zakupki.Collector.Configuration;

public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    public string RssUrl { get; set; } =
        "https://zakupki.gov.ru/epz/order/extendedsearch/rss.html?fz44=on&af=on&sortBy=UPDATE_DATE&sortDirection=false&recordsPerPage=_50";

    public int PollIntervalSeconds { get; set; } = 90;

    public string DataPath { get; set; } = "data/orders";

    public int RequestDelayMs { get; set; } = 700;

    public int MaxConcurrentDownloads { get; set; } = 2;

    public int HttpTimeoutSeconds { get; set; } = 60;

    public int MaxRetries { get; set; } = 3;
}
