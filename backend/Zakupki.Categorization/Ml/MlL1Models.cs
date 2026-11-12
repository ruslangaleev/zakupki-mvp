using Microsoft.ML.Data;

namespace Zakupki.Categorization.Ml;

public sealed class MlL1Input
{
    public string Text { get; set; } = "";
}

public sealed class MlL1Prediction
{
    [ColumnName("PredictedLabel")]
    public string PredictedLabel { get; set; } = "";

    [ColumnName("Score")]
    public float[] Score { get; set; } = [];
}
