namespace Zakupki.Collector.Models;

public sealed class OrderMeta
{
    public required string RegNumber { get; init; }
    public required string NoticeUrl { get; init; }
    public required string DocumentsUrl { get; init; }
    public string? Title { get; init; }
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
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<OrderDocumentInfo> Documents { get; init; } = [];
    public List<PurchasePositionDto> Positions { get; init; } = [];
}

public sealed class PurchasePositionDto
{
    public string? Okpd2Code { get; init; }
    public string? Okpd2Name { get; init; }
    public string? KtruCode { get; init; }
    public string? KtruName { get; init; }
    public string? Name { get; init; }
    public string? Unit { get; init; }
    public double? Quantity { get; init; }
    public double? UnitPrice { get; init; }
    public double? Amount { get; init; }
    public bool IsPrimary { get; init; }
}

