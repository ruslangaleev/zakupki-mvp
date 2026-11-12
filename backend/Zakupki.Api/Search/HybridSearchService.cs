using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Zakupki.Api.Analytics;
using Zakupki.Api.Configuration;
using Zakupki.Categorization.Taxonomy;

namespace Zakupki.Api.Search;

public sealed class HybridSearchService(
    IHttpClientFactory httpClientFactory,
    IOptions<SearchOptions> options,
    ITaxonomyCatalog taxonomy,
    AnalyticsService analytics,
    ILogger<HybridSearchService> logger)
{
    public async Task<SearchResponse> SearchAsync(
        string query,
        int? limit,
        string? categoryId,
        double? priceMin,
        double? priceMax,
        CancellationToken cancellationToken,
        int? from = null,
        SearchSort sort = SearchSort.Relevance)
    {
        var opts = options.Value;
        var take = Math.Clamp(limit ?? opts.DefaultLimit, 1, opts.MaxLimit);
        var skip = Math.Max(from ?? 0, 0);
        var q = query.Trim();
        var filters = SearchFilters.Create(categoryId, priceMin, priceMax);
        var sortApi = SearchSortParser.ToApi(sort);
        var totalInIndex = await CountOrdersAsync(filters: null, cancellationToken);
        if (string.IsNullOrWhiteSpace(q))
        {
            return new SearchResponse(q, [], TotalInIndex: totalInIndex, Total: 0, From: skip, Limit: take, Sort: sortApi);
        }

        // Direct lookup by procurement number (keyword field is not in BM25 text query).
        // If the query looks like a regNumber but nothing matches — do NOT fall through to
        // BM25/knn: long digit strings otherwise match random lots/docs and look "wrong".
        if (TryExtractRegNumberDigits(q, out var regDigits))
        {
            var byReg = await SearchByRegNumberAsync(regDigits, take, filters, cancellationToken, skip, sort);
            return byReg with { Query = q, TotalInIndex = totalInIndex, From = skip, Limit = take, Sort = sortApi };
        }

        var searchQuery = ExpandQuerySynonyms(q);

        // Non-relevance sorts: lexical query as filter + OpenSearch field sort (pageable totals).
        if (sort != SearchSort.Relevance)
        {
            var (sortedHits, sortedTotal, usedFuzzySorted) =
                await SearchBm25SortedAsync(searchQuery, skip, take, fuzzy: false, filters, sort, cancellationToken);
            var ranked = sortedHits;
            var matchedTotal = sortedTotal;
            var usedFuzzy = usedFuzzySorted;
            if (ranked.Count == 0 && ShouldTryFuzzy(searchQuery))
            {
                (ranked, matchedTotal, usedFuzzy) =
                    await SearchBm25SortedAsync(searchQuery, skip, take, fuzzy: true, filters, sort, cancellationToken);
                if (ranked.Count == 0 || ranked[0].Score < 8)
                {
                    ranked = [];
                    matchedTotal = 0;
                    usedFuzzy = false;
                }
            }

            return await MaterializeSearchAsync(
                q, ranked, matchedTotal, usedFuzzy, skip, take, sortApi, totalInIndex, searchQuery, filters, cancellationToken);
        }

        var embedding = await EmbedQueryAsync(searchQuery, cancellationToken);
        // Fetch a window large enough for skip+take, then page in-memory after fusion.
        var fetch = Math.Min(opts.MaxLimit, skip + take);
        var bm25Task = SearchBm25Async(searchQuery, Math.Max(fetch * 3, take * 3), fuzzy: false, filters, cancellationToken);
        var knnTask = SearchKnnAsync(embedding, Math.Max(fetch * 5, take * 5), cancellationToken);
        await Task.WhenAll(bm25Task, knnTask);

        var (bm25, bm25Total) = await bm25Task;
        var knn = await knnTask;
        // CutWeakTail only for pure-knn fallback — BM25 totals must stay pageable.
        var fused = bm25.Count > 0
            ? FuseRanked(bm25, knn, fetch)
            : CutWeakTail(FuseRanked(bm25, knn, fetch));
        var usedFuzzyRel = false;
        var matchedTotalRel = bm25Total;

        // Precision-first: fuzzy only when exact lexical + strong knn found nothing.
        // Skip fuzzy for short Latin tokens (RPA) and for very short queries — too noisy.
        if (fused.Count == 0 && ShouldTryFuzzy(searchQuery))
        {
            var (fuzzyHits, fuzzyTotal) = await SearchBm25Async(searchQuery, Math.Max(fetch * 3, take * 3), fuzzy: true, filters, cancellationToken);
            // Fuzzy without a strong top hit is usually vocabulary noise (канцтовары→медицина).
            var fuzzyFused = CutWeakTail(fuzzyHits.Take(fetch).ToList());
            if (fuzzyFused.Count > 0 && fuzzyFused[0].Score >= 8)
            {
                fused = fuzzyFused;
                usedFuzzyRel = true;
                matchedTotalRel = fuzzyTotal;
            }
        }

        if (fused.Count == 0)
        {
            return new SearchResponse(q, [], Fuzzy: false, TotalInIndex: totalInIndex, Total: 0, From: skip, Limit: take, Sort: sortApi);
        }

        fused = fused.Skip(skip).Take(take).ToList();
        return await MaterializeSearchAsync(
            q, fused, matchedTotalRel, usedFuzzyRel, skip, take, sortApi, totalInIndex, searchQuery, filters, cancellationToken);
    }

    private async Task<SearchResponse> MaterializeSearchAsync(
        string q,
        List<RankedHit> fused,
        long matchedTotal,
        bool usedFuzzy,
        int skip,
        int take,
        string sortApi,
        long totalInIndex,
        string searchQuery,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        if (fused.Count == 0)
        {
            return new SearchResponse(q, [], Fuzzy: usedFuzzy, TotalInIndex: totalInIndex, Total: 0, From: skip, Limit: take, Sort: sortApi);
        }

        var needsChunkLookup = fused
            .Where(h => h.MatchedIn is null or "fullText")
            .Select(h => h.RegNumber)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var chunkMatches = needsChunkLookup.Count == 0
            ? new Dictionary<string, ChunkMatch>(StringComparer.Ordinal)
            : await FindChunkMatchesAsync(searchQuery, needsChunkLookup, usedFuzzy, cancellationToken);

        var orders = await FetchOrdersAsync(fused.Select(x => x.RegNumber).ToList(), cancellationToken);
        var items = new List<SearchHit>(fused.Count);
        foreach (var ranked in fused)
        {
            orders.TryGetValue(ranked.RegNumber, out var order);
            // knn hits live in chunks index without filter fields — enforce filters here.
            if (!MatchesFilters(order, filters))
            {
                continue;
            }

            chunkMatches.TryGetValue(ranked.RegNumber, out var chunk);
            var (matchedIn, matchLabel) = ResolveMatchLocation(ranked, chunk);

            items.Add(new SearchHit(
                ranked.RegNumber,
                order?.Title,
                order?.Customer,
                order?.Price,
                ranked.Score,
                ranked.Snippet ?? chunk?.Snippet,
                order?.NoticeUrl,
                order?.Coverage ?? "partial",
                matchedIn,
                matchLabel,
                order?.CategoryL1Id,
                order?.CategoryL1Name,
                order?.CategoryL2Id,
                order?.CategoryL2Name,
                order?.HasAdvance ?? false,
                order?.AdvancePercent,
                order?.AdvanceLabel,
                order?.AdvanceSnippet,
                order?.ObjectInfo,
                order?.ApplicationDeadline,
                order?.ClassificationMethod,
                order?.ClassificationConfidence,
                order?.TaxonomyVersion,
                order?.ReviewStatus));
        }

        // BM25 total is authoritative; knn-only fallback reports page size after filters.
        var total = matchedTotal > 0 ? matchedTotal : items.Count + skip;
        return new SearchResponse(q, items, Fuzzy: usedFuzzy, TotalInIndex: totalInIndex, Total: total, From: skip, Limit: take, Sort: sortApi);
    }

    public async Task<SuggestResponse> SuggestAsync(
        string query,
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 8, 1, 20);
        var q = query.Trim();
        if (q.Length < 2)
        {
            return new SuggestResponse(q, []);
        }

        var digitBuf = new System.Text.StringBuilder(q.Length);
        foreach (var ch in q)
        {
            if (char.IsDigit(ch))
            {
                digitBuf.Append(ch);
            }
        }

        var digits = digitBuf.ToString();
        var nonSpace = q.Count(ch => !char.IsWhiteSpace(ch) && ch is not '№' and not '#');
        var looksLikeRegPrefix = digits.Length >= 6 && digits.Length >= nonSpace * 0.8;
        if (looksLikeRegPrefix)
        {
            return await SuggestRegNumbersAsync(digits, take, cancellationToken);
        }

        // Only suggest queries that real users typed and got results for.
        var items = analytics.GetSuccessfulQueries()
            .Where(x =>
                x.Query.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                || (q.Length >= 3 && x.Query.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(take)
            .Select(x => new SuggestItem(x.Query, "query"))
            .ToList();

        return new SuggestResponse(q, items);
    }

    private async Task<SuggestResponse> SuggestRegNumbersAsync(
        string digits,
        int take,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var body = new
        {
            size = Math.Min(take * 3, 30),
            track_total_hits = false,
            _source = new[] { "regNumber" },
            query = new
            {
                @bool = new
                {
                    should = new object[]
                    {
                        new { prefix = new Dictionary<string, object> { ["regNumber"] = digits } },
                        new { wildcard = new Dictionary<string, object> { ["regNumber"] = $"*{digits}" } }
                    },
                    minimum_should_match = 1
                }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Suggest regNumber query failed: {Status} {Body}", response.StatusCode, err);
            }

            return new SuggestResponse(digits, []);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var regItems = new List<SuggestItem>(take);
        var seenRegs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            if (!hit.TryGetProperty("_source", out var source))
            {
                continue;
            }

            var reg = source.TryGetProperty("regNumber", out var regEl) ? regEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(reg) || !seenRegs.Add(reg))
            {
                continue;
            }

            regItems.Add(new SuggestItem(reg, "regNumber", reg));
            if (regItems.Count >= take)
            {
                break;
            }
        }

        return new SuggestResponse(digits, regItems);
    }

    public async Task<RecentOrdersResponse> GetRecentAsync(
        int? limit,
        string? categoryId,
        double? priceMin,
        double? priceMax,
        CancellationToken cancellationToken,
        int? from = null,
        SearchSort sort = SearchSort.Deadline)
    {
        var opts = options.Value;
        var take = Math.Clamp(limit ?? opts.DefaultLimit, 1, opts.MaxLimit);
        var skip = Math.Max(from ?? 0, 0);
        var filters = SearchFilters.Create(categoryId, priceMin, priceMax);
        var sortApi = SearchSortParser.ToApi(sort == SearchSort.Relevance ? SearchSort.Deadline : sort);
        var effectiveSort = sort == SearchSort.Relevance ? SearchSort.Deadline : sort;
        var totalInIndexTask = CountOrdersAsync(filters: null, cancellationToken);
        var client = httpClientFactory.CreateClient("opensearch");
        var body = new
        {
            from = skip,
            size = take,
            track_total_hits = true,
            sort = BuildSortClauses(effectiveSort),
            query = WrapWithFilters(new { match_all = new { } }, filters)
        };

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        var totalInIndex = await totalInIndexTask;
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new RecentOrdersResponse([], totalInIndex, Total: 0, From: skip, Limit: take, Sort: sortApi);
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Recent orders query failed: {Status} {Body}", response.StatusCode, err);
            return new RecentOrdersResponse([], totalInIndex, Total: 0, From: skip, Limit: take, Sort: sortApi);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var total = ReadHitTotal(doc.RootElement);
        var items = new List<SearchHit>();
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var reg = source.TryGetProperty("regNumber", out var regEl) ? regEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(reg))
            {
                continue;
            }

            items.Add(ToSearchHit(source, score: 0, snippet: null));
        }

        return new RecentOrdersResponse(items, totalInIndex, total, From: skip, Limit: take, Sort: sortApi);
    }

    public async Task<PriceStatsResponse> GetPriceStatsAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var body = new
        {
            size = 0,
            track_total_hits = false,
            aggs = new
            {
                price_min = new { min = new { field = "priceValue" } },
                price_max = new { max = new { field = "priceValue" } }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
        {
            return new PriceStatsResponse(null, null);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        double? min = null;
        double? max = null;
        if (doc.RootElement.TryGetProperty("aggregations", out var aggs))
        {
            if (aggs.TryGetProperty("price_min", out var minEl) &&
                minEl.TryGetProperty("value", out var minVal) &&
                minVal.ValueKind is JsonValueKind.Number)
            {
                min = minVal.GetDouble();
            }

            if (aggs.TryGetProperty("price_max", out var maxEl) &&
                maxEl.TryGetProperty("value", out var maxVal) &&
                maxVal.ValueKind is JsonValueKind.Number)
            {
                max = maxVal.GetDouble();
            }
        }

        return new PriceStatsResponse(min, max);
    }

    private static SearchHit ToSearchHit(JsonElement source, double score, string? snippet)
    {
        var reg = source.TryGetProperty("regNumber", out var regEl) ? regEl.GetString() ?? string.Empty : string.Empty;
        var title = source.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var objectInfo = source.TryGetProperty("objectInfo", out var obj) ? obj.GetString() : null;
        var (hasAdvance, advancePercent, advanceLabel, advanceSnippet) = ReadAdvance(source);
        return new SearchHit(
            reg,
            title,
            source.TryGetProperty("customer", out var customer) ? customer.GetString() : null,
            source.TryGetProperty("price", out var price) ? price.GetString() : null,
            score,
            snippet,
            source.TryGetProperty("noticeUrl", out var notice) ? notice.GetString() : null,
            source.TryGetProperty("coverage", out var coverage) ? coverage.GetString() ?? "partial" : "partial",
            CategoryL1Id: source.TryGetProperty("categoryL1Id", out var l1Id) ? l1Id.GetString() : null,
            CategoryL1Name: source.TryGetProperty("categoryL1Name", out var l1Name) ? l1Name.GetString() : null,
            CategoryL2Id: source.TryGetProperty("categoryL2Id", out var l2Id) ? l2Id.GetString() : null,
            CategoryL2Name: source.TryGetProperty("categoryL2Name", out var l2Name) ? l2Name.GetString() : null,
            HasAdvance: hasAdvance,
            AdvancePercent: advancePercent,
            AdvanceLabel: advanceLabel,
            AdvanceSnippet: advanceSnippet,
            ObjectInfo: objectInfo,
            ApplicationDeadline: ReadDate(source, "applicationDeadline"),
            ClassificationMethod: source.TryGetProperty("classificationMethod", out var methodEl) ? methodEl.GetString() : null,
            ClassificationConfidence: source.TryGetProperty("classificationConfidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number
                ? confEl.GetDouble()
                : null,
            TaxonomyVersion: source.TryGetProperty("taxonomyVersion", out var taxEl) ? taxEl.GetString() : null,
            ReviewStatus: source.TryGetProperty("reviewStatus", out var revEl) ? revEl.GetString() : null);
    }

    private static DateTimeOffset? ReadDate(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static (bool HasAdvance, double? Percent, string? Label, string? Snippet) ReadAdvance(JsonElement source)
    {
        var hasAdvance = source.TryGetProperty("hasAdvance", out var hasEl) &&
                         hasEl.ValueKind is JsonValueKind.True;
        double? percent = null;
        if (source.TryGetProperty("advancePercent", out var pctEl) && pctEl.ValueKind is JsonValueKind.Number)
        {
            percent = pctEl.GetDouble();
        }

        var label = source.TryGetProperty("advanceLabel", out var labelEl) ? labelEl.GetString() : null;
        var snippet = source.TryGetProperty("advanceSnippet", out var snEl) ? snEl.GetString() : null;
        return (hasAdvance, percent, label, snippet);
    }

    private async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("embeddings");
        using var response = await client.PostAsJsonAsync("/embed", new { texts = new[] { query } }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken)
                      ?? throw new InvalidOperationException("Empty embeddings response");
        if (payload.Embeddings.Count == 0)
        {
            throw new InvalidOperationException("No embedding returned for query");
        }

        return payload.Embeddings[0];
    }

    private async Task<SearchResponse> SearchByRegNumberAsync(
        string digits,
        int take,
        SearchFilters filters,
        CancellationToken cancellationToken,
        int skip = 0,
        SearchSort sort = SearchSort.Relevance)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        object idQuery = digits.Length >= 18
            ? new { term = new Dictionary<string, object> { ["regNumber"] = digits } }
            : new
            {
                @bool = new
                {
                    should = new object[]
                    {
                        new { prefix = new Dictionary<string, object> { ["regNumber"] = digits } },
                        // allow pasting a distinctive tail of the number
                        new { wildcard = new Dictionary<string, object> { ["regNumber"] = $"*{digits}" } }
                    },
                    minimum_should_match = 1
                }
            };

        var body = new Dictionary<string, object>
        {
            ["from"] = skip,
            ["size"] = take,
            ["track_total_hits"] = true,
            ["query"] = WrapWithFilters(idQuery, filters)
        };
        if (sort != SearchSort.Relevance)
        {
            body["sort"] = BuildSortClauses(sort);
        }

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("RegNumber search failed: {Status} {Body}", response.StatusCode, err);
            }

            return new SearchResponse(digits, [], Total: 0);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var total = ReadHitTotal(doc.RootElement);
        var items = new List<SearchHit>();
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var reg = source.TryGetProperty("regNumber", out var regEl) ? regEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(reg))
            {
                continue;
            }

            items.Add(ToSearchHit(source, score: 100, snippet: null) with
            {
                MatchedIn = "card",
                MatchLabel = "Номер закупки"
            });
        }

        return new SearchResponse(digits, items, Fuzzy: false, Total: total);
    }

    private static bool TryExtractRegNumberDigits(string query, out string digits)
    {
        var sb = new System.Text.StringBuilder(query.Length);
        foreach (var ch in query)
        {
            if (char.IsDigit(ch))
            {
                sb.Append(ch);
            }
        }

        digits = sb.ToString();
        // EIS regNumbers are long digit strings; require enough digits to avoid eating normal queries.
        if (digits.Length < 10)
        {
            return false;
        }

        // Mostly-numeric query (ignore №/spaces/dashes/punctuation).
        var significant = query.Count(ch => !char.IsWhiteSpace(ch) && ch is not '№' and not '#' and not '-' and not '–');
        return digits.Length >= significant * 0.8;
    }

    private async Task<(List<RankedHit> Hits, long Total, bool Fuzzy)> SearchBm25SortedAsync(
        string query,
        int from,
        int size,
        bool fuzzy,
        SearchFilters filters,
        SearchSort sort,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var body = new
        {
            from,
            size,
            track_total_hits = true,
            query = WrapWithFilters(BuildBm25Query(query, fuzzy), filters),
            sort = BuildSortClauses(sort),
            highlight = new
            {
                fields = new
                {
                    objectInfo = new { fragment_size = 180, number_of_fragments = 1 },
                    title = new { fragment_size = 120, number_of_fragments = 1 },
                    customer = new { fragment_size = 120, number_of_fragments = 1 }
                }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("BM25 sorted search failed (fuzzy={Fuzzy}): {Status} {Body}", fuzzy, response.StatusCode, err);
            }

            return ([], 0, fuzzy);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return (ParseBm25Hits(doc.RootElement), ReadHitTotal(doc.RootElement), fuzzy);
    }

    private static object[] BuildSortClauses(SearchSort sort) => sort switch
    {
        SearchSort.Price =>
        [
            new Dictionary<string, object>
            {
                ["priceValue"] = new { order = "asc", missing = "_last", unmapped_type = "double" }
            },
            new Dictionary<string, object> { ["_score"] = new { order = "desc" } }
        ],
        SearchSort.Published =>
        [
            new Dictionary<string, object>
            {
                ["pubDate"] = new { order = "desc", missing = "_last", unmapped_type = "date" }
            },
            new Dictionary<string, object>
            {
                ["indexedAt"] = new { order = "desc", unmapped_type = "date" }
            }
        ],
        SearchSort.Deadline =>
        [
            new Dictionary<string, object>
            {
                ["applicationDeadline"] = new { order = "asc", missing = "_last", unmapped_type = "date" }
            },
            new Dictionary<string, object>
            {
                ["pubDate"] = new { order = "desc", missing = "_last", unmapped_type = "date" }
            },
            new Dictionary<string, object>
            {
                ["indexedAt"] = new { order = "desc", unmapped_type = "date" }
            }
        ],
        _ =>
        [
            new Dictionary<string, object> { ["_score"] = new { order = "desc" } }
        ]
    };

    private async Task<(List<RankedHit> Hits, long Total)> SearchBm25Async(
        string query,
        int size,
        bool fuzzy,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        // Highlight only card fields. fullText can exceed index.highlight.max_analyzed_offset
        // (~1MB) and then the whole BM25 request 400s — common words like «ремонт» looked empty.
        // Document snippets come from chunk lookup instead.
        var body = new
        {
            size,
            track_total_hits = true,
            query = WrapWithFilters(BuildBm25Query(query, fuzzy), filters),
            highlight = new
            {
                fields = new
                {
                    objectInfo = new { fragment_size = 180, number_of_fragments = 1 },
                    title = new { fragment_size = 120, number_of_fragments = 1 },
                    customer = new { fragment_size = 120, number_of_fragments = 1 }
                }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return ([], 0);
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("BM25 search failed (fuzzy={Fuzzy}): {Status} {Body}", fuzzy, response.StatusCode, err);
            return ([], 0);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return (ParseBm25Hits(doc.RootElement), ReadHitTotal(doc.RootElement));
    }

    private static List<RankedHit> ParseBm25Hits(JsonElement root)
    {
        var hits = new List<RankedHit>();
        foreach (var hit in root.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var reg = source.TryGetProperty("regNumber", out var regEl) ? regEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(reg))
            {
                continue;
            }

            string? snippet = null;
            string? matchedIn = null;
            string? matchLabel = null;
            if (hit.TryGetProperty("highlight", out var highlight))
            {
                if (TryFirstHighlight(highlight, "objectInfo", out snippet))
                {
                    matchedIn = "card";
                    matchLabel = "Объект закупки";
                }
                else if (TryFirstHighlight(highlight, "title", out snippet))
                {
                    matchedIn = "card";
                    matchLabel = "Название извещения";
                }
                else if (TryFirstHighlight(highlight, "customer", out snippet))
                {
                    matchedIn = "card";
                    matchLabel = "Заказчик";
                }
                else if (TryFirstHighlight(highlight, "fullText", out snippet))
                {
                    matchedIn = "fullText";
                    matchLabel = "Текст закупки";
                }
            }

            var score = hit.TryGetProperty("_score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number
                ? scoreEl.GetDouble()
                : 0;
            hits.Add(new RankedHit(reg, score, snippet, matchedIn, matchLabel));
        }

        return hits;
    }

    private async Task<long> CountOrdersAsync(SearchFilters? filters, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        object query = filters is null
            ? new { match_all = new { } }
            : WrapWithFilters(new { match_all = new { } }, filters.Value);

        using var response = await client.PostAsJsonAsync(
            $"/{opts.OrdersIndex}/_count",
            new { query },
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return 0;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Orders count failed: {Status} {Body}", response.StatusCode, err);
            return 0;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.TryGetProperty("count", out var countEl) && countEl.TryGetInt64(out var count)
            ? count
            : 0;
    }

    private static long ReadHitTotal(JsonElement root)
    {
        if (!root.TryGetProperty("hits", out var hits) ||
            !hits.TryGetProperty("total", out var total))
        {
            return 0;
        }

        if (total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var numeric))
        {
            return numeric;
        }

        if (total.ValueKind == JsonValueKind.Object &&
            total.TryGetProperty("value", out var value) &&
            value.TryGetInt64(out var objectValue))
        {
            return objectValue;
        }

        return 0;
    }

    private async Task<List<RankedHit>> SearchKnnAsync(float[] embedding, int size, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var body = new
        {
            size,
            query = new
            {
                knn = new
                {
                    embedding = new
                    {
                        vector = embedding,
                        k = size
                    }
                }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.ChunksIndex}/_search", body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("kNN search failed: {Status} {Body}", response.StatusCode, err);
            return [];
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var bestByReg = new Dictionary<string, RankedHit>(StringComparer.Ordinal);
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var chunkSource = source.TryGetProperty("source", out var srcField) ? srcField.GetString() ?? string.Empty : string.Empty;
            if (!IsContentChunkSource(chunkSource))
            {
                continue;
            }

            var reg = source.GetProperty("regNumber").GetString();
            if (string.IsNullOrWhiteSpace(reg))
            {
                continue;
            }

            var score = hit.GetProperty("_score").GetDouble();
            var snippet = Truncate(source.TryGetProperty("text", out var text) ? text.GetString() : null, 220);
            var (matchedIn, matchLabel) = DescribeChunkSource(chunkSource);
            if (!bestByReg.TryGetValue(reg, out var existing) || score > existing.Score)
            {
                bestByReg[reg] = new RankedHit(reg, score, snippet, matchedIn, matchLabel);
            }
        }

        return bestByReg.Values
            .OrderByDescending(x => x.Score)
            .ToList();
    }

    private static object BuildBm25Query(string query, bool fuzzy)
    {
        var multiTerm = CountQueryTerms(query) >= 2;

        if (!fuzzy)
        {
            if (multiTerm)
            {
                // Multi-word: require phrase proximity or all terms on the card.
                // Do NOT bag-of-words AND against fullText — boilerplate tokens like
                // «сайт» appear in almost every notice and fake a second-term match.
                return new
                {
                    @bool = new
                    {
                        should = new object[]
                        {
                            new
                            {
                                match_phrase = new Dictionary<string, object>
                                {
                                    ["objectInfo"] = new { query, slop = 3, boost = 20 }
                                }
                            },
                            new
                            {
                                match_phrase = new Dictionary<string, object>
                                {
                                    ["title"] = new { query, slop = 3, boost = 10 }
                                }
                            },
                            new
                            {
                                match_phrase = new Dictionary<string, object>
                                {
                                    ["fullText"] = new { query, slop = 4, boost = 4 }
                                }
                            },
                            new
                            {
                                multi_match = new
                                {
                                    query,
                                    fields = new[] { "objectInfo^12", "title^6" },
                                    type = "best_fields",
                                    @operator = "and"
                                }
                            }
                        },
                        minimum_should_match = 1
                    }
                };
            }

            // Single term: card first, fullText weaker.
            return new
            {
                @bool = new
                {
                    should = new object[]
                    {
                        new
                        {
                            multi_match = new
                            {
                                query,
                                fields = new[] { "objectInfo^12", "title^6", "customer^2" },
                                type = "best_fields",
                                @operator = "and"
                            }
                        },
                        new
                        {
                            multi_match = new
                            {
                                query,
                                fields = new[] { "objectInfo^4", "title^3", "fullText" },
                                type = "best_fields",
                                @operator = "and"
                            }
                        }
                    },
                    minimum_should_match = 1
                }
            };
        }

        // Typo tolerance: AUTO edit distance, prefix_length keeps first letter stable.
        // Multi-term fuzzy stays on the card — fullText fuzzy AND is too noisy.
        if (multiTerm)
        {
            return new
            {
                @bool = new
                {
                    should = new object[]
                    {
                        new
                        {
                            multi_match = new
                            {
                                query,
                                fields = new[] { "objectInfo^12", "title^6" },
                                type = "best_fields",
                                fuzziness = "AUTO",
                                prefix_length = 1,
                                max_expansions = 50,
                                fuzzy_transpositions = true,
                                @operator = "and"
                            }
                        }
                    },
                    minimum_should_match = 1
                }
            };
        }

        return new
        {
            @bool = new
            {
                should = new object[]
                {
                    new
                    {
                        multi_match = new
                        {
                            query,
                            fields = new[] { "objectInfo^12", "title^6", "customer^2" },
                            type = "best_fields",
                            fuzziness = "AUTO",
                            prefix_length = 1,
                            max_expansions = 50,
                            fuzzy_transpositions = true,
                            @operator = "and"
                        }
                    },
                    new
                    {
                        multi_match = new
                        {
                            query,
                            fields = new[] { "fullText" },
                            type = "best_fields",
                            fuzziness = "AUTO",
                            prefix_length = 1,
                            max_expansions = 50,
                            fuzzy_transpositions = true,
                            @operator = "and"
                        }
                    }
                },
                minimum_should_match = 1
            }
        };
    }

    private static int CountQueryTerms(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>
    /// Replace common user vocabulary that the index/analyzer otherwise drops or misses.
    /// Replacements (not appends) keep multi-term AND/phrase cardinality stable.
    /// </summary>
    private static string ExpandQuerySynonyms(string query)
    {
        var trimmed = query.Trim();
        // «разработка ПО» — analyzer drops «ПО», and bare «программное» collides with «программа …» in medicine.
        if (trimmed.Contains("разработ", StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(
                trimmed,
                @"\bПО\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            return "программное обеспечение";
        }

        var terms = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return query;
        }

        var expanded = new List<string>(terms.Length);
        foreach (var term in terms)
        {
            var lower = term.ToLowerInvariant();

            // Russian analyzer strips bare «ПО»; keep software intent.
            if (term.Equals("ПО", StringComparison.OrdinalIgnoreCase) ||
                term.Equals("ПО.", StringComparison.OrdinalIgnoreCase))
            {
                expanded.Add("программное");
                expanded.Add("обеспечение");
                continue;
            }

            if (lower is "канцтовары" or "канцтовар")
            {
                expanded.Add("канцелярские");
                continue;
            }

            if (lower is "крыши" or "крыша" or "крыш")
            {
                expanded.Add("кровли");
                continue;
            }

            expanded.Add(term);
        }

        return string.Join(' ', expanded);
    }

    private async Task<Dictionary<string, ChunkMatch>> FindChunkMatchesAsync(
        string query,
        IReadOnlyList<string> regNumbers,
        bool fuzzy,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var multiTerm = CountQueryTerms(query) >= 2;
        object textMatch;
        if (fuzzy)
        {
            // Fuzzy chunk lookup stays AND; multi-term still requires all tokens in the chunk.
            textMatch = new
            {
                match = new Dictionary<string, object>
                {
                    ["text"] = new
                    {
                        query,
                        @operator = "and",
                        fuzziness = "AUTO",
                        prefix_length = 1,
                        max_expansions = 50,
                        fuzzy_transpositions = true
                    }
                }
            };
        }
        else if (multiTerm)
        {
            textMatch = new
            {
                match_phrase = new Dictionary<string, object>
                {
                    ["text"] = new { query, slop = 4 }
                }
            };
        }
        else
        {
            textMatch = new
            {
                match = new Dictionary<string, object>
                {
                    ["text"] = new { query, @operator = "and" }
                }
            };
        }

        var body = new
        {
            size = Math.Min(Math.Max(regNumbers.Count * 3, 10), 100),
            query = new
            {
                @bool = new
                {
                    filter = new object[]
                    {
                        new { terms = new { regNumber = regNumbers } }
                    },
                    must = new[] { textMatch }
                }
            },
            highlight = new
            {
                fields = new
                {
                    text = new { fragment_size = 180, number_of_fragments = 1 }
                }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.ChunksIndex}/_search", body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Chunk match lookup failed: {Status} {Body}", response.StatusCode, err);
            }

            return new Dictionary<string, ChunkMatch>(StringComparer.Ordinal);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var best = new Dictionary<string, ChunkMatch>(StringComparer.Ordinal);
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var reg = source.TryGetProperty("regNumber", out var regEl) ? regEl.GetString() : null;
            var chunkSource = source.TryGetProperty("source", out var srcEl) ? srcEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(reg) || !IsContentChunkSource(chunkSource))
            {
                continue;
            }

            var score = hit.TryGetProperty("_score", out var scoreEl) ? scoreEl.GetDouble() : 0;
            string? snippet = null;
            if (hit.TryGetProperty("highlight", out var highlight) &&
                TryFirstHighlight(highlight, "text", out snippet))
            {
                // keep highlight snippet
            }
            else
            {
                snippet = Truncate(source.TryGetProperty("text", out var text) ? text.GetString() : null, 220);
            }

            var (matchedIn, matchLabel) = DescribeChunkSource(chunkSource);
            if (!best.TryGetValue(reg, out var existing) || score > existing.Score)
            {
                best[reg] = new ChunkMatch(matchedIn, matchLabel, snippet, score);
            }
        }

        return best;
    }

    private async Task<Dictionary<string, OrderSource>> FetchOrdersAsync(
        IReadOnlyList<string> regNumbers,
        CancellationToken cancellationToken)
    {
        if (regNumbers.Count == 0)
        {
            return new Dictionary<string, OrderSource>(StringComparer.Ordinal);
        }

        var opts = options.Value;
        var client = httpClientFactory.CreateClient("opensearch");
        var body = new
        {
            size = regNumbers.Count,
            query = new
            {
                terms = new
                {
                    regNumber = regNumbers
                }
            }
        };

        using var response = await client.PostAsJsonAsync($"/{opts.OrdersIndex}/_search", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        var map = new Dictionary<string, OrderSource>(StringComparer.Ordinal);
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var reg = source.GetProperty("regNumber").GetString();
            if (string.IsNullOrWhiteSpace(reg))
            {
                continue;
            }

            var categoryIds = new List<string>();
            if (source.TryGetProperty("categoryIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in idsEl.EnumerateArray())
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        categoryIds.Add(value);
                    }
                }
            }

            double? priceValue = null;
            if (source.TryGetProperty("priceValue", out var priceValueEl) &&
                priceValueEl.ValueKind is JsonValueKind.Number)
            {
                priceValue = priceValueEl.GetDouble();
            }

            var (hasAdvance, advancePercent, advanceLabel, advanceSnippet) = ReadAdvance(source);
            map[reg] = new OrderSource(
                source.TryGetProperty("title", out var title) ? title.GetString() : null,
                source.TryGetProperty("customer", out var customer) ? customer.GetString() : null,
                source.TryGetProperty("price", out var price) ? price.GetString() : null,
                source.TryGetProperty("noticeUrl", out var notice) ? notice.GetString() : null,
                source.TryGetProperty("objectInfo", out var obj) ? obj.GetString() : null,
                source.TryGetProperty("coverage", out var coverage) ? coverage.GetString() ?? "partial" : "partial",
                source.TryGetProperty("categoryL1Id", out var l1Id) ? l1Id.GetString() : null,
                source.TryGetProperty("categoryL1Name", out var l1Name) ? l1Name.GetString() : null,
                source.TryGetProperty("categoryL2Id", out var l2Id) ? l2Id.GetString() : null,
                source.TryGetProperty("categoryL2Name", out var l2Name) ? l2Name.GetString() : null,
                categoryIds,
                priceValue,
                hasAdvance,
                advancePercent,
                advanceLabel,
                advanceSnippet,
                ReadDate(source, "applicationDeadline"),
                source.TryGetProperty("classificationMethod", out var methodEl) ? methodEl.GetString() : null,
                source.TryGetProperty("classificationConfidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number
                    ? confEl.GetDouble()
                    : null,
                source.TryGetProperty("taxonomyVersion", out var taxEl) ? taxEl.GetString() : null,
                source.TryGetProperty("reviewStatus", out var revEl) ? revEl.GetString() : null);
        }

        return map;
    }

    private object WrapWithFilters(object query, SearchFilters filters)
    {
        var filterClauses = new List<object>();
        if (!string.IsNullOrWhiteSpace(filters.CategoryId))
        {
            var field = ResolveCategoryField(filters.CategoryId);
            filterClauses.Add(new
            {
                term = new Dictionary<string, object>
                {
                    [field] = filters.CategoryId
                }
            });
        }

        if (filters.PriceMin is not null || filters.PriceMax is not null)
        {
            var range = new Dictionary<string, object>();
            if (filters.PriceMin is not null)
            {
                range["gte"] = filters.PriceMin.Value;
            }

            if (filters.PriceMax is not null)
            {
                range["lte"] = filters.PriceMax.Value;
            }

            filterClauses.Add(new
            {
                range = new Dictionary<string, object>
                {
                    ["priceValue"] = range
                }
            });
        }

        if (filterClauses.Count == 0)
        {
            return query;
        }

        return new
        {
            @bool = new
            {
                filter = filterClauses,
                must = new[] { query }
            }
        };
    }

    private bool MatchesFilters(OrderSource? order, SearchFilters filters)
    {
        if (order is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filters.CategoryId))
        {
            var field = ResolveCategoryField(filters.CategoryId);
            var categoryOk = field == "categoryL2Id"
                ? string.Equals(order.CategoryL2Id, filters.CategoryId, StringComparison.Ordinal)
                : string.Equals(order.CategoryL1Id, filters.CategoryId, StringComparison.Ordinal);
            if (!categoryOk)
            {
                return false;
            }
        }

        if (filters.PriceMin is null && filters.PriceMax is null)
        {
            return true;
        }

        if (order.PriceValue is null)
        {
            return false;
        }

        if (filters.PriceMin is not null && order.PriceValue < filters.PriceMin)
        {
            return false;
        }

        if (filters.PriceMax is not null && order.PriceValue > filters.PriceMax)
        {
            return false;
        }

        return true;
    }

    private string ResolveCategoryField(string categoryId)
    {
        if (taxonomy.IsL2(categoryId))
        {
            return "categoryL2Id";
        }

        if (taxonomy.IsL1(categoryId))
        {
            return "categoryL1Id";
        }

        // Unknown id: prefer L2-looking legacy dotted ids, otherwise L1.
        return categoryId.Contains('.', StringComparison.Ordinal) ? "categoryL2Id" : "categoryL1Id";
    }

    private readonly record struct SearchFilters(string? CategoryId, double? PriceMin, double? PriceMax)
    {
        public static SearchFilters Create(string? categoryId, double? priceMin, double? priceMax)
        {
            var category = string.IsNullOrWhiteSpace(categoryId) ? null : categoryId.Trim();
            double? min = priceMin is null || double.IsNaN(priceMin.Value) || priceMin < 0
                ? null
                : priceMin;
            double? max = priceMax is null || double.IsNaN(priceMax.Value) || priceMax < 0
                ? null
                : priceMax;

            if (min is not null && max is not null && min > max)
            {
                (min, max) = (max, min);
            }

            return new SearchFilters(category, min, max);
        }
    }

    private static (string MatchedIn, string MatchLabel) ResolveMatchLocation(RankedHit ranked, ChunkMatch? chunk)
    {
        if (ranked.MatchedIn is "card")
        {
            return ("card", ranked.MatchLabel ?? "Карточка");
        }

        if (chunk is not null)
        {
            return (chunk.MatchedIn, chunk.MatchLabel);
        }

        if (ranked.MatchedIn is "document" or "page" or "card")
        {
            return (ranked.MatchedIn, ranked.MatchLabel ?? DescribeMatchedIn(ranked.MatchedIn));
        }

        if (ranked.MatchedIn is "fullText")
        {
            // fullText includes card meta + documents; without a chunk hit treat as card text.
            return ("card", "Карточка");
        }

        return (
            ranked.MatchedIn ?? "card",
            ranked.MatchLabel ?? "Карточка");
    }

    private static (string MatchedIn, string MatchLabel) DescribeChunkSource(string chunkSource)
    {
        if (chunkSource.StartsWith("documents/", StringComparison.Ordinal))
        {
            var fileName = chunkSource["documents/".Length..];
            // Collector stores files as "{uid}__{originalName}" — show the readable part.
            var display = fileName;
            var sep = fileName.IndexOf("__", StringComparison.Ordinal);
            if (sep >= 0 && sep + 2 < fileName.Length)
            {
                display = fileName[(sep + 2)..];
            }

            return ("document", $"Документ: {display}");
        }

        if (chunkSource.StartsWith("pages/", StringComparison.Ordinal))
        {
            var fileName = chunkSource["pages/".Length..];
            return ("page", $"Страница: {fileName}");
        }

        return ("document", chunkSource);
    }

    private static string DescribeMatchedIn(string matchedIn) => matchedIn switch
    {
        "card" => "Карточка",
        "document" => "Документ",
        "page" => "Страница",
        _ => matchedIn
    };

    private static List<RankedHit> FuseRanked(
        IReadOnlyList<RankedHit> bm25,
        IReadOnlyList<RankedHit> knn,
        int take)
    {
        // Precision-first: if lexical hits exist, keep BM25 order untouched.
        // kNN is used only for synonym/paraphrase recall when BM25 is empty.
        if (bm25.Count > 0)
        {
            return bm25.Take(take).ToList();
        }

        // Ignore weak semantic neighbors (no lexical anchor).
        return knn.Where(h => h.Score >= 0.90).Take(take).ToList();
    }

    private static bool IsShortLatinToken(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length is < 2 or > 6)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool ShouldTryFuzzy(string query)
    {
        if (IsShortLatinToken(query))
        {
            return false;
        }

        // Single very short token fuzzes into unrelated stems too easily.
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length > 0 && terms.All(t => t.Length >= 4);
    }

    private static List<RankedHit> CutWeakTail(IReadOnlyList<RankedHit> hits)
    {
        if (hits.Count <= 1)
        {
            return hits.ToList();
        }

        var top = hits[0].Score;
        if (top <= 0)
        {
            return hits.ToList();
        }

        // Drop long-tail noise much weaker than the best hit.
        var minScore = Math.Max(top * 0.25, top > 20 ? 10.0 : 0.0);
        return hits.Where(h => h.Score >= minScore).ToList();
    }

    private static bool IsContentChunkSource(string source) =>
        source.StartsWith("documents/", StringComparison.Ordinal)
        || source.StartsWith("pages/", StringComparison.Ordinal);

    private static bool TryFirstHighlight(JsonElement highlight, string field, out string? snippet)
    {
        snippet = null;
        if (!highlight.TryGetProperty(field, out var values) || values.GetArrayLength() == 0)
        {
            return false;
        }

        snippet = values[0].GetString();
        return !string.IsNullOrWhiteSpace(snippet);
    }

    private static string? Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= max ? normalized : normalized[..max] + "…";
    }

    private sealed record RankedHit(
        string RegNumber,
        double Score,
        string? Snippet,
        string? MatchedIn,
        string? MatchLabel);

    private sealed record ChunkMatch(
        string MatchedIn,
        string MatchLabel,
        string? Snippet,
        double Score);

    private sealed record OrderSource(
        string? Title,
        string? Customer,
        string? Price,
        string? NoticeUrl,
        string? ObjectInfo,
        string Coverage,
        string? CategoryL1Id,
        string? CategoryL1Name,
        string? CategoryL2Id,
        string? CategoryL2Name,
        IReadOnlyList<string> CategoryIds,
        double? PriceValue,
        bool HasAdvance,
        double? AdvancePercent,
        string? AdvanceLabel,
        string? AdvanceSnippet,
        DateTimeOffset? ApplicationDeadline,
        string? ClassificationMethod,
        double? ClassificationConfidence,
        string? TaxonomyVersion,
        string? ReviewStatus);

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; } = [];
    }
}
