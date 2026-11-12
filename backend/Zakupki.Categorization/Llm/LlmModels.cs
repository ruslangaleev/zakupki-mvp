namespace Zakupki.Categorization.Llm;

public sealed class LlmClassificationRequest
{
    public required string RegNumber { get; init; }
    public string Title { get; init; } = "";
    public string? ObjectName { get; init; }
    public IReadOnlyList<string> Okpd2Codes { get; init; } = [];
    public IReadOnlyList<string> KtruCodes { get; init; } = [];
    public string? DescriptionSnippet { get; init; }
}

public sealed class LlmClassificationResponse
{
    public string? L1Id { get; init; }
    public string? L2Id { get; init; }
    public IReadOnlyList<string> Extra { get; init; } = [];
    public double Confidence { get; init; }
    public bool Unsure { get; init; }
    public string? RawText { get; init; }
}

public interface ILlmClient
{
    /// <summary>True when a live provider is configured (API key present). Fake is always available.</summary>
    bool IsLiveProvider { get; }

    Task<LlmClassificationResponse> ClassifyAsync(
        LlmClassificationRequest request,
        CancellationToken cancellationToken = default);
}
