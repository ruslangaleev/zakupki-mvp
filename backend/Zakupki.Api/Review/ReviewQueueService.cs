using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zakupki.Api.Configuration;
using Zakupki.Categorization.Models;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Api.Review;

public sealed class ReviewQueueService(
    IHttpClientFactory httpClientFactory,
    IOptions<SearchOptions> searchOptions,
    IOptions<OrdersOptions> ordersOptions,
    ITaxonomyCatalog taxonomy,
    ILogger<ReviewQueueService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<ReviewQueueListResponse> ListAsync(int? limit, int? from, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var skip = Math.Max(from ?? 0, 0);
        var opts = searchOptions.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var body = new
        {
            from = skip,
            size = take,
            track_total_hits = true,
            sort = new object[]
            {
                new Dictionary<string, object> { ["classificationConfidence"] = new { order = "asc", missing = "_last" } },
                new Dictionary<string, object> { ["regNumber"] = new { order = "asc" } }
            },
            query = new
            {
                term = new Dictionary<string, object>
                {
                    ["reviewStatus"] = "queued"
                }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Review queue search failed: {Status} {Body}", response.StatusCode, err);
            return new ReviewQueueListResponse { Items = [], Total = 0, From = skip, Limit = take };
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var total = 0;
        if (doc.RootElement.TryGetProperty("hits", out var hits) &&
            hits.TryGetProperty("total", out var totalEl))
        {
            total = totalEl.ValueKind == JsonValueKind.Object && totalEl.TryGetProperty("value", out var v)
                ? v.GetInt32()
                : totalEl.ValueKind == JsonValueKind.Number ? totalEl.GetInt32() : 0;
        }

        var items = new List<ReviewQueueItem>();
        foreach (var hit in hits.GetProperty("hits").EnumerateArray())
        {
            if (!hit.TryGetProperty("_source", out var source))
            {
                continue;
            }

            var reg = source.TryGetProperty("regNumber", out var regEl) ? regEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(reg))
            {
                continue;
            }

            var method = source.TryGetProperty("classificationMethod", out var mEl) ? mEl.GetString() : null;
            var l2 = source.TryGetProperty("categoryL2Id", out var l2El) ? l2El.GetString() : null;
            items.Add(new ReviewQueueItem
            {
                RegNumber = reg,
                Title = source.TryGetProperty("title", out var tEl) ? tEl.GetString() : null,
                ObjectName = source.TryGetProperty("objectInfo", out var oEl) ? oEl.GetString()
                    : source.TryGetProperty("objectName", out var onEl) ? onEl.GetString() : null,
                CategoryL1Id = source.TryGetProperty("categoryL1Id", out var l1El) ? l1El.GetString() : null,
                CategoryL2Id = l2,
                Confidence = source.TryGetProperty("classificationConfidence", out var cEl) && cEl.ValueKind == JsonValueKind.Number
                    ? cEl.GetDouble() : null,
                ClassificationMethod = method,
                ReviewStatus = "queued",
                MappingVersion = source.TryGetProperty("mappingVersion", out var mvEl) ? mvEl.GetString() : null,
                Reason = InferReason(method, l2)
            });
        }

        return new ReviewQueueListResponse
        {
            Items = items,
            Total = total,
            From = skip,
            Limit = take
        };
    }

    public async Task<ReviewQueueDetailResponse?> GetAsync(string regNumber, CancellationToken cancellationToken)
    {
        var orderDir = ResolveOrderDir(regNumber);
        if (orderDir is null)
        {
            return null;
        }

        var meta = await ReadJsonAsync<StoredMeta>(Path.Combine(orderDir, "meta.json"), cancellationToken);
        var classification = await ReadJsonAsync<StoredClassification>(Path.Combine(orderDir, "classification.json"), cancellationToken);
        if (meta is null && classification is null)
        {
            return null;
        }

        var candidates = (classification?.Categories ?? [])
            .Select(c => new ReviewCandidate
            {
                Source = MapSource(classification?.Method),
                L1Id = c.L1Id,
                L2Id = c.L2Id,
                Confidence = classification?.Confidence ?? 0,
                IsPrimary = c.IsPrimary,
                Share = c.Share
            })
            .ToList();

        return new ReviewQueueDetailResponse
        {
            RegNumber = regNumber,
            Title = meta?.Title,
            ObjectName = meta?.ObjectName,
            ReviewStatus = classification?.ReviewStatus,
            Reason = InferReason(classification?.Method, classification?.Primary?.L2Id),
            Confidence = classification?.Confidence,
            ClassificationMethod = classification?.Method,
            MappingVersion = classification?.MappingVersion,
            TaxonomyVersion = classification?.TaxonomyVersion,
            Candidates = candidates,
            Positions = (meta?.Positions ?? [])
                .Select(p => new ReviewPosition
                {
                    Okpd2Code = p.Okpd2Code,
                    KtruCode = p.KtruCode,
                    Name = p.Name,
                    Amount = p.Amount,
                    IsPrimary = p.IsPrimary
                })
                .ToList()
        };
    }

    public async Task<(bool Ok, string? Error)> DecideAsync(
        string regNumber,
        ReviewDecisionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.L1Id) || string.IsNullOrWhiteSpace(request.L2Id))
        {
            return (false, "l1Id and l2Id are required");
        }

        if (!taxonomy.TryGetL2(request.L2Id.Trim(), out var l1, out var l2) ||
            !string.Equals(l1.Id, request.L1Id.Trim(), StringComparison.Ordinal))
        {
            return (false, "l2Id must belong to l1Id in taxonomy v2");
        }

        var orderDir = ResolveOrderDir(regNumber);
        if (orderDir is null)
        {
            return (false, "order not found on disk");
        }

        var assignments = new List<object>
        {
            new
            {
                l1Id = l1.Id,
                l1Name = l1.Name,
                l2Id = l2.Id,
                l2Name = l2.Name,
                score = 100,
                share = 1.0,
                isPrimary = true
            }
        };

        foreach (var extra in request.ExtraCategories ?? [])
        {
            if (!taxonomy.TryGetL2(extra.L2Id.Trim(), out var eL1, out var eL2) ||
                !string.Equals(eL1.Id, extra.L1Id.Trim(), StringComparison.Ordinal))
            {
                return (false, $"extra category invalid: {extra.L1Id}/{extra.L2Id}");
            }

            assignments.Add(new
            {
                l1Id = eL1.Id,
                l1Name = eL1.Name,
                l2Id = eL2.Id,
                l2Name = eL2.Name,
                score = 50,
                share = 0.0,
                isPrimary = false
            });
        }

        var existing = await ReadJsonAsync<StoredClassification>(Path.Combine(orderDir, "classification.json"), cancellationToken);
        var payload = new
        {
            taxonomyVersion = taxonomy.Version,
            method = ClassificationMethods.Manual,
            confidence = 1.0,
            reviewStatus = ReviewStatuses.Resolved,
            mappingVersion = existing?.MappingVersion,
            classifiedAt = DateTimeOffset.UtcNow,
            comment = request.Comment,
            categories = assignments
        };

        await File.WriteAllTextAsync(
            Path.Combine(orderDir, "classification.json"),
            JsonSerializer.Serialize(payload, JsonOptions),
            cancellationToken);

        // Keep a durable trail for weekly retrain (task И).
        var decisionLine = JsonSerializer.Serialize(new
        {
            regNumber,
            at = DateTimeOffset.UtcNow,
            l1Id = l1.Id,
            l2Id = l2.Id,
            comment = request.Comment,
            source = ClassificationMethods.Manual
        });
        await File.AppendAllTextAsync(
            Path.Combine(ResolveDataRoot(), "manual-review.jsonl"),
            decisionLine + Environment.NewLine,
            cancellationToken);

        var opts = searchOptions.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var categoryIds = new List<string> { l1.Id, l2.Id };
        foreach (var extra in request.ExtraCategories ?? [])
        {
            categoryIds.Add(extra.L1Id);
            categoryIds.Add(extra.L2Id);
        }

        var update = new
        {
            doc = new Dictionary<string, object?>
            {
                ["categoryL1Id"] = l1.Id,
                ["categoryL1Name"] = l1.Name,
                ["categoryL2Id"] = l2.Id,
                ["categoryL2Name"] = l2.Name,
                ["categoryIds"] = categoryIds.Distinct(StringComparer.Ordinal).ToList(),
                ["classificationConfidence"] = 1.0,
                ["classificationMethod"] = ClassificationMethods.Manual,
                ["taxonomyVersion"] = taxonomy.Version,
                ["reviewStatus"] = ReviewStatuses.Resolved,
                ["mappingVersion"] = existing?.MappingVersion
            }
        };

        using var response = await client.PostAsJsonAsync(
            $"/{opts.OrdersIndex}/_update/{Uri.EscapeDataString(regNumber)}?refresh=false",
            update,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("Manual review saved on disk but order {Reg} missing in OpenSearch", regNumber);
        }
        else if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("OpenSearch manual update failed for {Reg}: {Status} {Body}", regNumber, response.StatusCode, err);
            return (false, "failed to update OpenSearch");
        }

        return (true, null);
    }

    private string? ResolveOrderDir(string regNumber)
    {
        var root = Path.GetFullPath(ordersOptions.Value.DataPath);
        var dir = Path.Combine(root, regNumber);
        return Directory.Exists(dir) ? dir : null;
    }

    private string ResolveDataRoot()
    {
        var orders = Path.GetFullPath(ordersOptions.Value.DataPath);
        var parent = Directory.GetParent(orders)?.FullName;
        return parent ?? orders;
    }

    private static string MapSource(string? method) => method switch
    {
        ClassificationMethods.Okpd2Map or ClassificationMethods.KtruMap => "code",
        ClassificationMethods.InnDict => "dict",
        ClassificationMethods.Manual => "manual",
        _ when method is not null && method.Contains("model", StringComparison.OrdinalIgnoreCase) => "model",
        _ => method ?? "unknown"
    };

    private static string InferReason(string? method, string? l2Id)
    {
        if (string.Equals(l2Id, "g25c01", StringComparison.Ordinal))
        {
            return "mapped_to_prochee";
        }

        if (string.IsNullOrWhiteSpace(method) || method is ClassificationMethods.RulesV2)
        {
            return "weak_or_no_signal";
        }

        return "queued";
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private sealed class StoredMeta
    {
        public string? Title { get; set; }
        public string? ObjectName { get; set; }
        public List<StoredPosition>? Positions { get; set; }
    }

    private sealed class StoredPosition
    {
        public string? Okpd2Code { get; set; }
        public string? KtruCode { get; set; }
        public string? Name { get; set; }
        public double? Amount { get; set; }
        public bool IsPrimary { get; set; }
    }

    private sealed class StoredClassification
    {
        public string? Method { get; set; }
        public double Confidence { get; set; }
        public string? TaxonomyVersion { get; set; }
        public string? ReviewStatus { get; set; }
        public string? MappingVersion { get; set; }
        public List<StoredCategory>? Categories { get; set; }
        public StoredCategory? Primary =>
            Categories?.FirstOrDefault(c => c.IsPrimary) ?? Categories?.FirstOrDefault();
    }

    private sealed class StoredCategory
    {
        public string? L1Id { get; set; }
        public string? L2Id { get; set; }
        public double Share { get; set; }
        public bool IsPrimary { get; set; }
    }
}
