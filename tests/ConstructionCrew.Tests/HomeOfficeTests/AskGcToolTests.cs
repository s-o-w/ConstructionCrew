using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.HomeOffice.Tools;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.HomeOfficeTests;

public class AskGcToolTests
{
    private sealed class FakeForemanDirectory : IForemanDirectory
    {
        private readonly Dictionary<string, ForemanConfig> _byName;

        public FakeForemanDirectory(params ForemanConfig[] foremen) =>
            _byName = foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;
    }

    private static ForemanConfig Foreman(string name) =>
        new(name, CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>(),
            JobsiteName: "XINFRA", VaultFolders: ["Notes/XINFRA", "Plans/XINFRA"]);

    private static ForemanConfig Gc() =>
        new("GC", CrewRole.GC, "fake", "dir", "instructions.md", new Dictionary<string, string>(),
            VaultFolders: ["Notes/GC"]);

    private static JobRegistry NewRegistry(
        IForemanDirectory foremen,
        ILocalCliAgentFactory factory,
        IJobStatusSink sink,
        TimeSpan? askGcTimeout = null) =>
        new(
            foremen,
            new FakeJobsiteDirectory(),
            factory,
            sink,
            new LiveAgentRegistry(factory),
            "GC",
            new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(Path.Combine(Path.GetTempPath(), "cc-askgc-state"), askGcTimeout),
            new FakeCliProcessRunner(),
            new HomeOfficeNotificationOptions(null),
            new FakeRunLogWriter(),
            new FakeJobsLogWriter());

    [Fact]
    public async Task AskGc_UnknownJobId_ThrowsNamingTheTool()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend"), Gc());
        var factory = new RecordingAgentFactory();
        var tool = new AskGcTool(NewRegistry(foremen, factory, new JobStatusSink()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.AskGc("Frontend", "no-such-job", "what now?", CancellationToken.None));

        Assert.Contains("ask_gc", ex.Message);
        Assert.Contains("no-such-job", ex.Message);
        // Never a silent fallback to "the most recent job".
        Assert.Empty(factory.CreateCalls);
    }

    [Fact]
    public async Task AskGc_EmptyJobId_ThrowsNamingTheTool()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend"), Gc());
        var tool = new AskGcTool(NewRegistry(foremen, new RecordingAgentFactory(), new JobStatusSink()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.AskGc("Frontend", "", "what now?", CancellationToken.None));

        Assert.Contains("ask_gc", ex.Message);
    }

    [Fact]
    public async Task AskGc_GcAnswersInTime_ReturnsTheAnswerAndNeverParks()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend"), Gc());
        var factory = new RecordingAgentFactory();
        factory.For("GC").Reply = "ship it";
        var registry = NewRegistry(foremen, factory, new JobStatusSink(), TimeSpan.FromSeconds(30));
        var jobId = registry.StartJob("Frontend", "work");

        var answer = await new AskGcTool(registry).AskGc("Frontend", jobId, "ship it?", CancellationToken.None);

        Assert.Equal("ship it", answer);
        Assert.NotEqual(JobStatus.Parked, registry.GetJob(jobId)!.Status);
    }

    /// <summary>
    /// The park/resume round trip. GC is gated open past the (deliberately tiny)
    /// timeout, so ask_gc returns "parked: waiting on Boss" without throwing and
    /// without hanging the Foreman's turn -- then GC's reply landing, and nothing
    /// else, resumes the job and folds the park interval into ParkedDuration.
    /// </summary>
    [Fact]
    public async Task AskGc_GcDoesNotAnswerInTime_ParksThenResumesWhenTheReplyLands()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend"), Gc());
        var factory = new PerNameGatedAgentFactory();
        var frontend = factory.For("Frontend");
        var gc = factory.For("GC");
        var sink = new JobStatusSink();
        var registry = NewRegistry(foremen, factory, sink, TimeSpan.FromMilliseconds(100));

        var (frontendStarted, releaseFrontend) = frontend.ArmNextCall();
        var (gcStarted, releaseGc) = gc.ArmNextCall();
        var jobId = registry.StartJob("Frontend", "work");

        try
        {
            // The job has to actually be running before it can be parked.
            await frontendStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Running, registry.GetJob(jobId)!.Status);

            var answer = await new AskGcTool(registry).AskGc("Frontend", jobId, "what now?", CancellationToken.None);

            Assert.Equal("parked: waiting on Boss", answer);
            Assert.Equal(JobStatus.Parked, registry.GetJob(jobId)!.Status);

            // What the roster renders on: not busy, but not free either.
            Assert.False(registry.IsForemanBusy("Frontend"));
            Assert.True(registry.IsForemanParked("Frontend"));

            // The GC turn is still in flight -- the timeout bounded the WAIT, not
            // the send. That is what makes a resume possible at all.
            await gcStarted.WaitAsync(TimeSpan.FromSeconds(5));
            releaseGc();

            var resumed = await DrainUntil(sink, r => r.JobId == jobId && r.Status == JobStatus.Running &&
                                                      r.ParkedDuration > TimeSpan.Zero);

            Assert.Equal(JobStatus.Running, registry.GetJob(jobId)!.Status);
            Assert.True(registry.GetJob(jobId)!.ParkedDuration > TimeSpan.Zero);
            // StartedAt is stamped once, at real dispatch -- a resume never re-stamps it.
            Assert.Equal(registry.GetJob(jobId)!.StartedAt, resumed.StartedAt);
        }
        finally
        {
            releaseGc();
            releaseFrontend();
        }
    }

    /// <summary>
    /// A job that finished while parked is left exactly as it is: no transition
    /// back to Running, no throw, no error.
    /// </summary>
    [Fact]
    public async Task AskGc_JobCompletedWhileParked_ResumeIsANoOp()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend"), Gc());
        var factory = new PerNameGatedAgentFactory();
        var frontend = factory.For("Frontend");
        var gc = factory.For("GC");
        var sink = new JobStatusSink();
        var registry = NewRegistry(foremen, factory, sink, TimeSpan.FromMilliseconds(100));

        var (frontendStarted, releaseFrontend) = frontend.ArmNextCall();
        var (gcStarted, releaseGc) = gc.ArmNextCall();
        var jobId = registry.StartJob("Frontend", "work");

        try
        {
            await frontendStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await new AskGcTool(registry).AskGc("Frontend", jobId, "what now?", CancellationToken.None);
            Assert.Equal(JobStatus.Parked, registry.GetJob(jobId)!.Status);

            // The Foreman's own turn ends while it is still parked.
            releaseFrontend();
            await DrainUntil(sink, r => r.JobId == jobId && r.Status == JobStatus.Completed);

            await gcStarted.WaitAsync(TimeSpan.FromSeconds(5));
            releaseGc();

            // A no-op publishes nothing, so there is nothing to wait FOR. Hold the
            // negative assertion open instead: if the still-Parked guard is ever
            // dropped, the status flips to Running inside this window.
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
            while (DateTime.UtcNow < deadline)
            {
                Assert.Equal(JobStatus.Completed, registry.GetJob(jobId)!.Status);
                await Task.Delay(25);
            }
        }
        finally
        {
            releaseGc();
            releaseFrontend();
        }
    }

    /// <summary>
    /// A kind:"milestone" file_sitrep and a direct ask_gc must be the SAME code
    /// path: one JobRegistry.AskGc, one GC conversation. Two Create("GC") calls, or
    /// a message that never reached the GC agent, would mean a divergent
    /// conversation -- exactly what LiveAgentRegistry exists to prevent.
    /// </summary>
    [Fact]
    public async Task AskGcAndMilestoneSitrep_ShareOneGcConversation()
    {
        var vaultRoot = TestVaultRoot();
        try
        {
            var caller = Foreman("Frontend");
            var foremen = new FakeForemanDirectory(caller, Gc());
            var factory = new RecordingAgentFactory();
            factory.For("GC").Reply = "acknowledged";
            var registry = NewRegistry(foremen, factory, new JobStatusSink(), TimeSpan.FromSeconds(30));
            var jobId = registry.StartJob("Frontend", "work");

            var direct = await new AskGcTool(registry).AskGc("Frontend", jobId, "direct question", CancellationToken.None);

            var sitrep = new FileSitrepTool(
                foremen, new HomeOfficeVaultOptions(vaultRoot), registry, new SitrepWriter());
            var viaSitrep = await sitrep.FileSitrep(
                "Frontend", jobId, "summary", "milestone", "plan settled", CancellationToken.None);

            Assert.Equal("acknowledged", direct);
            Assert.Contains("acknowledged", viaSitrep);

            // Exactly one GC agent was ever created, and it saw both questions.
            Assert.Single(factory.CreateCalls, n => n.Equals("GC", StringComparison.OrdinalIgnoreCase));
            var gcMessages = factory.For("GC").Messages.ToList();
            Assert.Equal(2, gcMessages.Count);
            Assert.Contains("direct question", gcMessages[0]);
            Assert.Contains("plan settled", gcMessages[1]);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    internal static string TestVaultRoot()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "cc-sitrep-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(vaultRoot, "Notes", "XINFRA"));
        return vaultRoot;
    }

    /// <summary>
    /// Reads published transitions until one matches, or fails the test on a
    /// bounded timeout rather than hanging the run.
    /// </summary>
    internal static async Task<JobRecord> DrainUntil(JobStatusSink sink, Func<JobRecord, bool> match)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            var record = await sink.Reader.ReadAsync(cts.Token);
            if (match(record))
            {
                return record;
            }
        }
    }
}
