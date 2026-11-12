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
    private readonly ITaxonomyCatalog _taxonomy;
    private readonly List<(string Prefix, string L2Id)> _prefixes;
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
            .Select(p => (p.Prefix.Trim(), p.L2Id.Trim()))
            .OrderByDescending(p => p.Item1.Length)
            .ToList();
    }

    public ClassificationResult? TryMap(IReadOnlyList<PurchasePosition> positions)
    {
        if (positions.Count == 0)
        {
            return null;
        }

        var ranked = positions
            .Select((p, index) => (Position: p, Index: index, Weight: PositionWeight(p, index)))
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Index)
            .ToList();

        var mapped = new List<(PurchasePosition Position, string L2Id, string Method, double Weight)>();
        foreach (var item in ranked)
        {
            var hit = MapPosition(item.Position);
            if (hit is null)
            {
                continue;
            }

            mapped.Add((item.Position, hit.Value.L2Id, hit.Value.Method, item.Weight));
        }

        if (mapped.Count == 0)
        {
            return null;
        }

        var top = mapped[0];
        var conflicting = mapped
            .Where(m => !string.Equals(ResolveL1(m.L2Id), ResolveL1(top.L2Id), StringComparison.Ordinal))
            .ToList();
        if (conflicting.Count > 0 && conflicting.Sum(c => c.Weight) >= top.Weight * 0.8)
        {
            // Ambiguous multi-position basket → queue, not silent Прочее.
            return BuildQueued();
        }

        if (!_taxonomy.TryGetL2(top.L2Id, out var l1, out var l2))
        {
            return BuildQueued();
        }

        if (string.Equals(l2.Id, "g25c01", StringComparison.Ordinal))
        {
            return BuildQueued();
        }

        return new ClassificationResult
        {
            TaxonomyVersion = _taxonomy.Version,
            Method = top.Method,
            MappingVersion = Version,
            Confidence = 0.95,
            ReviewStatus = ReviewStatuses.None,
            Categories =
            [
                new CategoryAssignment
                {
                    L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                    L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                    Score = 20,
                    IsPrimary = true
                }
            ]
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

    private string? MatchPrefix(string code)
    {
        foreach (var (prefix, l2Id) in _prefixes)
        {
            if (code.StartsWith(prefix.TrimEnd('.'), StringComparison.Ordinal) ||
                code.StartsWith(prefix, StringComparison.Ordinal))
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
                    IsPrimary = true
                }
            ]
        };
    }

    private static double PositionWeight(PurchasePosition position, int index)
    {
        if (position.IsPrimary)
        {
            return 1_000_000;
        }

        if (position.Amount is > 0)
        {
            return position.Amount.Value;
        }

        return 1_000 - index;
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
}
