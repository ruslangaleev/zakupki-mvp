namespace Zakupki.Api.Search;

/// <summary>
/// relevance — hybrid/BM25 score; deadline — applicationDeadline; price — priceValue; published — pubDate.
/// </summary>
public enum SearchSort
{
    Relevance,
    Deadline,
    Price,
    Published
}

public static class SearchSortParser
{
    public static SearchSort Parse(string? raw, SearchSort fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "relevance" or "score" => SearchSort.Relevance,
            "deadline" or "applicationdeadline" or "подача" or "срок" => SearchSort.Deadline,
            "price" or "цена" => SearchSort.Price,
            "published" or "pubdate" or "публикация" or "дата" => SearchSort.Published,
            _ => fallback
        };
    }

    public static string ToApi(SearchSort sort) => sort switch
    {
        SearchSort.Deadline => "deadline",
        SearchSort.Price => "price",
        SearchSort.Published => "published",
        _ => "relevance"
    };
}

public sealed record SearchResponse(
    string Query,
    IReadOnlyList<SearchHit> Items,
    bool Fuzzy = false,
    long TotalInIndex = 0,
    long Total = 0,
    int From = 0,
    int Limit = 0,
    string Sort = "relevance");

public sealed record RecentOrdersResponse(
    IReadOnlyList<SearchHit> Items,
    long TotalInIndex = 0,
    long Total = 0,
    int From = 0,
    int Limit = 0,
    string Sort = "deadline");

public sealed record SuggestResponse(
    string Query,
    IReadOnlyList<SuggestItem> Items);

public sealed record SuggestItem(
    string Text,
    string Kind,
    string? RegNumber = null);

public sealed record SearchHit(
    string RegNumber,
    string? Title,
    string? Customer,
    string? Price,
    double Score,
    string? Snippet,
    string? NoticeUrl,
    string Coverage,
    string? MatchedIn = null,
    string? MatchLabel = null,
    string? CategoryL1Id = null,
    string? CategoryL1Name = null,
    string? CategoryL2Id = null,
    string? CategoryL2Name = null,
    bool HasAdvance = false,
    double? AdvancePercent = null,
    string? AdvanceLabel = null,
    string? AdvanceSnippet = null,
    string? ObjectInfo = null,
    DateTimeOffset? ApplicationDeadline = null,
    string? ClassificationMethod = null,
    double? ClassificationConfidence = null,
    string? TaxonomyVersion = null,
    string? ReviewStatus = null);

public sealed record PriceStatsResponse(double? Min, double? Max);
