using System.Text.Json;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.Providers;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.HomeOfficeTests;

public class JobRegistryTests
{
    /// <summary>
    /// Keeps every pre-Phase-6 call site reading as it did: the worktree/jobsite
    /// dependencies JobRegistry gained are irrelevant to any test that never
    /// spawns a Worker, so they default here rather than being spelled out ten
    /// times. Tests that DO care pass their own.
    /// </summary>
    private static JobRegistry BuildRegistry(
        IForemanDirectory foremen,
        ILocalCliAgentFactory agentFactory,
        IJobStatusSink statusSink,
        LiveAgentRegistry liveAgents,
        string gcForemanName,
        IJobsiteDirectory? jobsites = null,
        IWorktreeManager? worktrees = null,
        string? stateDirectory = null,
        ICliProcessRunner? cliProcessRunner = null,
        string? notificationsCommand = null,
        IRunLogWriter? runLogWriter = null,
        IJobsLogWriter? jobsLogWriter = null,
        TimeSpan? askGcTimeout = null) =>
        new(
            foremen,
            jobsites ?? new FakeJobsiteDirectory(),
            agentFactory,
            statusSink,
            liveAgents,
            gcForemanName,
            worktrees ?? new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(
                stateDirectory ?? Path.Combine(Path.GetTempPath(), "cc-test-state"), askGcTimeout),
            cliProcessRunner ?? new FakeCliProcessRunner(),
            new HomeOfficeNotificationOptions(notificationsCommand),
            runLogWriter ?? new FakeRunLogWriter(),
            jobsLogWriter ?? new FakeJobsLogWriter());

    private sealed class FakeForemanDirectory : IForemanDirectory
    {
        private readonly Dictionary<string, ForemanConfig> _byName;

        public FakeForemanDirectory(params ForemanConfig[] foremen) =>
            _byName = foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;
    }

    [Fact]
    public async Task StartJob_ReturnsImmediately_AndPublishesPendingThenCompleted()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "done", "", 0) };
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var sink = new JobStatusSink();
        var registry = BuildRegistry(directory, factory, sink, new LiveAgentRegistry(factory), "GC");

        var jobId = registry.StartJob("Frontend", "build the thing");

        Assert.False(string.IsNullOrWhiteSpace(jobId));

        // Drain transitions until Completed, with a timeout so a regression that
        // never completes fails the test instead of hanging the run.
        JobRecord? last = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (last is null || last.Status is JobStatus.Pending or JobStatus.Running)
        {
            last = await sink.Reader.ReadAsync(cts.Token);
        }

        Assert.Equal(JobStatus.Completed, last!.Status);
        Assert.Equal("done", last.Summary);
        Assert.Equal(jobId, last.JobId);
        Assert.Equal(JobStatus.Completed, registry.GetJob(jobId)!.Status);
    }

    [Fact]
    public void StartJob_UnknownForeman_Throws()
    {
        var registry = BuildRegistry(
            new FakeForemanDirectory(),
            new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner()),
            new JobStatusSink(),
            new LiveAgentRegistry(new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner())),
            "GC");

        var ex = Assert.Throws<InvalidOperationException>(() => registry.StartJob("Nope", "task"));
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public async Task IsForemanBusy_TrueWhileRunning_FalseOnceComplete()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "done", "", 0) };
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var sink = new JobStatusSink();
        var registry = BuildRegistry(directory, factory, sink, new LiveAgentRegistry(factory), "GC");

        Assert.False(registry.IsForemanBusy("Frontend"));

        registry.StartJob("Frontend", "build the thing");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JobRecord? last = null;
        while (last is null || last.Status is JobStatus.Pending or JobStatus.Running)
        {
            last = await sink.Reader.ReadAsync(cts.Token);
        }

        Assert.False(registry.IsForemanBusy("Frontend"));
    }

    [Fact]
    public void GetAllJobs_ReturnsEveryStartedJob()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner());
        var registry = BuildRegistry(directory, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

        registry.StartJob("Frontend", "task one");
        registry.StartJob("Frontend", "task two");

        Assert.Equal(2, registry.GetAllJobs().Count);
    }

    [Fact]
    public async Task StartWorkerJob_RunsUnderParentName_LabeledAsWorker()
    {
        var parent = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>(), JobsiteName: "XINFRA");
        var directory = new FakeForemanDirectory(parent);
        // The PARENT's turn hangs (it has to still hold its workorder when the
        // Worker is spawned); the Worker's own turn completes normally.
        var runner = new HangingCliProcessRunner
        {
            NextResult = new CliRunResult(true, "worker done", "", 0),
            ShouldHang = invocation => invocation.Arguments.Any(a => a.Contains("the feature", StringComparison.Ordinal)),
        };
        var provider = new FakeCliToolProvider("fake");
        var factory = new LocalCliAgentFactory([provider], runner);
        var sink = new JobStatusSink();
        var worktrees = new FakeWorktreeManager();
        var stateDirectory = Path.Combine(Path.GetTempPath(), "cc-worker-state");
        var registry = BuildRegistry(
            directory, factory, sink, new LiveAgentRegistry(factory), "GC",
            new FakeJobsiteDirectory(new JobsiteConfig("XINFRA", "/repos/xinfra", "the jobsite")),
            worktrees,
            stateDirectory);

        // A Worker's worktree is cut from the parent's OWN active workorder, so
        // the parent has to be holding one first.
        registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));

        var jobId = await registry.StartWorkerJob("Frontend", "do a small thing", engineOverride: null, CancellationToken.None);

        JobRecord? last = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (last is null || last.JobId != jobId || last.Status is JobStatus.Pending or JobStatus.Running)
        {
            last = await sink.Reader.ReadAsync(cts.Token);
        }

        Assert.Equal(JobStatus.Completed, last!.Status);
        Assert.StartsWith("Frontend/worker-", last.ForemanName);
        Assert.Equal(jobId, last.JobId);

        // The worktree was opened off the workorder's feature branch, under
        // <StateDirectory>/worktrees/<Jobsite>/, and the Worker actually ran in it.
        var opened = Assert.Single(worktrees.Opened);
        Assert.Equal("/repos/xinfra", opened.RepoPath);
        Assert.Equal("feature/named-graphs", opened.FeatureBranch);
        Assert.StartsWith("feature/named-graphs-worker-", opened.WorkerBranch);
        Assert.StartsWith(Path.Combine(stateDirectory, "worktrees", "XINFRA"), opened.WorktreePath);
        Assert.Equal(opened.WorktreePath, last.WorktreePath);
        Assert.Contains(provider.Requests, r => r.WorkingDirectory == opened.WorktreePath);
    }

    /// <summary>A Worker has nothing to branch from until its Foreman holds a workorder.</summary>
    [Fact]
    public async Task StartWorkerJob_ParentHoldsNoWorkorder_ThrowsNamingTheForeman()
    {
        var registry = NewRegistry(Foreman("Frontend"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.StartWorkerJob("Frontend", "do a small thing", null, CancellationToken.None));

        Assert.Contains("Frontend", ex.Message);
        Assert.Contains("workorder", ex.Message);
    }

    [Fact]
    public async Task ForgetLiveAgent_ThenDispatchAgain_StartsAFreshConversation()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var provider = new FakeCliToolProvider("fake");
        var factory = new LocalCliAgentFactory([provider], new FakeCliProcessRunner());
        var registry = BuildRegistry(directory, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

        await registry.AskForeman("Frontend", "first", CancellationToken.None);
        registry.ForgetLiveAgent("Frontend");
        await registry.AskForeman("Frontend", "second", CancellationToken.None);

        Assert.Equal(2, provider.Requests.Count);
        Assert.False(provider.Requests[0].ContinuePreviousConversation);
        Assert.False(provider.Requests[1].ContinuePreviousConversation);
    }

    [Fact]
    public async Task StartWorkerJob_UnknownParent_Throws()
    {
        var registry = BuildRegistry(
            new FakeForemanDirectory(),
            new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner()),
            new JobStatusSink(),
            new LiveAgentRegistry(new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner())),
            "GC");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.StartWorkerJob("Nope", "task", null, CancellationToken.None));
    }

    [Fact]
    public async Task AskForeman_ReturnsForemansAnswer()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var provider = new FakeCliToolProvider("fake");
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "42", "", 0) };
        var factory = new LocalCliAgentFactory([provider], runner);
        var registry = BuildRegistry(directory, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

        var answer = await registry.AskForeman("Frontend", "what is the answer?", CancellationToken.None);

        Assert.Equal("42", answer);
        Assert.Single(provider.Requests);
        Assert.Equal("what is the answer?", provider.Requests[0].Prompt);
    }

    private static ActiveWorkorder Workorder(string feature) =>
        new(feature, "XINFRA", $"/vault/Plans/XINFRA/{feature}", "main", $"feature/{feature}", DateTimeOffset.UtcNow);

    /// <summary>
    /// Every job dispatched through this registry hangs mid-turn, on purpose: the
    /// workorder-slot tests below assert on state a COMPLETION consumes (the job's
    /// ActiveWorkorder) or clears (the Foreman's busy slot), so an instantly
    /// completing fake would race every one of them.
    /// </summary>
    private static JobRegistry NewRegistry(params ForemanConfig[] foremen)
    {
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new HangingCliProcessRunner());
        return BuildRegistry(new FakeForemanDirectory(foremen), factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");
    }

    private static ForemanConfig Foreman(string name) =>
        new(name, CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>(), JobsiteName: "XINFRA");

    [Fact]
    public void StartJob_WithWorkorder_PopulatesBothMaps()
    {
        var registry = NewRegistry(Foreman("Frontend"));

        var jobId = registry.StartJob("Frontend", "do it", Workorder("named-graphs"));

        Assert.Equal(jobId, registry.GetWorkorderSlotOwner("Frontend"));
        Assert.Equal("named-graphs", registry.GetJobWorkorder(jobId)!.Feature);
    }

    [Fact]
    public void StartJob_WithoutWorkorder_ClaimsNothingAndIsNeverRejected()
    {
        var registry = NewRegistry(Foreman("Frontend"));

        registry.StartJob("Frontend", "one");
        registry.StartJob("Frontend", "two");

        Assert.Null(registry.GetWorkorderSlotOwner("Frontend"));
    }

    /// <summary>An ad-hoc dispatch to a Foreman already holding a workorder is fine -- only a second WORKORDER is rejected.</summary>
    [Fact]
    public void StartJob_AdHocDispatchToAForemanHoldingAWorkorder_IsAllowed()
    {
        var registry = NewRegistry(Foreman("Frontend"));
        registry.StartJob("Frontend", "do it", Workorder("named-graphs"));

        var adHocJobId = registry.StartJob("Frontend", "quick question");

        Assert.False(string.IsNullOrWhiteSpace(adHocJobId));
    }

    /// <summary>
    /// Mixed-case regression: _workorderSlots is OrdinalIgnoreCase AND the key is
    /// the canonical ForemanConfig.Name, so "Frontend" and "frontend" are one
    /// Foreman holding one slot.
    /// </summary>
    [Fact]
    public void StartJob_SecondWorkorderToTheSameForemanInDifferentCase_IsRejectedNamingTheFirstFeature()
    {
        var registry = NewRegistry(Foreman("Frontend"));
        registry.StartJob("Frontend", "first", Workorder("named-graphs"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.StartJob("frontend", "second", Workorder("shacl-shapes")));

        Assert.Contains("named-graphs", ex.Message);
    }

    [Fact]
    public void ReleaseWorkorder_ClearsTheSlotButLeavesTheJobsWorkorderReachable()
    {
        var registry = NewRegistry(Foreman("Frontend"));
        var jobId = registry.StartJob("Frontend", "first", Workorder("named-graphs"));

        registry.ReleaseWorkorder(jobId);

        Assert.Null(registry.GetWorkorderSlotOwner("Frontend"));
        Assert.Equal("named-graphs", registry.GetJobWorkorder(jobId)!.Feature);

        // Freed immediately: the next workorder goes straight through.
        var nextJobId = registry.StartJob("Frontend", "second", Workorder("shacl-shapes"));
        Assert.Equal(nextJobId, registry.GetWorkorderSlotOwner("Frontend"));
    }

    /// <summary>A stale release must never evict a slot a later job already claimed.</summary>
    [Fact]
    public void ReleaseWorkorder_AfterTheSlotMovedOn_DoesNotEvictTheNewOwner()
    {
        var registry = NewRegistry(Foreman("Frontend"));
        var firstJobId = registry.StartJob("Frontend", "first", Workorder("named-graphs"));
        registry.ReleaseWorkorder(firstJobId);
        var secondJobId = registry.StartJob("Frontend", "second", Workorder("shacl-shapes"));

        registry.ReleaseWorkorder(firstJobId);

        Assert.Equal(secondJobId, registry.GetWorkorderSlotOwner("Frontend"));
    }

    /// <summary>
    /// Best-effort stress test, NOT a deterministic proof of overlap: Barrier
    /// synchronizes arrival only, and TryClaimWorkorderSlot is a single atomic BCL
    /// call with no window to pause inside. The determinism guarantee comes from
    /// GetOrAdd itself; this test just biases toward overlap across enough
    /// iterations that a reintroduced check-then-set is very likely to be caught.
    /// Driven at the claim directly -- StartJob resolves the claim synchronously,
    /// before any agent dispatch, so a gated fake could never reach it.
    /// </summary>
    [Fact]
    public async Task TryClaimWorkorderSlot_TwoConcurrentClaimsForTheSameForeman_ExactlyOneWins()
    {
        var registry = NewRegistry();
        var workorderA = Workorder("feature-a");
        var workorderB = Workorder("feature-b");

        using var barrier = new Barrier(participantCount: 2);
        var barrierTimeout = TimeSpan.FromSeconds(5);

        bool ClaimAfterBarrier(string jobId, ActiveWorkorder workorder, string foremanName)
        {
            if (!barrier.SignalAndWait(barrierTimeout))
            {
                throw new TimeoutException("Barrier participant never arrived -- a test-setup bug, not a claim-logic failure.");
            }

            return registry.TryClaimWorkorderSlot(foremanName, jobId, workorder, out _);
        }

        for (var i = 0; i < 200; i++)
        {
            var foremanName = $"Frontend-{i}";
            var taskA = Task.Run(() => ClaimAfterBarrier($"job-a-{i}", workorderA, foremanName));
            var taskB = Task.Run(() => ClaimAfterBarrier($"job-b-{i}", workorderB, foremanName));

            var results = await Task.WhenAll(taskA, taskB);
            Assert.Equal(1, results.Count(r => r));
        }
    }

    /// <summary>
    /// The whole point of the onStarted redesign: a job is Pending until agent
    /// dispatch actually BEGINS -- i.e. until it wins the Foreman's semaphore --
    /// not from the moment it is scheduled. Two jobs to the same Foreman prove it:
    /// the second cannot leave Pending while the first holds the semaphore.
    ///
    /// Observed only through the public GetJob. Both waits are bounded, so a
    /// regression fails as a diagnosable TimeoutException instead of hanging; both
    /// gates are released in finally blocks, so nothing is left in flight when the
    /// test method returns.
    /// </summary>
    [Fact]
    public async Task StartJob_StaysPendingUntilDispatchBegins_ThenStampsStartedAtOnRunning()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var gate = new GatedFakeCliAgent();
        // One agent for every Create(), matching LiveAgentRegistry's real per-name
        // caching: both jobs land on the same fake and the same semaphore.
        var factory = new SingleAgentFactory(gate);
        var jobRegistry = BuildRegistry(
            new FakeForemanDirectory(config), factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

        var (job1Started, releaseJob1) = gate.ArmNextCall();
        var job1Id = jobRegistry.StartJob("Frontend", "task one");

        try
        {
            await job1Started.WaitAsync(TimeSpan.FromSeconds(5)); // a real signal: onStarted has fired
            Assert.Equal(JobStatus.Running, jobRegistry.GetJob(job1Id)!.Status);
            Assert.NotNull(jobRegistry.GetJob(job1Id)!.StartedAt);

            var (job2Started, releaseJob2) = gate.ArmNextCall();
            var job2Id = jobRegistry.StartJob("Frontend", "task two"); // same Foreman, still gated behind job one

            try
            {
                // Holds by construction: job two cannot acquire the semaphore, and
                // therefore cannot fire onStarted or leave Pending, until job one
                // releases it -- which has not happened yet.
                Assert.Equal(JobStatus.Pending, jobRegistry.GetJob(job2Id)!.Status);
                Assert.Null(jobRegistry.GetJob(job2Id)!.StartedAt);

                releaseJob1(); // only now let job one finish and release the semaphore

                await job2Started.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(JobStatus.Running, jobRegistry.GetJob(job2Id)!.Status);
                Assert.NotNull(jobRegistry.GetJob(job2Id)!.StartedAt);
            }
            finally
            {
                releaseJob2();
            }
        }
        finally
        {
            releaseJob1(); // harmless no-op (TrySetResult) if already released
        }
    }

    /// <summary>
    /// The Foreman has to be able to name its own job back to ask_gc / file_sitrep,
    /// and the task text is the only channel a provider-agnostic CLI invocation has.
    /// </summary>
    [Fact]
    public async Task StartJob_PrependsTheJobIdToTheTaskText()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var gate = new GatedFakeCliAgent();
        var factory = new SingleAgentFactory(gate);
        var registry = BuildRegistry(
            new FakeForemanDirectory(config), factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

        var (started, release) = gate.ArmNextCall();
        var jobId = registry.StartJob("Frontend", "build the thing");

        try
        {
            await started.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(gate.Messages.TryDequeue(out var message));
            Assert.Equal($"ConstructionCrew job id: {jobId}\n\nbuild the thing", message);
        }
        finally
        {
            release();
        }
    }

    /// <summary>
    /// The atomicity that makes a stale release harmless, driven at the helper
    /// directly: a release naming a job the slot has already moved past must not
    /// evict the new owner. TryRemove(key) alone would.
    /// </summary>
    [Fact]
    public void TryClearSlotIfOwnedBy_StaleJobId_LeavesANewerClaimIntact()
    {
        var registry = NewRegistry(Foreman("Frontend"));
        Assert.True(registry.TryClaimWorkorderSlot("Frontend", "job-one", Workorder("named-graphs"), out _));
        Assert.True(registry.TryClearSlotIfOwnedBy("Frontend", "job-one"));
        Assert.True(registry.TryClaimWorkorderSlot("Frontend", "job-two", Workorder("shacl-shapes"), out _));

        // The stale clear: right key, wrong value.
        Assert.False(registry.TryClearSlotIfOwnedBy("Frontend", "job-one"));

        Assert.Equal("job-two", registry.GetWorkorderSlotOwner("Frontend"));
    }

    [Fact]
    public void TryClearSlotIfOwnedBy_UnheldSlot_IsANoOpNotAnError()
    {
        var registry = NewRegistry(Foreman("Frontend"));

        Assert.False(registry.TryClearSlotIfOwnedBy("Frontend", "never-claimed"));
    }

    // ---- Phase 10: run log, jobs.jsonl, notifications ----------------------

    /// <summary>
    /// A registry whose jobs complete immediately, wired to observable log writers.
    ///
    /// Both writers are the waiting fakes, not because the real ones are unwanted
    /// (an `inner` writes through to them) but because Transition PUBLISHES a job's
    /// new status BEFORE performing any side-effect write: waiting on the status
    /// sink alone would race every assertion about those writes. JobsLog's append is
    /// the last statement in Transition, so waiting on it is the reliable "this
    /// transition is completely finished" barrier.
    /// </summary>
    private sealed record Rig(
        JobRegistry Registry, JobStatusSink Sink, FakeRunLogWriter RunLog, FakeJobsLogWriter JobsLog);

    private static Rig BuildRig(
        IRunLogWriter? runLogInner = null,
        IJobsLogWriter? jobsLogInner = null,
        Exception? runLogThrows = null,
        Exception? jobsLogThrows = null,
        Func<CliInvocation, CliRunResult>? handler = null,
        ICliProcessRunner? agentRunner = null,
        params ForemanConfig[] foremen)
    {
        var runLog = new FakeRunLogWriter(runLogInner) { ThrowOnAppend = runLogThrows };
        var jobsLog = new FakeJobsLogWriter(jobsLogInner) { ThrowOnAppend = jobsLogThrows };
        var runner = agentRunner ?? new FakeCliProcessRunner { Handler = handler };
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var sink = new JobStatusSink();
        var registry = BuildRegistry(
            new FakeForemanDirectory(foremen.Length == 0 ? [Foreman("Frontend")] : foremen),
            factory, sink, new LiveAgentRegistry(factory), "GC",
            runLogWriter: runLog, jobsLogWriter: jobsLog);

        return new Rig(registry, sink, runLog, jobsLog);
    }

    /// <summary>
    /// Nothing further may be published for this job. A side-effect write that
    /// failed and was swallowed leaves RunJobAsync with nothing left to do; one
    /// that escaped would land a second, Failed record right behind the first.
    /// </summary>
    private static async Task AssertNoFurtherFailure(JobStatusSink sink, string jobId)
    {
        using var settle = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            while (true)
            {
                var extra = await sink.Reader.ReadAsync(settle.Token);
                Assert.False(
                    extra.JobId == jobId && extra.Status == JobStatus.Failed,
                    "A side-effect write's failure escaped Transition and re-reported the job as Failed.");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Completion_WithAWorkorder_AppendsToTheRunLogOnceAndConsumesBothMaps()
    {
        var rig = BuildRig();

        var jobId = rig.Registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));
        await rig.JobsLog.WaitForAppends(2); // Running, then Completed -- the second one is Transition's last act

        var append = Assert.Single(rig.RunLog.Appends);
        Assert.Equal("/vault/Plans/XINFRA/named-graphs", append.PlansFolder);
        Assert.Equal(jobId, append.Job.JobId);
        Assert.Equal(JobStatus.Completed, append.Job.Status);

        // Consumed by the append, and the busy slot cleared by the safety net.
        Assert.Null(rig.Registry.GetJobWorkorder(jobId));
        Assert.Null(rig.Registry.GetWorkorderSlotOwner("Frontend"));
    }

    /// <summary>
    /// The finished run's own token/cost accounting rides into the JobRecord, which
    /// is what puts real numbers (not nulls) in the run log and, from there, in the
    /// Detail note.
    /// </summary>
    [Fact]
    public async Task Completion_CarriesTheRunsUsageIntoTheJobRecordAndTheRunLog()
    {
        var usage = new CliUsage(1200, 340, 0.42m, "{}");
        var rig = BuildRig(handler: _ => new CliRunResult(true, "done", "", 0, usage));

        var jobId = rig.Registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));
        await rig.JobsLog.WaitForAppends(2);

        Assert.Equal(usage, rig.Registry.GetJob(jobId)!.Usage);
        Assert.Equal(usage, Assert.Single(rig.RunLog.Appends).Job.Usage);
    }

    /// <summary>An ad-hoc dispatch has no Plans folder to log against, so it logs nothing.</summary>
    [Fact]
    public async Task Completion_WithoutAWorkorder_LogsNothing()
    {
        var rig = BuildRig();

        rig.Registry.StartJob("Frontend", "quick question");
        await rig.JobsLog.WaitForAppends(2); // the transition is over, so a run-log append would already have happened

        Assert.Empty(rig.RunLog.Appends);
    }

    /// <summary>
    /// Regression, the JobRegistry-level sibling of FileSitrepTool's: a pr-opened
    /// release frees the Foreman long before the job's own completion fires, and
    /// the completion must STILL know which Plans folder to log against.
    /// </summary>
    [Fact]
    public async Task ReleaseWorkorder_BeforeCompletion_StillAppendsToTheRunLogExactlyOnce()
    {
        var runner = new HangingCliProcessRunner();
        var rig = BuildRig(agentRunner: runner);

        var jobId = rig.Registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));
        await rig.JobsLog.WaitForAppends(1); // Running: the job is genuinely in flight

        // The pr-opened sitrep, firing while the job is still in flight.
        rig.Registry.ReleaseWorkorder(jobId);
        Assert.Null(rig.Registry.GetWorkorderSlotOwner("Frontend"));

        runner.Release();
        await rig.JobsLog.WaitForAppends(1); // Completed

        var append = Assert.Single(rig.RunLog.Appends);
        Assert.Equal("/vault/Plans/XINFRA/named-graphs", append.PlansFolder);
        Assert.Equal(jobId, append.Job.JobId);
    }

    /// <summary>
    /// Regression from the other direction: the completion's own safety-net clear
    /// frees the slot, a second workorder claims it, and only THEN does the first
    /// job's stale release arrive. It must not evict the new owner.
    /// </summary>
    [Fact]
    public async Task StaleReleaseWorkorder_AfterCompletionAndAReclaim_LeavesTheNewOwnerIntact()
    {
        // The first job completes; the second one hangs, so it still holds the slot
        // when the stale release lands.
        var runner = new HangingCliProcessRunner
        {
            ShouldHang = invocation => invocation.Arguments.Any(a => a.Contains("second", StringComparison.Ordinal)),
        };
        var rig = BuildRig(agentRunner: runner);

        var firstJobId = rig.Registry.StartJob("Frontend", "first", Workorder("named-graphs"));
        await rig.JobsLog.WaitForAppends(2); // Running, Completed -- including the safety-net clear
        Assert.Null(rig.Registry.GetWorkorderSlotOwner("Frontend"));

        var secondJobId = rig.Registry.StartJob("Frontend", "second", Workorder("shacl-shapes"));
        Assert.Equal(secondJobId, rig.Registry.GetWorkorderSlotOwner("Frontend"));

        // Stale, out of order, naming a job the slot moved past long ago.
        rig.Registry.ReleaseWorkorder(firstJobId);

        Assert.Equal(secondJobId, rig.Registry.GetWorkorderSlotOwner("Frontend"));
        runner.Release();
    }

    /// <summary>
    /// A RUN-LOG.md write failure is a logging problem, never a job outcome. It is
    /// swallowed inside Transition, so RunJobAsync's outer catch never sees it and
    /// the completed job stays completed.
    /// </summary>
    [Fact]
    public async Task RunLogAppendThrowing_StillReportsTheJobCompleted()
    {
        var rig = BuildRig(runLogThrows: new IOException("disk full"));

        var jobId = rig.Registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));
        await rig.JobsLog.WaitForAppends(2);

        await AssertNoFurtherFailure(rig.Sink, jobId);
        Assert.Equal(JobStatus.Completed, rig.Registry.GetJob(jobId)!.Status);
        // Still consumed: a failed append must not leave the map to be retried against.
        Assert.Null(rig.Registry.GetJobWorkorder(jobId));
    }

    /// <summary>Same rule for the state/jobs.jsonl append: it can fail, the job cannot.</summary>
    [Fact]
    public async Task JobsLogAppendThrowing_StillReportsTheJobCompleted()
    {
        var rig = BuildRig(jobsLogThrows: new IOException("disk full"));

        var jobId = rig.Registry.StartJob("Frontend", "the feature", Workorder("named-graphs"));
        await rig.JobsLog.WaitForAppends(2);

        await AssertNoFurtherFailure(rig.Sink, jobId);
        Assert.Equal(JobStatus.Completed, rig.Registry.GetJob(jobId)!.Status);
    }

    /// <summary>Every transition is logged: Running, then Completed.</summary>
    [Fact]
    public async Task EveryTransition_AppendsOneLineToTheJobsLog()
    {
        var rig = BuildRig();

        var jobId = rig.Registry.StartJob("Frontend", "the feature");
        await rig.JobsLog.WaitForAppends(2);

        var statuses = rig.JobsLog.Appends.Where(j => j.JobId == jobId).Select(j => j.Status).ToList();
        Assert.Equal([JobStatus.Running, JobStatus.Completed], statuses);
    }

    /// <summary>
    /// A transition INTO Parked fires the hook exactly once, with {event}, {jobId}
    /// and {foreman} substituted, shelled through the injected ICliProcessRunner.
    /// </summary>
    [Fact]
    public async Task TransitionIntoParked_FiresTheNotificationsCommandOnce()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend"), GcConfig());
        var factory = new PerNameGatedAgentFactory();
        var frontend = factory.For("Frontend");
        var gc = factory.For("GC");
        var notifications = new NotificationSpyRunner();
        var registry = BuildRegistry(
            foremen, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC",
            cliProcessRunner: notifications,
            notificationsCommand: "notify-send 'cc: {event} {jobId} {foreman}'",
            askGcTimeout: TimeSpan.FromMilliseconds(100));

        var (frontendStarted, releaseFrontend) = frontend.ArmNextCall();
        var (_, releaseGc) = gc.ArmNextCall();
        var jobId = registry.StartJob("Frontend", "work");

        try
        {
            await frontendStarted.WaitAsync(TimeSpan.FromSeconds(5));

            // GC never answers inside the timeout, so the job parks.
            Assert.Equal("parked: waiting on Boss", await registry.AskGc(jobId, "what now?", CancellationToken.None));
            Assert.Equal(JobStatus.Parked, registry.GetJob(jobId)!.Status);

            await notifications.FirstInvocation.WaitAsync(TimeSpan.FromSeconds(5));

            var invocation = Assert.Single(notifications.Invocations);
            Assert.Equal("/bin/sh", invocation.ExecutablePath);
            Assert.Equal("-c", invocation.Arguments[0]);
            Assert.Equal($"notify-send 'cc: parked {jobId} Frontend'", invocation.Arguments[1]);
        }
        finally
        {
            releaseGc();
            releaseFrontend();
        }
    }

    /// <summary>No command configured means no process spawned -- checked synchronously, before any task starts.</summary>
    [Fact]
    public async Task TransitionIntoParked_WithNoNotificationsCommand_SpawnsNothing()
    {
        var foremen = new FakeForemanDirectory(Foreman("Frontend"), GcConfig());
        var factory = new PerNameGatedAgentFactory();
        var frontend = factory.For("Frontend");
        var gc = factory.For("GC");
        var notifications = new NotificationSpyRunner();
        var registry = BuildRegistry(
            foremen, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC",
            cliProcessRunner: notifications,
            notificationsCommand: null,
            askGcTimeout: TimeSpan.FromMilliseconds(100));

        var (frontendStarted, releaseFrontend) = frontend.ArmNextCall();
        var (_, releaseGc) = gc.ArmNextCall();
        var jobId = registry.StartJob("Frontend", "work");

        try
        {
            await frontendStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await registry.AskGc(jobId, "what now?", CancellationToken.None);

            Assert.Equal(JobStatus.Parked, registry.GetJob(jobId)!.Status);
            Assert.Empty(notifications.Invocations);
        }
        finally
        {
            releaseGc();
            releaseFrontend();
        }
    }

    [Fact]
    public async Task NotifyPrOpened_FiresTheNotificationsCommandOnce()
    {
        var notifications = new NotificationSpyRunner();
        var factory = new RecordingAgentFactory();
        var registry = BuildRegistry(
            new FakeForemanDirectory(Foreman("Frontend")), factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC",
            cliProcessRunner: notifications,
            notificationsCommand: "notify-send 'cc: {event} {jobId} {foreman}'");

        registry.NotifyPrOpened("job-77", "Frontend");

        await notifications.FirstInvocation.WaitAsync(TimeSpan.FromSeconds(5));

        var invocation = Assert.Single(notifications.Invocations);
        Assert.Equal("/bin/sh", invocation.ExecutablePath);
        Assert.Equal("-c", invocation.Arguments[0]);
        Assert.Equal("notify-send 'cc: pr-opened job-77 Frontend'", invocation.Arguments[1]);
    }

    [Fact]
    public void NotifyPrOpened_WithNoNotificationsCommand_SpawnsNothing()
    {
        var notifications = new NotificationSpyRunner();
        var factory = new RecordingAgentFactory();
        var registry = BuildRegistry(
            new FakeForemanDirectory(Foreman("Frontend")), factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC",
            cliProcessRunner: notifications,
            notificationsCommand: "");

        registry.NotifyPrOpened("job-77", "Frontend");

        Assert.Empty(notifications.Invocations);
    }

    /// <summary>
    /// Coarse smoke test: several jobs completing at once, all against ONE
    /// RUN-LOG.md, must produce one whole entry each -- nothing partial, nothing
    /// interleaved. Driven through the real RunLogWriter and a real file.
    /// </summary>
    [Fact]
    public async Task ConcurrentCompletions_SharingOneRunLog_WriteOneWholeEntryEach()
    {
        const int jobCount = 8;
        var plansFolder = Path.Combine(Path.GetTempPath(), "cc-runlog-jobs-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(plansFolder);

        try
        {
            var foremen = Enumerable.Range(0, jobCount).Select(i => Foreman($"Frontend-{i}")).ToArray();
            // The agent echoes the prompt back, so each job's Summary carries its own token.
            var rig = BuildRig(
                runLogInner: new RunLogWriter(),
                handler: invocation => new CliRunResult(true, invocation.Arguments[0], "", 0),
                foremen: foremen);

            await Task.WhenAll(Enumerable.Range(0, jobCount).Select(i => Task.Run(() =>
                rig.Registry.StartJob(
                    $"Frontend-{i}",
                    $"token-{i}",
                    new ActiveWorkorder($"feature-{i}", "XINFRA", plansFolder, "main", $"feature/f-{i}", DateTimeOffset.UtcNow)))));

            await rig.RunLog.WaitForAppends(jobCount);

            var entries = File.ReadAllLines(Path.Combine(plansFolder, "RUN-LOG.md"))
                .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(jobCount, entries.Count);
            foreach (var i in Enumerable.Range(0, jobCount))
            {
                Assert.Single(entries, e => e.EndsWith($"token-{i}", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(plansFolder, recursive: true);
        }
    }

    /// <summary>
    /// The same coarse proof for JobsLogWriter's single fixed lock: concurrent
    /// completions must leave state/jobs.jsonl as whole, parseable JSON lines.
    /// </summary>
    [Fact]
    public async Task ConcurrentCompletions_WritingJobsJsonl_LeaveWholeJsonLines()
    {
        const int jobCount = 8;
        var stateDirectory = Path.Combine(Path.GetTempPath(), "cc-jobslog-" + Guid.NewGuid().ToString("n")[..8]);
        var path = Path.Combine(stateDirectory, "jobs.jsonl");

        try
        {
            var foremen = Enumerable.Range(0, jobCount).Select(i => Foreman($"Frontend-{i}")).ToArray();
            var rig = BuildRig(
                jobsLogInner: new JobsLogWriter(path),
                handler: _ => new CliRunResult(true, "done", "", 0),
                foremen: foremen);

            var jobIds = await Task.WhenAll(Enumerable.Range(0, jobCount).Select(i => Task.Run(() =>
                rig.Registry.StartJob($"Frontend-{i}", $"token-{i}"))));

            // Two transitions per job: Running, then Completed.
            await rig.JobsLog.WaitForAppends(jobCount * 2);

            var lines = File.ReadAllLines(path);

            Assert.Equal(jobCount * 2, lines.Length);
            foreach (var line in lines)
            {
                using var parsed = JsonDocument.Parse(line); // throws on a partial or interleaved write
                Assert.True(parsed.RootElement.TryGetProperty("JobId", out _));
            }

            foreach (var jobId in jobIds)
            {
                Assert.Equal(2, lines.Count(l => l.Contains(jobId, StringComparison.Ordinal)));
            }
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static ForemanConfig GcConfig() =>
        new("GC", CrewRole.GC, "fake", "dir", "instructions.md", new Dictionary<string, string>());

    [Fact]
    public void GetJob_UnknownId_ReturnsNull()
    {
        var registry = BuildRegistry(
            new FakeForemanDirectory(),
            new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner()),
            new JobStatusSink(),
            new LiveAgentRegistry(new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner())),
            "GC");

        Assert.Null(registry.GetJob("does-not-exist"));
    }
}
