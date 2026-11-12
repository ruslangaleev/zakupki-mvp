using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zakupki.Api.Configuration;

namespace Zakupki.Api.Analytics;

public sealed class AnalyticsStore(
    IOptions<AnalyticsOptions> options,
    ILogger<AnalyticsStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _writeLock = new();
    private string? _resolvedPath;

    public string EventsFilePath
    {
        get
        {
            EnsureDirectory();
            return Path.Combine(_resolvedPath!, "events.jsonl");
        }
    }

    public void Append(IReadOnlyList<AnalyticsEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        var path = EventsFilePath;
        var sb = new StringBuilder(events.Count * 128);
        foreach (var ev in events)
        {
            sb.AppendLine(JsonSerializer.Serialize(ev, JsonOptions));
        }

        lock (_writeLock)
        {
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }

    public IReadOnlyList<AnalyticsEvent> ReadAll(int? maxLines = null)
    {
        var path = EventsFilePath;
        if (!File.Exists(path))
        {
            return [];
        }

        List<string> lines;
        lock (_writeLock)
        {
            lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        }

        if (maxLines is > 0 && lines.Count > maxLines)
        {
            lines = lines.TakeLast(maxLines.Value).ToList();
        }

        var result = new List<AnalyticsEvent>(lines.Count);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var ev = JsonSerializer.Deserialize<AnalyticsEvent>(line, JsonOptions);
                if (ev is not null && !string.IsNullOrWhiteSpace(ev.Type))
                {
                    result.Add(ev);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skip malformed analytics line");
            }
        }

        return result;
    }

    private void EnsureDirectory()
    {
        if (_resolvedPath is not null)
        {
            return;
        }

        var configured = options.Value.DataPath;
        var full = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        Directory.CreateDirectory(full);
        _resolvedPath = full;
        logger.LogInformation("Analytics path: {Path}", full);
    }
}
