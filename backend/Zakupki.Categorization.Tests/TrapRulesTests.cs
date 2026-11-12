using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Models;

namespace Zakupki.Categorization.Tests;

public class TrapRulesTests
{
    private readonly IOrderClassifier _classifier;

    public TrapRulesTests()
    {
        var services = new ServiceCollection();
        services.AddZakupkiCategorization();
        _classifier = services.BuildServiceProvider().GetRequiredService<IOrderClassifier>();
    }

    [Theory]
    [InlineData("T01", "ПРОКАИН, ПРОКАИН", "g05c01")]
    [InlineData("T02", "ИНСУЛИН ГЛАРГИН, РАСТВОР ДЛЯ ПОДКОЖНОГО ВВЕДЕНИЯ В КАРТРИДЖАХ В ШПРИЦ-РУЧКЕ 100 ЕД/мл", "g05c01")]
    [InlineData("T03", "Поставка с доставкой бумаги офисной", "g15c02")]
    [InlineData("T04", "Поставка медицинских расходных материалов для заготовки донорской крови (Магистрали)", "g05c02")]
    [InlineData("T05", "Поставка ящиков для хранения песка, соли, антигололедных реагентов", "g13c12")]
    [InlineData("T06", "Оказание услуг по диагностике,техническому обслуживанию и ремонту автотранспортных средств", "g08c05")]
    [InlineData("T07", "на поставку расходных материалов для монтажа комплексов видео-конференц-связи", "g07c13")]
    [InlineData("T08", "Оказание услуг по очистке кровли от снега, наледи и сосулек", "g22c02")]
    [InlineData("T09", "Услуги по техническому обслуживанию системы АПС и СО", "g17c02")]
    [InlineData("T10", "Поставка лекарственных средств (МНН: АМОКСИЦИЛЛИН+ КЛАВУЛАНОВАЯ КИСЛОТА)", "g05c01")]
    [InlineData("T11", "Выполнение работ по устройству ограждения дизельной электростанции", "g01c06")]
    [InlineData("T12", "Поставка хозяйственных товаров (бумажно-гигиеническая продукция)", "g15c08")]
    [InlineData("T13", "Оказание услуг по обновлению модулей, а также разработке и внедрению дополнительных сервисов к АИС «Логистика»", "g07c04")]
    [InlineData("T14", "Бумага для офисной техники", "g15c02")]
    [InlineData("T15", "Клиндамицин", "g05c01")]
    public void Trap_examples_resolve_to_expected_l2(string id, string title, string expectedL2)
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = id,
            Title = title,
            ObjectName = title
        });

        Assert.Equal(expectedL2, result.Primary?.L2.Id);
        Assert.NotEqual(ReviewStatuses.Queued, result.ReviewStatus);
    }

    /// <summary>T05/T07/T08 must refine coarse OKPD map hits when real positions carry codes.</summary>
    [Theory]
    [InlineData(
        "T05",
        "Поставка ящиков для хранения песка, соли, антигололедных реагентов",
        "22.22.13.190",
        "Ящик для хранения песка, соли, антигололедных реагентов",
        "g13c12")]
    [InlineData(
        "T07",
        "на поставку расходных материалов для монтажа комплексов видео-конференц-связи",
        "27.90.12.130",
        "Труба гофрированная",
        "g07c13")]
    [InlineData(
        "T08",
        "Оказание услуг по очистке кровли от снега, наледи и сосулек",
        "81.22.12.000",
        "Оказание услуг по очистке кровли от снега, наледи и сосулек",
        "g22c02")]
    [InlineData(
        "T11",
        "Выполнение работ по устройству ограждения дизельной электростанции",
        "43.29.12.110",
        "Выполнение работ по устройству ограждения дизельной электростанции",
        "g01c06")]
    [InlineData(
        "T12",
        "Поставка хозяйственных товаров (бумажно-гигиеническая продукция)",
        "17.22.11.110",
        "Бумага туалетная",
        "g15c08")]
    public void Trap_examples_with_okpd_positions_resolve_to_expected_l2(
        string id,
        string title,
        string okpd,
        string positionName,
        string expectedL2)
    {
        var result = _classifier.Classify(new OrderClassificationInput
        {
            RegNumber = id,
            Title = title,
            ObjectName = title,
            Positions =
            [
                new PurchasePosition
                {
                    Okpd2Code = okpd,
                    Name = positionName,
                    IsPrimary = true,
                    Amount = 1000
                }
            ]
        });

        Assert.Equal(expectedL2, result.Primary?.L2.Id);
        Assert.NotEqual(ReviewStatuses.Queued, result.ReviewStatus);
    }
}
