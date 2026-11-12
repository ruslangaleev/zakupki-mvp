using System.Text.Json.Serialization;
using Zakupki.Categorization.Models;

namespace Zakupki.Indexer.Models;

public sealed record OrderMetaDocument
{
    public string? RegNumber { get; init; }
    public string? NoticeUrl { get; init; }
    public string? Title { get; init; }
    public string? Customer { get; init; }
    public string? ObjectName { get; init; }
    public string? Price { get; init; }
    public string? Currency { get; init; }
    public string? Stage { get; init; }
    public string? Law { get; init; }
    public string? PlacedAt { get; init; }
    public DateTimeOffset? PubDate { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
    public List<PurchasePosition>? Positions { get; init; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; init; }
}

public sealed class ExtractedChunk
{
    public required string ChunkId { get; init; }
    public required string Source { get; init; }
    public required string Text { get; init; }
}

public sealed class OrderExtractionResult
{
    public required string RegNumber { get; init; }
    public required OrderMetaDocument Meta { get; init; }
    public required string FullText { get; init; }
    public required IReadOnlyList<ExtractedChunk> Chunks { get; init; }
    public required string Coverage { get; init; }
    public required IReadOnlyList<string> SkippedFiles { get; init; }
}
