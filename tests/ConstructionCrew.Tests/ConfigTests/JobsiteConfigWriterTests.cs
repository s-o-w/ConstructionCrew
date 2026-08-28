using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

public class JobsiteConfigWriterTests
{
    [Fact]
    public void AppendJobsite_ThenReload_RoundTripsCorrectly()
    {
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(repoRoot, "repos", "xinfra");
        Directory.CreateDirectory(repoPath);

        var yamlPath = Path.GetTempFileName();
        File.Delete(yamlPath); // EnsureFileExists should recreate it from scratch

        try
        {
            var jobsite = new JobsiteConfig("XINFRA", repoPath, "The semantic data platform.", "https://github.com/spatialbiz/XINFRA", "purple");

            JobsiteConfigWriter.AppendJobsite(yamlPath, jobsite, repoRoot);

            var reloaded = new JobsiteConfigLoader().LoadFromFile(yamlPath, repoRoot);

            Assert.Single(reloaded);
            var reloadedJobsite = reloaded[0];
            Assert.Equal("XINFRA", reloadedJobsite.Name);
            Assert.Equal(Path.GetFullPath(repoPath), Path.GetFullPath(reloadedJobsite.RepoPath));
            Assert.Equal("The semantic data platform.", reloadedJobsite.Description);
            Assert.Equal("https://github.com/spatialbiz/XINFRA", reloadedJobsite.RepoUrl);
            Assert.Equal("purple", reloadedJobsite.ColorName);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void RemoveJobsite_RemovesOnlyTheNamedEntry_NeverTouchesRepoPath()
    {
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPathA = Path.Combine(repoRoot, "repos", "alpha");
        var repoPathB = Path.Combine(repoRoot, "repos", "beta");
        Directory.CreateDirectory(repoPathA);
        Directory.CreateDirectory(repoPathB);
        var canaryFile = Path.Combine(repoPathA, "do-not-delete-me.txt");
        File.WriteAllText(canaryFile, "this represents the real repo -- must survive");

        var yamlPath = Path.GetTempFileName();
        File.Delete(yamlPath);

        try
        {
            JobsiteConfigWriter.AppendJobsite(yamlPath, new JobsiteConfig("Alpha", repoPathA, "desc"), repoRoot);
            JobsiteConfigWriter.AppendJobsite(yamlPath, new JobsiteConfig("Beta", repoPathB, "desc"), repoRoot);

            var removed = JobsiteConfigWriter.RemoveJobsite(yamlPath, "Alpha");

            Assert.True(removed);

            var reloaded = new JobsiteConfigLoader().LoadFromFile(yamlPath, repoRoot);
            Assert.Single(reloaded);
            Assert.Equal("Beta", reloaded[0].Name);

            // The whole point: removing the config entry must never touch the
            // actual repo directory it pointed at.
            Assert.True(Directory.Exists(repoPathA));
            Assert.True(File.Exists(canaryFile));
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void LoadFromFile_MissingFile_ReturnsEmpty()
    {
        var result = new JobsiteConfigLoader().LoadFromFile(Path.Combine(Path.GetTempPath(), "does-not-exist.yaml"), "repoRoot");

        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromFile_EmptyJobsitesKey_ReturnsEmpty_DoesNotThrow()
    {
        // Regression test: "jobsites:" with nothing under it deserializes the
        // list property to null, not []. Hit for real on the freshly seeded
        // config/jobsites.yaml before this was guarded.
        var yamlPath = Path.GetTempFileName();
        File.WriteAllText(yamlPath, "jobsites:\n");

        try
        {
            var result = new JobsiteConfigLoader().LoadFromFile(yamlPath, "repoRoot");

            Assert.Empty(result);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }
}
