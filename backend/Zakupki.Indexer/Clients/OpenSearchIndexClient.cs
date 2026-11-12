using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zakupki.Categorization.Advance;
using Zakupki.Categorization.Models;
using Zakupki.Indexer.Configuration;
using Zakupki.Indexer.Models;
using Zakupki.Indexer.Parsing;

namespace Zakupki.Indexer.Clients;

public sealed class OpenSearchIndexClient(
    HttpClient httpClient,
    IOptions<IndexerOptions> options,
    ILogger<OpenSearchIndexClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private bool _ensured;

    public async Task EnsureIndicesAsync(CancellationToken cancellationToken)
    {
        if (_ensured)
        {
            return;
        }

        var opts = options.Value;
        await EnsureIndexAsync(opts.OrdersIndex, BuildOrdersMapping(), cancellationToken);
        await EnsureIndexAsync(opts.ChunksIndex, BuildChunksMapping(opts.EmbeddingDimensions), cancellationToken);
        await EnsureOrdersFilterMappingAsync(opts.OrdersIndex, cancellationToken);
        _ensured = true;
    }

    public async Task IndexOrderAsync(
        OrderExtractionResult extraction,
        IReadOnlyList<float[]> chunkVectors,
        ClassificationResult classification,
        AdvanceDetectionResult advance,
        CancellationToken cancellationToken)
    {
        await EnsureIndicesAsync(cancellationToken);
        var opts = options.Value;

        await DeleteByRegNumberAsync(opts.ChunksIndex, extraction.RegNumber, cancellationToken);

        var primary = classification.Primary;
        var categoryIds = classification.Categories
            .SelectMany(c => new[] { c.L1.Id, c.L2.Id })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var orderDoc = new Dictionary<string, object?>
        {
            ["regNumber"] = extraction.RegNumber,
            ["title"] = extraction.Meta.Title,
            ["customer"] = extraction.Meta.Customer,
            ["objectInfo"] = extraction.Meta.ObjectName,
            ["price"] = extraction.Meta.Price,
            ["priceValue"] = PriceParser.TryParse(extraction.Meta.Price),
            ["law"] = extraction.Meta.Law,
            ["stage"] = extraction.Meta.Stage,
            ["noticeUrl"] = extraction.Meta.NoticeUrl,
            ["fullText"] = extraction.FullText,
            ["coverage"] = extraction.Coverage,
            ["skippedFiles"] = extraction.SkippedFiles,
            ["indexedAt"] = DateTimeOffset.UtcNow,
            ["pubDate"] = extraction.Meta.PubDate,
            ["placedAt"] = extraction.Meta.PlacedAt,
            ["applicationDeadline"] = DateParser.TryParseApplicationDeadline(extraction.Meta.Fields),
            ["categoryL1Id"] = primary?.L1.Id,
            ["categoryL1Name"] = primary?.L1.Name,
            ["categoryL2Id"] = primary?.L2.Id,
            ["categoryL2Name"] = primary?.L2.Name,
            ["categoryIds"] = categoryIds,
            ["classificationConfidence"] = classification.Confidence,
            ["classificationMethod"] = classification.Method,
            ["taxonomyVersion"] = classification.TaxonomyVersion,
            ["reviewStatus"] = classification.ReviewStatus,
            ["mappingVersion"] = classification.MappingVersion,
            ["hasAdvance"] = advance.HasAdvance,
            ["advancePercent"] = advance.Percent,
            ["advanceLabel"] = advance.Label,
            ["advanceSnippet"] = advance.Snippet,
            ["advanceSource"] = advance.Source,
            ["advanceMethod"] = advance.Method
        };

        var orderJson = JsonSerializer.Serialize(orderDoc, JsonOptions);
        if (!orderJson.Contains("\"regNumber\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Order payload serialization failed for {extraction.RegNumber}");
        }

        using (var orderContent = new StringContent(orderJson, Encoding.UTF8, "application/json"))
        using (var put = await httpClient.PutAsync(
                   $"/{opts.OrdersIndex}/_doc/{Uri.EscapeDataString(extraction.RegNumber)}?refresh=true",
                   orderContent,
                   cancellationToken))
        {
            if (!put.IsSuccessStatusCode)
            {
                var err = await put.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Order index failed for {Reg}: {Status} {Body}", extraction.RegNumber, put.StatusCode, err);
                put.EnsureSuccessStatusCode();
            }
        }

        if (extraction.Chunks.Count == 0)
        {
            return;
        }

        var bulk = new StringBuilder();
        for (var i = 0; i < extraction.Chunks.Count; i++)
        {
            var chunk = extraction.Chunks[i];
            var id = $"{extraction.RegNumber}:{chunk.ChunkId}";
            var action = JsonSerializer.Serialize(new
            {
                index = new
                {
                    _index = opts.ChunksIndex,
                    _id = id
                }
            });
            var body = JsonSerializer.Serialize(new
            {
                regNumber = extraction.RegNumber,
                chunkId = chunk.ChunkId,
                source = chunk.Source,
                text = chunk.Text,
                embedding = chunkVectors[i]
            }, JsonOptions);

            bulk.Append(action).Append('\n').Append(body).Append('\n');
        }

        using var content = new StringContent(bulk.ToString(), Encoding.UTF8, "application/x-ndjson");
        using var bulkResponse = await httpClient.PostAsync("/_bulk?refresh=true", content, cancellationToken);
        var bulkBody = await bulkResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!bulkResponse.IsSuccessStatusCode)
        {
            logger.LogError("Bulk index failed: {Body}", bulkBody);
            bulkResponse.EnsureSuccessStatusCode();
        }

        using var bulkJson = JsonDocument.Parse(bulkBody);
        if (bulkJson.RootElement.TryGetProperty("errors", out var errors) && errors.GetBoolean())
        {
            throw new InvalidOperationException($"Bulk indexing reported errors for {extraction.RegNumber}");
        }
    }

    public async Task UpdateOrderClassificationAsync(
        string regNumber,
        ClassificationResult classification,
        CancellationToken cancellationToken)
    {
        await EnsureIndicesAsync(cancellationToken);
        var opts = options.Value;
        var primary = classification.Primary;
        var categoryIds = classification.Categories
            .SelectMany(c => new[] { c.L1.Id, c.L2.Id })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var payload = new
        {
            doc = new Dictionary<string, object?>
            {
                ["categoryL1Id"] = primary?.L1.Id,
                ["categoryL1Name"] = primary?.L1.Name,
                ["categoryL2Id"] = primary?.L2.Id,
                ["categoryL2Name"] = primary?.L2.Name,
                ["categoryIds"] = categoryIds,
                ["classificationConfidence"] = classification.Confidence,
                ["classificationMethod"] = classification.Method,
                ["taxonomyVersion"] = classification.TaxonomyVersion,
                ["reviewStatus"] = classification.ReviewStatus,
                ["mappingVersion"] = classification.MappingVersion
            }
        };

        await PatchOrderDocAsync(regNumber, payload, "classification", cancellationToken);
    }

    public async Task UpdateOrderAdvanceAsync(
        string regNumber,
        AdvanceDetectionResult advance,
        CancellationToken cancellationToken)
    {
        await EnsureIndicesAsync(cancellationToken);
        var payload = new
        {
            doc = new Dictionary<string, object?>
            {
                ["hasAdvance"] = advance.HasAdvance,
                ["advancePercent"] = advance.Percent,
                ["advanceLabel"] = advance.Label,
                ["advanceSnippet"] = advance.Snippet,
                ["advanceSource"] = advance.Source,
                ["advanceMethod"] = advance.Method
            }
        };

        await PatchOrderDocAsync(regNumber, payload, "advance", cancellationToken);
    }

    public async Task UpdateOrderSortFieldsAsync(
        string regNumber,
        DateTimeOffset? pubDate,
        string? placedAt,
        DateTimeOffset? applicationDeadline,
        double? priceValue,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        await EnsureIndicesAsync(cancellationToken);
        var payload = new
        {
            doc = new Dictionary<string, object?>
            {
                ["pubDate"] = pubDate,
                ["placedAt"] = placedAt,
                ["applicationDeadline"] = applicationDeadline,
                ["priceValue"] = priceValue
            }
        };

        await PatchOrderDocAsync(regNumber, payload, "dates", cancellationToken, refresh);
    }

    public async Task UpdateOrderObjectInfoAsync(
        string regNumber,
        string? objectInfo,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        await EnsureIndicesAsync(cancellationToken);
        var payload = new
        {
            doc = new Dictionary<string, object?>
            {
                ["objectInfo"] = objectInfo
            }
        };

        await PatchOrderDocAsync(regNumber, payload, "objectInfo", cancellationToken, refresh);
    }

    private async Task PatchOrderDocAsync(
        string regNumber,
        object payload,
        string kind,
        CancellationToken cancellationToken,
        bool refresh = true)
    {
        var opts = options.Value;
        var refreshParam = refresh ? "true" : "false";
        using var response = await httpClient.PostAsJsonAsync(
            $"/{opts.OrdersIndex}/_update/{Uri.EscapeDataString(regNumber)}?refresh={refreshParam}",
            payload,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Skip {Kind} update: order {Reg} not in OpenSearch", kind, regNumber);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("{Kind} update failed for {Reg}: {Status} {Body}", kind, regNumber, response.StatusCode, err);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task EnsureIndexAsync(string indexName, object mapping, CancellationToken cancellationToken)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, $"/{indexName}");
        using var headResponse = await httpClient.SendAsync(head, cancellationToken);
        if (headResponse.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        using var put = await httpClient.PutAsJsonAsync($"/{indexName}", mapping, JsonOptions, cancellationToken);
        if (put.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            logger.LogInformation("Created OpenSearch index {Index}", indexName);
            return;
        }

        var body = await put.Content.ReadAsStringAsync(cancellationToken);
        if (put.StatusCode == HttpStatusCode.BadRequest && body.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            return;
        }

        logger.LogError("Failed to create index {Index}: {Status} {Body}", indexName, put.StatusCode, body);
        put.EnsureSuccessStatusCode();
    }

    private async Task DeleteByRegNumberAsync(string indexName, string regNumber, CancellationToken cancellationToken)
    {
        var payload = new
        {
            query = new
            {
                term = new Dictionary<string, string>
                {
                    ["regNumber"] = regNumber
                }
            }
        };

        using var response = await httpClient.PostAsJsonAsync(
            $"/{indexName}/_delete_by_query?conflicts=proceed&refresh=true",
            payload,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("delete_by_query soft-fail for {Reg}: {Status} {Body}", regNumber, response.StatusCode, body);
        }
    }

    private static object BuildOrdersMapping() => new
    {
        settings = new
        {
            number_of_shards = 1,
            number_of_replicas = 0,
            analysis = RussianAnalysis()
        },
        mappings = new
        {
            properties = new Dictionary<string, object>
            {
                ["regNumber"] = new { type = "keyword" },
                ["title"] = RussianText(),
                ["customer"] = RussianText(),
                ["objectInfo"] = RussianText(),
                ["price"] = new { type = "keyword" },
                ["priceValue"] = new { type = "double" },
                ["law"] = new { type = "keyword" },
                ["stage"] = new { type = "keyword" },
                ["noticeUrl"] = new { type = "keyword" },
                ["fullText"] = RussianText(),
                ["coverage"] = new { type = "keyword" },
                ["skippedFiles"] = new { type = "keyword" },
                ["indexedAt"] = new { type = "date" },
                ["pubDate"] = new { type = "date" },
                ["placedAt"] = new { type = "keyword" },
                ["applicationDeadline"] = new { type = "date" },
                ["categoryL1Id"] = new { type = "keyword" },
                ["categoryL1Name"] = new { type = "keyword" },
                ["categoryL2Id"] = new { type = "keyword" },
                ["categoryL2Name"] = new { type = "keyword" },
                ["categoryIds"] = new { type = "keyword" },
                ["classificationConfidence"] = new { type = "float" },
                ["classificationMethod"] = new { type = "keyword" },
                ["taxonomyVersion"] = new { type = "keyword" },
                ["reviewStatus"] = new { type = "keyword" },
                ["mappingVersion"] = new { type = "keyword" },
                ["hasAdvance"] = new { type = "boolean" },
                ["advancePercent"] = new { type = "double" },
                ["advanceLabel"] = new { type = "keyword" },
                ["advanceSnippet"] = RussianText(),
                ["advanceSource"] = new { type = "keyword" },
                ["advanceMethod"] = new { type = "keyword" }
            }
        }
    };

    private async Task EnsureOrdersFilterMappingAsync(string indexName, CancellationToken cancellationToken)
    {
        // Existing indices keep old mapping; add filter fields if missing.
        var body = new
        {
            properties = new Dictionary<string, object>
            {
                ["priceValue"] = new { type = "double" },
                ["pubDate"] = new { type = "date" },
                ["placedAt"] = new { type = "keyword" },
                ["applicationDeadline"] = new { type = "date" },
                ["categoryL1Id"] = new { type = "keyword" },
                ["categoryL1Name"] = new { type = "keyword" },
                ["categoryL2Id"] = new { type = "keyword" },
                ["categoryL2Name"] = new { type = "keyword" },
                ["categoryIds"] = new { type = "keyword" },
                ["classificationConfidence"] = new { type = "float" },
                ["classificationMethod"] = new { type = "keyword" },
                ["taxonomyVersion"] = new { type = "keyword" },
                ["reviewStatus"] = new { type = "keyword" },
                ["mappingVersion"] = new { type = "keyword" },
                ["hasAdvance"] = new { type = "boolean" },
                ["advancePercent"] = new { type = "double" },
                ["advanceLabel"] = new { type = "keyword" },
                ["advanceSnippet"] = RussianText(),
                ["advanceSource"] = new { type = "keyword" },
                ["advanceMethod"] = new { type = "keyword" }
            }
        };

        using var response = await httpClient.PutAsJsonAsync(
            $"/{indexName}/_mapping",
            body,
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Filter mapping update soft-fail: {Status} {Body}", response.StatusCode, err);
        }
    }

    private static object BuildChunksMapping(int dimensions) => new
    {
        settings = new
        {
            number_of_shards = 1,
            number_of_replicas = 0,
            index = new
            {
                knn = true
            },
            analysis = RussianAnalysis()
        },
        mappings = new
        {
            properties = new Dictionary<string, object>
            {
                ["regNumber"] = new { type = "keyword" },
                ["chunkId"] = new { type = "keyword" },
                ["source"] = new { type = "keyword" },
                ["text"] = RussianText(),
                ["embedding"] = new
                {
                    type = "knn_vector",
                    dimension = dimensions,
                    method = new
                    {
                        name = "hnsw",
                        space_type = "cosinesimil",
                        engine = "lucene"
                    }
                }
            }
        }
    };

    private static object RussianAnalysis() => new
    {
        analyzer = new
        {
            zakupki_ru = new
            {
                type = "custom",
                tokenizer = "standard",
                filter = new[] { "lowercase", "russian_stop", "russian_stemmer" }
            }
        },
        filter = new
        {
            russian_stop = new { type = "stop", stopwords = "_russian_" },
            russian_stemmer = new { type = "stemmer", language = "russian" }
        }
    };

    private static object RussianText() => new
    {
        type = "text",
        analyzer = "zakupki_ru",
        search_analyzer = "zakupki_ru"
    };
}
