using Zakupki.Collector.Http;
using Zakupki.Collector.Models;
using Zakupki.Collector.Parsing;
using Zakupki.Collector.Storage;

namespace Zakupki.Collector.Services;

public sealed class OrderIngestionService
{
    private readonly RateLimitedHttpGateway _http;
    private readonly NoticeHtmlParser _noticeParser;
    private readonly DocumentsHtmlParser _documentsParser;
    private readonly DocumentDownloader _documentDownloader;
    private readonly OrderDiskStore _diskStore;
    private readonly OrderDedupStore _dedupStore;
    private readonly ILogger<OrderIngestionService> _logger;

    public OrderIngestionService(
        RateLimitedHttpGateway http,
        NoticeHtmlParser noticeParser,
        DocumentsHtmlParser documentsParser,
        DocumentDownloader documentDownloader,
        OrderDiskStore diskStore,
        OrderDedupStore dedupStore,
        ILogger<OrderIngestionService> logger)
    {
        _http = http;
        _noticeParser = noticeParser;
        _documentsParser = documentsParser;
        _documentDownloader = documentDownloader;
        _diskStore = diskStore;
        _dedupStore = dedupStore;
        _logger = logger;
    }

    public async Task IngestAsync(RssOrderItem item, CancellationToken cancellationToken)
    {
        var status = await _diskStore.ReadStatusAsync(item.RegNumber, cancellationToken)
            ?? new OrderProcessingStatus { RegNumber = item.RegNumber };

        status.State = OrderProcessingState.Pending;
        status.Attempt += 1;
        status.Error = null;
        await _diskStore.WriteStatusAsync(status, cancellationToken);
        await _diskStore.WriteRssAsync(item, cancellationToken);

        try
        {
            var noticeUrl = NoticeUrlHelper.ToCommonInfoUrl(item.Url);
            var documentsUrl = NoticeUrlHelper.ToDocumentsUrl(item.Url);

            _logger.LogInformation("Fetching common-info for {RegNumber}", item.RegNumber);
            var commonHtml = await _http.GetStringAsync(noticeUrl, cancellationToken);
            await _diskStore.WritePageAsync(item.RegNumber, "common-info.html", commonHtml, cancellationToken);

            var fields = _noticeParser.ParseFields(commonHtml);
            var positions = _noticeParser.ParsePositions(commonHtml).ToList();
            var objectName = FirstNonEmpty(
                item.ObjectName,
                GetField(fields, "Наименование объекта закупки"),
                GetField(fields, "Наименование товара, работы, услуги"),
                positions.FirstOrDefault()?.Name);
            if (NoticeHtmlParser.IsGenericProcurementTitle(objectName))
            {
                objectName = FirstNonEmpty(
                    _noticeParser.TryParseFirstPurchaseObjectName(commonHtml),
                    objectName);
            }

            _logger.LogInformation("Fetching documents page for {RegNumber}", item.RegNumber);
            var documentsHtml = await _http.GetStringAsync(documentsUrl, cancellationToken);
            await _diskStore.WritePageAsync(item.RegNumber, "documents.html", documentsHtml, cancellationToken);

            var documents = _documentsParser.Parse(documentsHtml).ToList();
            await _documentDownloader.DownloadAllAsync(item.RegNumber, documents, cancellationToken);

            var meta = new OrderMeta
            {
                RegNumber = item.RegNumber,
                NoticeUrl = noticeUrl,
                DocumentsUrl = documentsUrl,
                Title = item.Title,
                Customer = FirstNonEmpty(item.Customer, GetField(fields, "Организация, осуществляющая размещение")),
                ObjectName = objectName,
                Price = FirstNonEmpty(item.Price, GetField(fields, "Начальная (максимальная) цена контракта")),
                Currency = FirstNonEmpty(item.Currency, GetField(fields, "Валюта")),
                Stage = FirstNonEmpty(item.Stage, GetField(fields, "Этап закупки")),
                Law = item.Law,
                Ikz = FirstNonEmpty(item.Ikz, GetField(fields, "Идентификационный код закупки (ИКЗ)")),
                PlacedAt = item.PlacedAt,
                UpdatedAt = item.UpdatedAt,
                PubDate = item.PubDate,
                CollectedAt = DateTimeOffset.UtcNow,
                Fields = fields,
                Documents = documents,
                Positions = positions
            };

            await _diskStore.WriteMetaAsync(meta, cancellationToken);

            status.State = OrderProcessingState.Completed;
            status.Error = null;
            await _diskStore.WriteStatusAsync(status, cancellationToken);
            await _dedupStore.MarkSeenAsync(item.RegNumber, cancellationToken);

            _logger.LogInformation(
                "Completed collection for {RegNumber}: {DocumentCount} documents",
                item.RegNumber,
                documents.Count);
        }
        catch (Exception ex)
        {
            status.State = OrderProcessingState.Failed;
            status.Error = ex.Message;
            await _diskStore.WriteStatusAsync(status, cancellationToken);
            _logger.LogError(ex, "Failed to collect order {RegNumber}", item.RegNumber);
            throw;
        }
    }

    private static string? GetField(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
