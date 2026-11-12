using Microsoft.Extensions.Options;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Ml;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Priority: OKPD2/KTRU map → refine → (if still Прочее/queued) text path:
/// MNN → traps → ML L1 (no codes only) → rules-v2. Weak/uncertain → queued.
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
        if (mapped is null && mlL1.IsAvailable)
        {
            var text = BuildText(input);
            var hit = mlL1.TryPredict(text);
            if (hit is not null)
            {
                var (l1Id, confidence) = hit.Value;
                if (confidence >= classificationOptions.Value.AutoThreshold)
                {
                    var built = BuildFromL1(l1Id, confidence, ClassificationMethods.MlL1);
                    if (built is not null)
                    {
                        return built;
                    }
                }

                return BuildQueued(ClassificationMethods.MlL1, mappingVersion: null);
            }
        }

        var rulesResult = rules.Classify(input);
        if (rulesResult.Primary is null ||
            string.Equals(rulesResult.Primary.L2.Id, RuleBasedOrderClassifier.UnclassifiedL2Id, StringComparison.Ordinal) ||
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
        string.Equals(result.Primary?.L2.Id, RuleBasedOrderClassifier.UnclassifiedL2Id, StringComparison.Ordinal);

    private ClassificationResult? BuildFromL1(string l1Id, float confidence, string method)
    {
        var l1 = taxonomy.Roots.FirstOrDefault(r => string.Equals(r.Id, l1Id, StringComparison.Ordinal));
        var l2 = l1?.Children.FirstOrDefault();
        if (l1 is null || l2 is null)
        {
            return null;
        }

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = method,
            Confidence = confidence,
            ReviewStatus = ReviewStatuses.None,
            Categories =
            [
                new CategoryAssignment
                {
                    L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                    L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                    Score = confidence,
                    Share = 1,
                    IsPrimary = true
                }
            ]
        };
    }

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
