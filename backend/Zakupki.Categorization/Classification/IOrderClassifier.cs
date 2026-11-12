using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Assigns L1/L2 categories to an order. Swap implementations (rules → embeddings/LLM)
/// without changing Indexer/API pipeline (ADR-008).
/// </summary>
public interface IOrderClassifier
{
    string Method { get; }

    ClassificationResult Classify(OrderClassificationInput input);
}
