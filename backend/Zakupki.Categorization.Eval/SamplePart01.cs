using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakupki.Categorization.Eval;

/// <summary>
/// Deterministic sample of 100 orders for independent gold1000 labeling (issue #33 / #17).
/// Does not write classifier categories.
/// </summary>
internal static class SamplePart01
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static int Run(string[] args)
    {
        var goldPath = Path.GetFullPath(Arg(args, "--gold", "data/eval/gold150.csv"));
        var ordersPath = Path.GetFullPath(Arg(args, "--orders", "data/orders"));
        var outPath = Path.GetFullPath(Arg(args, "--out", "data/eval/gold1000_part01_sample.csv"));
        var seed = int.Parse(Arg(args, "--seed", "1"));
        var count = int.Parse(Arg(args, "--count", "100"));

        if (!File.Exists(goldPath))
        {
            Console.Error.WriteLine($"Gold file not found: {goldPath}");
            return 1;
        }

        if (!Directory.Exists(ordersPath))
        {
            Console.Error.WriteLine($"Orders directory not found: {ordersPath}");
            return 1;
        }

        var exclude = LoadGoldRegNumbers(goldPath);
        Console.WriteLine($"Exclude gold150: {exclude.Count} regNumbers");

        var candidates = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(ordersPath))
        {
            var reg = Path.GetFileName(dir);
            if (exclude.Contains(reg))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(dir, "meta.json")))
            {
                continue;
            }

            candidates.Add(reg);
        }

        candidates.Sort(StringComparer.Ordinal);
        if (candidates.Count < count)
        {
            Console.Error.WriteLine($"Not enough candidates: {candidates.Count} < {count}");
            return 1;
        }

        // Deterministic Fisher–Yates with fixed seed.
        var rng = new Random(seed);
        for (var i = candidates.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        var picked = candidates.Take(count).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var sb = new StringBuilder();
        sb.AppendLine("regNumber,title,okpd2,ktru,eisUrl,l1,l2,extra,disputed,comment");

        foreach (var reg in picked)
        {
            var metaPath = Path.Combine(ordersPath, reg, "meta.json");
            using var stream = File.OpenRead(metaPath);
            var meta = JsonSerializer.Deserialize<SampleMeta>(stream, JsonOptions)
                       ?? throw new InvalidOperationException($"Failed to parse {metaPath}");

            var title = FirstNonEmpty(meta.ObjectName, meta.Title) ?? "";
            var okpd = DistinctJoin(meta.Positions?.Select(p => p.Okpd2Code));
            var ktru = DistinctJoin(meta.Positions?.Select(p => p.KtruCode));
            var eisUrl = FirstNonEmpty(meta.NoticeUrl)
                         ?? $"https://zakupki.gov.ru/epz/order/notice/view/common-info.html?regNumber={reg}";

            sb.Append(Escape(reg)).Append(',')
                .Append(Escape(title)).Append(',')
                .Append(Escape(okpd)).Append(',')
                .Append(Escape(ktru)).Append(',')
                .Append(Escape(eisUrl)).Append(',')
                .Append(",,,,") // l1,l2,extra,disputed,comment — QA fills
                .AppendLine();
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Console.WriteLine($"Wrote {picked.Count} rows → {outPath} (seed={seed})");
        Console.WriteLine($"First/last: {picked[0]} … {picked[^1]}");
        return 0;
    }

    private static HashSet<string> LoadGoldRegNumbers(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path, Encoding.UTF8).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var comma = line.IndexOf(',');
            var reg = comma < 0 ? line.Trim() : line[..comma].Trim().Trim('"');
            if (reg.Length > 0)
            {
                set.Add(reg);
            }
        }

        return set;
    }

    private static string DistinctJoin(IEnumerable<string?>? values)
    {
        if (values is null)
        {
            return "";
        }

        return string.Join(
            ";",
            values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal));
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static string Arg(string[] argv, string name, string fallback)
    {
        for (var i = 0; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return argv[i + 1];
            }
        }

        return fallback;
    }

    private sealed class SampleMeta
    {
        public string? Title { get; set; }
        public string? ObjectName { get; set; }
        public string? NoticeUrl { get; set; }
        public List<SamplePosition>? Positions { get; set; }
    }

    private sealed class SamplePosition
    {
        public string? Okpd2Code { get; set; }
        public string? KtruCode { get; set; }
    }
}
