namespace Zakupki.Collector.Models;

public sealed class OrderDocumentInfo
{
    public required string Uid { get; init; }
    public required string DownloadUrl { get; init; }
    public string? FileName { get; init; }
    public string? Title { get; init; }
    public string? SavedAs { get; set; }
}
