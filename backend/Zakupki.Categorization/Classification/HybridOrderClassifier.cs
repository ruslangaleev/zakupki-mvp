using Microsoft.Extensions.Options;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Ml;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Priority: OKPD2/KTRU map → refine → (if still Прочее/queued) text path:
/// MNN → traps → ML L1 (no codes, conf≥threshold) → rules-v2. Queue only if rules say Прочее.
/// </summary>
public sealed class HybridOrderClassifier(
    ITaxonomyCatalog taxonomy,
    OkpdKtruMapper mapper,
    MnnDictionary mnnDictionary,
    TrapRules trapRules,
    RuleBasedOrderClassifier rules,
    IMlL1Predictor mlL1,
    IOptions<ClassificationOptions> classificationOptions) : IOrderClassifier
{
    public string Method => "hybrid-v1";

    public ClassificationResult Classify(OrderClassificationInput input)
    {
        var mapped = mapper.TryMap(input.Positions);
        if (mapped is not null)
        {
            var refined = trapRules.TryRefine(input, mapped) ?? mapped;
            if (!IsUnclassifiedOrQueued(refined))
            {
                return refined;
            }

            // OKPD said «Прочее» / conflict-queue — still try text signals (охрана, реагенты, …).
        }

        var mnn = mnnDictionary.TryClassify(input);
        if (mnn is not null)
        {
            return mnn;
        }

        var trap = trapRules.TryClassify(input);
        if (trap is not null)
        {
            return trap;
        }

        // ML L1 only when purchase has no usable OKPD2/KTRU codes.
        // Below threshold → fall through to rules-v2 (do not queue from ML alone).
        if (mapped is null && mlL1.IsAvailable)
        {
            var text = BuildText(input);
            var hit = mlL1.TryPredict(text);
            if (hit is not null)
            {
                var (l1Id, confidence) = hit.Value;
                if (confidence >= classificationOptions.Value.AutoThreshold)
                {
                    var built = BuildFromMlL1(input, l1Id, confidence);
                    if (built is not null)
                    {
                        return built;
                    }
                }
            }
        }

        var rulesResult = rules.Classify(input);
        if (rulesResult.Primary is null ||
            string.Equals(rulesResult.Primary.L2?.Id, RuleBasedOrderClassifier.UnclassifiedL2Id, StringComparison.Ordinal) ||
            rulesResult.Confidence <= 0.05)
        {
            return WithQueued(rulesResult, mapped?.MappingVersion);
        }

        return new ClassificationResult
        {
            TaxonomyVersion = rulesResult.TaxonomyVersion,
            Method = rulesResult.Method,
            Confidence = rulesResult.Confidence,
            ReviewStatus = ReviewStatuses.None,
            MappingVersion = mapped?.MappingVersion ?? rulesResult.MappingVersion,
            Categories = rulesResult.Categories,
            ClassifiedAt = rulesResult.ClassifiedAt
        };
    }

    private static bool IsUnclassifiedOrQueued(ClassificationResult result) =>
        result.ReviewStatus == ReviewStatuses.Queued ||
        string.Equals(result.Primary?.L2?.Id, RuleBasedOrderClassifier.UnclassifiedL2Id, StringComparison.Ordinal);

    /// <summary>
    /// ML predicts L1 only; pick L2 via rules-v2 inside that L1, else queue.
    /// </summary>
    private ClassificationResult? BuildFromMlL1(OrderClassificationInput input, string l1Id, float confidence)
    {
        var l1 = taxonomy.Roots.FirstOrDefault(r => string.Equals(r.Id, l1Id, StringComparison.Ordinal));
        if (l1 is null)
        {
            return null;
        }

        var rulesResult = rules.Classify(input);
        var rulesL2 = rulesResult.Primary?.L2;
        if (rulesL2 is not null &&
            !string.Equals(rulesL2.Id, RuleBasedOrderClassifier.UnclassifiedL2Id, StringComparison.Ordinal) &&
            string.Equals(rulesResult.Primary!.L1.Id, l1Id, StringComparison.Ordinal))
        {
            return new ClassificationResult
            {
                TaxonomyVersion = taxonomy.Version,
                Method = ClassificationMethods.MlL1,
                Confidence = confidence,
                ReviewStatus = ReviewStatuses.None,
                Categories =
                [
                    new CategoryAssignment
                    {
                        L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                        L2 = new CategoryRef { Id = rulesL2.Id, Name = rulesL2.Name },
                        Score = confidence,
                        Share = 1,
                        IsPrimary = true
                    }
                ]
            };
        }

        // Keep ML L1; no in-L1 L2 from rules → queue without inventing a child L2.
        var generalL2 = FindGeneralL2(l1);
        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = ClassificationMethods.MlL1,
            Confidence = confidence,
            ReviewStatus = ReviewStatuses.Queued,
            Categories =
            [
                new CategoryAssignment
                {
                    L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                    L2 = generalL2 is null
                        ? null
                        : new CategoryRef { Id = generalL2.Id, Name = generalL2.Name },
                    Score = confidence,
                    Share = 0,
                    IsPrimary = true
                }
            ]
        };
    }

    /// <summary>
    /// Catch-all L2 only: exact «Прочее»/«Иное» or name ending with «(общие)» / «(общее)».
    /// Avoid false hits like «общепит».
    /// </summary>
    private static TaxonomyL2? FindGeneralL2(TaxonomyL1 l1) =>
        l1.Children.FirstOrDefault(c =>
        {
            var n = c.Name.Trim().ToLowerInvariant();
            return n is "прочее" or "иное" ||
                   n.EndsWith("(общие)", StringComparison.Ordinal) ||
                   n.EndsWith("(общее)", StringComparison.Ordinal);
        });

    private ClassificationResult BuildQueued(string method, string? mappingVersion)
    {
        if (!taxonomy.TryGetL2(RuleBasedOrderClassifier.UnclassifiedL2Id, out var l1, out var l2))
        {
            throw new InvalidOperationException("taxonomy must contain unclassified L2");
        }

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = method,
            Confidence = 0.05,
            ReviewStatus = ReviewStatuses.Queued,
            MappingVersion = mappingVersion,
            Categories =
            [
                new CategoryAssignment
                {
                    L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                    L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                    Score = 0,
                    IsPrimary = true
                }
            ]
        };
    }

    private ClassificationResult WithQueued(ClassificationResult source, string? mappingVersion) =>
        BuildQueued(source.Method, mappingVersion ?? source.MappingVersion);

    private static string BuildText(OrderClassificationInput input)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.Title))
        {
            parts.Add(input.Title!);
        }

        if (!string.IsNullOrWhiteSpace(input.ObjectName) &&
            !string.Equals(input.ObjectName, input.Title, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(input.ObjectName!);
        }

        foreach (var p in input.Positions)
        {
            if (!string.IsNullOrWhiteSpace(p.Name))
            {
                parts.Add(p.Name!);
            }
        }

        return string.Join(' ', parts);
    }
}
