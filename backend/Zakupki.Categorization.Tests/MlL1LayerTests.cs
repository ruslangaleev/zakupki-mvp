using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Ml;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Tests;

public sealed class MlL1LayerTests
{
    [Fact]
    public void Without_model_file_predictor_is_null_implementation()
    {
        var services = new ServiceCollection();
        services.AddZakupkiCategorization();
        using var sp = services.BuildServiceProvider();
        var predictor = sp.GetRequiredService<IMlL1Predictor>();
        Assert.False(predictor.IsAvailable);
        Assert.IsType<NullMlL1Predictor>(predictor);
    }

    [Fact]
    public void No_code_order_still_classifies_via_rules_when_ml_absent()
    {
        var services = new ServiceCollection();
        services.AddZakupkiCategorization();
        using var sp = services.BuildServiceProvider();
        var classifier = sp.GetRequiredService<IOrderClassifier>();

        var result = classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "test-no-code",
            Title = "Охрана объекта круглосуточно",
            Positions = []
        });

        Assert.NotNull(result.Primary);
        Assert.NotEqual(ClassificationMethods.MlL1, result.Method);
    }

    [Fact]
    public void Low_confidence_ml_falls_through_to_same_result_as_rules()
    {
        var services = new ServiceCollection();
        services.AddOptions<ClassificationOptions>();
        services.AddSingleton<ITaxonomyCatalog, EmbeddedTaxonomyCatalog>();
        services.AddSingleton<OkpdKtruMapper>();
        services.AddSingleton<MnnDictionary>();
        services.AddSingleton<TrapRules>();
        services.AddSingleton<RuleBasedOrderClassifier>();
        services.AddSingleton<IMlL1Predictor>(_ => new StubLowConfidenceMl());
        services.AddSingleton<IOrderClassifier, HybridOrderClassifier>();
        using var sp = services.BuildServiceProvider();

        var input = new OrderClassificationInput
        {
            RegNumber = "test-low-ml",
            Title = "Охрана объекта круглосуточно",
            Positions = []
        };

        var withLowMl = sp.GetRequiredService<IOrderClassifier>().Classify(input);

        var servicesNoMl = new ServiceCollection();
        servicesNoMl.AddZakupkiCategorization(enableMl: false);
        using var spNoMl = servicesNoMl.BuildServiceProvider();
        var rulesOnly = spNoMl.GetRequiredService<IOrderClassifier>().Classify(input);

        Assert.Equal(rulesOnly.Method, withLowMl.Method);
        Assert.Equal(rulesOnly.Primary?.L2.Id, withLowMl.Primary?.L2.Id);
        Assert.Equal(rulesOnly.ReviewStatus, withLowMl.ReviewStatus);
        Assert.NotEqual(ClassificationMethods.MlL1, withLowMl.Method);
    }

    [Fact]
    public void High_confidence_ml_uses_rules_l2_inside_predicted_l1()
    {
        var services = new ServiceCollection();
        services.AddOptions<ClassificationOptions>();
        services.Configure<ClassificationOptions>(o => o.AutoThreshold = 0.65);
        services.AddSingleton<ITaxonomyCatalog, EmbeddedTaxonomyCatalog>();
        services.AddSingleton<OkpdKtruMapper>();
        services.AddSingleton<MnnDictionary>();
        services.AddSingleton<TrapRules>();
        services.AddSingleton<RuleBasedOrderClassifier>();
        services.AddSingleton<IMlL1Predictor>(_ => new StubHighConfidenceFuelMl());
        services.AddSingleton<IOrderClassifier, HybridOrderClassifier>();
        using var sp = services.BuildServiceProvider();

        var input = new OrderClassificationInput
        {
            RegNumber = "test-high-ml",
            Title = "Поставка бензина АИ-92",
            ObjectName = "Поставка бензина АИ-92",
            Positions = []
        };

        var servicesNoMl = new ServiceCollection();
        servicesNoMl.AddZakupkiCategorization(enableMl: false);
        using var spNoMl = servicesNoMl.BuildServiceProvider();
        var rulesOnly = spNoMl.GetRequiredService<IOrderClassifier>().Classify(input);

        var withMl = sp.GetRequiredService<IOrderClassifier>().Classify(input);

        Assert.Equal(ClassificationMethods.MlL1, withMl.Method);
        Assert.Equal("g10", withMl.Primary?.L1.Id);
        Assert.Equal(rulesOnly.Primary?.L2.Id, withMl.Primary?.L2.Id);
        Assert.Equal("g10c02", withMl.Primary?.L2.Id); // rules pick, not first child g10c01
        Assert.NotEqual(ReviewStatuses.Queued, withMl.ReviewStatus);
    }

    private sealed class StubLowConfidenceMl : IMlL1Predictor
    {
        public bool IsAvailable => true;

        public (string L1Id, float Confidence)? TryPredict(string text) => ("g15", 0.10f);
    }

    [Fact]
    public void High_confidence_ml_keeps_l1_when_rules_disagree_and_queues()
    {
        var services = new ServiceCollection();
        services.AddOptions<ClassificationOptions>();
        services.Configure<ClassificationOptions>(o => o.AutoThreshold = 0.65);
        services.AddSingleton<ITaxonomyCatalog, EmbeddedTaxonomyCatalog>();
        services.AddSingleton<OkpdKtruMapper>();
        services.AddSingleton<MnnDictionary>();
        services.AddSingleton<TrapRules>();
        services.AddSingleton<RuleBasedOrderClassifier>();
        // ML says g07 (IT), rules for fuel stay on g10 → queue but keep g07
        services.AddSingleton<IMlL1Predictor>(_ => new StubHighConfidenceItMl());
        services.AddSingleton<IOrderClassifier, HybridOrderClassifier>();
        using var sp = services.BuildServiceProvider();

        var withMl = sp.GetRequiredService<IOrderClassifier>().Classify(new OrderClassificationInput
        {
            RegNumber = "test-ml-mismatch",
            Title = "Поставка бензина АИ-92",
            ObjectName = "Поставка бензина АИ-92",
            Positions = []
        });

        Assert.Equal(ClassificationMethods.MlL1, withMl.Method);
        Assert.Equal(ReviewStatuses.Queued, withMl.ReviewStatus);
        Assert.Equal("g07", withMl.Primary?.L1.Id);
        Assert.NotEqual("g25", withMl.Primary?.L1.Id);
    }

    private sealed class StubHighConfidenceFuelMl : IMlL1Predictor
    {
        public bool IsAvailable => true;

        public (string L1Id, float Confidence)? TryPredict(string text) => ("g10", 0.91f);
    }

    private sealed class StubHighConfidenceItMl : IMlL1Predictor
    {
        public bool IsAvailable => true;

        public (string L1Id, float Confidence)? TryPredict(string text) => ("g07", 0.91f);
    }
}
