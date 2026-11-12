using System.Reflection;
using System.Text.Json;
using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Taxonomy;

public sealed class EmbeddedTaxonomyCatalog : ITaxonomyCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, (TaxonomyL1 L1, TaxonomyL2 L2)> _byL2;
    private readonly HashSet<string> _l1Ids;

    public EmbeddedTaxonomyCatalog()
    {
        var assembly = typeof(EmbeddedTaxonomyCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("taxonomy.v2.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded taxonomy.v2.json not found");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Cannot open resource {resourceName}");

        var doc = JsonSerializer.Deserialize<TaxonomyDocument>(stream, JsonOptions)
                  ?? throw new InvalidOperationException("Failed to deserialize taxonomy");

        Validate(doc);
        Version = doc.Version;
        Roots = doc.Categories;
        _l1Ids = new HashSet<string>(Roots.Select(r => r.Id), StringComparer.Ordinal);
        _byL2 = new Dictionary<string, (TaxonomyL1, TaxonomyL2)>(StringComparer.Ordinal);
        foreach (var l1 in Roots)
        {
            foreach (var l2 in l1.Children)
            {
                _byL2[l2.Id] = (l1, l2);
            }
        }
    }

    public string Version { get; }
    public IReadOnlyList<TaxonomyL1> Roots { get; }

    public bool TryGetL2(string l2Id, out TaxonomyL1 l1, out TaxonomyL2 l2)
    {
        if (_byL2.TryGetValue(l2Id, out var pair))
        {
            l1 = pair.L1;
            l2 = pair.L2;
            return true;
        }

        l1 = null!;
        l2 = null!;
        return false;
    }

    public bool IsL1(string categoryId) => _l1Ids.Contains(categoryId);

    public bool IsL2(string categoryId) => _byL2.ContainsKey(categoryId);

    private static void Validate(TaxonomyDocument doc)
    {
        if (doc.Categories.Count == 0)
        {
            throw new InvalidOperationException("Taxonomy has no L1 categories");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l1 in doc.Categories)
        {
            if (string.IsNullOrWhiteSpace(l1.Id) || !ids.Add(l1.Id))
            {
                throw new InvalidOperationException($"Invalid/duplicate L1 id: {l1.Id}");
            }

            if (l1.Children.Count == 0)
            {
                throw new InvalidOperationException($"L1 {l1.Id} has no L2 children");
            }

            foreach (var l2 in l1.Children)
            {
                if (string.IsNullOrWhiteSpace(l2.Id) || !ids.Add(l2.Id))
                {
                    throw new InvalidOperationException($"Invalid/duplicate L2 id: {l2.Id}");
                }
            }
        }

        if (!ids.Contains("g25c01"))
        {
            throw new InvalidOperationException("Taxonomy must contain unclassified L2 id g25c01");
        }
    }
}
