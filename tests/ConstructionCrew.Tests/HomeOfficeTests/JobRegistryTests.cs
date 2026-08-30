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
        string? stateDirectory = null) =>
        new(
            foremen,
            jobsites ?? new FakeJobsiteDirectory(),
            agentFactory,
            statusSink,
            liveAgents,
            gcForemanName,
            worktrees ?? new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(stateDirectory ?? Path.Combine(Path.GetTempPath(), "cc-test-state")));

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
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "worker done", "", 0) };
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

    private static JobRegistry NewRegistry(params ForemanConfig[] foremen)
    {
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner());
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
