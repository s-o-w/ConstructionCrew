using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

public class JobsiteConfigWriterTests
{
    [Fact]
    public void AppendJobsite_ThenReload_RoundTripsCorrectly()
    {
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(repoRoot, "repos", "lighthouse");
        Directory.CreateDirectory(repoPath);

        var yamlPath = Path.GetTempFileName();
        File.Delete(yamlPath); // EnsureFileExists should recreate it from scratch

        try
        {
            var jobsite = new JobsiteConfig("Lighthouse", repoPath, "The semantic data platform.", "https://github.com/example-org/Lighthouse", "purple");

            JobsiteConfigWriter.AppendJobsite(yamlPath, jobsite, repoRoot);

            var reloaded = new JobsiteConfigLoader().LoadFromFile(yamlPath, repoRoot);

            Assert.Single(reloaded);
            var reloadedJobsite = reloaded[0];
            Assert.Equal("Lighthouse", reloadedJobsite.Name);
            Assert.Equal(Path.GetFullPath(repoPath), Path.GetFullPath(reloadedJobsite.RepoPath));
            Assert.Equal("The semantic data platform.", reloadedJobsite.Description);
            Assert.Equal("https://github.com/example-org/Lighthouse", reloadedJobsite.RepoUrl);
            Assert.Equal("purple", reloadedJobsite.ColorName);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    /// <summary>
    /// The same three-part persistence rule for Phase 5's four new fields. Miss
    /// the writer block or the DTO and a Jobsite's build/test commands vanish on
    /// the next process start, and every Foreman hired after that gets
    /// instructions telling it no build command is configured.
    /// </summary>
    [Fact]
    public void AppendJobsite_WithBranchCommandsAndBacklog_RoundTripsAllOfThem()
    {
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(repoRoot, "repos", "phase5fields");
        Directory.CreateDirectory(repoPath);

        var yamlPath = Path.GetTempFileName();
        File.Delete(yamlPath);

        try
        {
            JobsiteConfigWriter.AppendJobsite(
                yamlPath,
                new JobsiteConfig(
                    "Lighthouse",
                    repoPath,
                    "desc",
                    DefaultBranch: "develop",
                    BuildCommand: "dotnet build",
                    TestCommand: "dotnet test",
                    BacklogUrl: "https://github.com/orgs/example-org/projects/73"),
                repoRoot);

            var reloaded = Assert.Single(new JobsiteConfigLoader().LoadFromFile(yamlPath, repoRoot));

            Assert.Equal("develop", reloaded.DefaultBranch);
            Assert.Equal("dotnet build", reloaded.BuildCommand);
            Assert.Equal("dotnet test", reloaded.TestCommand);
            Assert.Equal("https://github.com/orgs/example-org/projects/73", reloaded.BacklogUrl);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void AppendJobsite_WithVaultFolders_RoundTripsThemAsAList()
    {
        // The three-part persistence rule, proved end to end: model field, writer
        // block, DTO+loader. Miss any one and /hire writes a Jobsite whose vault
        // write scope silently vanishes on the next process start.
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(repoRoot, "repos", "vaultfolders");
        Directory.CreateDirectory(repoPath);

        var yamlPath = Path.GetTempFileName();
        File.Delete(yamlPath);

        try
        {
            var jobsite = new JobsiteConfig(
                "ConstructionCrew",
                repoPath,
                "The crew's own dogfood jobsite.",
                RepoUrl: null,
                ColorName: null,
                VaultFolders: ["Personal/Projects/ConstructionCrew", "Plans/ConstructionCrew"]);

            JobsiteConfigWriter.AppendJobsite(yamlPath, jobsite, repoRoot);

            var reloaded = new JobsiteConfigLoader().LoadFromFile(yamlPath, repoRoot);

            Assert.Equal(
                ["Personal/Projects/ConstructionCrew", "Plans/ConstructionCrew"],
                Assert.Single(reloaded).VaultFolders);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void AppendJobsite_WithNoVaultFolders_OmitsTheKeyAndReloadsEmpty()
    {
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(repoRoot, "repos", "novaultfolders");
        Directory.CreateDirectory(repoPath);

        var yamlPath = Path.GetTempFileName();
        File.Delete(yamlPath);

        try
        {
            JobsiteConfigWriter.AppendJobsite(yamlPath, new JobsiteConfig("Plain", repoPath, "desc"), repoRoot);

            Assert.DoesNotContain("vaultFolders", File.ReadAllText(yamlPath));
            Assert.Empty(Assert.Single(new JobsiteConfigLoader().LoadFromFile(yamlPath, repoRoot)).VaultFolders!);
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
    public void RemoveJobsite_WithANestedVaultFoldersBlock_ExcisesTheWholeEntry()
    {
        // vaultFolders is the first nested block jobsites.yaml has ever carried.
        // The remover groups lines by the "  - name:" prefix, so a more-indented
        // block belongs to its entry -- pinned here rather than assumed, since
        // /fire depends on it and a half-excised entry is a corrupt file.
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPathA = Path.Combine(repoRoot, "repos", "nested-alpha");
        var repoPathB = Path.Combine(repoRoot, "repos", "nested-beta");
        Directory.CreateDirectory(repoPathA);
        Directory.CreateDirectory(repoPathB);

        var yamlPath = Path.GetTempFileName();
        File.Delete(yamlPath);

        try
        {
            JobsiteConfigWriter.AppendJobsite(
                yamlPath,
                new JobsiteConfig("Alpha", repoPathA, "desc", VaultFolders: ["Notes/Alpha", "Plans/Alpha"]),
                repoRoot);
            JobsiteConfigWriter.AppendJobsite(
                yamlPath,
                new JobsiteConfig("Beta", repoPathB, "desc", VaultFolders: ["Notes/Beta", "Plans/Beta"]),
                repoRoot);

            Assert.True(JobsiteConfigWriter.RemoveJobsite(yamlPath, "Alpha"));

            var text = File.ReadAllText(yamlPath);
            Assert.DoesNotContain("Notes/Alpha", text);
            Assert.DoesNotContain("Plans/Alpha", text);

            var reloaded = new JobsiteConfigLoader().LoadFromFile(yamlPath, repoRoot);
            var beta = Assert.Single(reloaded);
            Assert.Equal("Beta", beta.Name);
            Assert.Equal(["Notes/Beta", "Plans/Beta"], beta.VaultFolders);
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
