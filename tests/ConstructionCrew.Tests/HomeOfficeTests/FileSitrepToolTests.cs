using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.HomeOffice.Tools;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.HomeOfficeTests;

public class FileSitrepToolTests
{
    private sealed class FakeForemanDirectory : IForemanDirectory
    {
        private readonly Dictionary<string, ForemanConfig> _byName;

        public FakeForemanDirectory(params ForemanConfig[] foremen) =>
            _byName = foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;
    }

    private static ForemanConfig Foreman(string name = "Frontend") =>
        new(name, CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>(),
            JobsiteName: "XINFRA", VaultFolders: ["Notes/XINFRA", "Plans/XINFRA"]);

    private static ForemanConfig Gc() =>
        new("GC", CrewRole.GC, "fake", "dir", "instructions.md", new Dictionary<string, string>(),
            VaultFolders: ["Notes/GC"]);

    private static ActiveWorkorder Workorder(string feature = "named-graphs") =>
        new(feature, "XINFRA", $"/vault/Plans/XINFRA/{feature}", "main", $"feature/{feature}", DateTimeOffset.UtcNow);

    private static string NewVault()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "cc-filesitrep-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(vaultRoot, "Notes", "XINFRA"));
        return vaultRoot;
    }

    private static JobRegistry NewRegistry(IForemanDirectory foremen, ILocalCliAgentFactory factory, IJobStatusSink? sink = null) =>
        new(
            foremen,
            new FakeJobsiteDirectory(),
            factory,
            sink ?? new JobStatusSink(),
            new LiveAgentRegistry(factory),
            "GC",
            new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(Path.Combine(Path.GetTempPath(), "cc-filesitrep-state"), TimeSpan.FromSeconds(30)));

    [Fact]
    public async Task FileSitrep_KindStatus_WritesTheFileAndNeverAsksTheGc()
    {
        var vaultRoot = NewVault();
        try
        {
            var foremen = new FakeForemanDirectory(Foreman(), Gc());
            var factory = new RecordingAgentFactory();
            var registry = NewRegistry(foremen, factory);
            var jobId = registry.StartJob("Frontend", "work");
            var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions(vaultRoot), registry, new SitrepWriter());

            var result = await tool.FileSitrep("Frontend", jobId, "summary", "status", "still going", CancellationToken.None);

            var path = Path.Combine(vaultRoot, "Notes", "XINFRA", "Sitreps", $"{DateTimeOffset.UtcNow:yyyy-MM-dd}-summary.md");
            Assert.True(File.Exists(path));
            Assert.Contains(path, result);
            Assert.Contains("still going", File.ReadAllText(path));
            // No GC conversation was ever opened.
            Assert.Null(factory.Existing("GC"));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FileSitrep_KindMilestone_AsksTheGcExactlyOnceAndSurfacesTheReply()
    {
        var vaultRoot = NewVault();
        try
        {
            var foremen = new FakeForemanDirectory(Foreman(), Gc());
            var factory = new RecordingAgentFactory();
            factory.For("GC").Reply = "go ahead";
            var registry = NewRegistry(foremen, factory);
            var jobId = registry.StartJob("Frontend", "work");
            var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions(vaultRoot), registry, new SitrepWriter());

            var result = await tool.FileSitrep(
                "Frontend", jobId, "summary", "milestone", "plan settled after two review rounds", CancellationToken.None);

            Assert.Contains("go ahead", result);
            var gcMessages = factory.For("GC").Messages.ToList();
            Assert.Single(gcMessages);
            Assert.Contains("plan settled after two review rounds", gcMessages[0]);
            Assert.Contains(jobId, gcMessages[0]);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>
    /// pr-opened frees the Foreman's workorder slot -- identified by JOB ID, not by
    /// Foreman name -- and raises exactly one notification. It never asks the GC:
    /// the GC learns of a PR opportunistically.
    /// </summary>
    [Fact]
    public async Task FileSitrep_KindPrOpened_ReleasesTheSlotNotifiesOnceAndNeverAsksTheGc()
    {
        var vaultRoot = NewVault();
        try
        {
            var foremen = new FakeForemanDirectory(Foreman(), Gc());
            var factory = new RecordingAgentFactory();
            var registry = NewRegistry(foremen, factory);
            var jobId = registry.StartJob("Frontend", "the feature", Workorder());
            Assert.Equal(jobId, registry.GetWorkorderSlotOwner("Frontend"));

            var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions(vaultRoot), registry, new SitrepWriter());
            await tool.FileSitrep("Frontend", jobId, "summary", "pr-opened", "PR #12 is open", CancellationToken.None);

            Assert.Null(registry.GetWorkorderSlotOwner("Frontend"));
            var notification = Assert.Single(registry.PrOpenedNotifications);
            Assert.Equal(jobId, notification.JobId);
            Assert.Equal("Frontend", notification.ForemanName);
            Assert.Null(factory.Existing("GC"));

            // Freed immediately: the next workorder goes straight through.
            var next = registry.StartJob("Frontend", "next feature", Workorder("shacl-shapes"));
            Assert.Equal(next, registry.GetWorkorderSlotOwner("Frontend"));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>
    /// Regression: releasing the slot at PR time must not cost the job the workorder
    /// it is still working on. The job's ActiveWorkorder (and its PlansFolder) stays
    /// reachable right through completion, which is what Phase 10's run-log append
    /// depends on -- Phase 10 adds the IRunLogWriter.Append assertion on top of this.
    /// </summary>
    [Fact]
    public async Task FileSitrep_KindPrOpened_ThenTheJobCompletes_StillKnowsItsWorkorder()
    {
        var vaultRoot = NewVault();
        try
        {
            var foremen = new FakeForemanDirectory(Foreman(), Gc());
            var factory = new PerNameGatedAgentFactory();
            var frontend = factory.For("Frontend");
            var sink = new JobStatusSink();
            var registry = NewRegistry(foremen, factory, sink);

            var (started, release) = frontend.ArmNextCall();
            var jobId = registry.StartJob("Frontend", "the feature", Workorder());
            await started.WaitAsync(TimeSpan.FromSeconds(5));

            var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions(vaultRoot), registry, new SitrepWriter());
            await tool.FileSitrep("Frontend", jobId, "summary", "pr-opened", "PR #12 is open", CancellationToken.None);
            Assert.Null(registry.GetWorkorderSlotOwner("Frontend"));

            release();
            var completed = await AskGcToolTests.DrainUntil(sink, r => r.JobId == jobId && r.Status == JobStatus.Completed);

            Assert.Equal(jobId, completed.JobId);
            var workorder = registry.GetJobWorkorder(jobId);
            Assert.NotNull(workorder);
            Assert.Equal("/vault/Plans/XINFRA/named-graphs", workorder!.PlansFolder);
            // Still clear: completion must not resurrect the released slot.
            Assert.Null(registry.GetWorkorderSlotOwner("Frontend"));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FileSitrep_UnknownForeman_ThrowsNamingTheTool()
    {
        var foremen = new FakeForemanDirectory(Foreman(), Gc());
        var registry = NewRegistry(foremen, new RecordingAgentFactory());
        var jobId = registry.StartJob("Frontend", "work");
        var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions("/vault"), registry, new SitrepWriter());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.FileSitrep("Nope", jobId, "summary", "status", "body", CancellationToken.None));

        Assert.Contains("file_sitrep", ex.Message);
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public async Task FileSitrep_UnknownJobId_ThrowsNamingTheTool()
    {
        var foremen = new FakeForemanDirectory(Foreman(), Gc());
        var tool = new FileSitrepTool(
            foremen, new HomeOfficeVaultOptions("/vault"), NewRegistry(foremen, new RecordingAgentFactory()), new SitrepWriter());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.FileSitrep("Frontend", "no-such-job", "summary", "status", "body", CancellationToken.None));

        Assert.Contains("file_sitrep", ex.Message);
        Assert.Contains("no-such-job", ex.Message);
    }

    [Theory]
    [InlineData("sideways", "status")]
    [InlineData("summary", "pr-closed")]
    public async Task FileSitrep_InvalidAltitudeOrKind_ThrowsNamingTheTool(string altitude, string kind)
    {
        var foremen = new FakeForemanDirectory(Foreman(), Gc());
        var registry = NewRegistry(foremen, new RecordingAgentFactory());
        var jobId = registry.StartJob("Frontend", "work");
        var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions("/vault"), registry, new SitrepWriter());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.FileSitrep("Frontend", jobId, altitude, kind, "body", CancellationToken.None));

        Assert.Contains("file_sitrep", ex.Message);
    }
}
