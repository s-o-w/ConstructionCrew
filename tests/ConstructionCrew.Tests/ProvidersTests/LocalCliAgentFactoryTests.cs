using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using ConstructionCrew.Tests.Fakes;
using Xunit;

namespace ConstructionCrew.Tests.ProvidersTests;

public class LocalCliAgentFactoryTests
{
    [Fact]
    public void Create_UnknownProvider_Throws()
    {
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("claude")], new FakeCliProcessRunner());
        var config = new ForemanConfig("Test", CrewRole.Foreman, "codex", "dir", "instructions.md", new Dictionary<string, string>());

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(config));
        Assert.Contains("codex", ex.Message);
        Assert.Contains("claude", ex.Message);
    }

    [Fact]
    public void Create_KnownProvider_ReturnsAgentNamedForForeman()
    {
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("claude")], new FakeCliProcessRunner());
        var config = new ForemanConfig("Frontend", CrewRole.Foreman, "claude", "dir", "instructions.md", new Dictionary<string, string>());

        var agent = factory.Create(config);

        Assert.Equal("Frontend", agent.Name);
    }
}
