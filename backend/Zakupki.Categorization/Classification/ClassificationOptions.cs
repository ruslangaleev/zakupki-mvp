namespace Zakupki.Categorization.Classification;

/// <summary>
/// Decision thresholds for hybrid classifier (ADR-013 / issue #8 task Д).
/// </summary>
public sealed class ClassificationOptions
{
    public const string SectionName = "Classification";

    /// <summary>Minimum model probability for auto-accept (prototype default 0.65).</summary>
    public double AutoThreshold { get; set; } = 0.65;

    /// <summary>L1 share required for majority code auto-accept.</summary>
    public double CodeMajorityShare { get; set; } = 0.7;

    /// <summary>Path to ML.NET L1 model zip; missing file = ML layer disabled.</summary>
    public string L1ModelPath { get; set; } = "models/l1.zip";
}
