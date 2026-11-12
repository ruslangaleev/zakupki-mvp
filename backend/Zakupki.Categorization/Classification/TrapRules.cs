using System.Globalization;
using System.Text;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Hard overrides for regression traps T01–T15 (product TZ).
/// Runs after OKPD/KTRU map and MNN dict, before keyword rules.
/// </summary>
public sealed class TrapRules(ITaxonomyCatalog taxonomy)
{
    /// <summary>
    /// Overrides coarse OKPD/KTRU hits when the card text is more specific
    /// (repair=33.*, plastics=22.*, security=80.*).
    /// </summary>
    public ClassificationResult? TryRefine(OrderClassificationInput input, ClassificationResult mapped)
    {
        var hay = Normalize($"{input.ObjectName} {input.Title}");
        if (string.IsNullOrWhiteSpace(hay))
        {
            return null;
        }

        var mappedL2 = mapped.Primary?.L2.Id;

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
            !ContainsAny(hay, "антигололед", "антигололёд") &&
            mappedL2 is "g05c01" or "g14c01" or "g14c02" or "g23c05" or "g25c01")
        {
            return Hit("g05c04", 0.92, mapped.MappingVersion, mapped.Method);
        }

        if (ContainsAny(hay, "картридж", "тонер") &&
            !ContainsAny(hay, "инсулин", "шприц") &&
            mappedL2 is "g14c01" or "g14c02" or "g14c05" or "g25c01")
        {
            return Hit("g07c02", 0.92, mapped.MappingVersion, mapped.Method);
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
        // Do NOT trigger on bare «медицин*» (заказчик/учреждение in the title).
        if (ContainsAny(hay,
                "медицинск оборудован", "медицинского оборудован", "медицинское оборудован",
                "медтехник", "томограф", "аппарат узи", "узи аппарат",
                "рентген", "центрифуг", "ивл", "аппарат ивл") &&
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
        var hay = Normalize($"{input.ObjectName} {input.Title}");
        if (string.IsNullOrWhiteSpace(hay))
        {
            return null;
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

        // T10 / medicine phrasing — beat chemistry keywords.
        if (ContainsAny(hay,
                "лекарственн средств",
                "лекарственн препарат",
                "антибактериальн препарат",
                "противоопухолев препарат",
                "психотропн"))
        {
            return Hit("g05c01", 0.92);
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

        // T05 — anti-ice reagents / sand boxes, not lab diagnostics.
        if (ContainsAny(hay, "антигололед", "антигололёд") ||
            (ContainsAny(hay, "ящик", "ящиков") && ContainsAny(hay, "песка", "антигололед", "антигололёд")))
        {
            return Hit("g13c12", 0.88);
        }

        // T06 — vehicle diagnostics / maintenance.
        if (ContainsAny(hay, "диагностик") &&
            ContainsAny(hay, "автотранспорт", "автомобил", "транспортн средств", "то автомобилей", "ремонт авто"))
        {
            return Hit("g08c05", 0.92);
        }

        // T07 — video conferencing gear, not event conferences.
        if (ContainsAny(hay, "видео конференц", "видеоконференц", "вкс") ||
            (ContainsAny(hay, "конференц") && ContainsAny(hay, "видео", "связь", "монтаж комплекс")))
        {
            return Hit("g07c13", 0.9);
        }

        // T08 — snow/ice roof cleaning → territory maintenance.
        if (ContainsAny(hay, "очистк") && ContainsAny(hay, "кровл", "крыш") &&
            ContainsAny(hay, "снег", "налед", "наледи", "сосулек", "сосульк"))
        {
            return Hit("g22c02", 0.92);
        }

        // T09 — APS / SOUE maintenance → security.
        if (ContainsAny(hay, " апс ", "соуэ", "пожарн сигнализац", "охранно пожарн") ||
            (ContainsAny(hay, "техническ обслуживан") && ContainsAny(hay, " апс", "соуэ", "сигнализац")) ||
            (ContainsAny(hay, "системы апс") || ContainsAny(hay, "апс и со")))
        {
            return Hit("g17c02", 0.92);
        }

        // Guard / monitoring services (often OKPD 84.24 → was silent Прочее).
        if (ContainsAny(hay, "охран") && !ContainsAny(hay, "охрана труда", "охраны труда", "окружающ"))
        {
            if (ContainsAny(hay, "сигнализац", "апс", "соуэ", "скуд", "тревожн", "пульт"))
            {
                return Hit("g17c02", 0.93);
            }

            return Hit("g17c01", 0.93);
        }

        // Lab reagents / tests (not medicines, not anti-ice).
        if (ContainsAny(hay, "реагент", "тест систем", "тест-систем", "диагностикум", "для анализатора") &&
            !ContainsAny(hay, "антигололед", "антигололёд", "лекарственн"))
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

        // T11 — fencing works at a power-plant site → fencing, not generators.
        if (ContainsAny(hay, "огражден") && ContainsAny(hay, "электростанц"))
        {
            return Hit("g01c06", 0.9);
        }

        // T12 — paper hygiene household goods, not printing machinery.
        if (ContainsAny(hay, "бумажно гигиеническ", "бумажногигиеническ") ||
            (ContainsAny(hay, "хозяйственн товар") && ContainsAny(hay, "бумажн", "гигиеническ")))
        {
            return Hit("g15c08", 0.9);
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

    private static bool ContainsAny(string hay, params string[] needles) =>
        needles.Any(n => hay.Contains(Normalize(n), StringComparison.Ordinal));

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
