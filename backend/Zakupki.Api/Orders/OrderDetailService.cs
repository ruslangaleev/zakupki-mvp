using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Zakupki.Api.Configuration;

namespace Zakupki.Api.Orders;

public sealed partial class OrderDetailService
{
    private static readonly Regex RegNumberPattern = new(@"^\d{10,30}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataRoot;
    private readonly ILogger<OrderDetailService> _logger;

    public OrderDetailService(
        IOptions<OrdersOptions> options,
        IHostEnvironment environment,
        ILogger<OrderDetailService> logger)
    {
        _logger = logger;
        var configured = options.Value.DataPath?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "data/orders";
        }

        _dataRoot = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }

    public async Task<OrderDetailResponse?> GetAsync(string regNumber, CancellationToken cancellationToken)
    {
        if (!RegNumberPattern.IsMatch(regNumber))
        {
            return null;
        }

        var orderDir = Path.GetFullPath(Path.Combine(_dataRoot, regNumber));
        if (!orderDir.StartsWith(_dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var metaPath = Path.Combine(orderDir, "meta.json");
        if (!File.Exists(metaPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(metaPath);
        var meta = await JsonSerializer.DeserializeAsync<StoredOrderMeta>(stream, JsonOptions, cancellationToken);
        if (meta is null || string.IsNullOrWhiteSpace(meta.RegNumber))
        {
            _logger.LogWarning("Invalid meta.json for {RegNumber}", regNumber);
            return null;
        }

        var classification = await TryReadClassificationAsync(orderDir, cancellationToken);
        var primary = classification?.Categories?.FirstOrDefault(c => c.IsPrimary)
                      ?? classification?.Categories?.FirstOrDefault();
        var advance = await TryReadAdvanceAsync(orderDir, cancellationToken);
        var applicationDeadline = TryParseApplicationDeadline(meta.Fields);

        return new OrderDetailResponse
        {
            RegNumber = meta.RegNumber,
            Title = meta.Title,
            ObjectName = meta.ObjectName,
            Customer = meta.Customer,
            Price = meta.Price,
            Currency = meta.Currency,
            Stage = meta.Stage,
            Law = meta.Law,
            Ikz = meta.Ikz,
            PlacedAt = meta.PlacedAt,
            UpdatedAt = meta.UpdatedAt,
            PubDate = meta.PubDate,
            CollectedAt = meta.CollectedAt,
            ApplicationDeadline = applicationDeadline,
            NoticeUrl = meta.NoticeUrl,
            DocumentsUrl = meta.DocumentsUrl,
            CategoryL1Id = primary?.L1Id,
            CategoryL1Name = primary?.L1Name,
            CategoryL2Id = primary?.L2Id,
            CategoryL2Name = primary?.L2Name,
            ClassificationConfidence = classification?.Confidence,
            ClassificationMethod = classification?.Method,
            TaxonomyVersion = classification?.TaxonomyVersion,
            ReviewStatus = classification?.ReviewStatus,
            MappingVersion = classification?.MappingVersion,
            Positions = (meta.Positions ?? [])
                .Select(p => new OrderPurchasePosition
                {
                    Okpd2Code = p.Okpd2Code,
                    Okpd2Name = p.Okpd2Name,
                    KtruCode = p.KtruCode,
                    KtruName = p.KtruName,
                    Name = p.Name,
                    Unit = p.Unit,
                    Quantity = p.Quantity,
                    UnitPrice = p.UnitPrice,
                    Amount = p.Amount,
                    IsPrimary = p.IsPrimary
                })
                .ToList(),
            HasAdvance = advance?.HasAdvance ?? false,
            AdvancePercent = advance?.Percent,
            AdvanceLabel = advance?.Label,
            AdvanceSnippet = advance?.Snippet,
            Fields = (meta.Fields ?? [])
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => new OrderFieldItem { Name = kv.Key, Value = kv.Value })
                .ToList(),
            Documents = (meta.Documents ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d.Uid))
                .Select(d => new OrderDocumentItem
                {
                    Uid = d.Uid!,
                    FileName = d.FileName,
                    Title = d.Title,
                    DownloadUrl = d.DownloadUrl
                })
                .ToList()
        };
    }

    private static DateTimeOffset? TryParseApplicationDeadline(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return null;
        }

        foreach (var (key, value) in fields)
        {
            if (key.Contains("окончания срока подачи", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("окончания подачи заявок", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseRuDate(value);
            }
        }

        return null;
    }

    private static DateTimeOffset? TryParseRuDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
        {
            return iso;
        }

        var match = RuDate().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var hour = match.Groups["hh"].Success ? int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture) : 0;
        var minute = match.Groups["mm"].Success ? int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture) : 0;
        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"^(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?<y>\d{4})(?:\s+(?<hh>\d{1,2}):(?<mm>\d{2}))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuDate();

    private async Task<StoredClassification?> TryReadClassificationAsync(
        string orderDir,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(orderDir, "classification.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<StoredClassification>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read classification.json in {Dir}", orderDir);
            return null;
        }
    }

    private async Task<StoredAdvance?> TryReadAdvanceAsync(
        string orderDir,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(orderDir, "advance.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<StoredAdvance>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read advance.json in {Dir}", orderDir);
            return null;
        }
    }

    private sealed class StoredOrderMeta
    {
        public string? RegNumber { get; set; }
        public string? NoticeUrl { get; set; }
        public string? DocumentsUrl { get; set; }
        public string? Title { get; set; }
        public string? Customer { get; set; }
        public string? ObjectName { get; set; }
        public string? Price { get; set; }
        public string? Currency { get; set; }
        public string? Stage { get; set; }
        public string? Law { get; set; }
        public string? Ikz { get; set; }
        public string? PlacedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public DateTimeOffset? PubDate { get; set; }
        public DateTimeOffset? CollectedAt { get; set; }
        public Dictionary<string, string>? Fields { get; set; }
        public List<StoredDocument>? Documents { get; set; }
        public List<StoredPosition>? Positions { get; set; }
    }

    private sealed class StoredPosition
    {
        public string? Okpd2Code { get; set; }
        public string? Okpd2Name { get; set; }
        public string? KtruCode { get; set; }
        public string? KtruName { get; set; }
        public string? Name { get; set; }
        public string? Unit { get; set; }
        public double? Quantity { get; set; }
        public double? UnitPrice { get; set; }
        public double? Amount { get; set; }
        public bool IsPrimary { get; set; }
    }

    private sealed class StoredDocument
    {
        public string? Uid { get; set; }
        public string? DownloadUrl { get; set; }
        public string? FileName { get; set; }
        public string? Title { get; set; }
    }

    private sealed class StoredClassification
    {
        public string? Method { get; set; }
        public double Confidence { get; set; }
        public string? TaxonomyVersion { get; set; }
        public string? ReviewStatus { get; set; }
        public string? MappingVersion { get; set; }
        public List<StoredCategoryAssignment>? Categories { get; set; }
    }

    private sealed class StoredCategoryAssignment
    {
        public string? L1Id { get; set; }
        public string? L1Name { get; set; }
        public string? L2Id { get; set; }
        public string? L2Name { get; set; }
        public bool IsPrimary { get; set; }
    }

    private sealed class StoredAdvance
    {
        public bool HasAdvance { get; set; }
        public double? Percent { get; set; }
        public string? Label { get; set; }
        public string? Snippet { get; set; }
    }
}
