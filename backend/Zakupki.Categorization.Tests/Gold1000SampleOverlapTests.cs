using Zakupki.Categorization.Eval;

namespace Zakupki.Categorization.Tests;

/// <summary>#35 / #37: gold1000 sample must not intersect gold150; overlap gate uses SamplePart01.</summary>
public class Gold1000SampleOverlapTests
{
    [Fact]
    public void Part01_sample_has_zero_overlap_with_gold150_fixture()
    {
        var repoRoot = FindRepoRoot();
        var goldPath = Path.Combine(
            repoRoot,
            "backend", "Zakupki.Categorization.Eval", "testdata", "gold150", "gold150.csv");
        var samplePath = Path.Combine(repoRoot, "data", "eval", "gold1000_part01_sample.csv");

        Assert.True(File.Exists(goldPath), goldPath);
        Assert.True(File.Exists(samplePath), samplePath);

        var gold = SamplePart01.LoadGoldRegNumbers(goldPath);
        var sample = SamplePart01.LoadGoldRegNumbers(samplePath);

        Assert.Equal(150, gold.Count);
        Assert.Equal(100, sample.Count);
        Assert.Equal(0, SamplePart01.CountOverlap(sample, gold));
    }

    [Fact]
    public void CountOverlap_detects_shared_regs()
    {
        var gold = new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };
        Assert.Equal(0, SamplePart01.CountOverlap(["x", "y"], gold));
        Assert.Equal(2, SamplePart01.CountOverlap(["a", "x", "c"], gold));
    }

    [Fact]
    public void WritePart01Csv_returns_2_and_does_not_write_file_when_overlap()
    {
        var tmp = Directory.CreateTempSubdirectory("gold1000-overlap-");
        try
        {
            var reg = "overlap-reg-1";
            var orderDir = Path.Combine(tmp.FullName, "orders", reg);
            Directory.CreateDirectory(orderDir);
            File.WriteAllText(
                Path.Combine(orderDir, "meta.json"),
                """{"regNumber":"overlap-reg-1","noticeUrl":"https://example.test","title":"t","objectName":"o","positions":[]}""");

            var outPath = Path.Combine(tmp.FullName, "out.csv");
            var gold = new HashSet<string>(StringComparer.Ordinal) { reg };
            var code = SamplePart01.WritePart01Csv([reg], gold, Path.Combine(tmp.FullName, "orders"), outPath);

            Assert.Equal(2, code);
            Assert.False(File.Exists(outPath));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found from " + AppContext.BaseDirectory);
    }
}
