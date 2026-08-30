using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.Providers;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.HomeOfficeTests;

public class JobRegistryTests
{
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
        var registry = new JobRegistry(directory, factory, sink, new LiveAgentRegistry(factory), "GC");

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
        var registry = new JobRegistry(
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
        var registry = new JobRegistry(directory, factory, sink, new LiveAgentRegistry(factory), "GC");

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
        var registry = new JobRegistry(directory, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

        registry.StartJob("Frontend", "task one");
        registry.StartJob("Frontend", "task two");

        Assert.Equal(2, registry.GetAllJobs().Count);
    }

    [Fact]
    public async Task StartWorkerJob_RunsUnderParentName_LabeledAsWorker()
    {
        var parent = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(parent);
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "worker done", "", 0) };
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var sink = new JobStatusSink();
        var registry = new JobRegistry(directory, factory, sink, new LiveAgentRegistry(factory), "GC");

        var jobId = registry.StartWorkerJob("Frontend", "do a small thing", engineOverride: null);

        JobRecord? last = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (last is null || last.Status is JobStatus.Pending or JobStatus.Running)
        {
            last = await sink.Reader.ReadAsync(cts.Token);
        }

        Assert.Equal(JobStatus.Completed, last!.Status);
        Assert.StartsWith("Frontend/worker-", last.ForemanName);
        Assert.Equal(jobId, last.JobId);
    }

    [Fact]
    public async Task ForgetLiveAgent_ThenDispatchAgain_StartsAFreshConversation()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var provider = new FakeCliToolProvider("fake");
        var factory = new LocalCliAgentFactory([provider], new FakeCliProcessRunner());
        var registry = new JobRegistry(directory, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

        await registry.AskForeman("Frontend", "first", CancellationToken.None);
        registry.ForgetLiveAgent("Frontend");
        await registry.AskForeman("Frontend", "second", CancellationToken.None);

        Assert.Equal(2, provider.Requests.Count);
        Assert.False(provider.Requests[0].ContinuePreviousConversation);
        Assert.False(provider.Requests[1].ContinuePreviousConversation);
    }

    [Fact]
    public void StartWorkerJob_UnknownParent_Throws()
    {
        var registry = new JobRegistry(
            new FakeForemanDirectory(),
            new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner()),
            new JobStatusSink(),
            new LiveAgentRegistry(new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner())),
            "GC");

        Assert.Throws<InvalidOperationException>(() => registry.StartWorkerJob("Nope", "task", null));
    }

    [Fact]
    public async Task AskForeman_ReturnsForemansAnswer()
    {
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var provider = new FakeCliToolProvider("fake");
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "42", "", 0) };
        var factory = new LocalCliAgentFactory([provider], runner);
        var registry = new JobRegistry(directory, factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");

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
        return new JobRegistry(new FakeForemanDirectory(foremen), factory, new JobStatusSink(), new LiveAgentRegistry(factory), "GC");
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

    [Fact]
    public void GetJob_UnknownId_ReturnsNull()
    {
        var registry = new JobRegistry(
            new FakeForemanDirectory(),
            new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner()),
            new JobStatusSink(),
            new LiveAgentRegistry(new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner())),
            "GC");

        Assert.Null(registry.GetJob("does-not-exist"));
    }
}
