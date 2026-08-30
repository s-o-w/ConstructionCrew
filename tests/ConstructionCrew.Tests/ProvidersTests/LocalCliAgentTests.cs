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
}
