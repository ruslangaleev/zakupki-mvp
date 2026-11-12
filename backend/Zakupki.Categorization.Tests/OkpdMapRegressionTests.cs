using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Tests;

public class OkpdMapRegressionTests
{
    private readonly IOrderClassifier _classifier;

    public OkpdMapRegressionTests()
    {
        var services = new ServiceCollection();
        services.AddZakupkiCategorization();
        _classifier = services.BuildServiceProvider().GetRequiredService<IOrderClassifier>();
    }

    [Theory]
    [InlineData("питание", "56.29.20.120", "g06c17")]
    [InlineData("страхование", "65.12.21.000", "g18c12")]
    [InlineData("банковские услуги", "64.19.21.000", "g18c10")]
    [InlineData("светильник", "27.40.25.123", "g11c07")]
    [InlineData("батарейки", "27.20.11.000", "g11c06")]
    [InlineData("посудомоечная машина", "27.51.28.130", "g15c06")]
    [InlineData("ТО лифтов", "33.12.15.000", "g02c09")]
    [InlineData("ремонт медтехники", "33.13.12.000", "g05c03")]
    [InlineData("авторезина", "22.11.11.000", "g08c03")]
    [InlineData("клеенка медицинская", "22.21.30.120", "g05c02")]
    [InlineData("молоко", "10.51.11.110", "g06c03")]
    [InlineData("овощи", "10.39.11.110", "g06c04")]
    [InlineData("интернет", "61.10.11.000", "g07c10")]
    [InlineData("пожарная безопасность", "84.25.11.000", "g22c04")]
    [InlineData("пассажирские перевозки", "49.31.10.000", "g09c02")]
    [InlineData("одеяло", "13.92.11.000", "g16c03")]
    [InlineData("подушка", "13.92.24.140", "g16c03")]
    [InlineData("ТО бассейна", "33.12.19.000", "g22c04")]
    // #20: bare «медицин*» in customer context must NOT force medtech.
    [InlineData("Ремонт компьютерной техники для нужд медицинской организации", "33.13.11.000", "g07c09")]
    [InlineData("Ремонт технологического оборудования пищеблока медицинского учреждения", "33.12.15.000", "g12c12")]
    // #22: phrasing variants + no false «ивл» inside «привлечением».
    [InlineData("Ремонт медицинской техники", "33.13.12.000", "g05c03")]
    [InlineData("Техническое обслуживание и ремонт медицинских изделий", "33.13.12.000", "g05c03")]
    [InlineData("Ремонт аппарата УЗИ", "33.13.12.000", "g05c03")]
    [InlineData("Ремонт оргтехники с привлечением субподрядчика", "33.13.11.000", "g07c09")]
    // #11 gold regression: 20.59.* medical articles.
    [InlineData("Ц-9845_Закупка медицинских изделий", "20.59.52.194", "g05c02")]
    // #23: genitive «изделий медицинского назначения».
    [InlineData("Закупка изделий медицинского назначения", "20.59.52.194", "g05c02")]
    // #11 QA: water-treatment reagents stay chemistry, not lab diagnostics.
    [InlineData("Поставка химических реагентов для водоочистки", "20.59.59.000", "g14c01")]
    [InlineData("Поставка реагентов для лаборатории", "20.59.52.195", "g05c04")]
    // #40: diagnostic reagents 21.20.23.* → g05c04 (not drugs g05c01).
    [InlineData("Поставка реактивов для ИФА лаборатории", "21.20.23.110", "g05c04")]
    [InlineData("Поставка контрольных материалов для гематологического анализатора", "21.20.23.111", "g05c04")]
    // #40: 32.50.50.190 + analyzer/IVD wording → g05c04; plain med device stays g05c02.
    [InlineData("Кювета для лабораторного анализатора ИВД", "32.50.50.190", "g05c04")]
    [InlineData("Поставка кюветы для лабораторного анализатора ИВД", "32.50.50.190", "g05c04")]
    [InlineData("Приобретение расходного материала для биохимического анализатора", "32.50.50.190", "g05c04")]
    [InlineData("Жгут на верхнюю/нижнюю конечность, многоразового использования", "32.50.50.190", "g05c02")]
    public void Dump_cases_map_to_expected_l2(string title, string okpd, string expectedL2)
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "map-reg",
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition { Okpd2Code = okpd, Name = title, IsPrimary = true }
            ]
        });

        Assert.Equal(expectedL2, result.Primary?.L2.Id);
    }
}
