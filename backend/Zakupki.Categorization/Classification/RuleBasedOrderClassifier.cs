using System.Globalization;
using System.Text;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Keyword scorer with light Russian stemming. Card fields beat document fullText (ADR-008).
/// </summary>
public sealed class RuleBasedOrderClassifier(ITaxonomyCatalog taxonomy) : IOrderClassifier
{
    public const string MethodName = "rules-v2";
    public const string UnclassifiedL2Id = "g25c01";
    private const double SecondaryRatio = 0.85;
    // primary + up to 2 secondary (categories_v2 classification policy)
    private const int MaxAssignments = 3;

    public string Method => MethodName;

    public ClassificationResult Classify(OrderClassificationInput input)
    {
        var objectHay = Normalize(input.ObjectName ?? string.Empty);
        var titleHay = Normalize(input.Title ?? string.Empty);
        var customerHay = Normalize(input.Customer ?? string.Empty);
        var full = input.FullText ?? string.Empty;
        if (full.Length > 6000)
        {
            full = full[..6000];
        }

        var fullHay = Normalize(full);
        var cardScores = new List<(TaxonomyL1 L1, TaxonomyL2 L2, double Score)>();
        var fullScores = new List<(TaxonomyL1 L1, TaxonomyL2 L2, double Score)>();

        foreach (var l1 in taxonomy.Roots)
        {
            foreach (var l2 in l1.Children)
            {
                if (l2.Id == UnclassifiedL2Id || l2.Keywords.Count == 0)
                {
                    continue;
                }

                // Customer names often contain false friends ("продовольствия", "информационных").
                var cardScore =
                    ScoreCategory(objectHay, l2.Keywords) * 6.0 +
                    ScoreCategory(titleHay, l2.Keywords) * 2.5 +
                    ScoreCategory(customerHay, l2.Keywords) * 0.05;

                if (cardScore > 0)
                {
                    cardScores.Add((l1, l2, cardScore));
                }

                var fullScore = ScoreCategory(fullHay, l2.Keywords);
                if (fullScore > 0)
                {
                    fullScores.Add((l1, l2, fullScore * 0.15));
                }
            }
        }

        // Precision-first: if the card matches anything, ignore document-only noise.
        List<(TaxonomyL1 L1, TaxonomyL2 L2, double Score)> scores;
        if (cardScores.Count > 0)
        {
            var fullByL2 = fullScores.ToDictionary(x => x.L2.Id, x => x.Score, StringComparer.Ordinal);
            scores = cardScores
                .Select(c => (c.L1, c.L2, c.Score + (fullByL2.TryGetValue(c.L2.Id, out var bonus) ? bonus : 0)))
                .ToList();
        }
        else
        {
            // No card signal — fullText is mostly EIS boilerplate ("сайт", "портал").
            // Demand a clearer margin and ignore ultra-short single-token hits.
            scores = fullScores
                .Where(s => s.Score >= 0.75)
                .ToList();
        }

        scores.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Weak/noisy matches (often fullText boilerplate) → unclassified rather than random L2.
        const double MinAcceptScore = 1.0;
        if (scores.Count == 0 || scores[0].Score < MinAcceptScore)
        {
            return FallbackUnclassified();
        }

        var top = scores[0];
        var selected = new List<(TaxonomyL1 L1, TaxonomyL2 L2, double Score)> { top };
        foreach (var candidate in scores.Skip(1))
        {
            if (selected.Count >= MaxAssignments)
            {
                break;
            }

            if (candidate.Score >= top.Score * SecondaryRatio)
            {
                selected.Add(candidate);
            }
        }

        var assignments = selected
            .Select((s, index) => new CategoryAssignment
            {
                L1 = new CategoryRef { Id = s.L1.Id, Name = s.L1.Name },
                L2 = new CategoryRef { Id = s.L2.Id, Name = s.L2.Name },
                Score = s.Score,
                IsPrimary = index == 0
            })
            .ToList();

        var confidence = Math.Clamp(top.Score / (top.Score + 4.0), 0.15, 0.98);

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = MethodName,
            Categories = assignments,
            Confidence = confidence,
            ClassifiedAt = DateTimeOffset.UtcNow
        };
    }

    private ClassificationResult FallbackUnclassified()
    {
        if (!taxonomy.TryGetL2(UnclassifiedL2Id, out var l1, out var l2))
        {
            throw new InvalidOperationException($"taxonomy must contain {UnclassifiedL2Id}");
        }

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = MethodName,
            Categories =
            [
                new CategoryAssignment
                {
                    L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                    L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                    Score = 0,
                    IsPrimary = true
                }
            ],
            Confidence = 0.05,
            ClassifiedAt = DateTimeOffset.UtcNow
        };
    }

    private static double ScoreCategory(string haystack, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return 0;
        }

        var padded = $" {haystack} ";
        double score = 0;
        foreach (var raw in keywords)
        {
            var keyword = Normalize(raw);
            if (keyword.Length < 3)
            {
                continue;
            }

            var parts = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (!parts.All(p => TokenMatches(padded, p)))
            {
                continue;
            }

            // Longer / more specific keywords weigh more.
            score += 1.0 + Math.Min(keyword.Length / 10.0, 1.5);
        }

        return score;
    }

    private static bool TokenMatches(string paddedHaystack, string token)
    {
        if (token.Length < 3)
        {
            return false;
        }

        // Match against whole words only — never raw substring across the haystack
        // ("химическ" must not hit inside "биохимические").
        foreach (var word in paddedHaystack.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals(token, StringComparison.Ordinal))
            {
                return true;
            }

            if (SharesRussianStem(token, word))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SharesRussianStem(string a, string b)
    {
        // Reject derived words that only share a short prefix
        // ("монитор"≠"мониторирования", "смесь"≠"смеситель", "водка"≠"воды").
        if (Math.Abs(a.Length - b.Length) > 3)
        {
            return false;
        }

        var shared = 0;
        var limit = Math.Min(a.Length, b.Length);
        while (shared < limit && a[shared] == b[shared])
        {
            shared++;
        }

        var shorter = Math.Min(a.Length, b.Length);
        if (shorter <= 5)
        {
            // Short roots: only light inflection (вода/воды, сок/соков), not водка/воды or кровь/кровать.
            return shared >= shorter - 1
                   && Math.Abs(a.Length - b.Length) <= 1
                   && shared >= Math.Min(4, shorter);
        }

        if (shorter <= 7)
        {
            // "мясо"≈"мяса", "блоки"≈"блока" — still block "сахар"≈"сахарного" via keyword fixes.
            return shared >= shorter - 1;
        }

        // Long tokens: keep prefix strict so "электроды" ≠ "электронный".
        var need = Math.Max(7, shorter - 2);
        return shared >= need;
    }

    private static string Normalize(string text)
    {
        var form = text.ToLower(CultureInfo.GetCultureInfo("ru-RU")).Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(' ');
            }
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
