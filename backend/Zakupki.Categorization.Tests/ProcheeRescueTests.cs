using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Tests;

public class ProcheeRescueTests
{
    private readonly IOrderClassifier _classifier;

    public ProcheeRescueTests()
    {
        var services = new ServiceCollection();
        services.AddZakupkiCategorization();
        _classifier = services.BuildServiceProvider().GetRequiredService<IOrderClassifier>();
    }

    [Theory]
    [InlineData("Оказание услуг вневедомственной охраны", "84.24.19.000", "g17c01")]
    [InlineData("Охрана объекта с помощью пожарной сигнализации", "84.24.19.000", "g17c02")]
    [InlineData("Услуги по охране объектов", "84.24.11.000", "g17c01")]
    [InlineData("Поставка реагентов для нужд КБ", "20.59.52.195", "g05c04")]
    [InlineData("Поставка кислорода медицинского газообразного", null, "g05c02")]
    [InlineData("Антивирус Kaspersky", "58.29.29.000", "g07c03")]
    [InlineData("Поставка картриджей", "20.59.59.000", "g07c02")]
    [InlineData("Поставка томографа", "28.99.39.000", "g05c03")]
    [InlineData("Поставка дезинфицирующих средств", "20.20.14.000", "g22c03")]
    public void Rescue_prochee_and_dumps(string title, string? okpd, string expectedL2)
    {
        var positions = string.IsNullOrWhiteSpace(okpd)
            ? Array.Empty<PurchasePosition>()
            : new[]
            {
                new PurchasePosition { Okpd2Code = okpd, Name = title, IsPrimary = true }
            };

        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "rescue",
            Title = title,
            ObjectName = title,
            Positions = positions
        });

        Assert.Equal(expectedL2, result.Primary?.L2.Id);
        Assert.NotEqual(ReviewStatuses.Queued, result.ReviewStatus);
    }
}
