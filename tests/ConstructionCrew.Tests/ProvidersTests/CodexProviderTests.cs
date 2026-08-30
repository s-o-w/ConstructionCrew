using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.ProvidersTests;

/// <summary>
/// Locks in the flag mapping captured from the real codex CLI on 2026-08-30
/// (docs/provider-flags/codex-help.txt). The full argv shape asserted here was also
/// run end to end against the live binary.
/// </summary>
public class CodexProviderTests
{
    private static CliTaskRequest Request(
        IReadOnlyDictionary<string, string>? options = null,
        bool continuePrevious = false,
        IReadOnlyList<string>? addDirs = null) =>
        new("do the thing", "/work", options ?? new Dictionary<string, string>(), continuePrevious, addDirs);

    [Fact]
    public void BuildInvocation_UsesExecSubcommand_AndTerminatesOptionsBeforePrompt()
    {
        var invocation = new CodexProvider().BuildInvocation(Request());
        var args = invocation.Arguments.ToList();

        Assert.Equal("codex", invocation.ExecutablePath);
        Assert.Equal("exec", args[0]);
        Assert.Equal(["exec", "--skip-git-repo-check", "--", "do the thing"], args);
    }

    [Fact]
    public void BuildInvocation_ContinuesViaExecResumeLast()
    {
        // `codex exec resume --last -- <prompt>` binds the text to PROMPT, not
        // SESSION_ID -- verified by running it against the real CLI.
        var args = new CodexProvider().BuildInvocation(Request(continuePrevious: true)).Arguments.ToList();

        Assert.Equal(["exec", "resume", "--last"], args.Take(3));
        Assert.Equal("do the thing", args[^1]);
        Assert.Equal("--", args[^2]);
    }

    [Fact]
    public void BuildInvocation_WiresMcpThroughDottedTomlOverride()
    {
        // Codex has no --mcp-config flag; -c key=value with a TOML-quoted string is
        // the documented (and live-verified) transport.
        var args = new CodexProvider()
            .BuildInvocation(Request(new Dictionary<string, string> { ["mcpServerUrl"] = "http://127.0.0.1:5199/mcp" }))
            .Arguments.ToList();

        var index = args.IndexOf("-c");
        Assert.True(index >= 0);
        Assert.Equal("mcp_servers.home_office.url=\"http://127.0.0.1:5199/mcp\"", args[index + 1]);
    }

    [Fact]
    public void BuildInvocation_MapsSandboxAndBypass_AndPrefersAnExplicitSandbox()
    {
        var sandboxed = new CodexProvider()
            .BuildInvocation(Request(new Dictionary<string, string>
            {
                ["sandbox"] = "workspace-write",
                ["dangerouslySkipPermissions"] = "true",
            }))
            .Arguments.ToList();

        Assert.Contains("--sandbox", sandboxed);
        Assert.Equal("workspace-write", sandboxed[sandboxed.IndexOf("--sandbox") + 1]);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", sandboxed);

        var bypassed = new CodexProvider()
            .BuildInvocation(Request(new Dictionary<string, string> { ["dangerouslySkipPermissions"] = "true" }))
            .Arguments.ToList();

        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", bypassed);
    }

    [Fact]
    public void BuildInvocation_EmitsOneAddDirPerEntry_AllAheadOfTheTerminator()
    {
        var args = new CodexProvider()
            .BuildInvocation(Request(addDirs: ["/repo", "/vault"]))
            .Arguments.ToList();

        Assert.Equal(2, args.Count(a => a == "--add-dir"));
        Assert.Equal("/repo", args[args.IndexOf("--add-dir") + 1]);
        Assert.Equal("/vault", args[args.LastIndexOf("--add-dir") + 1]);
        Assert.True(args.LastIndexOf("--add-dir") < args.IndexOf("--"));
    }

    [Fact]
    public void ExecutableName_IsProbedAsCodex()
    {
        ICliToolProvider provider = new CodexProvider();
        Assert.Equal("codex", provider.ExecutableName);
        Assert.True(provider.IsImplemented);
    }
}
