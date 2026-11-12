using System.Text.RegularExpressions;

namespace Zakupki.Api.Analytics;

public sealed partial class AnalyticsService(AnalyticsStore store, ILogger<AnalyticsService> logger)
{
    private const int MaxQLength = 300;
    private const int MaxBatch = 50;

    public int Ingest(IEnumerable<AnalyticsEventDto>? rawEvents)
    {
        if (rawEvents is null)
        {
            return 0;
        }

        var accepted = new List<AnalyticsEvent>();
        foreach (var raw in rawEvents.Take(MaxBatch))
        {
            var normalized = Normalize(raw);
            if (normalized is not null)
            {
                accepted.Add(normalized);
            }
        }

        if (accepted.Count == 0)
        {
            return 0;
        }

        try
        {
            store.Append(accepted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to append analytics events");
            throw;
        }

        return accepted.Count;
    }

    public AnalyticsSummaryResponse GetSummary(int top = 20, int recent = 40)
    {
        top = Math.Clamp(top, 1, 100);
        recent = Math.Clamp(recent, 1, 200);
        var all = store.ReadAll();

        var searches = all.Where(e => e.Type == AnalyticsEventTypes.Search && !string.IsNullOrWhiteSpace(e.Q)).ToList();
        var topQueries = searches
            .Where(e => (e.Total ?? 0) > 0)
            .GroupBy(e => NormalizeQueryKey(e.Q!), StringComparer.OrdinalIgnoreCase)
            .Select(g => new AnalyticsCountItem(g.First().Q!.Trim(), g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

        var zero = searches
            .Where(e => (e.Total ?? 0) == 0)
            .GroupBy(e => NormalizeQueryKey(e.Q!), StringComparer.OrdinalIgnoreCase)
            .Select(g => new AnalyticsCountItem(g.First().Q!.Trim(), g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

        var opened = all
            .Where(e => e.Type == AnalyticsEventTypes.OrderOpen && !string.IsNullOrWhiteSpace(e.RegNumber))
            .GroupBy(e => e.RegNumber!, StringComparer.Ordinal)
            .Select(g => new AnalyticsCountItem(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(top)
            .ToList();

        var recentItems = all
            .OrderByDescending(e => e.Ts)
            .Take(recent)
            .ToList();

        return new AnalyticsSummaryResponse(topQueries, zero, opened, recentItems, all.Count);
    }

    public AnalyticsEventsResponse GetEvents(int limit = 50, string? type = null)
    {
        limit = Math.Clamp(limit, 1, 500);
        var all = store.ReadAll();
        IEnumerable<AnalyticsEvent> query = all;
        if (!string.IsNullOrWhiteSpace(type) && AnalyticsEventTypes.IsKnown(type.Trim().ToLowerInvariant()))
        {
            var t = type.Trim().ToLowerInvariant();
            query = query.Where(e => e.Type == t);
        }

        var items = query.OrderByDescending(e => e.Ts).Take(limit).ToList();
        return new AnalyticsEventsResponse(items, all.Count);
    }

    /// <summary>
    /// Successful user queries (search with total &gt; 0), ranked by frequency.
    /// </summary>
    public IReadOnlyList<(string Query, int Count)> GetSuccessfulQueries(int max = 500)
    {
        max = Math.Clamp(max, 1, 5000);
        var all = store.ReadAll();
        return all
            .Where(e => e.Type == AnalyticsEventTypes.Search
                        && !string.IsNullOrWhiteSpace(e.Q)
                        && (e.Total ?? 0) > 0)
            .GroupBy(e => NormalizeQueryKey(e.Q!), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Query: CollapseSpaces(g.First().Q!), Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Query, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static AnalyticsEvent? Normalize(AnalyticsEventDto raw)
    {
        var type = raw.Type?.Trim().ToLowerInvariant();
        if (!AnalyticsEventTypes.IsKnown(type))
        {
            return null;
        }

        var sessionId = string.IsNullOrWhiteSpace(raw.SessionId)
            ? "anonymous"
            : raw.SessionId.Trim();
        if (sessionId.Length > 80)
        {
            sessionId = sessionId[..80];
        }

        var ts = raw.Ts ?? DateTimeOffset.UtcNow;
        var q = Truncate(CollapseSpaces(raw.Q), MaxQLength);
        var suggestion = Truncate(CollapseSpaces(raw.Suggestion), MaxQLength);
        var reg = Truncate(raw.RegNumber?.Trim(), 32);

        return type switch
        {
            AnalyticsEventTypes.Search when !string.IsNullOrWhiteSpace(q) =>
                new AnalyticsEvent(
                    AnalyticsEventTypes.Search,
                    sessionId,
                    ts,
                    Q: q,
                    Total: raw.Total,
                    Fuzzy: raw.Fuzzy,
                    Category: Truncate(raw.Category, 64),
                    PriceMin: raw.PriceMin,
                    PriceMax: raw.PriceMax),
            AnalyticsEventTypes.SuggestSelect when !string.IsNullOrWhiteSpace(suggestion) =>
                new AnalyticsEvent(
                    AnalyticsEventTypes.SuggestSelect,
                    sessionId,
                    ts,
                    Q: q,
                    Suggestion: suggestion),
            AnalyticsEventTypes.OrderOpen when !string.IsNullOrWhiteSpace(reg) =>
                new AnalyticsEvent(
                    AnalyticsEventTypes.OrderOpen,
                    sessionId,
                    ts,
                    Q: q,
                    RegNumber: reg),
            _ => null
        };
    }

    private static string NormalizeQueryKey(string q) =>
        CollapseSpaces(q).ToLowerInvariant();

    private static string CollapseSpaces(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return MultiSpace().Replace(value.Trim(), " ");
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        return v.Length <= max ? v : v[..max];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}
