namespace Zakupki.Categorization.Models;

public sealed class TaxonomyDocument
{
    public string Version { get; init; } = "v2";
    public List<TaxonomyL1> Categories { get; init; } = [];
}

public sealed class TaxonomyL1
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<TaxonomyL2> Children { get; init; } = [];
}

public sealed class TaxonomyL2
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<string> Keywords { get; init; } = [];
}

public sealed class CategoryRef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class CategoryAssignment
{
    public required CategoryRef L1 { get; init; }
    public required CategoryRef L2 { get; init; }
    public double Score { get; init; }
    /// <summary>Share of basket weight in [0..1] when known (OKPD multimarkers).</summary>
    public double Share { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed class OrderClassificationInput
{
    public required string RegNumber { get; init; }
    public string? Title { get; init; }
    public string? ObjectName { get; init; }
    public string? Customer { get; init; }
    public string? FullText { get; init; }
    public IReadOnlyList<PurchasePosition> Positions { get; init; } = [];
}

/// <summary>Position from EIS purchase-object / KTRU table (not IKZ).</summary>
public sealed class PurchasePosition
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

public static class ClassificationMethods
{
    public const string Okpd2Map = "okpd2-map";
    public const string KtruMap = "ktru-map";
    public const string RulesV2 = "rules-v2";
    public const string InnDict = "inn-dict";
    public const string Llm = "llm";
    public const string Manual = "manual";
}

public static class ReviewStatuses
{
    public const string None = "none";
    public const string Queued = "queued";
    public const string Confirmed = "confirmed";
    public const string Corrected = "corrected";
}

public sealed class ClassificationResult
{
    public required string TaxonomyVersion { get; init; }
    public required string Method { get; init; }
    public required IReadOnlyList<CategoryAssignment> Categories { get; init; }
    public double Confidence { get; init; }
    public string ReviewStatus { get; init; } = ReviewStatuses.None;
    public string? MappingVersion { get; init; }
    public DateTimeOffset ClassifiedAt { get; init; } = DateTimeOffset.UtcNow;

    public CategoryAssignment? Primary =>
        Categories.FirstOrDefault(c => c.IsPrimary) ?? Categories.FirstOrDefault();
}
