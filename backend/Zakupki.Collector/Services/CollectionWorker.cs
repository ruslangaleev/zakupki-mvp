using Microsoft.Extensions.Options;
using Zakupki.Collector.Configuration;
using Zakupki.Collector.Rss;
using Zakupki.Collector.Storage;

namespace Zakupki.Collector.Services;

public sealed class CollectionWorker : BackgroundService
{
    private readonly RssFeedClient _rssFeedClient;
    private readonly OrderIngestionService _ingestionService;
    private readonly OrderDedupStore _dedupStore;
    private readonly OrderDiskStore _diskStore;
    private readonly CollectorOptions _options;
    private readonly ILogger<CollectionWorker> _logger;

    public CollectionWorker(
        RssFeedClient rssFeedClient,
        OrderIngestionService ingestionService,
        OrderDedupStore dedupStore,
        OrderDiskStore diskStore,
        IOptions<CollectorOptions> options,
        ILogger<CollectionWorker> logger)
    {
        _rssFeedClient = rssFeedClient;
        _ingestionService = ingestionService;
        _dedupStore = dedupStore;
        _diskStore = diskStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.DataPath);
        await _dedupStore.InitializeAsync(stoppingToken);

        _logger.LogInformation(
            "Collector started. RSS={RssUrl}; DataPath={DataPath}; Interval={Interval}s",
            _options.RssUrl,
            Path.GetFullPath(_options.DataPath),
            _options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSS collection cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Polling RSS feed");
        var items = await _rssFeedClient.FetchAsync(_options.RssUrl, cancellationToken);
        _logger.LogInformation("RSS returned {Count} items", items.Count);

        var processed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await _diskStore.ReadStatusAsync(item.RegNumber, cancellationToken);
            var isSeen = _dedupStore.IsSeen(item.RegNumber);
            if (!_diskStore.ShouldProcess(item.RegNumber, status, isSeen))
            {
                skipped++;
                continue;
            }

            try
            {
                _logger.LogInformation("New/retry order detected: {RegNumber} ({Title})", item.RegNumber, item.Title);
                await _ingestionService.IngestAsync(item, cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Order {RegNumber} left in failed state and will be retried later", item.RegNumber);
            }
        }

        _logger.LogInformation(
            "Cycle finished. processed={Processed}, skipped={Skipped}, failed={Failed}",
            processed,
            skipped,
            failed);
    }
}
