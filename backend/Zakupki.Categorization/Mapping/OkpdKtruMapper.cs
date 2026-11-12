using System.Text.Json;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Mapping;

public sealed class OkpdKtruMapDocument
{
    public string Version { get; init; } = "v1";
    public List<OkpdKtruMapEntry> Prefixes { get; init; } = [];
}

public sealed class OkpdKtruMapEntry
{
    public required string Prefix { get; init; }
    public required string L2Id { get; init; }
}

public sealed class OkpdKtruMapper
{
    private const double MajorityShare = 0.7;
    private const double SecondaryShareMin = 0.15;
    private const int MaxAssignments = 3;

    private readonly ITaxonomyCatalog _taxonomy;
    private readonly List<(string Digits, string L2Id)> _prefixes;
    public string Version { get; }

    public OkpdKtruMapper(ITaxonomyCatalog taxonomy)
    {
        _taxonomy = taxonomy;
        using var stream = typeof(OkpdKtruMapper).Assembly
            .GetManifestResourceStream("Zakupki.Categorization.Mapping.okpd2-map.v1.json")
            ?? throw new InvalidOperationException("Embedded okpd2-map.v1.json is missing");
        var doc = JsonSerializer.Deserialize<OkpdKtruMapDocument>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse okpd2-map.v1.json");

        Version = doc.Version;
        _prefixes = doc.Prefixes
            .Where(p => !string.IsNullOrWhiteSpace(p.Prefix) && !string.IsNullOrWhiteSpace(p.L2Id))
            .Select(p => (DigitsOnly(p.Prefix.Trim()), p.L2Id.Trim()))
            .Where(p => p.Item1.Length > 0)
            .OrderByDescending(p => p.Item1.Length)
            .ToList();
    }

    public ClassificationResult? TryMap(IReadOnlyList<PurchasePosition> positions)
    {
        if (positions.Count == 0)
        {
            return null;
        }

        var mapped = new List<(PurchasePosition Position, string L2Id, string Method, double Weight)>();
        for (var i = 0; i < positions.Count; i++)
        {
            var position = positions[i];
            var hit = MapPosition(position);
            if (hit is null)
            {
                continue;
            }

            mapped.Add((position, hit.Value.L2Id, hit.Value.Method, PositionWeight(position, i)));
        }

        if (mapped.Count == 0)
        {
            return null;
        }

        var totalWeight = mapped.Sum(m => m.Weight);
        if (totalWeight <= 0)
        {
            return null;
        }

        // Aggregate by L1 (keep best L2 per L1 by weight).
        var byL1 = mapped
            .GroupBy(m => ResolveL1(m.L2Id) ?? string.Empty)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.Weight).First();
                return (
                    L1Id: g.Key,
                    L2Id: best.L2Id,
                    Method: best.Method,
                    Weight: g.Sum(x => x.Weight));
            })
            .OrderByDescending(x => x.Weight)
            .ToList();

        if (byL1.Count == 0)
        {
            return null;
        }

        var top = byL1[0];
        var share = top.Weight / totalWeight;
        if (share < MajorityShare && byL1.Count > 1)
        {
            // Ambiguous basket — queue, do not silently pick one L1.
            return BuildQueued();
        }

        if (!_taxonomy.TryGetL2(top.L2Id, out var topL1, out var topL2) ||
            string.Equals(topL2.Id, "g25c01", StringComparison.Ordinal))
        {
            return BuildQueued();
        }

        var assignments = new List<CategoryAssignment>
        {
            new()
            {
                L1 = new CategoryRef { Id = topL1.Id, Name = topL1.Name },
                L2 = new CategoryRef { Id = topL2.Id, Name = topL2.Name },
                Score = top.Weight,
                Share = share,
                IsPrimary = true
            }
        };

        foreach (var secondary in byL1.Skip(1))
        {
            if (assignments.Count >= MaxAssignments)
            {
                break;
            }

            var secondaryShare = secondary.Weight / totalWeight;
            if (secondaryShare < SecondaryShareMin)
            {
                continue;
            }

            if (!_taxonomy.TryGetL2(secondary.L2Id, out var l1, out var l2) ||
                string.Equals(l2.Id, "g25c01", StringComparison.Ordinal))
            {
                continue;
            }

            // L2 must belong to its L1.
            if (!l2.Id.StartsWith(l1.Id, StringComparison.Ordinal))
            {
                continue;
            }

            assignments.Add(new CategoryAssignment
            {
                L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                Score = secondary.Weight,
                Share = secondaryShare,
                IsPrimary = false
            });
        }

        return new ClassificationResult
        {
            TaxonomyVersion = _taxonomy.Version,
            Method = top.Method,
            MappingVersion = Version,
            Confidence = share >= 0.85 ? 0.95 : 0.85,
            ReviewStatus = ReviewStatuses.None,
            Categories = assignments
        };
    }

    private (string L2Id, string Method)? MapPosition(PurchasePosition position)
    {
        if (!string.IsNullOrWhiteSpace(position.KtruCode))
        {
            var code = NormalizeCode(position.KtruCode);
            var l2 = MatchPrefix(code);
            if (l2 is not null)
            {
                return (l2, ClassificationMethods.KtruMap);
            }
        }

        if (!string.IsNullOrWhiteSpace(position.Okpd2Code))
        {
            var code = NormalizeCode(position.Okpd2Code);
            var l2 = MatchPrefix(code);
            if (l2 is not null)
            {
                return (l2, ClassificationMethods.Okpd2Map);
            }
        }

        return null;
    }

    /// <summary>Distinct L2 ids produced by OKPD2/KTRU prefixes for the given positions.</summary>
    public IReadOnlyList<string> DistinctMappedL2Ids(IReadOnlyList<PurchasePosition> positions)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var position in positions)
        {
            var hit = MapPosition(position);
            if (hit is not null)
            {
                set.Add(hit.Value.L2Id);
            }
        }

        return set.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private string? MatchPrefix(string code)
    {
        var digits = DigitsOnly(code);
        if (digits.Length == 0)
        {
            return null;
        }

        foreach (var (prefixDigits, l2Id) in _prefixes)
        {
            if (digits.StartsWith(prefixDigits, StringComparison.Ordinal))
            {
                return l2Id;
            }
        }

        return null;
    }

    private string? ResolveL1(string l2Id) =>
        _taxonomy.TryGetL2(l2Id, out var l1, out _) ? l1.Id : null;

    private ClassificationResult BuildQueued()
    {
        if (!_taxonomy.TryGetL2("g25c01", out var l1, out var l2))
        {
            throw new InvalidOperationException("taxonomy must contain g25c01");
        }

        return new ClassificationResult
        {
            TaxonomyVersion = _taxonomy.Version,
            Method = ClassificationMethods.Okpd2Map,
            MappingVersion = Version,
            Confidence = 0.05,
            ReviewStatus = ReviewStatuses.Queued,
            Categories =
            [
                new CategoryAssignment
                {
                    L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                    L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                    Score = 0,
                    Share = 0,
                    IsPrimary = true
                }
            ]
        };
    }

    private static double PositionWeight(PurchasePosition position, int index)
    {
        var weight = position.Amount is > 0 ? position.Amount.Value : 1.0;
        if (position.IsPrimary)
        {
            weight *= 1.5;
        }

        // Tiny index bias for stable ties.
        return weight + (0.0001 * Math.Max(0, 1000 - index));
    }

    private static string NormalizeCode(string raw)
    {
        var value = raw.Trim();
        var dash = value.IndexOf('-');
        if (dash > 0)
        {
            value = value[..dash];
        }

        return value.Trim();
    }

    private static string DigitsOnly(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var n = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                buffer[n++] = ch;
            }
        }

        return n == 0 ? string.Empty : new string(buffer[..n]);
    }
}
