namespace Zakupki.Categorization.Advance;

public interface IAdvanceDetector
{
    string Method { get; }
    AdvanceDetectionResult Detect(AdvanceDetectionInput input);
}

public sealed class AdvanceDetectionInput
{
    public required string RegNumber { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<AdvanceTextPart> Texts { get; init; } = [];
}

public sealed class AdvanceTextPart
{
    public required string Source { get; init; }
    public required string Text { get; init; }
}

public sealed class AdvanceDetectionResult
{
    public required bool HasAdvance { get; init; }
    public double? Percent { get; init; }
    public string? Label { get; init; }
    public string? Snippet { get; init; }
    public string? Source { get; init; }
    public required string Method { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}
