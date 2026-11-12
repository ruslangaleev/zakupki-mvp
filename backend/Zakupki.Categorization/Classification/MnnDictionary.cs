using System.Globalization;
using System.Text;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Categorization.Classification;

/// <summary>
/// Seed MNN/INN dictionary for medicine names that appear without "лекарственный препарат".
/// </summary>
public sealed class MnnDictionary(ITaxonomyCatalog taxonomy)
{
    // Lowercase Russian INN / stems (MVP seed from audit «Прочее» + common ЖНВЛП).
    private static readonly string[] Inns =
    [
        "азитромицин", "амантадин", "амброксол", "амиодарон", "амикацин", "амлодипин",
        "амоксициллин", "ампициллин", "антитромбин", "аторвастатин", "атракурия",
        "ацетилсалициловая", "ацикловир", "бензилпенициллин", "бетаметазон", "блеомицин",
        "бисопролол", "валерианы", "ванкомицин", "варфарин", "винпоцетин", "висмута",
        "габапентин", "гентамицин", "гепарин", "гемцитабин", "гидрокортизон", "гидроксиэтилкрахмал",
        "глюкоза", "дакарбазин", "декспантенол", "дексаметазон", "диазепам", "дигоксин",
        "диклофенак", "диоксометилтетрагидропиримидин", "допамин", "добутамин",
        "доксорубицин", "дротаверин", "дурвалумаб", "ибупрофен", "имипенем", "индапамид",
        "инсулин", "иринотекан", "йогексол", "каптоприл", "калия хлорид", "карбамазепин",
        "клавулановая", "кларитромицин", "клиндамицин", "клопидогрел", "левофлоксацин",
        "левотироксин", "лидокаин", "линезолид", "лозартан", "магния сульфат", "меропенем",
        "метамизол", "метилпреднизолон", "метформин", "метронидазол", "мидазолам",
        "моксифлоксацин", "морфин", "натрия хлорид", "нетакимаб", "ниволумаб", "нифедипин",
        "норэпинефрин", "омепразол", "ондансетрон", "окситоцин", "офлоксацин", "пантенол",
        "парацетамол", "пеметрексед", "пентоксифиллин", "пипекурония", "пирацетам",
        "преднизолон", "прогестерон", "прокаин", "пропофол", "ранитидин", "рокурония",
        "секукинумаб", "севофлуран", "ситаглиптин", "спиронолактон", "сульфадиметоксин",
        "тамоксифен", "трамадол", "тримекаин", "урокиназа", "фамотидин", "фентанил",
        "фенофибрат", "филграстим", "флудрокортизон", "флуконазол", "фуросемид",
        "хлорамфеникол", "хлоргексидин", "холина альфосцерат", "цетуксимаб", "цефтриаксон",
        "цефазолин", "цефепим", "цефотаксим", "ципрофлоксацин", "цитиколин", "эноксапарин",
        "эпоэтин", "эритромицин", "этамзилат", "этанол", "эуфиллин"
    ];

    public ClassificationResult? TryClassify(OrderClassificationInput input)
    {
        var hay = Normalize($"{input.ObjectName} {input.Title}");
        if (string.IsNullOrWhiteSpace(hay))
        {
            return null;
        }

        foreach (var inn in Inns)
        {
            var token = Normalize(inn);
            if (token.Length < 4)
            {
                continue;
            }

            if (!ContainsWholeToken(hay, token))
            {
                continue;
            }

            if (!taxonomy.TryGetL2("g05c01", out var l1, out var l2))
            {
                return null;
            }

            return new ClassificationResult
            {
                TaxonomyVersion = taxonomy.Version,
                Method = ClassificationMethods.InnDict,
                Confidence = 0.9,
                ReviewStatus = ReviewStatuses.None,
                Categories =
                [
                    new CategoryAssignment
                    {
                        L1 = new CategoryRef { Id = l1.Id, Name = l1.Name },
                        L2 = new CategoryRef { Id = l2.Id, Name = l2.Name },
                        Score = 18,
                        IsPrimary = true
                    }
                ]
            };
        }

        return null;
    }

    private static bool ContainsWholeToken(string hay, string token)
    {
        var padded = $" {hay} ";
        var needle = $" {token} ";
        if (padded.Contains(needle, StringComparison.Ordinal))
        {
            return true;
        }

        // Allow mild ending drift: клиндамицин/клиндамицина
        foreach (var word in hay.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals(token, StringComparison.Ordinal))
            {
                return true;
            }

            if (token.Contains(' '))
            {
                continue;
            }

            if (Math.Abs(word.Length - token.Length) <= 2 &&
                word.StartsWith(token[..Math.Min(token.Length, Math.Max(4, token.Length - 1))], StringComparison.Ordinal))
            {
                var shared = 0;
                var n = Math.Min(word.Length, token.Length);
                while (shared < n && word[shared] == token[shared])
                {
                    shared++;
                }

                if (shared >= token.Length - 1)
                {
                    return true;
                }
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
