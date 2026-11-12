namespace Zakupki.Categorization.Tests;

/// <summary>#35: committed part01 sample must not intersect gold150 fixture.</summary>
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

        var gold = LoadRegNumbers(goldPath);
        var sample = LoadRegNumbers(samplePath);
        var overlap = sample.Count(r => gold.Contains(r));

        Assert.Equal(150, gold.Count);
        Assert.Equal(100, sample.Count);
        Assert.Equal(0, overlap);
    }

    [Fact]
    public void CountOverlap_detects_shared_regs()
    {
        var gold = new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };
        Assert.Equal(0, CountOverlap(["x", "y"], gold));
        Assert.Equal(2, CountOverlap(["a", "x", "c"], gold));
    }

    private static int CountOverlap(IEnumerable<string> regs, ISet<string> gold) =>
        regs.Count(r => gold.Contains(r));

    private static HashSet<string> LoadRegNumbers(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var comma = line.IndexOf(',');
            var reg = comma < 0 ? line.Trim() : line[..comma].Trim().Trim('"');
            if (reg.Length > 0)
            {
                set.Add(reg);
            }
        }

        return set;
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
