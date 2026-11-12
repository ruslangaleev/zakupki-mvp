using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zakupki.Collector.Configuration;
using Zakupki.Collector.Models;

namespace Zakupki.Collector.Storage;

public sealed class OrderDedupStore
{
    private readonly CollectorOptions _options;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public OrderDedupStore(IOptions<CollectorOptions> options)
    {
        _options = options.Value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(IndexDirectory);

        if (File.Exists(SeenFilePath))
        {
            foreach (var line in await File.ReadAllLinesAsync(SeenFilePath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("regNumber", out var reg))
                    {
                        var value = reg.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            _seen.TryAdd(value, 0);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines.
                }
            }
        }

        // Rebuild from completed folders in case index was truncated.
        if (Directory.Exists(_options.DataPath))
        {
            foreach (var dir in Directory.EnumerateDirectories(_options.DataPath))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name) || name.Equals("index", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var statusPath = Path.Combine(dir, "status.json");
                if (!File.Exists(statusPath))
                {
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(statusPath, cancellationToken);
                    var status = JsonSerializer.Deserialize<OrderProcessingStatus>(json, JsonSerializerDefaults.Options);
                    if (status?.State == OrderProcessingState.Completed)
                    {
                        _seen.TryAdd(name, 0);
                    }
                }
                catch (JsonException)
                {
                    // Ignore broken status files.
                }
            }
        }

        _initialized = true;
    }

    public bool IsSeen(string regNumber) => _seen.ContainsKey(regNumber);

    public async Task MarkSeenAsync(string regNumber, CancellationToken cancellationToken)
    {
        if (!_seen.TryAdd(regNumber, 0))
        {
            return;
        }

        Directory.CreateDirectory(IndexDirectory);
        var payload = JsonSerializer.Serialize(new
        {
            regNumber,
            seenAt = DateTimeOffset.UtcNow
        }, JsonSerializerDefaults.Options);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(SeenFilePath, payload + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string IndexDirectory => Path.Combine(_options.DataPath, "index");
    private string SeenFilePath => Path.Combine(IndexDirectory, "seen.jsonl");
}
