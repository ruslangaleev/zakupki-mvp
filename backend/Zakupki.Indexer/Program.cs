using Zakupki.Categorization;
using Zakupki.Indexer.Advance;
using Zakupki.Indexer.Classification;
using Zakupki.Indexer.Clients;
using Zakupki.Indexer.Configuration;
using Zakupki.Indexer.Extraction;
using Zakupki.Indexer.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<IndexerOptions>()
    .Bind(builder.Configuration.GetSection(IndexerOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.DataPath), "Indexer:DataPath is required")
    .Validate(o => !string.IsNullOrWhiteSpace(o.OpenSearchUrl), "Indexer:OpenSearchUrl is required")
    .Validate(o => !string.IsNullOrWhiteSpace(o.EmbeddingsUrl), "Indexer:EmbeddingsUrl is required")
    .Validate(o => o.PollIntervalSeconds >= 10, "Indexer:PollIntervalSeconds must be >= 10")
    .Validate(o => o.EmbeddingDimensions >= 8, "Indexer:EmbeddingDimensions must be >= 8")
    .ValidateOnStart();

builder.Services.AddHttpClient<EmbeddingClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IndexerOptions>>().Value;
    client.BaseAddress = new Uri(options.EmbeddingsUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHttpClient<OpenSearchIndexClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IndexerOptions>>().Value;
    client.BaseAddress = new Uri(options.OpenSearchUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddZakupkiCategorization();
builder.Services.AddSingleton<ClassificationArtifactStore>();
builder.Services.AddSingleton<AdvanceArtifactStore>();
builder.Services.AddSingleton<DocumentTextExtractor>();
builder.Services.AddSingleton<OrderTextExtractor>();
builder.Services.AddSingleton<OrderIndexingService>();
builder.Services.AddHostedService<IndexingWorker>();

var host = builder.Build();
host.Run();
