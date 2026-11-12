namespace Zakupki.Api.Review;

public sealed class ReviewQueueListResponse
{
    public required IReadOnlyList<ReviewQueueItem> Items { get; init; }
    public int Total { get; init; }
    public int From { get; init; }
    public int Limit { get; init; }
}

public sealed class ReviewQueueItem
{
    public required string RegNumber { get; init; }
    public string? Title { get; init; }
    public string? ObjectName { get; init; }
    public string? CategoryL1Id { get; init; }
    public string? CategoryL2Id { get; init; }
    public double? Confidence { get; init; }
    public string? ClassificationMethod { get; init; }
    public string? ReviewStatus { get; init; }
    public string? MappingVersion { get; init; }
    public string? Reason { get; init; }
}

public sealed class ReviewQueueDetailResponse
{
    public required string RegNumber { get; init; }
    public string? Title { get; init; }
    public string? ObjectName { get; init; }
    public string? ReviewStatus { get; init; }
    public string? Reason { get; init; }
    public double? Confidence { get; init; }
    public string? ClassificationMethod { get; init; }
    public string? MappingVersion { get; init; }
    public string? TaxonomyVersion { get; init; }
    public IReadOnlyList<ReviewCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<ReviewPosition> Positions { get; init; } = [];
}

public sealed class ReviewCandidate
{
    public required string Source { get; init; }
    public string? L1Id { get; init; }
    public string? L2Id { get; init; }
    public double Confidence { get; init; }
    public bool IsPrimary { get; init; }
    public double Share { get; init; }
}

public sealed class ReviewPosition
{
    public string? Okpd2Code { get; init; }
    public string? KtruCode { get; init; }
    public string? Name { get; init; }
    public double? Amount { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed class ReviewDecisionRequest
{
    public required string L1Id { get; init; }
    public required string L2Id { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<ReviewExtraCategory>? ExtraCategories { get; init; }
}

public sealed class ReviewExtraCategory
{
    public required string L1Id { get; init; }
    public required string L2Id { get; init; }
}

public sealed class ReviewOptions
{
    public const string SectionName = "Review";
    /// <summary>Shared secret for POST /api/review/queue/* (header X-Review-Key).</summary>
    public string OperatorKey { get; set; } = "dev-review-key";
}
