using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Zakupki.Categorization.Advance;
using Zakupki.Categorization.Classification;
using Zakupki.Categorization.Models;
using Zakupki.Indexer.Advance;
using Zakupki.Indexer.Classification;
using Zakupki.Indexer.Clients;
using Zakupki.Indexer.Configuration;
using Zakupki.Indexer.Extraction;
using Zakupki.Indexer.Models;
using Zakupki.Indexer.Parsing;

namespace Zakupki.Indexer.Services;

public sealed class OrderIndexingService(
    OrderTextExtractor orderTextExtractor,
    IOrderClassifier orderClassifier,
    IAdvanceDetector advanceDetector,
    ClassificationArtifactStore classificationStore,
    AdvanceArtifactStore advanceStore,
    DocumentTextExtractor documentTextExtractor,
    EmbeddingClient embeddingClient,
    OpenSearchIndexClient openSearchIndexClient,
    IOptions<IndexerOptions> options,
    ILogger<OrderIndexingService> logger)
{
    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public async Task<int> IndexAllAsync(CancellationToken cancellationToken)
    {
        var dataPath = options.Value.DataPath;
        if (!Directory.Exists(dataPath))
        {
            logger.LogWarning("Data path {Path} does not exist", dataPath);
            return 0;
        }

        await openSearchIndexClient.EnsureIndicesAsync(cancellationToken);

        var indexed = 0;
        foreach (var dir in Directory.EnumerateDirectories(dataPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "index", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                bool ok;
                if (options.Value.ObjectInfoOnly)
                {
                    ok = await PatchObjectInfoOneAsync(dir, cancellationToken);
                }
                else if (options.Value.DatesOnly)
                {
                    ok = await PatchDatesOneAsync(dir, cancellationToken);
                }
                else if (options.Value.AdvanceOnly)
                {
                    ok = await DetectAdvanceOneAsync(dir, cancellationToken);
                }
                else if (options.Value.ClassifyOnly)
                {
                    ok = await ClassifyOneAsync(dir, cancellationToken);
                }
                else
                {
                    ok = await IndexOneAsync(dir, cancellationToken);
                }

                if (ok)
                {
                    indexed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to index order folder {Dir}", dir);
            }
        }

        return indexed;
    }

    private async Task<bool> IndexOneAsync(string orderDirectory, CancellationToken cancellationToken)
    {
        var extraction = await orderTextExtractor.ExtractAsync(orderDirectory, cancellationToken);
        if (extraction is null)
        {
            return false;
        }

        var positions = await EnsurePositionsAsync(orderDirectory, extraction.Meta.Positions, cancellationToken);
        var classification = orderClassifier.Classify(new OrderClassificationInput
        {
            RegNumber = extraction.RegNumber,
            Title = extraction.Meta.Title,
            ObjectName = extraction.Meta.ObjectName,
            Customer = extraction.Meta.Customer,
            FullText = extraction.FullText,
            Positions = positions
        });
        await classificationStore.WriteAsync(orderDirectory, classification, cancellationToken);

        var advance = DetectAdvance(orderDirectory, extraction);
        await advanceStore.WriteAsync(orderDirectory, advance, cancellationToken);

        var vectors = await embeddingClient.EmbedAsync(
            extraction.Chunks.Select(c => c.Text).ToList(),
            cancellationToken);

        if (vectors.Count != extraction.Chunks.Count)
        {
            throw new InvalidOperationException(
                $"Embedding count mismatch for {extraction.RegNumber}: {vectors.Count} vs {extraction.Chunks.Count}");
        }

        await openSearchIndexClient.IndexOrderAsync(extraction, vectors, classification, advance, cancellationToken);
        logger.LogInformation(
            "Indexed {RegNumber}: chunks={Chunks}, coverage={Coverage}, category={Category}, advance={Advance}, confidence={Confidence:F2}",
            extraction.RegNumber,
            extraction.Chunks.Count,
            extraction.Coverage,
            classification.Primary?.L2.Id ?? "none",
            advance.HasAdvance ? advance.Label ?? "yes" : "no",
            classification.Confidence);
        return true;
    }

    private async Task<bool> ClassifyOneAsync(string orderDirectory, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(orderDirectory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }

        OrderMetaDocument? meta;
        await using (var metaStream = File.OpenRead(metaPath))
        {
            meta = await JsonSerializer.DeserializeAsync<OrderMetaDocument>(metaStream, MetaJsonOptions, cancellationToken);
        }

        var regNumber = meta?.RegNumber ?? Path.GetFileName(orderDirectory);
        if (meta is null || string.IsNullOrWhiteSpace(regNumber))
        {
            return false;
        }

        var objectName = meta.ObjectName;
        if (string.IsNullOrWhiteSpace(objectName) || PurchaseObjectResolver.IsGenericTitle(objectName))
        {
            var fields = meta.Fields is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(meta.Fields, StringComparer.OrdinalIgnoreCase);
            var commonInfoPath = Path.Combine(orderDirectory, "pages", "common-info.html");
            string? commonHtml = File.Exists(commonInfoPath)
                ? await File.ReadAllTextAsync(commonInfoPath, cancellationToken)
                : null;
            var resolved = PurchaseObjectResolver.ResolveObjectName(objectName, meta.Title, commonHtml, fields);
            if (!string.IsNullOrWhiteSpace(resolved) && !PurchaseObjectResolver.IsGenericTitle(resolved))
            {
                objectName = resolved;
            }
        }

        var positions = await EnsurePositionsAsync(orderDirectory, meta.Positions, cancellationToken);
        var classification = orderClassifier.Classify(new OrderClassificationInput
        {
            RegNumber = regNumber,
            Title = meta.Title,
            ObjectName = objectName,
            Customer = meta.Customer,
            FullText = null,
            Positions = positions
        });
        await classificationStore.WriteAsync(orderDirectory, classification, cancellationToken);
        await openSearchIndexClient.UpdateOrderClassificationAsync(regNumber, classification, cancellationToken);

        logger.LogInformation(
            "Reclassified {RegNumber}: category={Category}, method={Method}, review={Review}, confidence={Confidence:F2}, positions={Positions}",
            regNumber,
            classification.Primary?.L2.Id ?? "none",
            classification.Method,
            classification.ReviewStatus,
            classification.Confidence,
            positions.Count);
        return true;
    }

    private async Task<IReadOnlyList<PurchasePosition>> EnsurePositionsAsync(
        string orderDirectory,
        IReadOnlyList<PurchasePosition>? existing,
        CancellationToken cancellationToken)
    {
        if (existing is { Count: > 0 })
        {
            return existing.ToList();
        }

        var commonInfoPath = Path.Combine(orderDirectory, "pages", "common-info.html");
        if (!File.Exists(commonInfoPath))
        {
            return [];
        }

        var html = await File.ReadAllTextAsync(commonInfoPath, cancellationToken);
        var parsed = PurchasePositionsParser.Parse(html);
        if (parsed.Count == 0)
        {
            return [];
        }

        await TryWritePositionsToMetaAsync(orderDirectory, parsed, cancellationToken);
        return parsed;
    }

    private async Task TryWritePositionsToMetaAsync(
        string orderDirectory,
        IReadOnlyList<PurchasePosition> positions,
        CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(orderDirectory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                var raw = await File.ReadAllTextAsync(metaPath, cancellationToken);
                using var doc = JsonDocument.Parse(raw);
                using var buffer = new MemoryStream();
                await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    var wrotePositions = false;
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.NameEquals("positions"))
                        {
                            writer.WritePropertyName("positions");
                            JsonSerializer.Serialize(writer, positions, MetaJsonOptions);
                            wrotePositions = true;
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }

                    if (!wrotePositions)
                    {
                        writer.WritePropertyName("positions");
                        JsonSerializer.Serialize(writer, positions, MetaJsonOptions);
                    }

                    writer.WriteEndObject();
                }

                await File.WriteAllBytesAsync(metaPath, buffer.ToArray(), cancellationToken);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 8)
            {
                lastError = ex;
                await Task.Delay(150 * attempt, cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        if (lastError is not null)
        {
            logger.LogWarning(lastError, "Failed to persist positions into {Meta}", metaPath);
        }
    }

    private async Task<bool> PatchObjectInfoOneAsync(string orderDirectory, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(orderDirectory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }

        var raw = await File.ReadAllTextAsync(metaPath, cancellationToken);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var regNumber = root.TryGetProperty("regNumber", out var regEl)
            ? regEl.GetString()
            : Path.GetFileName(orderDirectory);
        if (string.IsNullOrWhiteSpace(regNumber))
        {
            return false;
        }

        var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var objectName = root.TryGetProperty("objectName", out var objEl) ? objEl.GetString() : null;
        // Only rewrite шаблонные карточки (ст. 93 и т.п.) или пустой objectName.
        if (!PurchaseObjectResolver.IsGenericTitle(title) && !PurchaseObjectResolver.IsGenericTitle(objectName))
        {
            return false;
        }

        var fields = ReadFields(root);
        var commonInfoPath = Path.Combine(orderDirectory, "pages", "common-info.html");
        string? commonHtml = File.Exists(commonInfoPath)
            ? await File.ReadAllTextAsync(commonInfoPath, cancellationToken)
            : null;

        var resolved = PurchaseObjectResolver.ResolveObjectName(objectName, title, commonHtml, fields);
        if (string.IsNullOrWhiteSpace(resolved) || PurchaseObjectResolver.IsGenericTitle(resolved))
        {
            return false;
        }

        if (!string.Equals(resolved.Trim(), objectName?.Trim(), StringComparison.Ordinal))
        {
            await using var stream = File.Create(metaPath);
            await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("objectName"))
                {
                    writer.WriteString("objectName", resolved);
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!root.TryGetProperty("objectName", out _))
            {
                writer.WriteString("objectName", resolved);
            }

            writer.WriteEndObject();
        }

        await openSearchIndexClient.UpdateOrderObjectInfoAsync(regNumber, resolved, cancellationToken);
        logger.LogInformation("ObjectInfo {RegNumber}: {ObjectInfo}", regNumber, resolved);
        return true;
    }

    private async Task<bool> PatchDatesOneAsync(string orderDirectory, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(orderDirectory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }

        await using var metaStream = File.OpenRead(metaPath);
        var meta = await JsonSerializer.DeserializeAsync<OrderMetaDocument>(metaStream, MetaJsonOptions, cancellationToken);
        var regNumber = meta?.RegNumber ?? Path.GetFileName(orderDirectory);
        if (meta is null || string.IsNullOrWhiteSpace(regNumber))
        {
            return false;
        }

        var fields = meta.Fields is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(meta.Fields, StringComparer.OrdinalIgnoreCase);
        var deadline = DateParser.TryParseApplicationDeadline(fields);
        var priceValue = PriceParser.TryParse(meta.Price);
        await openSearchIndexClient.UpdateOrderSortFieldsAsync(
            regNumber,
            meta.PubDate,
            meta.PlacedAt,
            deadline,
            priceValue,
            cancellationToken);

        logger.LogInformation(
            "Sort fields {RegNumber}: deadline={Deadline}, pubDate={PubDate}, priceValue={Price}",
            regNumber,
            deadline?.ToString("yyyy-MM-dd") ?? "n/a",
            meta.PubDate?.ToString("yyyy-MM-dd") ?? "n/a",
            priceValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a");
        return true;
    }

    private async Task<bool> DetectAdvanceOneAsync(string orderDirectory, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(orderDirectory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }

        var raw = await File.ReadAllTextAsync(metaPath, cancellationToken);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var regNumber = root.TryGetProperty("regNumber", out var regEl)
            ? regEl.GetString()
            : Path.GetFileName(orderDirectory);
        if (string.IsNullOrWhiteSpace(regNumber))
        {
            return false;
        }

        var fields = ReadFields(root);
        var texts = await CollectAdvanceTextsAsync(orderDirectory, cancellationToken);
        var advance = advanceDetector.Detect(new AdvanceDetectionInput
        {
            RegNumber = regNumber,
            Fields = fields,
            Texts = texts
        });
        await advanceStore.WriteAsync(orderDirectory, advance, cancellationToken);
        await openSearchIndexClient.UpdateOrderAdvanceAsync(regNumber, advance, cancellationToken);

        logger.LogInformation(
            "Advance {RegNumber}: {Advance}",
            regNumber,
            advance.HasAdvance ? advance.Label ?? "yes" : "no");
        return true;
    }

    private AdvanceDetectionResult DetectAdvance(string orderDirectory, OrderExtractionResult extraction)
    {
        var fields = ReadFieldsFromMetaFile(orderDirectory);
        var texts = new List<AdvanceTextPart>
        {
            new() { Source = "fullText", Text = extraction.FullText }
        };
        texts.AddRange(extraction.Chunks.Select(c => new AdvanceTextPart
        {
            Source = c.Source,
            Text = c.Text
        }));

        // Pages are often not in fullText; still scan raw page files for EIS advance section.
        foreach (var page in EnumerateSideFiles(orderDirectory, "pages"))
        {
            var attempt = documentTextExtractor.ExtractFile(page);
            if (!attempt.Skipped && !string.IsNullOrWhiteSpace(attempt.Text))
            {
                texts.Add(new AdvanceTextPart
                {
                    Source = $"pages/{Path.GetFileName(page)}",
                    Text = attempt.Text
                });
            }
        }

        return advanceDetector.Detect(new AdvanceDetectionInput
        {
            RegNumber = extraction.RegNumber,
            Fields = fields,
            Texts = texts
        });
    }

    private async Task<IReadOnlyList<AdvanceTextPart>> CollectAdvanceTextsAsync(
        string orderDirectory,
        CancellationToken cancellationToken)
    {
        var texts = new List<AdvanceTextPart>();
        foreach (var sub in new[] { "pages", "documents" })
        {
            foreach (var file in EnumerateSideFiles(orderDirectory, sub))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempt = documentTextExtractor.ExtractFile(file);
                if (attempt.Skipped)
                {
                    // Fallback for HTML: strip tags if extractor skipped oddly.
                    if (file.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = await File.ReadAllTextAsync(file, cancellationToken);
                        var plain = HtmlTagRegex.Replace(raw, " ");
                        if (!string.IsNullOrWhiteSpace(plain))
                        {
                            texts.Add(new AdvanceTextPart
                            {
                                Source = $"{sub}/{Path.GetFileName(file)}",
                                Text = plain
                            });
                        }
                    }

                    continue;
                }

                texts.Add(new AdvanceTextPart
                {
                    Source = $"{sub}/{Path.GetFileName(file)}",
                    Text = attempt.Text
                });
            }
        }

        return texts;
    }

    private static IEnumerable<string> EnumerateSideFiles(string orderDirectory, string subfolder)
    {
        var dir = Path.Combine(orderDirectory, subfolder);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir)
            : [];
    }

    private static IReadOnlyDictionary<string, string> ReadFieldsFromMetaFile(string orderDirectory)
    {
        var metaPath = Path.Combine(orderDirectory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        return ReadFields(doc.RootElement);
    }

    private static IReadOnlyDictionary<string, string> ReadFields(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var prop in fields.EnumerateObject())
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.ToString(),
                _ => prop.Value.ToString()
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[prop.Name] = value;
            }
        }

        return map;
    }
}
