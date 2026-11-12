namespace Zakupki.Categorization.Ml;

public interface IMlL1Predictor
{
    bool IsAvailable { get; }

    /// <summary>Returns L1 id and max class probability, or null if model unavailable / empty text.</summary>
    (string L1Id, float Confidence)? TryPredict(string text);
}
