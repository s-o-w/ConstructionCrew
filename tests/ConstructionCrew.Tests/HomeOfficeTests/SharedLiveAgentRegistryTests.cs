using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.HomeOfficeTests;

/// <summary>
/// Phase 1b's single-conversation fix. Before it, JobRegistry built its own
/// LiveAgentRegistry internally and Program.cs held a separate standalone GC
/// agent -- two divergent GC conversations, either one of which reproduces the
/// bug on its own.
/// </summary>
public class SharedLiveAgentRegistryTests
{
    [Fact]
    public async Task ABossTurnAndADispatchToGc_ShareExactlyOneAgent()
    {
        var gcConfig = new ForemanConfig("GC", CrewRole.GC, "fake", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new FakeDirectory(gcConfig);
        var factory = new SpyAgentFactory();

        // One registry, both consumers -- exactly how Program.cs wires it.
        var liveAgents = new LiveAgentRegistry(factory);
        var registry = new JobRegistry(
            directory,
            new FakeJobsiteDirectory(),
            factory,
            new JobStatusSink(),
            liveAgents,
            "GC",
            new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(Path.Combine(Path.GetTempPath(), "cc-test-state")));

        // A Boss turn goes straight through the shared registry...
        await liveAgents.SendAsync("GC", gcConfig, "hello", CancellationToken.None);

        // ...and a dispatched job to GC goes through JobRegistry.
        registry.StartJob("GC", "do the thing");

        // The dispatch runs on a background task; wait for its agent turn to land.
        await WaitForAsync(() => factory.Turns >= 2);

        Assert.Equal(["GC"], factory.Created);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class FakeDirectory(params ForemanConfig[] foremen) : IForemanDirectory
    {
        private readonly Dictionary<string, ForemanConfig> _byName =
            foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;
    }

    private sealed class SpyAgentFactory : ILocalCliAgentFactory
    {
        private int _turns;

        public List<string> Created { get; } = [];

        public int Turns => Volatile.Read(ref _turns);

        public ILocalCliAgent Create(ForemanConfig config)
        {
            lock (Created)
            {
                Created.Add(config.Name);
            }

            return new SpyAgent(config.Name, () => Interlocked.Increment(ref _turns));
        }
    }

    private sealed class SpyAgent(string name, Action onTurn) : ILocalCliAgent
    {
        public string Name { get; } = name;

        public Task<CliRunResult> SendAsync(string message, CancellationToken cancellationToken)
        {
            onTurn();
            return Task.FromResult(new CliRunResult(true, "ok", string.Empty, 0));
        }
    }
}
