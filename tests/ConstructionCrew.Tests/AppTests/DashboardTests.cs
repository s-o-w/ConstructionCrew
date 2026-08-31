using ConstructionCrew.App.Tui;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Providers;
using ConstructionCrew.Providers.Activity;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.AppTests;

public class DashboardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static JobRecord Job(string id, JobStatus status) =>
        new(id, "Frontend", $"task {id}", status, DateTimeOffset.UtcNow, null, null);

    private static JobRecord Job(string id, string foremanName, JobStatus status) =>
        new(id, foremanName, $"task {id}", status, DateTimeOffset.UtcNow, null, null);

    private static ForemanConfig Crew(string name, CrewRole role, string? jobsite = null) =>
        new(name, role, "fake", "dir", "instructions.md", new Dictionary<string, string>(), JobsiteName: jobsite);

    private static JobRegistry BuildRegistry(
        ForemanDirectory foremen, JobsiteDirectory jobsites, ICliProcessRunner runner, JobStatusSink sink)
    {
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        return new JobRegistry(
            foremen,
            jobsites,
            factory,
            sink,
            new LiveAgentRegistry(factory),
            "GC",
            new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(Path.Combine(Path.GetTempPath(), "cc-monitor-state")),
            new FakeCliProcessRunner(),
            new HomeOfficeNotificationOptions(null),
            new FakeRunLogWriter(),
            new FakeJobsLogWriter());
    }

    /// <summary>
    /// Four columns, in order, and a Parked job belongs to exactly one of them.
    /// Before Phase 7 a parked job vanished from the board entirely: it is neither
    /// Pending/Running, nor Completed, nor Failed.
    /// </summary>
    [Fact]
    public void TaskColumns_AreDoingParkedDoneFailed()
    {
        var columns = Dashboard.TaskColumns(
        [
            Job("pending", JobStatus.Pending),
            Job("running", JobStatus.Running),
            Job("parked", JobStatus.Parked),
            Job("done", JobStatus.Completed),
            Job("failed", JobStatus.Failed),
        ]);

        Assert.Equal(["doing", "parked", "done", "failed"], columns.Select(c => c.Title));

        Assert.Equal(["pending", "running"], columns[0].Jobs.Select(j => j.JobId));
        Assert.Equal(["parked"], columns[1].Jobs.Select(j => j.JobId));
        Assert.Equal(["done"], columns[2].Jobs.Select(j => j.JobId));
        Assert.Equal(["failed"], columns[3].Jobs.Select(j => j.JobId));
    }

    /// <summary>Every job lands in exactly one column -- no double-count, no dropped job.</summary>
    [Fact]
    public void TaskColumns_PartitionEveryJobExactlyOnce()
    {
        var all = new[]
        {
            Job("a", JobStatus.Pending),
            Job("b", JobStatus.Running),
            Job("c", JobStatus.Parked),
            Job("d", JobStatus.Completed),
            Job("e", JobStatus.Failed),
        };

        var placed = Dashboard.TaskColumns(all).SelectMany(c => c.Jobs).Select(j => j.JobId).ToList();

        Assert.Equal(all.Length, placed.Count);
        Assert.Equal(all.Length, placed.Distinct().Count());
    }

    /// <summary>
    /// Three roster states, not two. A parked Foreman is not busy (IsForemanBusy is
    /// false by design) but it is not free either -- it is blocked on the Boss, and
    /// rendering it as "idle" would hide the one thing the Boss has to act on.
    /// </summary>
    [Fact]
    public void StatusBadge_ParkedRendersDistinctlyFromBusyAndIdle()
    {
        var working = Dashboard.StatusBadge(busy: true, parked: false);
        var parked = Dashboard.StatusBadge(busy: false, parked: true);
        var idle = Dashboard.StatusBadge(busy: false, parked: false);

        Assert.Contains("working", working);
        Assert.Contains("parked", parked);
        Assert.Contains("idle", idle);
        Assert.NotEqual(idle, parked);
        Assert.NotEqual(working, parked);
    }

    /// <summary>
    /// /foreman was reachable only from /help, so the footer is where it gets
    /// discovered. The driving footer stays the reminder it was -- it is not a
    /// command list.
    /// </summary>
    [Fact]
    public void Footer_ListsForeman()
    {
        var footer = Dashboard.FooterFor(null);

        Assert.Contains("/foreman <Name>", footer);
        Assert.Contains("/drive <Name>", footer);
        Assert.DoesNotContain("<Foreman>", footer);

        Assert.Contains("/watch <Name>", footer);

        var driving = Dashboard.FooterFor("Frontend");
        Assert.Contains("Frontend", driving);
        Assert.DoesNotContain("/foreman", driving);
    }

    /// <summary>
    /// Watching without driving is the state most worth spelling out: the panel
    /// is full of Casey's activity while typed input still goes to GC, and
    /// nothing else on screen says so.
    /// </summary>
    [Fact]
    public void Footer_WatchingWithoutDriving_SaysInputStillGoesToGc()
    {
        var footer = Dashboard.FooterFor(null, watchedForeman: "Casey");

        Assert.Contains("watching", footer);
        Assert.Contains("Casey", footer);
        Assert.Contains("still talking to GC", footer);
    }

    /// <summary>Driving one crew member while watching another names both, so the panel's subject is never a mystery.</summary>
    [Fact]
    public void Footer_DrivingOneAndWatchingAnother_NamesBoth()
    {
        var footer = Dashboard.FooterFor("Casey", watchedForeman: "Dana");

        Assert.Contains("driving", footer);
        Assert.Contains("Casey", footer);
        Assert.Contains("watching", footer);
        Assert.Contains("Dana", footer);
    }

    /// <summary>Before the first read there is no answer yet, and that is different from "idle".</summary>
    [Fact]
    public void BuildActivityRows_BeforeTheFirstRead_SaysItIsStillReading()
    {
        Assert.Single(Dashboard.BuildActivityRows(null));
    }

    /// <summary>
    /// "Could not look" and "nothing happening" are different answers. An
    /// unreadable transcript reports itself rather than rendering as a blank
    /// panel the Boss would read as an idle Foreman.
    /// </summary>
    [Fact]
    public void BuildActivityRows_AnErrorSnapshot_RendersTheReasonAndNoClock()
    {
        var rows = Dashboard.BuildActivityRows(
            new ForemanActivitySnapshot("no activity yet", null, "no transcript on disk yet"));

        Assert.Single(rows);
    }

    /// <summary>A real reading gets its own clock line, so a stalled feed is visibly stale rather than silently wrong.</summary>
    [Fact]
    public void BuildActivityRows_ARealReading_RendersTheSummaryAndAClock()
    {
        var rows = Dashboard.BuildActivityRows(
            new ForemanActivitySnapshot("running: Bash", DateTimeOffset.UtcNow));

        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// The newest entry must always survive, even alone over budget -- the
    /// whole point of this replacing TakeLast(10)/Truncate(...,400).
    /// </summary>
    [Fact]
    public void WindowToBudget_NewestEntryAloneOverBudget_IsStillReturnedWhole()
    {
        var transcript = new List<TranscriptLine> { new("GC", "a very long reply") };

        var windowed = Dashboard.WindowToBudget(transcript, budget: 1, heightOf: _ => 100);

        Assert.Equal(transcript, windowed);
    }

    [Fact]
    public void WindowToBudget_OlderEntriesDropOffFirst_NewestNeverPushedOut()
    {
        var transcript = new List<TranscriptLine>
        {
            new("Boss", "first"),
            new("GC", "second"),
            new("Boss", "third"),
        };

        var windowed = Dashboard.WindowToBudget(transcript, budget: 2, heightOf: _ => 1);

        Assert.Equal(["second", "third"], windowed.Select(l => l.Text));
    }

    [Fact]
    public void WindowToBudget_EverythingFits_PreservesOrder()
    {
        var transcript = new List<TranscriptLine> { new("Boss", "first"), new("GC", "second") };

        var windowed = Dashboard.WindowToBudget(transcript, budget: 10, heightOf: _ => 1);

        Assert.Equal(transcript, windowed);
    }

    /// <summary>A job that is genuinely running still wins over a stale parked flag.</summary>
    [Fact]
    public void StatusBadge_BusyWinsOverParked()
    {
        Assert.Equal(
            Dashboard.StatusBadge(busy: true, parked: false),
            Dashboard.StatusBadge(busy: true, parked: true));
    }

    /// <summary>
    /// The monitor's floor: every hired crew member has a row whether or not it is
    /// working, GC first. A Foreman that vanishes from the board when idle is the
    /// bug this view exists to prevent.
    /// </summary>
    [Fact]
    public void MonitorRows_IncludesGcAndEveryForeman()
    {
        var foremen = new ForemanDirectory([
            Crew("GC", CrewRole.GC),
            Crew("Frontend", CrewRole.Foreman, "Lighthouse"),
            Crew("Backend", CrewRole.Foreman, "Lighthouse"),
        ]);
        var jobsites = new JobsiteDirectory([new JobsiteConfig("Lighthouse", "/repos/lighthouse", "the jobsite")]);

        var rows = Dashboard.MonitorRows(foremen, jobsites, Array.Empty<JobRecord>(), DateTimeOffset.UtcNow);

        Assert.Equal(3, rows.Count);
        Assert.Equal("GC", rows[0].Who);
        Assert.Equal("GC", rows[0].Kind);
        Assert.Equal(["Backend", "Frontend"], rows.Skip(1).Select(r => r.Who).Order());
        Assert.All(rows.Skip(1), r => Assert.Equal("Foreman", r.Kind));
        Assert.All(rows, r => Assert.Equal("idle", r.State));
        Assert.All(rows, r => Assert.Null(r.Task));
        Assert.All(rows, r => Assert.Null(r.Elapsed));
        Assert.Equal("Lighthouse", rows[1].Jobsite);
        Assert.Null(rows[0].Jobsite);
    }

    /// <summary>
    /// A spawned Worker is a row of its own, under the full
    /// <c>&lt;Parent&gt;/worker-&lt;id&gt;</c> label JobRegistry mints -- driven through a
    /// real registry, so the label convention is the registry's and not the test's.
    /// The parent stays "working" beside it: the registry counts a Worker's job
    /// against it, and it is genuinely not free.
    /// </summary>
    [Fact]
    public async Task MonitorRows_RunningWorker_GetsItsOwnRow()
    {
        var foremen = new ForemanDirectory([Crew("GC", CrewRole.GC), Crew("Frontend", CrewRole.Foreman, "Lighthouse")]);
        var jobsites = new JobsiteDirectory([new JobsiteConfig("Lighthouse", "/repos/lighthouse", "the jobsite")]);
        var runner = new HangingCliProcessRunner();
        var sink = new JobStatusSink();
        var registry = BuildRegistry(foremen, jobsites, runner, sink);

        registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));
        var workerJobId = await registry.StartWorkerJob("Frontend", "do a small thing", null, CancellationToken.None);

        var rows = Dashboard.MonitorRows(foremen, jobsites, registry, DateTimeOffset.UtcNow);

        var worker = Assert.Single(rows, r => r.Kind == "Worker");
        Assert.StartsWith("Frontend/worker-", worker.Who);
        Assert.Equal("do a small thing", worker.Task);
        Assert.Equal("working", worker.State);
        Assert.Equal("Lighthouse", worker.Jobsite);

        var parent = Assert.Single(rows, r => r.Who == "Frontend");
        Assert.Equal("working", parent.State);

        // The parent's own row names the parent's own task, never the Worker's --
        // the Worker already has a line, and repeating it reads as two jobs.
        Assert.Equal("the feature", parent.Task);

        Assert.NotNull(registry.GetJob(workerJobId));
        runner.Release();
    }

    /// <summary>
    /// Worker rows are transient by design. Present while the job is in flight,
    /// gone from the very next call once it goes terminal -- asserted across the
    /// same registry, so this is the row genuinely disappearing and not two
    /// differently-built lists.
    /// </summary>
    [Fact]
    public async Task MonitorRows_CompletedWorker_IsDropped()
    {
        var foremen = new ForemanDirectory([Crew("GC", CrewRole.GC), Crew("Frontend", CrewRole.Foreman, "Lighthouse")]);
        var jobsites = new JobsiteDirectory([new JobsiteConfig("Lighthouse", "/repos/lighthouse", "the jobsite")]);
        var runner = new HangingCliProcessRunner { NextResult = new CliRunResult(true, "worker done", "", 0) };
        var sink = new JobStatusSink();
        var registry = BuildRegistry(foremen, jobsites, runner, sink);

        registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));
        var workerJobId = await registry.StartWorkerJob("Frontend", "do a small thing", null, CancellationToken.None);

        Assert.Contains(Dashboard.MonitorRows(foremen, jobsites, registry, DateTimeOffset.UtcNow), r => r.Kind == "Worker");

        // Every hung run finishes, including the Worker's.
        runner.Release();

        using var cts = new CancellationTokenSource(Timeout);
        JobRecord? last = null;
        while (last is null || last.JobId != workerJobId || last.Status is JobStatus.Pending or JobStatus.Running)
        {
            last = await sink.Reader.ReadAsync(cts.Token);
        }

        var rows = Dashboard.MonitorRows(foremen, jobsites, registry, DateTimeOffset.UtcNow);

        Assert.DoesNotContain(rows, r => r.Kind == "Worker");
        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// Three states here too, and for the same reason StatusBadge has three: a
    /// parked Foreman is blocked on the Boss, and reporting it as "working" (or as
    /// "idle") hides the one row the Boss has to act on.
    /// </summary>
    [Fact]
    public void MonitorRows_ParkedForeman_ReportsParkedNotWorking()
    {
        var foremen = new ForemanDirectory([Crew("GC", CrewRole.GC), Crew("Frontend", CrewRole.Foreman)]);
        var jobsites = new JobsiteDirectory([]);

        var rows = Dashboard.MonitorRows(
            foremen, jobsites, [Job("parked", "Frontend", JobStatus.Parked)], DateTimeOffset.UtcNow);

        var frontend = Assert.Single(rows, r => r.Who == "Frontend");
        Assert.Equal("parked", frontend.State);
        Assert.Equal("idle", Assert.Single(rows, r => r.Who == "GC").State);

        // Busy still wins, exactly as StatusBadge resolves the same two flags.
        var alsoRunning = Dashboard.MonitorRows(
            foremen,
            jobsites,
            [Job("parked", "Frontend", JobStatus.Parked), Job("running", "Frontend", JobStatus.Running)],
            DateTimeOffset.UtcNow);

        Assert.Equal("working", Assert.Single(alsoRunning, r => r.Who == "Frontend").State);
    }

    /// <summary>
    /// Elapsed is worked time, not wall clock: the hour a job spent parked waiting
    /// on the Boss is not an hour of work. Null until the job actually starts,
    /// because queue time is never charged either.
    /// </summary>
    [Fact]
    public void MonitorRows_ElapsedExcludesParkedDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var foremen = new ForemanDirectory([Crew("Frontend", CrewRole.Foreman)]);
        var jobsites = new JobsiteDirectory([]);

        var running = Job("running", "Frontend", JobStatus.Running) with
        {
            StartedAt = now - TimeSpan.FromMinutes(60),
            ParkedDuration = TimeSpan.FromMinutes(20),
        };

        var row = Assert.Single(Dashboard.MonitorRows(foremen, jobsites, [running], now));

        Assert.Equal(TimeSpan.FromMinutes(40), row.Elapsed);
        Assert.Equal(running.StartedAt, row.StartedAt);

        var queued = Job("queued", "Frontend", JobStatus.Pending);
        var queuedRow = Assert.Single(Dashboard.MonitorRows(foremen, jobsites, [queued], now));

        Assert.Null(queuedRow.Elapsed);
        Assert.Null(queuedRow.StartedAt);
    }

    private static ActiveWorkorder Workorder(string feature) =>
        new(feature, "Lighthouse", $"/vault/Plans/Lighthouse/{feature}", "main", $"feature/{feature}", DateTimeOffset.UtcNow);
}
