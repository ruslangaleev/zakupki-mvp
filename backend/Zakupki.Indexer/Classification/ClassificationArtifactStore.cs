using System.Text.Json;
using Zakupki.Categorization.Models;

namespace Zakupki.Indexer.Classification;

public sealed class ClassificationArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task WriteAsync(
        string orderDirectory,
        ClassificationResult result,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(orderDirectory, "classification.json");
        var payload = new
        {
            result.TaxonomyVersion,
            result.Method,
            result.Confidence,
            result.ReviewStatus,
            result.MappingVersion,
            classifiedAt = result.ClassifiedAt,
            categories = result.Categories.Select(c => new
            {
                l1Id = c.L1.Id,
                l1Name = c.L1.Name,
                l2Id = c.L2?.Id,
                l2Name = c.L2?.Name,
                c.Score,
                c.Share,
                c.IsPrimary
            })
        };

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(payload, JsonOptions),
            cancellationToken);
    }

    public async Task<StoredClassification?> TryReadAsync(
        string orderDirectory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(orderDirectory, "classification.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredClassification>(stream, JsonOptions, cancellationToken);
    }
}

public sealed class StoredClassification
{
    public string? TaxonomyVersion { get; set; }
    public string? Method { get; set; }
    public double Confidence { get; set; }
    public string? ReviewStatus { get; set; }
    public string? MappingVersion { get; set; }
    public DateTimeOffset? ClassifiedAt { get; set; }
    public List<StoredCategoryAssignment>? Categories { get; set; }

    public StoredCategoryAssignment? Primary =>
        Categories?.FirstOrDefault(c => c.IsPrimary) ?? Categories?.FirstOrDefault();
}

public sealed class StoredCategoryAssignment
{
    public string? L1Id { get; set; }
    public string? L1Name { get; set; }
    public string? L2Id { get; set; }
    public string? L2Name { get; set; }
    public double Score { get; set; }
    public double Share { get; set; }
    public bool IsPrimary { get; set; }
}
