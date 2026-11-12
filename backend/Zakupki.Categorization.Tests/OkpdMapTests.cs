using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Tests;

public class OkpdMapTests
{
    [Fact]
    public void Okpd_prefix_beats_rules()
    {
        var services = new ServiceCollection();
        services.AddZakupkiCategorization();
        var classifier = services.BuildServiceProvider().GetRequiredService<IOrderClassifier>();

        var result = classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "map-smoke",
            Title = "Поставка бумаги офисной",
            ObjectName = "Поставка бумаги офисной",
            Positions =
            [
                new PurchasePosition
                {
                    Okpd2Code = "21.20.10.111",
                    Name = "Препарат",
                    IsPrimary = true
                }
            ]
        });

        Assert.Equal("g05c01", result.Primary?.L2.Id);
        Assert.Equal(ClassificationMethods.Okpd2Map, result.Method);
    }
}
