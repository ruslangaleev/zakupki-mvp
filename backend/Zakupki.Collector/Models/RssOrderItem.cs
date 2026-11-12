namespace Zakupki.Collector.Models;

public sealed class RssOrderItem
{
    public required string RegNumber { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Customer { get; init; }
    public string? ObjectName { get; init; }
    public string? Price { get; init; }
    public string? Currency { get; init; }
    public string? Stage { get; init; }
    public string? Law { get; init; }
    public string? Ikz { get; init; }
    public string? PlacedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public DateTimeOffset? PubDate { get; init; }
    public string? DescriptionHtml { get; init; }
}
