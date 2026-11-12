using Zakupki.Collector.Http;

namespace Zakupki.Collector.Rss;

public sealed class RssFeedClient
{
    private readonly RateLimitedHttpGateway _http;
    private readonly RssFeedParser _parser;

    public RssFeedClient(RateLimitedHttpGateway http, RssFeedParser parser)
    {
        _http = http;
        _parser = parser;
    }

    public async Task<IReadOnlyList<Models.RssOrderItem>> FetchAsync(string rssUrl, CancellationToken cancellationToken)
    {
        var xml = await _http.GetStringAsync(rssUrl, cancellationToken);
        return _parser.Parse(xml);
    }
}
