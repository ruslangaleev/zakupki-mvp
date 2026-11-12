namespace Zakupki.Api.Configuration;

public sealed class OrdersOptions
{
    public const string SectionName = "Orders";

    /// <summary>Path to collector output (folders per regNumber with meta.json).</summary>
    public string DataPath { get; set; } = "data/orders";
}
