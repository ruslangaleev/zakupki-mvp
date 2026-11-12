using Zakupki.Collector.Configuration;
using Zakupki.Collector.Http;
using Zakupki.Collector.Parsing;
using Zakupki.Collector.Rss;
using Zakupki.Collector.Services;
using Zakupki.Collector.Storage;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<CollectorOptions>()
    .Bind(builder.Configuration.GetSection(CollectorOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.RssUrl), "Collector:RssUrl is required")
    .Validate(o => !string.IsNullOrWhiteSpace(o.DataPath), "Collector:DataPath is required")
    .Validate(o => o.PollIntervalSeconds >= 10, "Collector:PollIntervalSeconds must be >= 10")
    .ValidateOnStart();

builder.Services.AddHttpClient(RateLimitedHttpGateway.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (compatible; ZakupkiCollector/0.1; +https://localhost)");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
});

builder.Services.AddSingleton<RateLimitedHttpGateway>();
builder.Services.AddSingleton<RssFeedParser>();
builder.Services.AddSingleton<RssFeedClient>();
builder.Services.AddSingleton<NoticeHtmlParser>();
builder.Services.AddSingleton<DocumentsHtmlParser>();
builder.Services.AddSingleton<OrderDedupStore>();
builder.Services.AddSingleton<OrderDiskStore>();
builder.Services.AddSingleton<DocumentDownloader>();
builder.Services.AddSingleton<OrderIngestionService>();
builder.Services.AddHostedService<CollectionWorker>();

var host = builder.Build();
host.Run();
