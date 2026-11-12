namespace Zakupki.Api.Configuration;

public sealed class SearchOptions
{
    public const string SectionName = "Search";

    public string OpenSearchUrl { get; set; } = "http://localhost:9200";
    public string EmbeddingsUrl { get; set; } = "http://localhost:8090";
    public int EmbeddingDimensions { get; set; } = 384;
    public string OrdersIndex { get; set; } = "zakupki-orders";
    public string ChunksIndex { get; set; } = "zakupki-chunks";
    public int DefaultLimit { get; set; } = 20;
    public int MaxLimit { get; set; } = 50;
}
