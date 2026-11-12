using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ML;
using Microsoft.Extensions.Options;
using Zakupki.Categorization.Advance;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Llm;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Ml;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddZakupkiCategorization(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        bool enableMl = true)
    {
        if (configuration is not null)
        {
            services.Configure<ClassificationOptions>(configuration.GetSection(ClassificationOptions.SectionName));
            services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        }
        else
        {
            services.AddOptions<ClassificationOptions>();
            services.AddOptions<LlmOptions>();
        }

        services.AddSingleton<ITaxonomyCatalog, EmbeddedTaxonomyCatalog>();
        services.AddSingleton<OkpdKtruMapper>();
        services.AddSingleton<MnnDictionary>();
        services.AddSingleton<TrapRules>();
        services.AddSingleton<RuleBasedOrderClassifier>();
        services.AddSingleton<IOrderClassifier, HybridOrderClassifier>();
        services.AddSingleton<IAdvanceDetector, RuleBasedAdvanceDetector>();
        services.AddSingleton<ILlmClient>(sp => CreateLlmClient(sp.GetRequiredService<IOptions<LlmOptions>>().Value));

        var modelPath = ResolveModelPath(configuration);
        if (enableMl && !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
        {
            Console.WriteLine($"[categorization] ML L1 model loaded: {modelPath}");
            services.AddPredictionEnginePool<MlL1Input, MlL1Prediction>()
                .FromFile(MlL1Predictor.ModelName, modelPath, watchForChanges: false);
            services.AddSingleton<IMlL1Predictor, MlL1Predictor>();
        }
        else
        {
            Console.WriteLine(
                enableMl
                    ? $"[categorization] ML L1 model not found (looked for {modelPath}); using rules-only fallback"
                    : "[categorization] ML L1 disabled");
            services.AddSingleton<IMlL1Predictor, NullMlL1Predictor>();
        }

        return services;
    }

    /// <summary>
    /// Live HTTP providers are wired in a follow-up issue. Until then always <see cref="FakeLlmClient"/>
    /// (even if <c>LLM_API_KEY</c> is set), so classification stays offline-safe.
    /// </summary>
    public static ILlmClient CreateLlmClient(LlmOptions options)
    {
        var hasKey = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(LlmOptions.ApiKeyEnvironmentVariable));
        if (hasKey &&
            !string.Equals(options.Provider, "fake", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"[categorization] LLM_API_KEY present (provider={options.Provider}, model={options.Model}); " +
                "live client not wired yet — using FakeLlmClient");
        }

        return new FakeLlmClient();
    }

    private static string ResolveModelPath(IConfiguration? configuration)
    {
        var configured = configuration?["Classification:L1ModelPath"];
        var path = string.IsNullOrWhiteSpace(configured) ? "models/l1.zip" : configured!;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }
}
