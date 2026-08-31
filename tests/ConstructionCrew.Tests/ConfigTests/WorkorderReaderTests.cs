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
            var path = WriteWorkorder(vaultRoot, "Lighthouse", "named-graphs", "feature: named-graphs\njobsite: Lighthouse\nsourceBranch: develop");

            var parsed = new WorkorderReader().Read(path, vaultRoot);

            Assert.Equal("named-graphs", parsed.Feature);
            Assert.Equal("Lighthouse", parsed.Jobsite);
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
            var path = WriteWorkorder(vaultRoot, "Lighthouse", "named-graphs", "feature: named-graphs\njobsite: Lighthouse");

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
            var folder = Path.Combine(vaultRoot, "Notes", "Lighthouse");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "WORKORDER.md");
            File.WriteAllText(path, "---\nfeature: named-graphs\njobsite: Lighthouse\n---\n");

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
            var folder = Path.Combine(vaultRoot, "Plans", "Lighthouse");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "WORKORDER.md");
            File.WriteAllText(path, "---\nfeature: named-graphs\njobsite: Lighthouse\n---\n");

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
            var path = WriteWorkorder(vaultRoot, "Lighthouse", "named-graphs", "feature: named-graphs\njobsite: Tidepool");

            var ex = Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));

            Assert.Contains("Tidepool", ex.Message);
            Assert.Contains("Lighthouse", ex.Message);
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
            var path = WriteWorkorder(vaultRoot, "Lighthouse", "named-graphs", "feature: something-else\njobsite: Lighthouse");

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
            var folder = Path.Combine(vaultRoot, "Plans", "Lighthouse", "named-graphs");
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
            var path = Path.Combine(vaultRoot, "Plans", "Lighthouse", "named-graphs", "WORKORDER.md");

            Assert.Throws<InvalidOperationException>(() => new WorkorderReader().Read(path, vaultRoot));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }
}
