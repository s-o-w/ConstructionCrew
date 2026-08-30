using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.ProvidersTests;

/// <summary>
/// Locks in the flag mapping captured from the real GitHub Copilot CLI on 2026-08-30
/// (docs/provider-flags/copilot-help.txt).
/// </summary>
public class CopilotProviderTests
{
    private static CliTaskRequest Request(
        IReadOnlyDictionary<string, string>? options = null,
        bool continuePrevious = false,
        IReadOnlyList<string>? addDirs = null) =>
        new("do the thing", "/work", options ?? new Dictionary<string, string>(), continuePrevious, addDirs);

    [Fact]
    public void BuildInvocation_PassesThePromptAsTheValueOfMinusP()
    {
        var invocation = new CopilotProvider().BuildInvocation(Request());
        var args = invocation.Arguments.ToList();

        Assert.Equal("copilot", invocation.ExecutablePath);
        Assert.Equal(["-p", "do the thing"], args);
    }

    [Fact]
    public void BuildInvocation_SplitsAllowedToolsIntoOneFlagPerTool()
    {
        // --allow-tool is variadic ("[tools...]") on the real CLI. One flag per tool
        // keeps it from swallowing whatever argument follows.
        var args = new CopilotProvider()
            .BuildInvocation(Request(new Dictionary<string, string> { ["allowedTools"] = "shell, write ,home_office" }))
            .Arguments.ToList();

        Assert.Equal(3, args.Count(a => a == "--allow-tool"));
        Assert.Contains("shell", args);
        Assert.Contains("write", args);
        Assert.Contains("home_office", args);
        // The prompt is still the value of -p, never mistaken for a tool name.
        Assert.Equal("do the thing", args[^1]);
        Assert.Equal("-p", args[^2]);
    }

    [Fact]
    public void BuildInvocation_FallsBackToAllowAllToolsWhenPermissionsAreSkipped()
    {
        var args = new CopilotProvider()
            .BuildInvocation(Request(new Dictionary<string, string> { ["dangerouslySkipPermissions"] = "true" }))
            .Arguments.ToList();

        Assert.Contains("--allow-all-tools", args);
        Assert.DoesNotContain("--allow-tool", args);
    }

    [Fact]
    public void BuildInvocation_PrefixesTheMcpConfigPathWithAt()
    {
        // `--additional-mcp-config <json>`: "JSON string or file path (prefix with @)".
        var args = new CopilotProvider()
            .BuildInvocation(Request(new Dictionary<string, string> { ["mcpConfigPath"] = "/cfg/copilot-mcp-config.json" }))
            .Arguments.ToList();

        var index = args.IndexOf("--additional-mcp-config");
        Assert.True(index >= 0);
        Assert.Equal("@/cfg/copilot-mcp-config.json", args[index + 1]);
    }

    [Fact]
    public void BuildInvocation_ContinuesWithContinueFlag_AndEmitsOneAddDirPerEntry()
    {
        var args = new CopilotProvider()
            .BuildInvocation(Request(continuePrevious: true, addDirs: ["/repo", "/vault"]))
            .Arguments.ToList();

        Assert.Equal("--continue", args[0]);
        Assert.Equal(2, args.Count(a => a == "--add-dir"));
        Assert.Equal("/repo", args[args.IndexOf("--add-dir") + 1]);
        Assert.Equal("/vault", args[args.LastIndexOf("--add-dir") + 1]);
    }

    [Fact]
    public void ExecutableName_IsProbedAsCopilot()
    {
        ICliToolProvider provider = new CopilotProvider();
        Assert.Equal("copilot", provider.ExecutableName);
        Assert.True(provider.IsImplemented);
    }
}
