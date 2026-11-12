using Zakupki.Api.Analytics;
using Zakupki.Api.Configuration;
using Zakupki.Api.Orders;
using Zakupki.Api.Review;
using Zakupki.Api.Search;
using Zakupki.Categorization;
using Zakupki.Categorization.Taxonomy;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<SearchOptions>()
    .Bind(builder.Configuration.GetSection(SearchOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.OpenSearchUrl), "Search:OpenSearchUrl is required")
    .Validate(o => !string.IsNullOrWhiteSpace(o.EmbeddingsUrl), "Search:EmbeddingsUrl is required")
    .ValidateOnStart();

builder.Services
    .AddOptions<OrdersOptions>()
    .Bind(builder.Configuration.GetSection(OrdersOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.DataPath), "Orders:DataPath is required")
    .ValidateOnStart();

builder.Services
    .AddOptions<AnalyticsOptions>()
    .Bind(builder.Configuration.GetSection(AnalyticsOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.DataPath), "Analytics:DataPath is required")
    .ValidateOnStart();

builder.Services
    .AddOptions<ReviewOptions>()
    .Bind(builder.Configuration.GetSection(ReviewOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(
                builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
                ?? ["http://localhost:5173", "http://localhost:3000"])
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddHttpClient("opensearch", (sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SearchOptions>>().Value;
    client.BaseAddress = new Uri(options.OpenSearchUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("embeddings", (sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SearchOptions>>().Value;
    client.BaseAddress = new Uri(options.EmbeddingsUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddZakupkiCategorization();
builder.Services.AddSingleton<AnalyticsStore>();
builder.Services.AddSingleton<AnalyticsService>();
builder.Services.AddSingleton<HybridSearchService>();
builder.Services.AddSingleton<OrderDetailService>();
builder.Services.AddSingleton<ReviewQueueService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "Zakupki.Api",
    timestamp = DateTimeOffset.UtcNow
}))
.WithName("Health");

app.MapGet("/api/info", () => Results.Ok(new
{
    name = "Zakupki MVP",
    version = "0.4.0"
}))
.WithName("Info");

app.MapGet("/api/categories", (ITaxonomyCatalog taxonomy) =>
{
    return Results.Ok(new
    {
        version = taxonomy.Version,
        items = taxonomy.Roots.Select(l1 => new
        {
            id = l1.Id,
            name = l1.Name,
            children = l1.Children.Select(l2 => new
            {
                id = l2.Id,
                name = l2.Name
            })
        })
    });
})
.WithName("Categories");

app.MapGet("/api/search", async (
    string? q,
    int? limit,
    int? from,
    string? category,
    double? priceMin,
    double? priceMax,
    string? sort,
    HybridSearchService search,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { error = "Query parameter 'q' is required" });
    }

    var sortMode = SearchSortParser.Parse(sort, SearchSort.Relevance);
    var response = await search.SearchAsync(q, limit, category, priceMin, priceMax, cancellationToken, from, sortMode);
    return Results.Ok(response);
})
.WithName("Search");

app.MapGet("/api/suggest", async (
    string? q,
    int? limit,
    HybridSearchService search,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.Ok(new SuggestResponse(string.Empty, []));
    }

    var response = await search.SuggestAsync(q, limit, cancellationToken);
    return Results.Ok(response);
})
.WithName("Suggest");

app.MapPost("/api/analytics/events", (AnalyticsEventsRequest? body, AnalyticsService analytics) =>
{
    var count = analytics.Ingest(body?.Events);
    return Results.Ok(new { accepted = count });
})
.WithName("AnalyticsIngest");

app.MapGet("/api/admin/analytics/summary", (int? top, int? recent, AnalyticsService analytics) =>
{
    return Results.Ok(analytics.GetSummary(top ?? 20, recent ?? 40));
})
.WithName("AnalyticsSummary");

app.MapGet("/api/admin/analytics/events", (int? limit, string? type, AnalyticsService analytics) =>
{
    return Results.Ok(analytics.GetEvents(limit ?? 50, type));
})
.WithName("AnalyticsEvents");

app.MapGet("/api/orders/recent", async (
    int? limit,
    int? from,
    string? category,
    double? priceMin,
    double? priceMax,
    string? sort,
    HybridSearchService search,
    CancellationToken cancellationToken) =>
{
    var sortMode = SearchSortParser.Parse(sort, SearchSort.Deadline);
    var response = await search.GetRecentAsync(limit, category, priceMin, priceMax, cancellationToken, from, sortMode);
    return Results.Ok(response);
})
.WithName("RecentOrders");

app.MapGet("/api/stats/prices", async (HybridSearchService search, CancellationToken cancellationToken) =>
{
    var stats = await search.GetPriceStatsAsync(cancellationToken);
    return Results.Ok(stats);
})
.WithName("PriceStats");

app.MapGet("/api/orders/{regNumber}", async (
    string regNumber,
    OrderDetailService orders,
    CancellationToken cancellationToken) =>
{
    var detail = await orders.GetAsync(regNumber, cancellationToken);
    return detail is null ? Results.NotFound(new { error = "Order not found" }) : Results.Ok(detail);
})
.WithName("OrderDetail");

app.MapGet("/api/review/queue", async (
    int? limit,
    int? from,
    ReviewQueueService review,
    CancellationToken cancellationToken) =>
{
    var response = await review.ListAsync(limit, from, cancellationToken);
    return Results.Ok(response);
})
.WithName("ReviewQueue");

app.MapGet("/api/review/queue/{regNumber}", async (
    string regNumber,
    ReviewQueueService review,
    CancellationToken cancellationToken) =>
{
    var detail = await review.GetAsync(regNumber, cancellationToken);
    return detail is null ? Results.NotFound(new { error = "Order not found" }) : Results.Ok(detail);
})
.WithName("ReviewQueueDetail");

app.MapPost("/api/review/queue/{regNumber}", async (
    string regNumber,
    ReviewDecisionRequest? body,
    HttpRequest httpRequest,
    ReviewQueueService review,
    Microsoft.Extensions.Options.IOptions<ReviewOptions> reviewOptions,
    CancellationToken cancellationToken) =>
{
    var expected = reviewOptions.Value.OperatorKey?.Trim();
    var provided = httpRequest.Headers["X-Review-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(expected) ||
        !string.Equals(provided, expected, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    if (body is null)
    {
        return Results.BadRequest(new { error = "body required" });
    }

    var (ok, error) = await review.DecideAsync(regNumber, body, cancellationToken);
    return ok
        ? Results.Ok(new { status = "resolved", regNumber })
        : Results.BadRequest(new { error });
})
.WithName("ReviewQueueDecide");

app.Run();
