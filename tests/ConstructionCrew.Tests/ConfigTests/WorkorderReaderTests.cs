using ConstructionCrew.Config;

namespace ConstructionCrew.Tests.ConfigTests;

public class WorkorderReaderTests
{
    /// <summary>Writes a workorder at &lt;vault&gt;/Plans/&lt;jobsite&gt;/&lt;feature&gt;/WORKORDER.md and returns its path.</summary>
    private static string WriteWorkorder(string vaultRoot, string jobsite, string feature, string frontmatter)
    {
        var folder = Path.Combine(vaultRoot, "Plans", jobsite, feature);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "WORKORDER.md");
        File.WriteAllText(path, $"---\n{frontmatter}\n---\n\nDo the thing.\n");
        return path;
    }

    private static string NewVault()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "cc-workorder-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(vaultRoot);
        return vaultRoot;
    }

    [Fact]
    public void Read_ValidFrontmatter_ReturnsFeatureJobsiteAndSourceBranch()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = WriteWorkorder(vaultRoot, "XINFRA", "named-graphs", "feature: named-graphs\njobsite: XINFRA\nsourceBranch: develop");

            var parsed = new WorkorderReader().Read(path, vaultRoot);

            Assert.Equal("named-graphs", parsed.Feature);
            Assert.Equal("XINFRA", parsed.Jobsite);
            Assert.Equal("develop", parsed.SourceBranch);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>
    /// The reader deliberately does NOT resolve the fallback chain -- it reports a
    /// missing sourceBranch as null and leaves DispatchTaskTool to resolve
    /// jobsite DefaultBranch, then "main".
    /// </summary>
    [Fact]
    public void Read_MissingSourceBranch_ReturnsNullRatherThanAFallback()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = WriteWorkorder(vaultRoot, "XINFRA", "named-graphs", "feature: named-graphs\njobsite: XINFRA");

            var parsed = new WorkorderReader().Read(path, vaultRoot);

            Assert.Null(parsed.SourceBranch);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_PathOutsidePlans_IsRejected()
    {
        var vaultRoot = NewVault();
        try
        {
            var folder = Path.Combine(vaultRoot, "Notes", "XINFRA");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "WORKORDER.md");
            File.WriteAllText(path, "---\nfeature: named-graphs\njobsite: XINFRA\n---\n");

            var ex = Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));

            Assert.Contains("Plans", ex.Message);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_WrongDepthUnderPlans_IsRejected()
    {
        var vaultRoot = NewVault();
        try
        {
            var folder = Path.Combine(vaultRoot, "Plans", "XINFRA");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "WORKORDER.md");
            File.WriteAllText(path, "---\nfeature: named-graphs\njobsite: XINFRA\n---\n");

            Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_FrontmatterJobsiteDisagreesWithPath_IsRejected()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = WriteWorkorder(vaultRoot, "XINFRA", "named-graphs", "feature: named-graphs\njobsite: SDS-BSD");

            var ex = Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));

            Assert.Contains("SDS-BSD", ex.Message);
            Assert.Contains("XINFRA", ex.Message);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_FrontmatterFeatureDisagreesWithPath_IsRejected()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = WriteWorkorder(vaultRoot, "XINFRA", "named-graphs", "feature: something-else\njobsite: XINFRA");

            var ex = Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));

            Assert.Contains("something-else", ex.Message);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_NoFrontmatter_IsRejected()
    {
        var vaultRoot = NewVault();
        try
        {
            var folder = Path.Combine(vaultRoot, "Plans", "XINFRA", "named-graphs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "WORKORDER.md");
            File.WriteAllText(path, "Just a body, no frontmatter.\n");

            var ex = Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));

            Assert.Contains("frontmatter", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_MissingFile_IsRejected()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = Path.Combine(vaultRoot, "Plans", "XINFRA", "named-graphs", "WORKORDER.md");

            Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }
}
