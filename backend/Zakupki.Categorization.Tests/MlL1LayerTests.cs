using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Ml;
using Zakupki.Categorization.Models;

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
}
