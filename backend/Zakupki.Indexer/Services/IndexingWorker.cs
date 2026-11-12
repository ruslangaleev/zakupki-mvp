using Microsoft.Extensions.Options;
using Zakupki.Indexer.Configuration;

namespace Zakupki.Indexer.Services;

public sealed class IndexingWorker(
    OrderIndexingService indexingService,
    IOptions<IndexerOptions> options,
    ILogger<IndexingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Indexer started. DataPath={DataPath}", options.Value.DataPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await indexingService.IndexAllAsync(stoppingToken);
                var oneShot = options.Value.ClassifyOnly
                    || options.Value.AdvanceOnly
                    || options.Value.DatesOnly
                    || options.Value.ObjectInfoOnly;
                logger.LogInformation(
                    options.Value.ObjectInfoOnly
                        ? "ObjectInfo-only pass complete. Orders updated: {Count}"
                        : options.Value.DatesOnly
                            ? "Dates-only pass complete. Orders updated: {Count}"
                            : options.Value.AdvanceOnly
                                ? "Advance-only pass complete. Orders updated: {Count}"
                                : options.Value.ClassifyOnly
                                    ? "Classify-only pass complete. Orders updated: {Count}"
                                    : "Indexing pass complete. Orders upserted: {Count}",
                    count);

                if (oneShot)
                {
                    var mode = options.Value.ObjectInfoOnly
                        ? "ObjectInfoOnly"
                        : options.Value.DatesOnly
                            ? "DatesOnly"
                            : options.Value.AdvanceOnly
                                ? "AdvanceOnly"
                                : "ClassifyOnly";
                    logger.LogInformation("{Mode}=true — exiting after one pass", mode);
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Indexing pass failed");
                if (options.Value.ClassifyOnly
                    || options.Value.AdvanceOnly
                    || options.Value.DatesOnly
                    || options.Value.ObjectInfoOnly)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
