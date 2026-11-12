using Microsoft.Extensions.Options;
using Zakupki.Collector.Configuration;
using Zakupki.Collector.Http;
using Zakupki.Collector.Models;
using Zakupki.Collector.Storage;

namespace Zakupki.Collector.Services;

public sealed class DocumentDownloader
{
    private readonly RateLimitedHttpGateway _http;
    private readonly OrderDiskStore _diskStore;
    private readonly CollectorOptions _options;
    private readonly ILogger<DocumentDownloader> _logger;

    public DocumentDownloader(
        RateLimitedHttpGateway http,
        OrderDiskStore diskStore,
        IOptions<CollectorOptions> options,
        ILogger<DocumentDownloader> logger)
    {
        _http = http;
        _diskStore = diskStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task DownloadAllAsync(
        string regNumber,
        IList<OrderDocumentInfo> documents,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(_options.MaxConcurrentDownloads, _options.MaxConcurrentDownloads);
        var tasks = documents.Select(async document =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Downloading document {Uid} for {RegNumber}", document.Uid, regNumber);
                var (content, fileName) = await _http.DownloadAsync(document.DownloadUrl, cancellationToken);
                await _diskStore.SaveDocumentAsync(regNumber, document, content, fileName, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}
