namespace Zakupki.Api.Analytics;

public static class AnalyticsEventTypes
{
    public const string Search = "search";
    public const string SuggestSelect = "suggest_select";
    public const string OrderOpen = "order_open";

    public static bool IsKnown(string? type) =>
        type is Search or SuggestSelect or OrderOpen;
}

public sealed class AnalyticsEventDto
{
    public string? Type { get; set; }
    public string? SessionId { get; set; }
    public string? Q { get; set; }
    public long? Total { get; set; }
    public bool? Fuzzy { get; set; }
    public string? Category { get; set; }
    public double? PriceMin { get; set; }
    public double? PriceMax { get; set; }
    public string? Suggestion { get; set; }
    public string? RegNumber { get; set; }
    public DateTimeOffset? Ts { get; set; }
}

public sealed class AnalyticsEventsRequest
{
    public List<AnalyticsEventDto>? Events { get; set; }
}

public sealed record AnalyticsEvent(
    string Type,
    string SessionId,
    DateTimeOffset Ts,
    string? Q = null,
    long? Total = null,
    bool? Fuzzy = null,
    string? Category = null,
    double? PriceMin = null,
    double? PriceMax = null,
    string? Suggestion = null,
    string? RegNumber = null);

public sealed record AnalyticsCountItem(string Key, long Count);

public sealed record AnalyticsSummaryResponse(
    IReadOnlyList<AnalyticsCountItem> TopQueries,
    IReadOnlyList<AnalyticsCountItem> ZeroResultQueries,
    IReadOnlyList<AnalyticsCountItem> TopOpenedOrders,
    IReadOnlyList<AnalyticsEvent> Recent,
    long TotalEvents);

public sealed record AnalyticsEventsResponse(
    IReadOnlyList<AnalyticsEvent> Items,
    long Total);
