using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.HomeOffice.Tools;
using ConstructionCrew.Providers;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.HomeOfficeTests;

public class DispatchTaskToolTests
{
    private sealed class FakeForemanDirectory : IForemanDirectory
    {
        private readonly Dictionary<string, ForemanConfig> _byName;

        public FakeForemanDirectory(params ForemanConfig[] foremen) =>
            _byName = foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;
    }

    private sealed class FakeJobsiteDirectory : IJobsiteDirectory
    {
        private readonly Dictionary<string, JobsiteConfig> _byName;

        public FakeJobsiteDirectory(params JobsiteConfig[] jobsites) =>
            _byName = jobsites.ToDictionary(j => j.Name, StringComparer.OrdinalIgnoreCase);

        public JobsiteConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<JobsiteConfig> All() => _byName.Values;
    }

    /// <summary>Proves the null-VaultRoot guard runs BEFORE the reader is ever touched.</summary>
    private sealed class ExplodingWorkorderReader : IWorkorderReader
    {
        public ParsedWorkorder Read(string path, string vaultRoot) =>
            throw new InvalidOperationException("the reader must not be reached when no Vault is configured");
    }

    private static ForemanConfig Foreman(string name, string? jobsiteName) =>
        new(name, CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>(), JobsiteName: jobsiteName);

    private static JobRegistry NewRegistry(IForemanDirectory foremen)
    {
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner());
        return new JobRegistry(foremen, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");
    }

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
        var vaultRoot = Path.Combine(Path.GetTempPath(), "cc-dispatch-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(vaultRoot);
        return vaultRoot;
    }

    [Fact]
    public void DispatchTask_WithWorkorder_ButNoVaultConfigured_RejectsBeforeReadingAnything()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend", "XINFRA"));
        var tool = new DispatchTaskTool(
            NewRegistry(foremen),
            new ExplodingWorkorderReader(),
            foremen,
            new FakeJobsiteDirectory(),
            new HomeOfficeVaultOptions(null));

        var ex = Assert.Throws<InvalidOperationException>(
            () => tool.DispatchTask("Frontend", "do it", "/anywhere/Plans/XINFRA/f/WORKORDER.md"));

        Assert.Contains("dispatch_task", ex.Message);
        Assert.Contains("Vault", ex.Message);
        // The exploding reader's own message would be a different failure entirely.
        Assert.DoesNotContain("must not be reached", ex.Message);
    }

    /// <summary>An unconfigured Vault must never block ordinary ad-hoc dispatch.</summary>
    [Fact]
    public void DispatchTask_WithoutWorkorder_NeverTouchesVaultOptions()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend", "XINFRA"));
        var tool = new DispatchTaskTool(
            NewRegistry(foremen),
            new ExplodingWorkorderReader(),
            foremen,
            new FakeJobsiteDirectory(),
            new HomeOfficeVaultOptions(null));

        var jobId = tool.DispatchTask("Frontend", "answer a quick question");

        Assert.False(string.IsNullOrWhiteSpace(jobId));
    }

    [Fact]
    public void DispatchTask_WorkorderJobsiteDoesNotMatchTargetForeman_IsRejected()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = WriteWorkorder(vaultRoot, "XINFRA", "named-graphs", "feature: named-graphs\njobsite: XINFRA");
            // Backend is assigned to a different jobsite than the workorder names.
            var foremen = new FakeForemanDirectory(Foreman("Backend", "SDS-BSD"));
            var tool = new DispatchTaskTool(
                NewRegistry(foremen),
                new WorkorderReader(),
                foremen,
                new FakeJobsiteDirectory(),
                new HomeOfficeVaultOptions(vaultRoot));

            var ex = Assert.Throws<InvalidOperationException>(() => tool.DispatchTask("Backend", "do it", path));

            Assert.Contains("XINFRA", ex.Message);
            Assert.Contains("SDS-BSD", ex.Message);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void DispatchTask_WithWorkorder_ClaimsTheSlotAndResolvesTheSourceBranchChain()
    {
        var vaultRoot = NewVault();
        try
        {
            var repoPath = Path.Combine(vaultRoot, "repo");
            Directory.CreateDirectory(repoPath);
            var path = WriteWorkorder(vaultRoot, "XINFRA", "named-graphs", "feature: named-graphs\njobsite: XINFRA");

            var foremen = new FakeForemanDirectory(Foreman("Frontend", "XINFRA"));
            var registry = NewRegistry(foremen);
            var tool = new DispatchTaskTool(
                registry,
                new WorkorderReader(),
                foremen,
                // No sourceBranch in the workorder -> the jobsite's DefaultBranch wins.
                new FakeJobsiteDirectory(new JobsiteConfig("XINFRA", repoPath, "desc", DefaultBranch: "develop")),
                new HomeOfficeVaultOptions(vaultRoot));

            var jobId = tool.DispatchTask("Frontend", "do it", path);

            var claimed = registry.GetJobWorkorder(jobId);
            Assert.NotNull(claimed);
            Assert.Equal("named-graphs", claimed!.Feature);
            Assert.Equal("XINFRA", claimed.Jobsite);
            Assert.Equal(Path.Combine(vaultRoot, "Plans", "XINFRA", "named-graphs"), claimed.PlansFolder);
            Assert.Equal("develop", claimed.SourceBranch);
            Assert.Equal("feature/named-graphs", claimed.FeatureBranch);
            Assert.Equal(jobId, registry.GetWorkorderSlotOwner("Frontend"));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>No jobsite entry and no sourceBranch: the chain bottoms out at "main".</summary>
    [Fact]
    public void DispatchTask_NoSourceBranchAndNoDefaultBranch_FallsBackToMain()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = WriteWorkorder(vaultRoot, "XINFRA", "named-graphs", "feature: named-graphs\njobsite: XINFRA");
            var foremen = new FakeForemanDirectory(Foreman("Frontend", "XINFRA"));
            var registry = NewRegistry(foremen);
            var tool = new DispatchTaskTool(
                registry,
                new WorkorderReader(),
                foremen,
                new FakeJobsiteDirectory(),
                new HomeOfficeVaultOptions(vaultRoot));

            var jobId = tool.DispatchTask("Frontend", "do it", path);

            Assert.Equal("main", registry.GetJobWorkorder(jobId)!.SourceBranch);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }
}
