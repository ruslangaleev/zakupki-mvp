namespace Zakupki.Categorization.Ml;

public sealed class NullMlL1Predictor : IMlL1Predictor
{
    public bool IsAvailable => false;

    public (string L1Id, float Confidence)? TryPredict(string text) => null;
}
