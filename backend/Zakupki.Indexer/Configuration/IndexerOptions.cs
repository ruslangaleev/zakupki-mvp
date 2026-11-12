namespace Zakupki.Indexer.Configuration;

public sealed class IndexerOptions
{
    public const string SectionName = "Indexer";

    public string DataPath { get; set; } = "data/orders";
    public string OpenSearchUrl { get; set; } = "http://localhost:9200";
    public string EmbeddingsUrl { get; set; } = "http://localhost:8090";
    public int PollIntervalSeconds { get; set; } = 60;
    public int EmbeddingDimensions { get; set; } = 384;
    public int ChunkSize { get; set; } = 800;
    public int ChunkOverlap { get; set; } = 120;
    public int EmbedBatchSize { get; set; } = 16;
    public int MaxChunksPerOrder { get; set; } = 80;
    public int MinEmbedChunkChars { get; set; } = 80;
    public string OrdersIndex { get; set; } = "zakupki-orders";
    public string ChunksIndex { get; set; } = "zakupki-chunks";

    /// <summary>
    /// Reclassify from meta (+ optional disk fullText) and patch OpenSearch order fields without re-embedding.
    /// </summary>
    public bool ClassifyOnly { get; set; }

    /// <summary>
    /// Recompute advance signal from meta/pages/documents and patch OpenSearch without re-embedding.
    /// </summary>
    public bool AdvanceOnly { get; set; }

    /// <summary>
    /// Patch pubDate / applicationDeadline (and priceValue) from meta.json without re-embedding.
    /// </summary>
    public bool DatesOnly { get; set; }

    /// <summary>
    /// Resolve meaningful objectName (notice / first TRU row) into meta + OpenSearch objectInfo without re-embedding.
    /// </summary>
    public bool ObjectInfoOnly { get; set; }
}
