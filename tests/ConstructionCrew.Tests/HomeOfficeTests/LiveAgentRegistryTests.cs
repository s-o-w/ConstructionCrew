using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.HomeOfficeTests;

public class LiveAgentRegistryTests
{
    [Fact]
    public async Task SendAsync_SameName_ReusesAgent_SecondCallContinues()
    {
        var provider = new FakeCliToolProvider("fake");
        var factory = new LocalCliAgentFactory([provider], new FakeCliProcessRunner());
        var registry = new LiveAgentRegistry(factory);
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());

        await registry.SendAsync("Frontend", config, "first", CancellationToken.None);
        await registry.SendAsync("Frontend", config, "second", CancellationToken.None);

        Assert.Equal(2, provider.Requests.Count);
        Assert.False(provider.Requests[0].ContinuePreviousConversation);
        Assert.True(provider.Requests[1].ContinuePreviousConversation);
    }

    [Fact]
    public async Task SendAsync_ConcurrentCallsSameName_AreSerialized()
    {
        // A Worker's ask_foreman could otherwise race a GC dispatch to the same
        // Foreman and run two --continue invocations concurrently against the
        // same conversation. This proves the per-name lock actually prevents that.
        var current = 0;
        var maxConcurrent = 0;
        var gate = new object();

        var runner = new SlowFakeRunner(async () =>
        {
            lock (gate)
            {
                current++;
                maxConcurrent = Math.Max(maxConcurrent, current);
            }

            await Task.Delay(50);

            lock (gate)
            {
                current--;
            }
        });

        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        var registry = new LiveAgentRegistry(factory);
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());

        await Task.WhenAll(
            registry.SendAsync("Frontend", config, "one", CancellationToken.None),
            registry.SendAsync("Frontend", config, "two", CancellationToken.None));

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task Remove_ThenSendAsyncAgain_StartsAFreshConversation()
    {
        // Backs the /fire safety story: a name re-hired later must never
        // silently continue a fired Foreman's old conversation history.
        var provider = new FakeCliToolProvider("fake");
        var factory = new LocalCliAgentFactory([provider], new FakeCliProcessRunner());
        var registry = new LiveAgentRegistry(factory);
        var config = new ForemanConfig("Frontend", "fake", "dir", "instructions.md", new Dictionary<string, string>());

        await registry.SendAsync("Frontend", config, "first", CancellationToken.None);
        registry.Remove("Frontend");
        await registry.SendAsync("Frontend", config, "second", CancellationToken.None);

        Assert.Equal(2, provider.Requests.Count);
        Assert.False(provider.Requests[0].ContinuePreviousConversation);
        Assert.False(provider.Requests[1].ContinuePreviousConversation);
    }

    private sealed class SlowFakeRunner : ICliProcessRunner
    {
        private readonly Func<Task> _onRun;

        public SlowFakeRunner(Func<Task> onRun) => _onRun = onRun;

        public async Task<CliRunResult> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
        {
            await _onRun();
            return new CliRunResult(true, "ok", "", 0);
        }
    }
}
