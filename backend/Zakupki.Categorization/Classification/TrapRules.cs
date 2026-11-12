using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Hard overrides for regression traps T01–T15 (product TZ).
/// Runs after OKPD/KTRU map and MNN dict, before keyword rules.
/// </summary>
public sealed class TrapRules(ITaxonomyCatalog taxonomy, OkpdKtruMapper mapper)
{
    private static readonly Regex FoodTitleWithClarifier = new(
        @"продукт\w*\s+питани\w*\s*(?:\(([^)]+)\)|[:—–-]\s*([^,;/(]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Overrides coarse OKPD/KTRU hits when the card text is more specific
    /// (repair=33.*, plastics=22.*, security=80.*).
    /// </summary>
    public ClassificationResult? TryRefine(OrderClassificationInput input, ClassificationResult mapped)
    {
        var hay = BuildHaystack(input);
        if (string.IsNullOrWhiteSpace(hay))
        {
            return null;
        }

        var mappedL2 = mapped.Primary?.L2?.Id;

        if (TryFoodAssortment(input, mapped, hay) is { } foodAssortment)
        {
            return foodAssortment;
        }

        // #46: roadworks OKPD 42.11.20 used for sidewalks / street cleaning → g22.
        // Avoid winter road maintenance («уборка снега» / автогрейдер).
        if (HasOkpdPrefix(input, "42.11.20") &&
            !ContainsAny(hay, "автогрейд", "автомобильн дорог", "содержание дорог") &&
            (ContainsAny(hay, "тротуар", "озеленен", "озеленён", "благоустр") ||
             (ContainsAny(hay, "уборк") && ContainsAny(hay, "улично", "территор", "озелен"))))
        {
            return PromoteWithSecondary("g22c02", mapped);
        }

        // #46: freight OKPD 49.41 used for MSW haulage → g24.
        if (HasOkpdPrefix(input, "49.41") &&
            ContainsAny(hay, "отход", "тко", "мусор"))
        {
            return PromoteWithSecondary("g24c05", mapped);
        }

        // #47: metrology OKPD 71.12.4 on medical equipment → medtech (keep g12 secondary).
        if (HasOkpdPrefix(input, "71.12.4") &&
            ContainsAny(hay, "медицин") &&
            mapped.Primary?.L2?.Id is "g12c06" or "g04c01")
        {
            return PromoteWithSecondary("g05c03", mapped);
        }

        // #48: construction/repair wording + OKPD 42/43 → g01 (after #46 sidewalk refine).
        if (TryConstructionWorks(input, mapped, hay) is { } construction)
        {
            return construction;
        }

        // #48: utility meters sold as measuring instruments → engineering networks g02.
        if (ContainsAny(hay, "поставка") &&
            ContainsAny(hay, "прибор") &&
            ContainsAny(hay, "учет", "учёт") &&
            ContainsAny(hay, "тепл", "вод", "газ", "электр") &&
            mapped.Primary?.L1.Id is "g12")
        {
            return PromoteWithSecondary("g02c11", mapped);
        }

        // Text traps must override coarse OKPD hits (plastics/cables/cleaning/paper/fencing).
        if (TryTrapT05T07T08(hay, mapped.MappingVersion, mapped.Method) is { } trapHit)
        {
            return trapHit;
        }

        if (TryTrapT11T12(hay, mapped.MappingVersion, mapped.Method) is { } trapHit2)
        {
            return trapHit2;
        }

        if (ContainsAny(hay, "клеенк", "клеёнк") && ContainsAny(hay, "медицин"))
        {
            return Hit("g05c02", 0.93, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "бассейн") &&
            (ContainsAny(hay, "обслуживан", "содержан", "очистк") || ContainsAny(hay, "то бассейн")))
        {
            return Hit("g22c04", 0.9, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "лифт") &&
            (ContainsAny(hay, "обслуживан", "ремонт") || ContainsAny(hay, "то лифт", "то лифтов")))
        {
            return Hit("g02c09", 0.92, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "одеяло", "одеяла", "подушка", "подушки", "мягкий инвентар") &&
            mappedL2 is "g16c01" or "g16c02" or "g25c01")
        {
            return Hit("g16c03", 0.92, mapped.MappingVersion, mapped.Method);
        }

        if ((ContainsAny(hay, "апс", "соуэ", "скуд", "пожар") && ContainsAny(hay, "сигнализац")) &&
            ContainsAny(hay, "обслуживан", "техническ") &&
            mappedL2 is "g17c01" or "g11c09" or "g12c12" or "g13c09")
        {
            return Hit("g17c02", 0.93, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "вентиляц", "кондиционир") &&
            ContainsAny(hay, "обслуживан", "то ", "ремонт") &&
            mappedL2 is "g12c12" or "g13c09")
        {
            return Hit("g02c08", 0.9, mapped.MappingVersion, mapped.Method);
        }

        // Security coded as public admin 84.* / Прочее, or fire-signal refine of guard services.
        if (ContainsAny(hay, "охран") &&
            !ContainsAny(hay, "охрана труда", "охраны труда", "окружающ"))
        {
            if (ContainsAny(hay, "сигнализац", "апс", "соуэ", "скуд", "тревожн") &&
                mappedL2 is "g17c01" or "g25c01" or "g13c07" or "g14c01" or "g14c02")
            {
                return Hit("g17c02", 0.93, mapped.MappingVersion, mapped.Method);
            }

            if (mappedL2 is "g25c01" or "g13c07" or "g14c01" or "g14c02")
            {
                return Hit("g17c01", 0.93, mapped.MappingVersion, mapped.Method);
            }
        }

        if (ContainsAny(hay, "реагент", "тест систем", "тест-систем", "диагностикум") &&
            !ContainsAny(hay, "антигололед", "антигололёд", "водоочист", "водоподготов", "водообработ", "бассейн") &&
            mappedL2 is "g05c01" or "g14c01" or "g14c02" or "g23c05" or "g25c01")
        {
            return Hit("g05c04", 0.92, mapped.MappingVersion, mapped.Method);
        }

        // #40: generic med-device OKPD 32.50.50.190 + lab-analyzer wording → diagnostics.
        if (HasOkpdPrefix(input, "32.50.50.190") &&
            mappedL2 is "g05c02" &&
            (ContainsAny(hay, "анализатор", "ивд", "кювет", "реагент", "реактив", "калибратор") ||
             (ContainsAny(hay, "контрольн") && ContainsAny(hay, "материал"))))
        {
            return Hit("g05c04", 0.93, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "картридж", "тонер") &&
            !ContainsAny(hay, "инсулин", "шприц") &&
            mappedL2 is "g14c01" or "g14c02" or "g14c05" or "g25c01")
        {
            return Hit("g07c02", 0.92, mapped.MappingVersion, mapped.Method);
        }

        // Prototype refine: 20.59 chemistry code + medical articles → med supplies.
        // FormKD: «изделий» → «изделии»; cover both word orders.
        if (ContainsAny(hay,
                "медицинск издели", "медицинских издели", "медицинскои издели",
                "изделия медицин", "изделии медицин", "издели медицин", "мед издели") &&
            mappedL2 is "g14c01" or "g14c02" or "g14c05" or "g25c01")
        {
            return Hit("g05c02", 0.92, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "перчатк") && ContainsAny(hay, "медицин") &&
            mappedL2 is "g14c01" or "g14c02" or "g14c04" or "g14c05")
        {
            return Hit("g05c02", 0.92, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "дезинфицир", "дезсредств", "дез средств") &&
            mappedL2 is "g23c05" or "g14c01")
        {
            return Hit("g22c03", 0.9, mapped.MappingVersion, mapped.Method);
        }

        // Medtech repair often coded as 33.12/33.13 (machines / electronics) in OKPD.
        // Avoid bare «медицин*» (учреждение) and substring «ивл» inside «привлечением».
        if ((ContainsAny(hay,
                "медицинск оборудован", "медицинского оборудован", "медицинское оборудован",
                "медицинскои техник", "медицинск техник", "медицинских издели",
                "медтехник", "томограф", "аппарат узи", "аппарата узи", "узи аппарат",
                "рентген", "центрифуг", "аппарат ивл") ||
             ContainsWhole(hay, "узи", "ивл")) &&
            mappedL2 is "g12c01" or "g12c06" or "g12c08" or "g12c12" or "g11c01" or "g13c07"
                or "g07c09" or "g25c01")
        {
            return Hit("g05c03", 0.93, mapped.MappingVersion, mapped.Method);
        }

        if ((ContainsAny(hay, "антивирус", "kaspersky", "касперск", "dr web", "др веб") ||
             (ContainsAny(hay, "лицензи") && ContainsAny(hay, "средств защиты информации"))) &&
            mappedL2 is "g15c11" or "g15c01" or "g15c09" or "g25c01")
        {
            return Hit("g07c03", 0.93, mapped.MappingVersion, mapped.Method);
        }

        if ((ContainsAny(hay, "канцеляр", "термос", "хозинвентар", "хозяйственн инвентар") ||
             ContainsAny(hay, "ручка шариков", "степлер")) &&
            mappedL2 is "g13c07" or "g13c02")
        {
            return Hit(ContainsAny(hay, "термос", "хозинвентар", "хозяйственн") ? "g15c08" : "g15c01",
                0.88, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "дверн полотн", "дверные полотна", "полотно дверн") &&
            mappedL2 is "g23c01" or "g23c11" or "g23c12")
        {
            return Hit("g03c03", 0.9, mapped.MappingVersion, mapped.Method);
        }

        // Food coded under coarse agriculture 01.* → products.
        if ((mappedL2 is "g23c01" or "g23c02" or "g23c03") &&
            ContainsAny(hay,
                "яблок", "груш", "слив", "вишн", "черешн", "абрикос", "персик",
                "овощ", "картофел", "капуст", "морков", "свекл", "лук ", "чеснок",
                "зерн", "пшениц", "мук", "яйц", "мяс", "птиц", "рыб", "молочн", "молоко"))
        {
            if (ContainsAny(hay, "рыб"))
            {
                return Hit("g06c02", 0.9, mapped.MappingVersion, mapped.Method);
            }

            if (ContainsAny(hay, "мяс", "птиц", "яйц"))
            {
                return Hit("g06c01", 0.9, mapped.MappingVersion, mapped.Method);
            }

            if (ContainsAny(hay, "овощ", "картофел", "капуст", "морков", "свекл", "яблок", "груш", "слив", "вишн", "черешн", "абрикос", "персик"))
            {
                return Hit("g06c04", 0.9, mapped.MappingVersion, mapped.Method);
            }

            if (ContainsAny(hay, "зерн", "пшениц", "мук"))
            {
                return Hit("g06c05", 0.88, mapped.MappingVersion, mapped.Method);
            }
        }

        return null;
    }

    public ClassificationResult? TryClassify(OrderClassificationInput input)
    {
        var hay = BuildHaystack(input);
        if (string.IsNullOrWhiteSpace(hay))
        {
            return null;
        }

        if (TryTrapT05T07T08(hay, mappingVersion: null, method: null) is { } earlyTrap)
        {
            return earlyTrap;
        }

        if (TryTrapT11T12(hay, mappingVersion: null, method: null) is { } earlyTrap2)
        {
            return earlyTrap2;
        }

        // T01 — «прокат» inside Прокаин (also covered by MNN; keep as safety net).
        if (ContainsAny(hay, "прокаин"))
        {
            return Hit("g05c01", 0.93);
        }

        // T02 — insulin cartridge / pen, not office supplies.
        if (ContainsAny(hay, "инсулин") && ContainsAny(hay, "картридж", "шприц ручк", "шприцручк"))
        {
            return Hit("g05c01", 0.93);
        }

        // T10 / medicine phrasing — beat chemistry keywords, false «апс» in «капсулы»,
        // and «охран» inside «здравоохранения». Match all Russian inflections of лекарственн*.
        if ((ContainsAny(hay, "лекарственн") &&
             ContainsAny(hay, "препарат", "средств", "средство")) ||
            ContainsAny(hay,
                "антибактериальн препарат",
                "противоопухолев препарат",
                "психотропн"))
        {
            return Hit("g05c01", 0.94);
        }

        // T15-style single-token drugs are handled by MnnDictionary.

        // T03 — «с доставкой» of goods → classify by goods (office paper).
        if (ContainsAny(hay, "бумаг") && ContainsAny(hay, "офисн", "а4", "для офисной техники") &&
            ContainsAny(hay, "доставк", "поставк"))
        {
            return Hit("g15c02", 0.9);
        }

        // T14 — office paper without delivery trap.
        if (ContainsAny(hay, "бумага для офисной техники", "бумага офисная", "офисная бумага", "бумага а4") ||
            (ContainsAny(hay, "бумаг") && ContainsAny(hay, "офисной техники", "для офисной")))
        {
            return Hit("g15c02", 0.9);
        }

        // T04 — blood collection «магистрали», not roofing.
        if (ContainsAny(hay, "магистрал") &&
            ContainsAny(hay, "медицин", "донорск", "крови", "крови", "заготовк крови", "трансфуз"))
        {
            return Hit("g05c02", 0.92);
        }

        // T06 — vehicle diagnostics / maintenance.
        if (ContainsAny(hay, "диагностик") &&
            ContainsAny(hay, "автотранспорт", "автомобил", "транспортн средств", "то автомобилей", "ремонт авто"))
        {
            return Hit("g08c05", 0.92);
        }

        // T09 — APS / SOUE maintenance → security.
        // Use whole-token «апс» — substring hits «капсулы» / «ингаляц*» otherwise.
        if (ContainsWhole(hay, "апс", "соуэ") ||
            ContainsAny(hay, "пожарн сигнализац", "охранно пожарн", "системы апс", "апс и со") ||
            (ContainsAny(hay, "техническ обслуживан") &&
             (ContainsWhole(hay, "апс", "соуэ") || ContainsAny(hay, "сигнализац"))))
        {
            return Hit("g17c02", 0.92);
        }

        // Guard / monitoring services (often OKPD 84.24 → was silent Прочее).
        // «охран» is also a substring of «здравоохранения» — exclude that false friend.
        if (ContainsAny(hay, "охран") &&
            !ContainsAny(hay, "охрана труда", "охраны труда", "окружающ", "здравоохран") &&
            !ContainsAny(hay, "лекарственн"))
        {
            if (ContainsAny(hay, "сигнализац", "скуд", "тревожн", "пульт") ||
                ContainsWhole(hay, "апс", "соуэ"))
            {
                return Hit("g17c02", 0.93);
            }

            return Hit("g17c01", 0.93);
        }

        // Lab reagents / tests (not medicines, not anti-ice, not water treatment).
        if (ContainsAny(hay, "реагент", "тест систем", "тест-систем", "диагностикум", "для анализатора") &&
            !ContainsAny(hay, "антигололед", "антигололёд", "лекарственн",
                "водоочист", "водоподготов", "водообработ", "бассейн"))
        {
            return Hit("g05c04", 0.92);
        }

        // Medical oxygen.
        if (ContainsAny(hay, "кислород") && ContainsAny(hay, "медицин"))
        {
            return Hit("g05c02", 0.9);
        }

        // Antivirus / endpoint security licenses.
        if (ContainsAny(hay, "антивирус", "kaspersky", "касперск", "dr web", "др веб"))
        {
            return Hit("g07c03", 0.93);
        }

        // Medical equipment names that often carry industrial OKPD.
        if (ContainsAny(hay, "томограф", "центрифуг", "ивл", "аппарат ивл", "рентген аппарат"))
        {
            return Hit("g05c03", 0.92);
        }

        // Office toner vs chemistry dumps.
        if (ContainsAny(hay, "картридж", "тонер") && !ContainsAny(hay, "инсулин", "шприц"))
        {
            return Hit("g07c02", 0.9);
        }

        // Disinfectants ≠ agrochemistry.
        if (ContainsAny(hay, "дезинфицир", "дезсредств", "дез средств"))
        {
            return Hit("g22c03", 0.9);
        }

        // Bedding / soft inventory (not clothing).
        if (ContainsAny(hay, "одеяло", "одеяла", "подушка", "подушки", "мягкий инвентар"))
        {
            return Hit("g16c03", 0.92);
        }

        // Medical oilcloth → med supplies, not chemistry/plastics.
        if (ContainsAny(hay, "клеенк", "клеёнк") && ContainsAny(hay, "медицин"))
        {
            return Hit("g05c02", 0.93);
        }

        // Pool / elevator maintenance when no OKPD map fired.
        if (ContainsAny(hay, "бассейн") &&
            (ContainsAny(hay, "обслуживан", "содержан", "очистк") || ContainsAny(hay, "то бассейн")))
        {
            return Hit("g22c04", 0.9);
        }

        if (ContainsAny(hay, "лифт") && ContainsAny(hay, "обслуживан", "ремонт"))
        {
            return Hit("g02c09", 0.92);
        }

        // T13 — IS named «Логистика» / software development, not freight.
        if (ContainsAny(hay, "аис") && ContainsAny(hay, "логистик"))
        {
            return Hit("g07c04", 0.9);
        }

        if (ContainsAny(hay, "логистик") &&
            ContainsAny(hay, "разработк", "внедрен", "доработк", "обновлен модул", "информационн систем"))
        {
            return Hit("g07c04", 0.9);
        }

        return null;
    }

    private ClassificationResult? TryTrapT05T07T08(string hay, string? mappingVersion, string? method)
    {
        // T05 — anti-ice reagents / sand boxes, not lab diagnostics / plastics.
        if (ContainsAny(hay, "антигололед", "антигололёд") ||
            (ContainsAny(hay, "ящик", "ящиков") && ContainsAny(hay, "песка", "антигололед", "антигололёд")))
        {
            return Hit("g13c12", 0.88, mappingVersion, method);
        }

        // T07 — video conferencing gear (not cables/event conferences).
        if (ContainsAny(hay, "видео конференц", "видеоконференц", "вкс") ||
            (ContainsAny(hay, "конференц") && ContainsAny(hay, "видео", "связь", "монтаж комплекс")))
        {
            return Hit("g07c13", 0.9, mappingVersion, method);
        }

        // T08 — snow/ice roof cleaning → territory maintenance (not indoor cleaning).
        if (ContainsAny(hay, "очистк") && ContainsAny(hay, "кровл", "крыш") &&
            ContainsAny(hay, "снег", "налед", "наледи", "сосулек", "сосульк"))
        {
            return Hit("g22c02", 0.92, mappingVersion, method);
        }

        return null;
    }

    private ClassificationResult? TryTrapT11T12(string hay, string? mappingVersion, string? method)
    {
        // T11 — fencing works at a power-plant site → fencing, not HVAC/other construction.
        if (ContainsAny(hay, "огражден") && ContainsAny(hay, "электростанц"))
        {
            return Hit("g01c06", 0.9, mappingVersion, method);
        }

        // T12 — paper hygiene household goods, not office paper (g15c02).
        if (ContainsAny(hay, "бумажно гигиеническ", "бумажногигиеническ") ||
            (ContainsAny(hay, "хозяйственн товар") && ContainsAny(hay, "бумажн", "гигиеническ")))
        {
            return Hit("g15c08", 0.9, mappingVersion, method);
        }

        return null;
    }

    private static string BuildHaystack(OrderClassificationInput input)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.ObjectName))
        {
            parts.Add(input.ObjectName!);
        }

        if (!string.IsNullOrWhiteSpace(input.Title))
        {
            parts.Add(input.Title!);
        }

        foreach (var p in input.Positions)
        {
            if (!string.IsNullOrWhiteSpace(p.Name))
            {
                parts.Add(p.Name!);
            }
        }

        return Normalize(string.Join(' ', parts));
    }

    /// <summary>
    /// #41: mixed food basket or generic «продукты питания» title → primary g06c16,
    /// keep distinct g06 L2s from codes as secondary labels.
    /// </summary>
    private ClassificationResult? TryFoodAssortment(
        OrderClassificationInput input,
        ClassificationResult mapped,
        string hay)
    {
        if (!string.Equals(mapped.Primary?.L1.Id, "g06", StringComparison.Ordinal))
        {
            return null;
        }

        var foodL2s = mapper.DistinctMappedL2Ids(input.Positions)
            .Where(id => id.StartsWith("g06", StringComparison.Ordinal) &&
                         !string.Equals(id, "g06c16", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var multiFoodL2 = foodL2s.Count >= 2;
        var genericTitle = IsGenericFoodAssortmentTitle(input, hay);
        if (!multiFoodL2 && !genericTitle)
        {
            return null;
        }

        // #44: keep every distinct mapped g06 L2 as secondary (no Take(2) cap).
        if (string.Equals(mapped.Primary?.L2?.Id, "g06c16", StringComparison.Ordinal))
        {
            var existing = mapped.Categories
                .Where(c => !c.IsPrimary && c.L2 is not null)
                .Select(c => c.L2!.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (foodL2s.Count > 0 && foodL2s.All(existing.Contains))
            {
                return null;
            }
        }

        if (!taxonomy.TryGetL2("g06c16", out var l1, out var assortment))
        {
            throw new InvalidOperationException("taxonomy missing g06c16");
        }

        var categories = new List<CategoryAssignment>
        {
            new()
            {
                L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                L2 = new CategoryRef { Id = assortment.Id, Name = assortment.Name },
                Score = 19,
                Share = 1,
                IsPrimary = true
            }
        };

        foreach (var l2Id in foodL2s)
        {
            if (!taxonomy.TryGetL2(l2Id, out var secL1, out var secL2))
            {
                continue;
            }

            categories.Add(new CategoryAssignment
            {
                L1 = new CategoryRef { Id = secL1.Id, Name = secL1.Name },
                L2 = new CategoryRef { Id = secL2.Id, Name = secL2.Name },
                Score = 10,
                Share = 0,
                IsPrimary = false
            });
        }

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = mapped.Method,
            MappingVersion = mapped.MappingVersion,
            Confidence = 0.9,
            ReviewStatus = ReviewStatuses.None,
            Categories = categories
        };
    }

    private static bool IsGenericFoodAssortmentTitle(OrderClassificationInput input, string hay)
    {
        if (!ContainsAny(hay, "продуктов питания", "продукты питания", "продукт питания", "продуктами питания"))
        {
            return false;
        }

        return !LooksLikeSingleFoodKindClarifier(input);
    }

    private static bool LooksLikeSingleFoodKindClarifier(OrderClassificationInput input)
    {
        var raw = $"{input.ObjectName} {input.Title}";
        var match = FoodTitleWithClarifier.Match(raw);
        if (!match.Success)
        {
            return false;
        }

        var inner = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Trim()
            .ToLowerInvariant();
        if (inner.Length == 0 ||
            inner.Contains(',') ||
            inner.Contains(';', StringComparison.Ordinal) ||
            Regex.IsMatch(inner, @"\sи\s") ||
            inner.Contains("спецпитани", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private ClassificationResult Hit(
        string l2Id,
        double confidence,
        string? mappingVersion = null,
        string? method = null)
    {
        if (!taxonomy.TryGetL2(l2Id, out var l1, out var l2))
        {
            throw new InvalidOperationException($"taxonomy missing {l2Id}");
        }

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = method ?? ClassificationMethods.RulesV2,
            MappingVersion = mappingVersion,
            Confidence = confidence,
            ReviewStatus = ReviewStatuses.None,
            Categories =
            [
                new CategoryAssignment
                {
                    L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                    L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                    Score = 19,
                    IsPrimary = true
                }
            ]
        };
    }

    private ClassificationResult? TryConstructionWorks(
        OrderClassificationInput input,
        ClassificationResult mapped,
        string hay)
    {
        if (!HasOkpdPrefix(input, "42") && !HasOkpdPrefix(input, "43"))
        {
            return null;
        }

        var capitalRepair = ContainsAny(hay, "капитальн") && ContainsAny(hay, "ремонт");
        var constructionCue = capitalRepair ||
                              ContainsAny(hay, "строительств", "реконструкц", "устройств", "сооружен");
        if (!constructionCue && !ContainsAny(hay, "выполнение работ"))
        {
            return null;
        }

        // Need a strong construction cue so winter road maintenance stays on g02 (#46 control).
        if (!constructionCue)
        {
            return null;
        }

        // T11: fencing works at a power-plant site stay g01c06, not generic construction.
        var primaryL2 = ContainsAny(hay, "огражден") && ContainsAny(hay, "электростанц")
            ? "g01c06"
            : capitalRepair ? "g01c02" : "g01c01";
        if (!taxonomy.TryGetL2(primaryL2, out var l1, out var l2))
        {
            throw new InvalidOperationException($"taxonomy missing {primaryL2}");
        }

        var categories = new List<CategoryAssignment>
        {
            new()
            {
                L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                Score = 19,
                Share = 1,
                IsPrimary = true
            }
        };

        var seenL1 = new HashSet<string>(StringComparer.Ordinal) { l1.Id };
        foreach (var mappedL2Id in mapper.DistinctMappedL2Ids(input.Positions))
        {
            if (string.Equals(mappedL2Id, primaryL2, StringComparison.Ordinal) ||
                !taxonomy.TryGetL2(mappedL2Id, out var secL1, out var secL2) ||
                !seenL1.Add(secL1.Id))
            {
                continue;
            }

            categories.Add(new CategoryAssignment
            {
                L1 = new CategoryRef { Id = secL1.Id, Name = secL1.Name },
                L2 = new CategoryRef { Id = secL2.Id, Name = secL2.Name },
                Score = 10,
                Share = 0,
                IsPrimary = false
            });
        }

        return new ClassificationResult
        {
            TaxonomyVersion = taxonomy.Version,
            Method = mapped.Method,
            MappingVersion = mapped.MappingVersion,
            Confidence = 0.93,
            ReviewStatus = ReviewStatuses.None,
            Categories = categories
        };
    }

    /// <summary>Promote a more specific L2; keep mapped primary L1/L2 as secondary when different.</summary>
    private ClassificationResult PromoteWithSecondary(string l2Id, ClassificationResult mapped)
    {
        var result = Hit(l2Id, 0.93, mapped.MappingVersion, mapped.Method);
        var mappedPrimary = mapped.Primary;
        if (mappedPrimary?.L2 is null ||
            string.Equals(mappedPrimary.L2.Id, l2Id, StringComparison.Ordinal))
        {
            return result;
        }

        var categories = result.Categories.ToList();
        categories.Add(new CategoryAssignment
        {
            L1 = mappedPrimary.L1,
            L2 = mappedPrimary.L2,
            Score = 10,
            Share = 0,
            IsPrimary = false
        });
        return new ClassificationResult
        {
            TaxonomyVersion = result.TaxonomyVersion,
            Method = result.Method,
            MappingVersion = result.MappingVersion,
            Confidence = result.Confidence,
            ReviewStatus = result.ReviewStatus,
            Categories = categories
        };
    }

    private static bool HasOkpdPrefix(OrderClassificationInput input, string prefix)
    {
        var want = new string(prefix.Where(char.IsDigit).ToArray());
        if (want.Length == 0)
        {
            return false;
        }

        foreach (var p in input.Positions)
        {
            if (string.IsNullOrWhiteSpace(p.Okpd2Code))
            {
                continue;
            }

            var got = new string(p.Okpd2Code.Where(char.IsDigit).ToArray());
            if (got.StartsWith(want, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string hay, params string[] needles) =>
        needles.Any(n => hay.Contains(Normalize(n), StringComparison.Ordinal));

    private static bool ContainsWhole(string hay, params string[] tokens)
    {
        var padded = $" {hay} ";
        foreach (var token in tokens)
        {
            var needle = $" {Normalize(token)} ";
            if (padded.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string text)
    {
        var form = text.ToLower(CultureInfo.GetCultureInfo("ru-RU")).Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(' ');
            }
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
