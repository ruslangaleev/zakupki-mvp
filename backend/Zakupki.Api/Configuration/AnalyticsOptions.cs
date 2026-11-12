namespace Zakupki.Api.Configuration;

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Directory for events.jsonl (created on first write).</summary>
    public string DataPath { get; set; } = "data/analytics";
}
