using Zakupki.Indexer.Parsing;

namespace Zakupki.Categorization.Tests;

public class PurchasePositionsParserTests
{
    [Fact]
    public void Parses_okpd_from_eis_table_with_leading_empty_header()
    {
        var htmlPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "data", "orders", "0101300092326000004", "pages", "common-info.html"));
        Assert.True(File.Exists(htmlPath), htmlPath);

        var html = File.ReadAllText(htmlPath);
        var positions = PurchasePositionsParser.Parse(html);

        Assert.NotEmpty(positions);
        Assert.Contains(positions, p => p.Okpd2Code is not null && p.Okpd2Code.StartsWith("81.", StringComparison.Ordinal));
    }
}
