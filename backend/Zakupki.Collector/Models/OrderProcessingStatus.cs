namespace Zakupki.Collector.Models;

public enum OrderProcessingState
{
    Pending,
    Completed,
    Failed
}

public sealed class OrderProcessingStatus
{
    public required string RegNumber { get; init; }
    public OrderProcessingState State { get; set; } = OrderProcessingState.Pending;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Error { get; set; }
    public int Attempt { get; set; }
}
