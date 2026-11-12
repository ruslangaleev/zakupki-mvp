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
    // #40: diagnostic reagents 21.20.23.110/111 → g05c04; other 21.20.23.* stay drugs g05c01.
    [InlineData("Поставка реактивов для ИФА лаборатории", "21.20.23.110", "g05c04")]
    [InlineData("Поставка контрольных материалов для гематологического анализатора", "21.20.23.111", "g05c04")]
    [InlineData("Поставка экстемпоральных лекарственных препаратов для медицинского применения", "21.20.23.190", "g05c01")]
    // #43: dressing materials 21.20.24 → med devices; drug 21.20.10 stays g05c01.
    [InlineData("Вата медицинская гигроскопическая", "21.20.24.150", "g05c02")]
    [InlineData("Лекарственный препарат", "21.20.10.000", "g05c01")]
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

    /// <summary>#48: construction works with 42.* → g01; utility meters → g02.</summary>
    [Fact]
    public void Capital_repair_heat_network_42_to_g01()
    {
        var title = "Выполнение работ по объекту: \"Капитальный ремонт тепловой сети\"";
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "0136300000726000359",
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition { Okpd2Code = "42.21.21.000", Name = title, Amount = 100, IsPrimary = true }
            ]
        });

        Assert.Equal("g01", result.Primary?.L1.Id);
        Assert.Equal("g01c02", result.Primary?.L2.Id);
    }

    [Fact]
    public void Sports_facility_works_multilabel_g01_with_code_secondaries()
    {
        var title = "Выполнение работ по устройству спортивных сооружений";
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "0119200000126020066",
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition { Okpd2Code = "42.99.22.110", Name = title, Amount = 100, IsPrimary = true },
                new PurchasePosition { Okpd2Code = "27.40.39.113", Name = "светильник", Amount = 10 },
                new PurchasePosition { Okpd2Code = "27.90.40.190", Name = "опора", Amount = 10 },
                new PurchasePosition { Okpd2Code = "32.30.14.121", Name = "тренажер", Amount = 10 }
            ]
        });

        Assert.Equal("g01", result.Primary?.L1.Id);
        Assert.Equal("g01c01", result.Primary?.L2.Id);
        var secondaryL1 = result.Categories.Where(c => !c.IsPrimary).Select(c => c.L1.Id).ToHashSet();
        Assert.Contains("g02", secondaryL1);
        Assert.Contains("g11", secondaryL1);
        Assert.Contains("g21", secondaryL1);
    }

    [Fact]
    public void Heat_meter_supply_to_g02_not_g12()
    {
        var title = "поставка приборов учета тепловой энергии";
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "0116300000226000214",
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition { Okpd2Code = "26.51.51.110", Name = "термометр", Amount = 100, IsPrimary = true }
            ]
        });

        Assert.Equal("g02", result.Primary?.L1.Id);
        Assert.Equal("g02c11", result.Primary?.L2.Id);
        Assert.Contains(result.Categories, c => !c.IsPrimary && c.L1.Id == "g12");
    }

    /// <summary>#47: metrology / info-security OKPD prefix fixes.</summary>
    [Theory]
    [InlineData(
        "0369200011326000823",
        "Услуги по поверке и калибровке средств измерений - дозаторы",
        "71.12.40.129",
        "g12",
        "g12c06")]
    [InlineData(
        "0351300051426000214",
        "Оказание услуг по проведению метрологического обслуживания медицинской техники",
        "71.12.40.129",
        "g05",
        "g05c03")]
    [InlineData(
        "0151100016326000032",
        "услуги по проведению контроля функций средств защиты информации",
        "74.90.20.144",
        "g07",
        "g07c03")]
    public void Metrology_and_infosec_okpd_l1(
        string reg,
        string title,
        string okpd,
        string expectedL1,
        string expectedL2)
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = reg,
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition { Okpd2Code = okpd, Name = title, Amount = 100, IsPrimary = true }
            ]
        });

        Assert.Equal(expectedL1, result.Primary?.L1.Id);
        Assert.Equal(expectedL2, result.Primary?.L2.Id);
    }

    /// <summary>#46: street works / MSW haul coded under coarse OKPD → title refine.</summary>
    [Theory]
    [InlineData(
        "0134300054826000044",
        "Выполнение работ по устройству тротуара по ул. 40 лет Победы п. Утулик",
        "42.11.20.300",
        "g22",
        "g22c02")]
    [InlineData(
        "0351300062426000121",
        "Оказание услуг по уборке улично-дорожной сети и озелененных территорий",
        "42.11.20.300",
        "g22",
        "g22c02")]
    [InlineData(
        "0520600005126000038",
        "Оказание услуг по транспортированию твердых коммунальных отходов",
        "49.41.19.900",
        "g24",
        "g24c05")]
    public void Title_refine_roadworks_and_waste_haul(
        string reg,
        string title,
        string okpd,
        string expectedL1,
        string expectedL2)
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = reg,
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition { Okpd2Code = okpd, Name = title, Amount = 100, IsPrimary = true }
            ]
        });

        Assert.Equal(expectedL1, result.Primary?.L1.Id);
        Assert.Equal(expectedL2, result.Primary?.L2.Id);
        Assert.Contains(result.Categories, c => !c.IsPrimary && c.L1 is not null);
    }

    [Fact]
    public void Winter_road_maintenance_42_11_20_stays_g02()
    {
        var title = "Выполнение работ по зимнему содержанию автомобильных дорог";
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "0119600018326000130",
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition
                {
                    Okpd2Code = "42.11.20.300",
                    Name = "Содержание дорог. Уборка снежных валов автогрейдерами",
                    Amount = 100,
                    IsPrimary = true
                }
            ]
        });

        Assert.Equal("g02", result.Primary?.L1.Id);
        Assert.DoesNotContain(result.Categories, c => c.L2?.Id == "g22c02" && c.IsPrimary);
    }

    /// <summary>#43: cotton/dressings 21.20.24 must not outweigh med devices into drugs.</summary>
    [Fact]
    public void Med_devices_with_dressings_primary_g05c02()
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "0260100000926000246",
            Title = "Изделия медицинского назначения",
            ObjectName = "Изделия медицинского назначения",
            Positions =
            [
                new PurchasePosition { Okpd2Code = "21.20.24.150", Name = "вата", Amount = 300, IsPrimary = true },
                new PurchasePosition { Okpd2Code = "32.50.50.000", Name = "изделие", Amount = 200 },
                new PurchasePosition { Okpd2Code = "32.50.50.190", Name = "жгут", Amount = 100 }
            ]
        });

        Assert.Equal("g05c02", result.Primary?.L2.Id);
    }

    /// <summary>#41/#44: mixed food basket → primary g06c16 + all distinct g06 L2 secondaries.</summary>
    [Fact]
    public void Mixed_food_okpd_basket_promotes_to_g06c16_with_all_secondaries()
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = "0138600003126000088",
            Title = "Поставка продуктов питания",
            ObjectName = "Поставка продуктов питания",
            Positions =
            [
                new PurchasePosition { Okpd2Code = "01.11.75.110", Name = "горох", Amount = 12199.8, IsPrimary = true },
                new PurchasePosition { Okpd2Code = "01.11.71.110", Name = "фасоль", Amount = 20433.5 },
                new PurchasePosition { Okpd2Code = "10.82.14.000", Name = "какао", Amount = 20273.4 },
                new PurchasePosition { Okpd2Code = "10.83.12.120", Name = "кофейный напиток", Amount = 34053.4 },
                new PurchasePosition { Okpd2Code = "10.84.23.164", Name = "лавровый лист", Amount = 26466.66 },
                new PurchasePosition { Okpd2Code = "10.86.10.243", Name = "сок детский", Amount = 140701.5 },
                new PurchasePosition { Okpd2Code = "10.84.23.120", Name = "ваниль", Amount = 10687.84 },
                new PurchasePosition { Okpd2Code = "10.84.12.150", Name = "приправы", Amount = 28013.35 },
                new PurchasePosition { Okpd2Code = "10.61", Name = "рис", Amount = 84840 },
                new PurchasePosition { Okpd2Code = "10.84.12.150", Name = "приправы", Amount = 22410.68 }
            ]
        });

        Assert.Equal("g06c16", result.Primary?.L2.Id);
        var secondary = result.Categories
            .Where(c => !c.IsPrimary && c.L2 is not null)
            .Select(c => c.L2!.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "g06c05", "g06c08", "g06c09", "g06c11" }, secondary);
    }

    [Theory]
    [InlineData(
        "0319200060826000071",
        "Поставка продуктов питания (чай черный, масло растительное)",
        "10.41.54.000",
        "10.83.13.120",
        "g06c16")]
    [InlineData(
        "0358300284326000156",
        "Осуществление закупки продуктов питания для нужд ГБУ",
        "10.41.54.000",
        null,
        "g06c16")]
    [InlineData(
        "0816500000626017018",
        "Поставка продуктов питания (спецпитание)",
        "10.51.11.000",
        "10.51.52.140",
        "g06c16")]
    [InlineData(
        "0343300000926000017",
        "Поставка продуктов питания (сок)",
        "10.32.10.000",
        null,
        "g06c13")]
    [InlineData(
        "0354300078926000050",
        "Поставка продуктов питания: яблоки",
        "01.24.10.000",
        null,
        "g06c04")]
    public void Food_title_assortment_or_single_kind(
        string reg,
        string title,
        string okpd1,
        string? okpd2,
        string expectedL2)
    {
        var positions = new List<PurchasePosition>
        {
            new() { Okpd2Code = okpd1, Name = title, Amount = 100, IsPrimary = true }
        };
        if (okpd2 is not null)
        {
            positions.Add(new PurchasePosition { Okpd2Code = okpd2, Name = title, Amount = 100 });
        }

        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = reg,
            Title = title,
            ObjectName = title,
            Positions = positions
        });

        Assert.Equal(expectedL2, result.Primary?.L2.Id);
    }
}
