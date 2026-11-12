using System.Text.Json;
using Zakupki.Categorization.Advance;

namespace Zakupki.Indexer.Advance;

public sealed class AdvanceArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task WriteAsync(
        string orderDirectory,
        AdvanceDetectionResult result,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(orderDirectory, "advance.json");
        var payload = new
        {
            hasAdvance = result.HasAdvance,
            percent = result.Percent,
            label = result.Label,
            snippet = result.Snippet,
            source = result.Source,
            method = result.Method,
            confidence = result.Confidence,
            detectedAt = result.DetectedAt
        };

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(payload, JsonOptions),
            cancellationToken);
    }

    public async Task<StoredAdvance?> TryReadAsync(
        string orderDirectory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(orderDirectory, "advance.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredAdvance>(stream, JsonOptions, cancellationToken);
    }
}

public sealed class StoredAdvance
{
    public bool HasAdvance { get; set; }
    public double? Percent { get; set; }
    public string? Label { get; set; }
    public string? Snippet { get; set; }
    public string? Source { get; set; }
    public string? Method { get; set; }
    public double Confidence { get; set; }
}
