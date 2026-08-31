using ConstructionCrew.App.Tui;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.HomeOffice.Tools;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Phase 4's gate: hiring a Foreman produces a sitewalk note in the Vault AND the
/// GC actually receives a live notification of it -- not merely a file appearing.
///
/// HireWizard.Run itself is a blocking Spectre prompt sequence and is not driven
/// here. What is driven is everything it hands off to: the pointer prompt it
/// dispatches, the ordinary StartJob path it dispatches through, the job id that
/// rides in on the task text, and the kind="milestone" file_sitrep that lands in
/// the GC's own conversation.
/// </summary>
public class SitewalkTests
{
    private sealed class FakeForemanDirectory : IForemanDirectory
    {
        private readonly Dictionary<string, ForemanConfig> _byName;

        public FakeForemanDirectory(params ForemanConfig[] foremen) =>
            _byName = foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;
    }

    private static ForemanConfig Foreman() =>
        new("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>(),
            JobsiteName: "Lighthouse", VaultFolders: ["Notes/Lighthouse", "Plans/Lighthouse"]);

    private static ForemanConfig Gc() =>
        new("GC", CrewRole.GC, "fake", "dir", "instructions.md", new Dictionary<string, string>(),
            VaultFolders: ["Notes/GC"]);

    private static JobRegistry NewRegistry(IForemanDirectory foremen, ILocalCliAgentFactory factory) =>
        new(
            foremen,
            new FakeJobsiteDirectory(),
            factory,
            new JobStatusSink(),
            new LiveAgentRegistry(factory),
            "GC",
            new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(
                Path.Combine(Path.GetTempPath(), "cc-sitewalk-state"), TimeSpan.FromSeconds(30)),
            new FakeCliProcessRunner(),
            new HomeOfficeNotificationOptions(null),
            new FakeRunLogWriter(),
            new FakeJobsLogWriter());

    private static string NewVault()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "cc-sitewalk-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(vaultRoot, "Notes", "Lighthouse"));
        return vaultRoot;
    }

    /// <summary>
    /// The dispatched text is a POINTER at the template brief, not a second copy of
    /// it. It names the three things the Foreman must not have to guess -- where the
    /// note goes, that the report is a milestone, and that build_graph closes it --
    /// and stays short enough that the instructions file stays the source of truth.
    /// </summary>
    [Fact]
    public void SitewalkPrompt_PointsAtTheBriefWithoutRestatingIt()
    {
        var prompt = HireWizard.SitewalkPrompt("Frontend", "Lighthouse");

        Assert.Contains("Lighthouse", prompt);
        Assert.Contains("Frontend", prompt);
        Assert.Contains("Notes/Lighthouse/Sitewalk.md", prompt);
        Assert.Contains("milestone", prompt);
        Assert.Contains("build_graph", prompt);
        // Read-only is the one hard constraint on a sitewalk.
        Assert.Contains("Change no code", prompt);
        // A pointer, not the brief: the numbered steps live in the template only.
        Assert.True(prompt.Length < 600, $"Sitewalk prompt is {prompt.Length} chars -- it is drifting into a second copy of the brief.");
    }

    /// <summary>
    /// Phase 9 adds a SECOND dispatch site (<c>/foreman &lt;Name&gt;</c> -> "run sitewalk")
    /// on top of the hire-time one, and both go through this one string. Pinned here
    /// against a jobsite name other than the fixture's, so the note path is proven to
    /// be derived from the jobsite rather than hardcoded.
    /// </summary>
    [Fact]
    public void SitewalkPrompt_NamesTheJobsiteTheNoteAndTheClosingBuildGraph()
    {
        var prompt = HireWizard.SitewalkPrompt("Backend", "Tidepool");

        Assert.Contains("Notes/Tidepool/Sitewalk.md", prompt);
        Assert.Contains("milestone", prompt);
        Assert.Contains("build_graph", prompt);
    }

    /// <summary>
    /// The gate, end to end at the plumbing level: dispatch the sitewalk the way
    /// HireWizard does, take the job id the Foreman can actually READ off its task
    /// text (not the one StartJob returned to the caller), file the milestone sitrep
    /// with it, and assert the GC's own conversation received the notification while
    /// the Foreman's turn was still open.
    /// </summary>
    [Fact]
    public async Task Sitewalk_MilestoneSitrep_ReachesTheGcsOwnConversation()
    {
        var vaultRoot = NewVault();
        var foremen = new FakeForemanDirectory(Foreman(), Gc());
        var factory = new PerNameGatedAgentFactory();
        var frontend = factory.For("Frontend");
        var gc = factory.For("GC");
        var registry = NewRegistry(foremen, factory);
        Action? releaseFrontend = null;

        try
        {
            var (frontendStarted, releaseFrontendTurn) = frontend.ArmNextCall();
            releaseFrontend = releaseFrontendTurn;

            var jobId = registry.StartJob("Frontend", HireWizard.SitewalkPrompt("Frontend", "Lighthouse"));
            await frontendStarted.WaitAsync(TimeSpan.FromSeconds(5));

            // What the Foreman actually sees: its job id, then the sitewalk pointer.
            var task = Assert.Single(frontend.Messages.ToList());
            Assert.StartsWith($"ConstructionCrew job id: {jobId}", task);
            Assert.Contains("Notes/Lighthouse/Sitewalk.md", task);

            // Parse the id back out of the task text -- that is the only channel the
            // Foreman has for it, so parsing it is what proves the round trip.
            var idFromTaskText = task
                .Split('\n')[0]["ConstructionCrew job id: ".Length..]
                .Trim();
            Assert.Equal(jobId, idFromTaskText);

            // The Foreman, mid-turn, files its sitewalk as a milestone.
            var (gcStarted, releaseGc) = gc.ArmNextCall();
            var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions(vaultRoot), registry, new SitrepWriter());
            var sitrep = tool.FileSitrep(
                "Frontend",
                idFromTaskText,
                "summary",
                "milestone",
                "Sitewalk done: Notes/Lighthouse/Sitewalk.md written; build succeeded, 12 tests green.",
                CancellationToken.None);

            // The GC is genuinely woken: this only completes because AskGc opened
            // GC's conversation and is waiting on its turn.
            await gcStarted.WaitAsync(TimeSpan.FromSeconds(5));
            releaseGc();

            var result = await sitrep.WaitAsync(TimeSpan.FromSeconds(10));

            // 1. The note really landed in the Vault.
            var path = Path.Combine(vaultRoot, "Notes", "Lighthouse", "Sitreps", $"{DateTimeOffset.UtcNow:yyyy-MM-dd}-summary.md");
            Assert.True(File.Exists(path));
            Assert.Contains("Sitewalk done", File.ReadAllText(path));

            // 2. And the GC was told, in its own conversation, exactly once.
            var gcMessages = gc.Messages.ToList();
            var notification = Assert.Single(gcMessages);
            Assert.Contains("Frontend", notification);
            Assert.Contains(jobId, notification);
            Assert.Contains("Sitewalk done", notification);
            Assert.Contains(path, notification);

            // 3. And GC's reply came back to the Foreman, so the milestone is a
            //    conversation and not a fire-and-forget write.
            Assert.Contains("GC: done", result);
            Assert.DoesNotContain("parked", result);

            // Exactly two conversations, one each -- never a second, divergent GC.
            Assert.Equal(["Frontend", "GC"], factory.CreateCalls.ToList());
        }
        finally
        {
            releaseFrontend?.Invoke();
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>
    /// The negative half of the same gate: a kind="status" sitrep writes the same
    /// file and notifies nobody. This is why the brief says milestone, and why
    /// "a note appeared in the Vault" is not the gate.
    /// </summary>
    [Fact]
    public async Task Sitewalk_FiledAsStatusInstead_WritesTheFileButNeverReachesTheGc()
    {
        var vaultRoot = NewVault();
        try
        {
            var foremen = new FakeForemanDirectory(Foreman(), Gc());
            var factory = new RecordingAgentFactory();
            var registry = NewRegistry(foremen, factory);
            var jobId = registry.StartJob("Frontend", HireWizard.SitewalkPrompt("Frontend", "Lighthouse"));
            var tool = new FileSitrepTool(foremen, new HomeOfficeVaultOptions(vaultRoot), registry, new SitrepWriter());

            var result = await tool.FileSitrep(
                "Frontend", jobId, "summary", "status", "Sitewalk done", CancellationToken.None);

            Assert.Contains("sitrep filed", result);
            Assert.Null(factory.Existing("GC"));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }
}
