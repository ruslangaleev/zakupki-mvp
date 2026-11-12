using System.Text;
using Microsoft.ML;
using Microsoft.ML.Data;

// Train L1 multiclass model from data/train/train.tsv → models/l1.zip
// Usage: dotnet run -c Release --project backend/Zakupki.Categorization.TrainMl -- --in data/train/train.tsv --out models/l1.zip

var inPath = Path.GetFullPath(Arg(args, "--in", "data/train/train.tsv"));
var outPath = Path.GetFullPath(Arg(args, "--out", "models/l1.zip"));
// Cap per L1 (PM: ≤50k). Default 4000 — better holdout MicroAccuracy on STE→orders transfer.
var perClass = int.Parse(Arg(args, "--per-class", "4000"));
var seed = int.Parse(Arg(args, "--seed", "42"));

if (!File.Exists(inPath))
{
    Console.Error.WriteLine($"Train file not found: {inPath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

var samplePath = Path.Combine(Path.GetDirectoryName(outPath)!, "_train_l1_sample.tsv");
var sampled = WriteStratifiedSample(inPath, samplePath, perClass, seed);
Console.WriteLine($"Sampled {sampled} rows (cap {perClass}/L1) → {samplePath}");

var ml = new MLContext(seed: seed);
var data = ml.Data.LoadFromTextFile<TrainRow>(
    samplePath,
    hasHeader: true,
    separatorChar: '\t',
    allowQuoting: false,
    allowSparse: false);

var split = ml.Data.TrainTestSplit(data, testFraction: 0.1, seed: seed);

var pipeline = ml.Transforms.Conversion.MapValueToKey("Label")
    .Append(ml.Transforms.Text.FeaturizeText("Features", "Text"))
    .Append(ml.MulticlassClassification.Trainers.SdcaMaximumEntropy(
        labelColumnName: "Label",
        featureColumnName: "Features"))
    .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

Console.WriteLine("Training SdcaMaximumEntropy…");
var model = pipeline.Fit(split.TrainSet);

var metrics = ml.MulticlassClassification.Evaluate(
    model.Transform(split.TestSet),
    labelColumnName: "Label",
    predictedLabelColumnName: "PredictedLabel");
Console.WriteLine($"MicroAccuracy={metrics.MicroAccuracy:F4} MacroAccuracy={metrics.MacroAccuracy:F4} LogLoss={metrics.LogLoss:F4}");

ml.Model.Save(model, data.Schema, outPath);
Console.WriteLine($"Saved {outPath} ({new FileInfo(outPath).Length} bytes)");
return 0;

static int WriteStratifiedSample(string inPath, string outPath, int perClass, int seed)
{
    var rng = new Random(seed);
    var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    using (var reader = new StreamReader(inPath, Encoding.UTF8))
    {
        _ = reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var l1 = parts[1];
            if (!buckets.TryGetValue(l1, out var list))
            {
                list = [];
                buckets[l1] = list;
            }

            if (list.Count < perClass)
            {
                list.Add(line);
            }
            else
            {
                var seen = list.Count + 1;
                var j = rng.Next(seen);
                if (j < perClass)
                {
                    list[j] = line;
                }
            }
        }
    }

    using var writer = new StreamWriter(outPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
    writer.WriteLine("Text\tLabel");
    var n = 0;
    foreach (var list in buckets.Values)
    {
        foreach (var line in list)
        {
            var parts = line.Split('\t');
            var text = Unescape(parts[0]);
            writer.Write(Escape(text));
            writer.Write('\t');
            writer.Write(parts[1]);
            writer.Write('\n');
            n++;
        }
    }

    return n;
}

static string Escape(string value) =>
    value.Replace("\\", "\\\\").Replace("\t", " ").Replace("\n", " ").Replace("\r", " ");

static string Unescape(string value)
{
    var sb = new StringBuilder(value.Length);
    for (var i = 0; i < value.Length; i++)
    {
        if (value[i] == '\\' && i + 1 < value.Length)
        {
            sb.Append(value[i + 1] switch
            {
                't' => ' ',
                'n' => ' ',
                'r' => ' ',
                '\\' => '\\',
                var o => o
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

file sealed class TrainRow
{
    [LoadColumn(0)]
    public string Text { get; set; } = "";

    [LoadColumn(1)]
    public string Label { get; set; } = "";
}
