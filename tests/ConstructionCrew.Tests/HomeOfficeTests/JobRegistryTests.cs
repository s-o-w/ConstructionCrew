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
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "done", "", 0) };
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var sink = new JobStatusSink();
        var registry = new JobRegistry(directory, factory, sink);

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
            new JobStatusSink());

        var ex = Assert.Throws<InvalidOperationException>(() => registry.StartJob("Nope", "task"));
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public async Task IsForemanBusy_TrueWhileRunning_FalseOnceComplete()
    {
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "done", "", 0) };
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var sink = new JobStatusSink();
        var registry = new JobRegistry(directory, factory, sink);

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
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner());
        var registry = new JobRegistry(directory, factory, new JobStatusSink());

        registry.StartJob("Frontend", "task one");
        registry.StartJob("Frontend", "task two");

        Assert.Equal(2, registry.GetAllJobs().Count);
    }

    [Fact]
    public async Task StartWorkerJob_RunsUnderParentName_LabeledAsWorker()
    {
        var parent = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(parent);
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "worker done", "", 0) };
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var sink = new JobStatusSink();
        var registry = new JobRegistry(directory, factory, sink);

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
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var provider = new FakeCliToolProvider("fake");
        var factory = new LocalCliAgentFactory([provider], new FakeCliProcessRunner());
        var registry = new JobRegistry(directory, factory, new JobStatusSink());

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
            new JobStatusSink());

        Assert.Throws<InvalidOperationException>(() => registry.StartWorkerJob("Nope", "task", null));
    }

    [Fact]
    public async Task AskForeman_ReturnsForemansAnswer()
    {
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeForemanDirectory(config);
        var provider = new FakeCliToolProvider("fake");
        var runner = new FakeCliProcessRunner { NextResult = new CliRunResult(true, "42", "", 0) };
        var factory = new LocalCliAgentFactory([provider], runner);
        var registry = new JobRegistry(directory, factory, new JobStatusSink());

        var answer = await registry.AskForeman("Frontend", "what is the answer?", CancellationToken.None);

        Assert.Equal("42", answer);
        Assert.Single(provider.Requests);
        Assert.Equal("what is the answer?", provider.Requests[0].Prompt);
    }

    [Fact]
    public void GetJob_UnknownId_ReturnsNull()
    {
        var registry = new JobRegistry(
            new FakeForemanDirectory(),
            new LocalCliAgentFactory([new FakeCliToolProvider("fake")], new FakeCliProcessRunner()),
            new JobStatusSink());

        Assert.Null(registry.GetJob("does-not-exist"));
    }
}
