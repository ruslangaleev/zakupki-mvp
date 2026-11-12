using Microsoft.Extensions.ML;
using Microsoft.Extensions.Options;
using Zakupki.Categorization.Classification;

namespace Zakupki.Categorization.Ml;

public sealed class MlL1Predictor(
    IOptions<ClassificationOptions> options,
    PredictionEnginePool<MlL1Input, MlL1Prediction> pool) : IMlL1Predictor
{
    public const string ModelName = "L1";

    public bool IsAvailable => File.Exists(options.Value.L1ModelPath);

    public (string L1Id, float Confidence)? TryPredict(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var pred = pool.Predict(ModelName, new MlL1Input { Text = text });
            if (string.IsNullOrWhiteSpace(pred.PredictedLabel))
            {
                return null;
            }

            var conf = pred.Score is { Length: > 0 } ? pred.Score.Max() : 0f;
            return (pred.PredictedLabel.Trim(), conf);
        }
        catch
        {
            return null;
        }
    }
}
