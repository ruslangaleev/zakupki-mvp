namespace Zakupki.Api.Orders;

public sealed class OrderDetailResponse
{
    public required string RegNumber { get; init; }
    public string? Title { get; init; }
    public string? ObjectName { get; init; }
    public string? Customer { get; init; }
    public string? Price { get; init; }
    public string? Currency { get; init; }
    public string? Stage { get; init; }
    public string? Law { get; init; }
    public string? Ikz { get; init; }
    public string? PlacedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public DateTimeOffset? PubDate { get; init; }
    public DateTimeOffset? CollectedAt { get; init; }
    public DateTimeOffset? ApplicationDeadline { get; init; }
    public string? NoticeUrl { get; init; }
    public string? DocumentsUrl { get; init; }
    public string? CategoryL1Id { get; init; }
    public string? CategoryL1Name { get; init; }
    public string? CategoryL2Id { get; init; }
    public string? CategoryL2Name { get; init; }
    public double? ClassificationConfidence { get; init; }
    public string? ClassificationMethod { get; init; }
    public string? TaxonomyVersion { get; init; }
    public string? ReviewStatus { get; init; }
    public string? MappingVersion { get; init; }
    public IReadOnlyList<OrderCategoryAssignment> Categories { get; init; } = [];
    public IReadOnlyList<string> Okpd2 { get; init; } = [];
    public IReadOnlyList<string> Ktru { get; init; } = [];
    public IReadOnlyList<OrderPurchasePosition> Positions { get; init; } = [];
    public bool HasAdvance { get; init; }
    public double? AdvancePercent { get; init; }
    public string? AdvanceLabel { get; init; }
    public string? AdvanceSnippet { get; init; }
    public IReadOnlyList<OrderFieldItem> Fields { get; init; } = [];
    public IReadOnlyList<OrderDocumentItem> Documents { get; init; } = [];
}

public sealed class OrderFieldItem
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public sealed class OrderDocumentItem
{
    public required string Uid { get; init; }
    public string? FileName { get; init; }
    public string? Title { get; init; }
    public string? DownloadUrl { get; init; }
}

public sealed class OrderCategoryAssignment
{
    public string? L1Id { get; init; }
    public string? L1Name { get; init; }
    public string? L2Id { get; init; }
    public string? L2Name { get; init; }
    public double Score { get; init; }
    public double Share { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed class OrderPurchasePosition
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
