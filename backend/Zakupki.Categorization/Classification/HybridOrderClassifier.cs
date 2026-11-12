using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Priority: OKPD2/KTRU map → refine → (if still Прочее/queued) text path:
/// MNN → traps → rules-v2. Weak/uncertain → queued.
/// </summary>
public sealed class HybridOrderClassifier(
    ITaxonomyCatalog taxonomy,
    OkpdKtruMapper mapper,
    MnnDictionary mnnDictionary,
    TrapRules trapRules,
    RuleBasedOrderClassifier rules) : IOrderClassifier
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

    private ClassificationResult WithQueued(ClassificationResult source, string? mappingVersion)
    {
        if (!taxonomy.TryGetL2(RuleBasedOrderClassifier.UnclassifiedL2Id, out var l1, out var l2))
        {
            throw new InvalidOperationException("taxonomy must contain unclassified L2");
        }

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = source.Method,
            Confidence = 0.05,
            ReviewStatus = ReviewStatuses.Queued,
            MappingVersion = mappingVersion ?? source.MappingVersion,
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
}
