using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using ConstructionCrew.Tests.Fakes;
using Xunit;

namespace ConstructionCrew.Tests.ProvidersTests;

public class LocalCliAgentTests
{
    [Fact]
    public async Task FirstMessage_PrependsInstructionsFile_AndDoesNotContinue()
    {
        var instructionsPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(instructionsPath, "You are a test Foreman.");

        try
        {
            var config = new ForemanConfig("Test", CrewRole.Foreman, "fake", Path.GetTempPath(), instructionsPath, new Dictionary<string, string>());
            var provider = new FakeCliToolProvider();
            var runner = new FakeCliProcessRunner();
            var agent = new LocalCliAgent(config, provider, runner);

            await agent.SendAsync("do the thing", CancellationToken.None);

            Assert.Single(provider.Requests);
            Assert.Contains("You are a test Foreman.", provider.Requests[0].Prompt);
            Assert.Contains("do the thing", provider.Requests[0].Prompt);
            Assert.False(provider.Requests[0].ContinuePreviousConversation);
        }
        finally
        {
            File.Delete(instructionsPath);
        }
    }

    [Fact]
    public async Task SecondMessage_ContinuesConversation_AndDoesNotRePrependInstructions()
    {
        var instructionsPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(instructionsPath, "You are a test Foreman.");

        try
        {
            var config = new ForemanConfig("Test", CrewRole.Foreman, "fake", Path.GetTempPath(), instructionsPath, new Dictionary<string, string>());
            var provider = new FakeCliToolProvider();
            var runner = new FakeCliProcessRunner();
            var agent = new LocalCliAgent(config, provider, runner);

            await agent.SendAsync("first", CancellationToken.None);
            await agent.SendAsync("second", CancellationToken.None);

            Assert.Equal(2, provider.Requests.Count);
            Assert.True(provider.Requests[1].ContinuePreviousConversation);
            Assert.Equal("second", provider.Requests[1].Prompt);
        }
        finally
        {
            File.Delete(instructionsPath);
        }
    }

    [Fact]
    public async Task MissingInstructionsFile_FallsBackToRawMessage()
    {
        var config = new ForemanConfig("Test", CrewRole.Foreman, "fake", Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "does-not-exist.md"), new Dictionary<string, string>());
        var provider = new FakeCliToolProvider();
        var runner = new FakeCliProcessRunner();
        var agent = new LocalCliAgent(config, provider, runner);

        await agent.SendAsync("just do it", CancellationToken.None);

        Assert.Equal("just do it", provider.Requests[0].Prompt);
    }

    /// <summary>
    /// The provider gets the last word on its own output shape: an opt-in
    /// --output-format json run comes back with Usage filled and the answer text
    /// unwrapped, without JobRegistry or LocalCliAgent knowing anything about
    /// Claude Code's envelope.
    /// </summary>
    [Fact]
    public async Task SendAsync_AppliesTheProvidersPostProcess_SoUsageReachesTheCaller()
    {
        var config = new ForemanConfig(
            "Test", CrewRole.Foreman, "claude", Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "does-not-exist.md"),
            new Dictionary<string, string> { ["outputFormat"] = "json" });
        var runner = new FakeCliProcessRunner
        {
            NextResult = new CliRunResult(
                true,
                """{"type":"result","result":"done","total_cost_usd":0.5,"usage":{"input_tokens":10,"output_tokens":20}}""",
                "",
                0),
        };
        var agent = new LocalCliAgent(config, new ClaudeCodeProvider(), runner);

        var result = await agent.SendAsync("just do it", CancellationToken.None);

        Assert.Equal("done", result.StandardOutput);
        Assert.Equal(10, result.Usage!.InputTokens);
        Assert.Equal(20, result.Usage.OutputTokens);
        Assert.Equal(0.5m, result.Usage.CostUsd);
    }

    /// <summary>
    /// The agent remembers the engine's own session id off every turn, which is
    /// what the watcher looks a transcript up by. Taken straight off CliUsage --
    /// no second parse of the envelope and no "is this Claude?" check in here.
    /// </summary>
    [Fact]
    public async Task SendAsync_RemembersTheSessionIdTheProviderReported()
    {
        var provider = new FakeCliToolProvider
        {
            NextUsage = new CliUsage(null, null, null, null, "abc-123"),
        };
        var agent = NewAgent(provider);

        Assert.Null(agent.SessionId);

        await agent.SendAsync("do the thing", CancellationToken.None);

        Assert.Equal("abc-123", agent.SessionId);
    }

    /// <summary>
    /// Sticky on purpose. A turn that reported no id (a crashed CLI, a
    /// plain-text provider) must not blank a conversation that is still
    /// perfectly resumable -- dropping it would send the next turn to a fresh
    /// session and lose the Foreman's context.
    /// </summary>
    [Fact]
    public async Task SendAsync_ATurnWithNoSessionId_KeepsTheOneAlreadyKnown()
    {
        var provider = new FakeCliToolProvider
        {
            NextUsage = new CliUsage(null, null, null, null, "abc-123"),
        };
        var agent = NewAgent(provider);

        await agent.SendAsync("first", CancellationToken.None);

        provider.NextUsage = null;
        await agent.SendAsync("second", CancellationToken.None);

        Assert.Equal("abc-123", agent.SessionId);
    }

    /// <summary>A provider that reports no usage at all leaves the agent with no session to watch, and says so.</summary>
    [Fact]
    public async Task SendAsync_ProviderThatReportsNoUsage_LeavesSessionIdNull()
    {
        var agent = NewAgent(new FakeCliToolProvider());

        await agent.SendAsync("do the thing", CancellationToken.None);

        Assert.Null(agent.SessionId);
    }

    /// <summary>
    /// Once an id is known, every later turn carries it: the provider then asks
    /// to resume that exact conversation rather than whatever ran in the
    /// directory last. The first turn carries none, so it starts genuinely fresh.
    /// </summary>
    [Fact]
    public async Task SendAsync_OnceASessionIdIsKnown_EveryLaterTurnCarriesIt()
    {
        var provider = new FakeCliToolProvider
        {
            NextUsage = new CliUsage(null, null, null, null, "abc-123"),
        };
        var agent = NewAgent(provider);

        await agent.SendAsync("first", CancellationToken.None);
        await agent.SendAsync("second", CancellationToken.None);

        Assert.Null(provider.Requests[0].ResumeSessionId);
        Assert.False(provider.Requests[0].ContinuePreviousConversation);

        Assert.Equal("abc-123", provider.Requests[1].ResumeSessionId);
        Assert.True(provider.Requests[1].ContinuePreviousConversation);
    }

    /// <summary>
    /// A provider that never reports an id still gets the continuation flag, so
    /// the conversation survives on the weaker directory-scoped guess rather
    /// than restarting every turn.
    /// </summary>
    [Fact]
    public async Task SendAsync_ProviderWithNoSessionId_StillAsksToContinue()
    {
        var provider = new FakeCliToolProvider();
        var agent = NewAgent(provider);

        await agent.SendAsync("first", CancellationToken.None);
        await agent.SendAsync("second", CancellationToken.None);

        Assert.Null(provider.Requests[1].ResumeSessionId);
        Assert.True(provider.Requests[1].ContinuePreviousConversation);
    }

    private static LocalCliAgent NewAgent(FakeCliToolProvider provider) =>
        new(
            new ForemanConfig(
                "Test", CrewRole.Foreman, "fake", Path.GetTempPath(),
                Path.Combine(Path.GetTempPath(), "does-not-exist.md"),
                new Dictionary<string, string>()),
            provider,
            new FakeCliProcessRunner());
}
