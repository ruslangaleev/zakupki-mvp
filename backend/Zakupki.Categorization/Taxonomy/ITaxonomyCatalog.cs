using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Taxonomy;

public interface ITaxonomyCatalog
{
    string Version { get; }
    IReadOnlyList<TaxonomyL1> Roots { get; }
    bool TryGetL2(string l2Id, out TaxonomyL1 l1, out TaxonomyL2 l2);
    bool IsL1(string categoryId);
    bool IsL2(string categoryId);
}
