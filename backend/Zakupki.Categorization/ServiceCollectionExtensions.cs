using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization.Advance;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddZakupkiCategorization(this IServiceCollection services)
    {
        services.AddSingleton<ITaxonomyCatalog, EmbeddedTaxonomyCatalog>();
        services.AddSingleton<OkpdKtruMapper>();
        services.AddSingleton<MnnDictionary>();
        services.AddSingleton<TrapRules>();
        services.AddSingleton<RuleBasedOrderClassifier>();
        services.AddSingleton<IOrderClassifier, HybridOrderClassifier>();
        services.AddSingleton<IAdvanceDetector, RuleBasedAdvanceDetector>();
        return services;
    }
}
