using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zakupki.Indexer.Configuration;
using Zakupki.Indexer.Models;
using Zakupki.Indexer.Parsing;

namespace Zakupki.Indexer.Extraction;

public sealed class OrderTextExtractor(
    DocumentTextExtractor documentTextExtractor,
    IOptions<IndexerOptions> options,
    ILogger<OrderTextExtractor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<OrderExtractionResult?> ExtractAsync(string orderDirectory, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(orderDirectory, "meta.json");
        if (!File.Exists(metaPath))
        {
            logger.LogDebug("Skip {Dir}: meta.json missing", orderDirectory);
            return null;
        }

        await using var metaStream = File.OpenRead(metaPath);
        var meta = await JsonSerializer.DeserializeAsync<OrderMetaDocument>(metaStream, JsonOptions, cancellationToken);
        var regNumber = meta?.RegNumber ?? Path.GetFileName(orderDirectory);
        if (meta is null || string.IsNullOrWhiteSpace(regNumber))
        {
            return null;
        }

        var commonInfoPath = Path.Combine(orderDirectory, "pages", "common-info.html");
        string? commonInfoHtml = File.Exists(commonInfoPath)
            ? await File.ReadAllTextAsync(commonInfoPath, cancellationToken)
            : null;
        var resolvedObjectName = PurchaseObjectResolver.ResolveObjectName(
            meta.ObjectName,
            meta.Title,
            commonInfoHtml,
            meta.Fields);
        meta = meta with { RegNumber = regNumber, ObjectName = resolvedObjectName };

        var opts = options.Value;
        var fullText = new StringBuilder();
        var embedChunks = new List<ExtractedChunk>();
        var skipped = new List<string>();

        void AppendFullText(string text)
        {
            var normalized = TextChunker.NormalizeWhitespace(text);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                fullText.AppendLine(normalized);
            }
        }

        void AddEmbedChunks(string source, string text, bool includeInFullText)
        {
            var normalized = TextChunker.NormalizeWhitespace(text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            // Notice HTML / contract templates often contain universal 44-FZ boilerplate
            // ("лекарственные препараты или реагенты") — keep that out of BM25 fullText.
            if (includeInFullText)
            {
                var cleaned = StripProcurementBoilerplate(normalized);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    fullText.AppendLine(cleaned);
                }
            }

            foreach (var chunk in TextChunker.Chunk(source, normalized, opts.ChunkSize, opts.ChunkOverlap))
            {
                if (chunk.Text.Length < opts.MinEmbedChunkChars)
                {
                    continue;
                }

                embedChunks.Add(chunk);
            }
        }

        // Meta feeds BM25 via order fields + fullText; do not embed short labels as knn chunks.
        AppendFullText(meta.Title ?? string.Empty);
        AppendFullText(meta.Customer ?? string.Empty);
        AppendFullText(meta.ObjectName ?? string.Empty);
        AppendFullText(meta.Price ?? string.Empty);
        AppendFullText(meta.Law ?? string.Empty);
        AppendFullText(meta.Stage ?? string.Empty);

        var pagesDir = Path.Combine(orderDirectory, "pages");
        if (Directory.Exists(pagesDir))
        {
            foreach (var page in Directory.EnumerateFiles(pagesDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempt = documentTextExtractor.ExtractFile(page);
                if (attempt.Skipped)
                {
                    skipped.Add($"{attempt.FileName}: {attempt.Reason}");
                    continue;
                }

                AddEmbedChunks($"pages/{attempt.FileName}", attempt.Text, includeInFullText: false);
            }
        }

        var documentsDir = Path.Combine(orderDirectory, "documents");
        if (Directory.Exists(documentsDir))
        {
            foreach (var file in Directory.EnumerateFiles(documentsDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempt = documentTextExtractor.ExtractFile(file);
                if (attempt.Skipped)
                {
                    skipped.Add($"{attempt.FileName}: {attempt.Reason}");
                    continue;
                }

                AddEmbedChunks($"documents/{attempt.FileName}", attempt.Text, includeInFullText: true);
            }
        }

        if (fullText.Length == 0)
        {
            AppendFullText(regNumber);
        }

        var limitedChunks = PrioritizeAndLimitChunks(embedChunks, opts.MaxChunksPerOrder);
        var coverage = skipped.Count == 0 ? "full" : "partial";
        return new OrderExtractionResult
        {
            RegNumber = regNumber,
            Meta = meta,
            FullText = fullText.ToString().Trim(),
            Chunks = limitedChunks,
            Coverage = coverage,
            SkippedFiles = skipped
        };
    }

    private static string StripProcurementBoilerplate(string text)
    {
        // Drop sentences that only exist as legal template noise across most notices/contracts.
        var markers = new[]
        {
            "части 9 статьи 37",
            "ч. 9 ст. 37",
            "лекарственные препараты или реагенты",
            "остаточный срок годности"
        };

        var parts = text.Split(['.', '\n', ';'], StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Where(part =>
        {
            var lower = part.ToLowerInvariant();
            return !markers.Any(m => lower.Contains(m, StringComparison.Ordinal));
        });

        return TextChunker.NormalizeWhitespace(string.Join(". ", kept));
    }

    private static IReadOnlyList<ExtractedChunk> PrioritizeAndLimitChunks(
        List<ExtractedChunk> chunks,
        int maxChunks)
    {
        if (maxChunks <= 0 || chunks.Count <= maxChunks)
        {
            return chunks;
        }

        static int Rank(string source) => source switch
        {
            var s when s.StartsWith("documents/", StringComparison.Ordinal) => 0,
            var s when s.StartsWith("pages/", StringComparison.Ordinal) => 1,
            _ => 2
        };

        return chunks
            .OrderBy(c => Rank(c.Source))
            .ThenBy(c => c.ChunkId, StringComparer.Ordinal)
            .Take(maxChunks)
            .ToList();
    }
}
