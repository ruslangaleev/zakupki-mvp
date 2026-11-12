using System.Net;
using Microsoft.Extensions.Options;
using Zakupki.Collector.Configuration;

namespace Zakupki.Collector.Http;

public sealed class RateLimitedHttpGateway
{
    public const string HttpClientName = "zakupki";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CollectorOptions _options;
    private readonly ILogger<RateLimitedHttpGateway> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RateLimitedHttpGateway(
        IHttpClientFactory httpClientFactory,
        IOptions<CollectorOptions> options,
        ILogger<RateLimitedHttpGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<(byte[] Content, string? FileName)> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        fileName = fileName?.Trim('"');

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (bytes, fileName);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string url,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_options.RequestDelayMs > 0)
                {
                    await Task.Delay(_options.RequestDelayMs, cancellationToken);
                }

                var client = _httpClientFactory.CreateClient(HttpClientName);
                var response = await client.GetAsync(url, completionOption, cancellationToken);

                if ((int)response.StatusCode is 429 or >= 500)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"Transient HTTP {(int)status} for {url}");
                }

                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
                _logger.LogWarning(ex, "HTTP attempt {Attempt}/{Max} failed for {Url}", attempt, _options.MaxRetries, url);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        throw new HttpRequestException($"Failed to GET {url} after {_options.MaxRetries} attempts", lastError);
    }
}
