using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Zakupki.Collector.Configuration;
using Zakupki.Collector.Models;

namespace Zakupki.Collector.Storage;

public sealed partial class OrderDiskStore
{
    private readonly CollectorOptions _options;

    public OrderDiskStore(IOptions<CollectorOptions> options)
    {
        _options = options.Value;
    }

    public string GetOrderDirectory(string regNumber) =>
        Path.Combine(_options.DataPath, SanitizePathSegment(regNumber));

    public async Task<OrderProcessingStatus?> ReadStatusAsync(string regNumber, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetOrderDirectory(regNumber), "status.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<OrderProcessingStatus>(json, JsonSerializerDefaults.Options);
    }

    public async Task WriteStatusAsync(OrderProcessingStatus status, CancellationToken cancellationToken)
    {
        var dir = GetOrderDirectory(status.RegNumber);
        Directory.CreateDirectory(dir);
        status.UpdatedAt = DateTimeOffset.UtcNow;
        await WriteJsonAsync(Path.Combine(dir, "status.json"), status, cancellationToken);
    }

    public async Task WriteRssAsync(RssOrderItem item, CancellationToken cancellationToken)
    {
        var dir = GetOrderDirectory(item.RegNumber);
        Directory.CreateDirectory(dir);
        await WriteJsonAsync(Path.Combine(dir, "rss.json"), item, cancellationToken);
    }

    public async Task WriteMetaAsync(OrderMeta meta, CancellationToken cancellationToken)
    {
        var dir = GetOrderDirectory(meta.RegNumber);
        Directory.CreateDirectory(dir);
        await WriteJsonAsync(Path.Combine(dir, "meta.json"), meta, cancellationToken);
    }

    public async Task WritePageAsync(string regNumber, string fileName, string html, CancellationToken cancellationToken)
    {
        var pagesDir = Path.Combine(GetOrderDirectory(regNumber), "pages");
        Directory.CreateDirectory(pagesDir);
        await File.WriteAllTextAsync(Path.Combine(pagesDir, fileName), html, cancellationToken);
    }

    public async Task<string> SaveDocumentAsync(
        string regNumber,
        OrderDocumentInfo document,
        byte[] content,
        string? contentFileName,
        CancellationToken cancellationToken)
    {
        var docsDir = Path.Combine(GetOrderDirectory(regNumber), "documents");
        Directory.CreateDirectory(docsDir);

        var preferredName = contentFileName ?? document.FileName ?? document.Title ?? document.Uid;
        var safeName = SanitizeFileName(preferredName);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(safeName)) && !string.IsNullOrWhiteSpace(document.FileName))
        {
            var ext = Path.GetExtension(document.FileName);
            if (!string.IsNullOrWhiteSpace(ext))
            {
                safeName += ext;
            }
        }

        var savedAs = $"{document.Uid}__{safeName}";
        var fullPath = Path.Combine(docsDir, savedAs);
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
        document.SavedAs = savedAs;
        return savedAs;
    }

    public bool ShouldProcess(string regNumber, OrderProcessingStatus? status, bool isSeen)
    {
        if (isSeen || status?.State == OrderProcessingState.Completed)
        {
            return false;
        }

        return true;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonSerializerDefaults.Options);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }

    private static string SanitizeFileName(string value)
    {
        var decoded = value.Trim();
        decoded = UnsafeFileNameChars().Replace(decoded, "_");
        decoded = decoded.Trim('.', ' ', '_');
        if (decoded.Length > 120)
        {
            var ext = Path.GetExtension(decoded);
            var stem = Path.GetFileNameWithoutExtension(decoded);
            decoded = stem[..Math.Min(100, stem.Length)] + ext;
        }

        return string.IsNullOrWhiteSpace(decoded) ? "document" : decoded;
    }

    [GeneratedRegex(@"[^\w\.\-=\(\)\[\] ]+", RegexOptions.Compiled)]
    private static partial Regex UnsafeFileNameChars();
}
