using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Eval;
using Zakupki.Categorization.Models;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Any(a => string.Equals(a, "--sample-part01", StringComparison.OrdinalIgnoreCase)))
    {
        return SamplePart01.Run(args);
    }

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    var goldPath = Path.GetFullPath(Arg(args, "--gold", "data/eval/gold150.csv"));
    var ordersPath = Path.GetFullPath(Arg(args, "--orders", "data/orders"));
    var outPath = Path.GetFullPath(Arg(args, "--out", "eval/metrics.json"));
    var errorsOutArg = Arg(args, "--errors-out", "");
    var thresholdsPath = Arg(args, "--thresholds", "");
    var stripCodes = HasFlag(args, "--strip-codes");
    var enableMl = !HasFlag(args, "--no-ml");

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

    var rows = ReadGoldCsv(goldPath);
    var services = new ServiceCollection();
    services.AddZakupkiCategorization(enableMl: enableMl);
    var sp = services.BuildServiceProvider();
    var classifier = sp.GetRequiredService<IOrderClassifier>();
    var mapperVersion = sp.GetRequiredService<Zakupki.Categorization.Mapping.OkpdKtruMapper>().Version;
    Console.WriteLine($"eval flags: stripCodes={stripCodes} enableMl={enableMl}");

    Console.WriteLine(
        "WARNING: gold150 labels mostly come from rules-v2 (sample-as-is, seed=7, 11.11); " +
        "only ~16 rows are independent (source=xlsx-fix). Text-only / --strip-codes accuracy is inflated. " +
        "Use gold150 as regression floor; independent gold1000 is #17.");

    var evaluated = 0;
    var skipped = 0;
    var l1Hits = 0;
    var l2Hits = 0;
    var other = 0;
    var queued = 0;
    var fixEval = 0;
    var fixL1Hits = 0;
    var fixL2Hits = 0;
    var errors = new List<object>();
    var l2ErrorRows = new List<L2ErrorRow>();
    var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (var row in rows)
    {
        var orderDir = Path.Combine(ordersPath, row.RegNumber);
        var metaPath = Path.Combine(orderDir, "meta.json");
        if (!File.Exists(metaPath))
        {
            skipped++;
            Bump(skipReasons, "missing_meta");
            continue;
        }

        OrderMeta? meta;
        try
        {
            await using var stream = File.OpenRead(metaPath);
            meta = await JsonSerializer.DeserializeAsync<OrderMeta>(stream, jsonOptions);
        }
        catch (Exception ex)
        {
            skipped++;
            Bump(skipReasons, "meta_parse_error");
            Console.Error.WriteLine($"Skip {row.RegNumber}: {ex.Message}");
            continue;
        }

        if (meta is null)
        {
            skipped++;
            Bump(skipReasons, "meta_null");
            continue;
        }

        var positions = meta.Positions ?? [];
        if (stripCodes && positions.Count > 0)
        {
            positions = positions
                .Select(p => new PurchasePosition
                {
                    Okpd2Code = null,
                    Okpd2Name = p.Okpd2Name,
                    KtruCode = null,
                    KtruName = p.KtruName,
                    Name = p.Name,
                    Unit = p.Unit,
                    Quantity = p.Quantity,
                    UnitPrice = p.UnitPrice,
                    Amount = p.Amount,
                    IsPrimary = p.IsPrimary
                })
                .ToList();
        }

        var result = classifier.Classify(new OrderClassificationInput
        {
            RegNumber = row.RegNumber,
            Title = meta.Title ?? row.Title,
            ObjectName = meta.ObjectName ?? row.Title,
            Customer = meta.Customer,
            Positions = positions
        });

        evaluated++;
        var gotL1 = result.Primary?.L1.Id;
        var gotL2 = result.Primary?.L2?.Id;
        if (string.Equals(gotL2, "g25c01", StringComparison.Ordinal) ||
            string.Equals(gotL1, "g25", StringComparison.Ordinal))
        {
            other++;
        }

        if (string.Equals(result.ReviewStatus, ReviewStatuses.Queued, StringComparison.Ordinal))
        {
            queued++;
        }

        var l1Ok = string.Equals(gotL1, row.L1, StringComparison.Ordinal);
        var l2Ok = string.Equals(gotL2, row.L2, StringComparison.Ordinal);
        if (l1Ok)
        {
            l1Hits++;
        }

        if (l2Ok)
        {
            l2Hits++;
        }

        var isFix = string.Equals(row.Source, "xlsx-fix", StringComparison.OrdinalIgnoreCase);
        if (isFix)
        {
            fixEval++;
            if (l1Ok)
            {
                fixL1Hits++;
            }

            if (l2Ok)
            {
                fixL2Hits++;
            }
        }

        if (!l1Ok || !l2Ok)
        {
            errors.Add(new
            {
                regNumber = row.RegNumber,
                expectedL1 = row.L1,
                expectedL2 = row.L2,
                gotL1,
                gotL2,
                method = result.Method,
                reviewStatus = result.ReviewStatus,
                confidence = result.Confidence,
                source = row.Source,
                title = row.Title
            });
        }

        if (!l2Ok)
        {
            var title = FirstNonEmpty(meta.ObjectName, meta.Title, row.Title) ?? "";
            var okpd = DistinctJoin(positions.Select(p => p.Okpd2Code));
            var ktru = DistinctJoin(positions.Select(p => p.KtruCode));
            l2ErrorRows.Add(new L2ErrorRow(
                row.RegNumber,
                title,
                okpd,
                ktru,
                row.L1,
                row.L2,
                gotL1 ?? "",
                gotL2 ?? "",
                MapErrorSource(result.Method, result.ReviewStatus)));
        }
    }

    var metrics = new
    {
        generatedAt = DateTimeOffset.UtcNow,
        mappingVersion = mapperVersion,
        goldPath,
        ordersPath,
        stripCodes,
        enableMl,
        goldLeakageWarning =
            "Most gold150 labels are rules-v2 sample-as-is (seed=7); xlsx-fix is the small independent slice.",
        goldTotal = rows.Count,
        evaluated,
        skipped,
        skipReasons,
        l1_acc = evaluated == 0 ? 0 : Math.Round((double)l1Hits / evaluated, 4),
        l2_acc = evaluated == 0 ? 0 : Math.Round((double)l2Hits / evaluated, 4),
        l1_hits = l1Hits,
        l2_hits = l2Hits,
        xlsx_fix_evaluated = fixEval,
        xlsx_fix_l1_acc = fixEval == 0 ? 0 : Math.Round((double)fixL1Hits / fixEval, 4),
        xlsx_fix_l2_acc = fixEval == 0 ? 0 : Math.Round((double)fixL2Hits / fixEval, 4),
        other_share = evaluated == 0 ? 0 : Math.Round((double)other / evaluated, 4),
        queued_share = evaluated == 0 ? 0 : Math.Round((double)queued / evaluated, 4),
        other_count = other,
        queued_count = queued,
        errors
    };

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    await File.WriteAllTextAsync(
        outPath,
        JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine(
        $"gold={rows.Count} evaluated={evaluated} skipped={skipped} " +
        $"l1_acc={metrics.l1_acc:P1} ({l1Hits}/{evaluated}) " +
        $"l2_acc={metrics.l2_acc:P1} ({l2Hits}/{evaluated}) " +
        $"other={metrics.other_share:P1} queued={metrics.queued_share:P1} " +
        $"mapping={mapperVersion}");
    Console.WriteLine(
        $"xlsx-fix: evaluated={fixEval} " +
        $"l1_acc={metrics.xlsx_fix_l1_acc:P1} ({fixL1Hits}/{fixEval}) " +
        $"l2_acc={metrics.xlsx_fix_l2_acc:P1} ({fixL2Hits}/{fixEval})");
    Console.WriteLine($"Wrote {outPath}");

    if (!string.IsNullOrWhiteSpace(errorsOutArg))
    {
        var errorsPath = Path.GetFullPath(errorsOutArg);
        Directory.CreateDirectory(Path.GetDirectoryName(errorsPath)!);
        var sb = new StringBuilder();
        sb.AppendLine("regNumber,title,okpd2,ktru,expected_l1,expected_l2,got_l1,got_l2,source");
        foreach (var e in l2ErrorRows.OrderBy(r => r.RegNumber, StringComparer.Ordinal))
        {
            sb.Append(CsvEscape(e.RegNumber)).Append(',')
                .Append(CsvEscape(e.Title)).Append(',')
                .Append(CsvEscape(e.Okpd2)).Append(',')
                .Append(CsvEscape(e.Ktru)).Append(',')
                .Append(CsvEscape(e.ExpectedL1)).Append(',')
                .Append(CsvEscape(e.ExpectedL2)).Append(',')
                .Append(CsvEscape(e.GotL1)).Append(',')
                .Append(CsvEscape(e.GotL2)).Append(',')
                .Append(CsvEscape(e.Source))
                .AppendLine();
        }

        await File.WriteAllTextAsync(errorsPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var l2Miss = evaluated - l2Hits;
        var l1Miss = evaluated - l1Hits;
        var l2Only = l2ErrorRows.Count(r =>
            string.Equals(r.ExpectedL1, r.GotL1, StringComparison.Ordinal));
        var noOkpd = l2ErrorRows.Count(r => string.IsNullOrWhiteSpace(r.Okpd2));
        Console.WriteLine(
            $"errors-out: wrote {l2ErrorRows.Count} L2-error rows → {errorsPath} " +
            $"(l2_miss={l2Miss}, l1_miss={l1Miss}, l2_only={l2Only}, no_okpd2={noOkpd})");
        if (l2ErrorRows.Count != l2Miss)
        {
            Console.Error.WriteLine(
                $"errors-out row count {l2ErrorRows.Count} != l2_miss {l2Miss}");
            return 1;
        }

        Console.WriteLine("top expected_l2 → got_l2:");
        foreach (var g in l2ErrorRows
                     .GroupBy(r => $"{r.ExpectedL2} → {r.GotL2}")
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.Ordinal)
                     .Take(10))
        {
            Console.WriteLine($"  {g.Count(),3}  {g.Key}");
        }
    }

    if (!string.IsNullOrWhiteSpace(thresholdsPath))
    {
        var thrFull = Path.GetFullPath(thresholdsPath);
        if (!File.Exists(thrFull))
        {
            Console.Error.WriteLine($"Thresholds file not found: {thrFull}");
            return 1;
        }

        using var thrDoc = JsonDocument.Parse(await File.ReadAllTextAsync(thrFull));
        if (!thrDoc.RootElement.TryGetProperty("gold150", out var goldThr))
        {
            Console.Error.WriteLine("thresholds.json missing gold150 section");
            return 1;
        }

        // Compare one-decimal percents so 103/150 (68.666…%) matches the published 68.7% floor.
        var minL1Pct = goldThr.TryGetProperty("min_l1_pct", out var l1El)
            ? l1El.GetDouble()
            : goldThr.GetProperty("min_l1_acc").GetDouble() * 100;
        var minL2Pct = goldThr.TryGetProperty("min_l2_pct", out var l2El)
            ? l2El.GetDouble()
            : goldThr.GetProperty("min_l2_acc").GetDouble() * 100;
        var l1Pct = evaluated == 0 ? 0 : Math.Round(100.0 * l1Hits / evaluated, 1);
        var l2Pct = evaluated == 0 ? 0 : Math.Round(100.0 * l2Hits / evaluated, 1);
        var ok = l1Pct + 1e-9 >= minL1Pct && l2Pct + 1e-9 >= minL2Pct;
        Console.WriteLine(
            $"thresholds: min_l1={minL1Pct:0.0}% min_l2={minL2Pct:0.0}% " +
            $"(got {l1Pct:0.0}% / {l2Pct:0.0}%) → {(ok ? "PASS" : "FAIL")}");
        if (!ok)
        {
            Console.Error.WriteLine(
                $"Regression: l1={l1Pct:0.0}% (min {minL1Pct:0.0}%), " +
                $"l2={l2Pct:0.0}% (min {minL2Pct:0.0}%)");
            return 2;
        }
    }

    return 0;

    static string Arg(string[] argv, string name, string fallback)
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

    static bool HasFlag(string[] argv, string name) =>
        argv.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    static void Bump(Dictionary<string, int> map, string key) =>
        map[key] = map.TryGetValue(key, out var n) ? n + 1 : 1;

    static string MapErrorSource(string? method, string? reviewStatus)
    {
        if (string.Equals(reviewStatus, ReviewStatuses.Queued, StringComparison.Ordinal))
        {
            return "queue";
        }

        if (string.Equals(method, ClassificationMethods.MlL1, StringComparison.Ordinal))
        {
            return "ml";
        }

        if (string.Equals(method, ClassificationMethods.RulesV2, StringComparison.Ordinal))
        {
            return "rules";
        }

        return "mapping";
    }

    static string DistinctJoin(IEnumerable<string?> values) =>
        string.Join(
            ";",
            values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal));

    static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    static string CsvEscape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    static List<GoldRow> ReadGoldCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
        {
            return [];
        }

        var list = new List<GoldRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var cols = SplitCsvLine(line);
            if (cols.Count < 3)
            {
                continue;
            }

            list.Add(new GoldRow(
                cols[0].Trim(),
                cols[1].Trim(),
                cols[2].Trim(),
                cols.Count > 3 ? cols[3].Trim() : "",
                cols.Count > 4 ? cols[4].Trim() : ""));
        }

        return list;
    }

    static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
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

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        result.Add(sb.ToString());
        return result;
    }
}

internal sealed record GoldRow(string RegNumber, string L1, string L2, string Source, string Title);

internal sealed record L2ErrorRow(
    string RegNumber,
    string Title,
    string Okpd2,
    string Ktru,
    string ExpectedL1,
    string ExpectedL2,
    string GotL1,
    string GotL2,
    string Source);

internal sealed class OrderMeta
{
    public string? Title { get; set; }
    public string? ObjectName { get; set; }
    public string? Customer { get; set; }
    public List<PurchasePosition>? Positions { get; set; }
}
