using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization;
using Zakupki.Categorization.Mapping;
using Zakupki.Categorization.Models;

return Run(args);

static int Run(string[] args)
{
    var inPath = Path.GetFullPath(Arg(args, "--in", "data/train/_candidates.tsv"));
    var goldPath = Path.GetFullPath(Arg(args, "--gold", "data/eval/gold150.csv"));
    var outPath = Path.GetFullPath(Arg(args, "--out", "data/train/train.tsv"));

    if (!File.Exists(inPath))
    {
        Console.Error.WriteLine($"Candidates file not found: {inPath}");
        return 1;
    }

    if (!File.Exists(goldPath))
    {
        Console.Error.WriteLine($"Gold file not found: {goldPath}");
        return 1;
    }

    var gold = LoadGold(goldPath);
    Console.WriteLine($"Gold rows: {gold.RegNumbers.Count}, titles: {gold.Titles.Count}");

    var services = new ServiceCollection();
    services.AddZakupkiCategorization();
    using var sp = services.BuildServiceProvider();
    var mapper = sp.GetRequiredService<OkpdKtruMapper>();
    Console.WriteLine($"Mapper version: {mapper.Version}");

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

    var seenText = new HashSet<string>(StringComparer.Ordinal);
    var l1Counts = new Dictionary<string, int>(StringComparer.Ordinal);
    var written = 0;
    var skippedNoMap = 0;
    var skippedGold = 0;
    var skippedDup = 0;
    var skippedGoldReg = 0;

    using var reader = new StreamReader(inPath, Encoding.UTF8);
    using var writer = new StreamWriter(outPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    {
        NewLine = "\n"
    };
    writer.WriteLine("text\tl1\tl2");

    var header = reader.ReadLine();
    if (header is null)
    {
        Console.Error.WriteLine("Empty candidates file");
        return 1;
    }

    var cols = header.Split('\t');
    var iText = Array.IndexOf(cols, "text");
    var iOkpd = Array.IndexOf(cols, "okpd2");
    var iKtru = Array.IndexOf(cols, "ktru");
    var iReg = Array.IndexOf(cols, "regNumber");
    if (iText < 0 || iOkpd < 0)
    {
        Console.Error.WriteLine("Candidates header must contain text and okpd2 columns");
        return 1;
    }

    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var parts = line.Split('\t');
        if (parts.Length <= Math.Max(iText, iOkpd))
        {
            continue;
        }

        var text = Unescape(parts[iText]).Trim();
        if (text.Length == 0)
        {
            continue;
        }

        if (iReg >= 0 && iReg < parts.Length)
        {
            var reg = Unescape(parts[iReg]).Trim();
            if (reg.Length > 0 && gold.RegNumbers.Contains(reg))
            {
                skippedGoldReg++;
                continue;
            }
        }

        var norm = Normalize(text);
        if (gold.Titles.Contains(norm))
        {
            skippedGold++;
            continue;
        }

        if (!seenText.Add(norm))
        {
            skippedDup++;
            continue;
        }

        var okpd = iOkpd < parts.Length ? Unescape(parts[iOkpd]).Trim() : "";
        var ktru = iKtru >= 0 && iKtru < parts.Length ? Unescape(parts[iKtru]).Trim() : "";
        if (okpd.Length == 0 && ktru.Length == 0)
        {
            continue;
        }

        var mapped = mapper.TryMap(
        [
            new PurchasePosition
            {
                Name = text,
                Okpd2Code = string.IsNullOrEmpty(okpd) ? null : okpd,
                KtruCode = string.IsNullOrEmpty(ktru) ? null : ktru,
                IsPrimary = true
            }
        ]);

        var primary = mapped?.Categories.FirstOrDefault(c => c.IsPrimary)
                      ?? mapped?.Categories.FirstOrDefault();
        if (primary?.L1?.Id is null || primary.L2?.Id is null)
        {
            skippedNoMap++;
            continue;
        }

        // (L2 required for supervised rows)

        // Skip queue / прочее / null-L2 for supervised training (L2 required).
        if (primary.L1.Id is "g25" ||
            primary.L2 is null ||
            mapped!.ReviewStatus == ReviewStatuses.Queued)
        {
            skippedNoMap++;
            continue;
        }

        writer.Write(Escape(text));
        writer.Write('\t');
        writer.Write(primary.L1.Id);
        writer.Write('\t');
        writer.Write(primary.L2.Id);
        writer.Write('\n');

        written++;
        l1Counts[primary.L1.Id] = l1Counts.GetValueOrDefault(primary.L1.Id) + 1;
    }

    Console.WriteLine($"Wrote {written} rows → {outPath}");
    Console.WriteLine($"Skipped: dup={skippedDup}, gold_title={skippedGold}, gold_reg={skippedGoldReg}, no_map={skippedNoMap}");
    Console.WriteLine("L1 distribution:");
    foreach (var (l1, count) in l1Counts.OrderBy(kv => kv.Key))
    {
        var flag = count < 50 ? "  <50" : "";
        Console.WriteLine($"  {l1}\t{count}{flag}");
    }

    var below = l1Counts.Where(kv => kv.Value < 50).Select(kv => kv.Key).OrderBy(x => x).ToList();
    if (below.Count > 0)
    {
        Console.WriteLine("L1 with <50 examples: " + string.Join(", ", below));
    }
    else
    {
        Console.WriteLine("All L1 categories have ≥50 examples.");
    }

    if (written < 30_000)
    {
        Console.Error.WriteLine($"FAIL: need ≥30000 rows, got {written}");
        return 2;
    }

    return 0;
}

static (HashSet<string> RegNumbers, HashSet<string> Titles) LoadGold(string path)
{
    var regs = new HashSet<string>(StringComparer.Ordinal);
    var titles = new HashSet<string>(StringComparer.Ordinal);
    using var reader = new StreamReader(path, Encoding.UTF8);
    var header = reader.ReadLine();
    if (header is null)
    {
        return (regs, titles);
    }

    var cols = header.Split(',');
    var iReg = Array.IndexOf(cols, "regNumber");
    var iTitle = Array.IndexOf(cols, "title");
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        var fields = SplitCsv(line);
        if (iReg >= 0 && iReg < fields.Count)
        {
            var reg = fields[iReg].Trim();
            if (reg.Length > 0)
            {
                regs.Add(reg);
            }
        }

        if (iTitle >= 0 && iTitle < fields.Count)
        {
            var title = Normalize(fields[iTitle]);
            if (title.Length > 0)
            {
                titles.Add(title);
            }
        }
    }

    return (regs, titles);
}

static List<string> SplitCsv(string line)
{
    var result = new List<string>();
    var sb = new StringBuilder();
    var inQuotes = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (c == '"')
        {
            if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else
            {
                inQuotes = !inQuotes;
            }
        }
        else if (c == ',' && !inQuotes)
        {
            result.Add(sb.ToString());
            sb.Clear();
        }
        else
        {
            sb.Append(c);
        }
    }

    result.Add(sb.ToString());
    return result;
}

static string Normalize(string text) =>
    string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

static string Escape(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

static string Unescape(string value)
{
    var sb = new StringBuilder(value.Length);
    for (var i = 0; i < value.Length; i++)
    {
        if (value[i] == '\\' && i + 1 < value.Length)
        {
            sb.Append(value[i + 1] switch
            {
                't' => '\t',
                'n' => '\n',
                'r' => '\r',
                '\\' => '\\',
                var other => other
            });
            i++;
        }
        else
        {
            sb.Append(value[i]);
        }
    }

    return sb.ToString();
}

static string Arg(string[] args, string name, string fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return fallback;
}
