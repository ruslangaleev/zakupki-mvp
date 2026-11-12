using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Tests;

public class OkpdMapV13Tests
{
    private readonly IOrderClassifier _classifier;
    private readonly OkpdKtruMapper _mapper;

    public OkpdMapV13Tests()
    {
        var services = new ServiceCollection();
        services.AddZakupkiCategorization();
        var sp = services.BuildServiceProvider();
        _classifier = sp.GetRequiredService<IOrderClassifier>();
        _mapper = sp.GetRequiredService<OkpdKtruMapper>();
    }

    [Theory]
    [InlineData("01.11.12.000", "g06c05")]
    [InlineData("01.13.51.000", "g06c04")]
    [InlineData("01.24.10.000", "g06c04")]
    [InlineData("01.47.12.000", "g06c01")]
    [InlineData("03.11.12.000", "g06c02")]
    [InlineData("01.41.10.000", "g23c02")]
    [InlineData("01.61.10.000", "g23c09")]
    public void Food_and_agri_prefixes_map(string okpd, string expectedL2)
    {
        var result = Classify("Поставка", okpd);
        Assert.Equal(expectedL2, result.Primary?.L2.Id);
        Assert.Equal("v1.4", result.MappingVersion);
        Assert.Equal(ClassificationMethods.Okpd2Map, result.Method);
    }

    [Fact]
    public void Digit_only_prefix_matches_without_dots()
    {
        var result = Classify("Зерно", "011112000");
        Assert.Equal("g06c05", result.Primary?.L2.Id);
    }

    [Fact]
    public void Multilabel_majority_keeps_primary_and_secondary()
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "multi",
            Title = "Смешанная поставка",
            ObjectName = "Смешанная поставка",
            Positions =
            [
                new PurchasePosition
                {
                    Okpd2Code = "10.51.11.110",
                    Name = "Молоко",
                    Amount = 700,
                    IsPrimary = true
                },
                new PurchasePosition
                {
                    Okpd2Code = "17.12.14.110",
                    Name = "Бумага",
                    Amount = 250
                }
            ]
        });

        Assert.Equal("g06c03", result.Primary?.L2.Id);
        Assert.True(result.Primary!.Share >= 0.7);
        Assert.Contains(result.Categories, c => !c.IsPrimary && c.L2.Id == "g15c02" && c.Share >= 0.15);
        Assert.NotEqual(ReviewStatuses.Queued, result.ReviewStatus);
    }

    [Fact]
    public void Multilabel_without_majority_queues()
    {
        var result = _mapper.TryMap(
        [
            new PurchasePosition { Okpd2Code = "10.51.11.110", Name = "Молоко", Amount = 50 },
            new PurchasePosition { Okpd2Code = "17.12.14.110", Name = "Бумага", Amount = 50 }
        ]);

        Assert.NotNull(result);
        Assert.Equal(ReviewStatuses.Queued, result!.ReviewStatus);
        Assert.Equal("g25c01", result.Primary?.L2.Id);
    }

    [Theory]
    [InlineData("Метформин", "g05c01")]
    [InlineData("Фуросемид", "g05c01")]
    [InlineData("Цефепим", "g05c01")]
    public void Expanded_mnn_classifies(string title, string expectedL2)
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "mnn",
            Title = title,
            ObjectName = title
        });

        Assert.Equal(expectedL2, result.Primary?.L2.Id);
        Assert.Equal(ClassificationMethods.InnDict, result.Method);
    }

    private ClassificationResult Classify(string title, string okpd) =>
        _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "v13",
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition { Okpd2Code = okpd, Name = title, IsPrimary = true }
            ]
        });
}
