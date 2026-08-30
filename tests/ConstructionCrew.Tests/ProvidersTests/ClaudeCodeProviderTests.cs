using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.ProvidersTests;

public class ClaudeCodeProviderTests
{
    [Fact]
    public void BuildInvocation_AlwaysEndsOptionsBeforePrompt()
    {
        // Regression test for a real bug hit 2026-08-28: --mcp-config (and
        // --allowedTools) are variadic on the real CLI and swallow the next
        // positional argument -- including the prompt itself -- unless "--"
        // explicitly ends option parsing first. Confirmed by direct repro
        // against the real `claude` binary; this test locks the fix in.
        var provider = new ClaudeCodeProvider();
        var options = new Dictionary<string, string>
        {
            ["allowedTools"] = "Read",
            ["mcpConfigPath"] = "C:\\some\\config.json",
        };
        var request = new CliTaskRequest("do the thing", "C:\\work", options);

        var invocation = provider.BuildInvocation(request);

        var separatorIndex = invocation.Arguments.ToList().IndexOf("--");
        Assert.True(separatorIndex >= 0, "Expected a \"--\" end-of-options marker.");
        Assert.Equal("do the thing", invocation.Arguments[separatorIndex + 1]);
        Assert.Equal(invocation.Arguments.Count - 1, separatorIndex + 1);
    }

    [Fact]
    public void BuildInvocation_EmitsOneAddDirPerEntry_AllAheadOfTheTerminator()
    {
        // GC's cwd is the Vault; ConstructionCrew's own repo (and anything else
        // it needs to read) arrives as its own --add-dir. One flag per entry --
        // Claude Code has no multi-value form that survives the "--" terminator.
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "do the thing",
            "/vault",
            new Dictionary<string, string>(),
            AddDirs: ["/repo", "/other"]);

        var invocation = provider.BuildInvocation(request);
        var args = invocation.Arguments.ToList();

        Assert.Equal(2, args.Count(a => a == "--add-dir"));
        Assert.Equal("/repo", args[args.IndexOf("--add-dir") + 1]);
        Assert.Equal("/other", args[args.LastIndexOf("--add-dir") + 1]);
        Assert.True(args.LastIndexOf("--add-dir") < args.IndexOf("--"), "Every --add-dir must precede the \"--\" terminator.");
    }

    [Fact]
    public void BuildInvocation_NoOptionalFlags_StillTerminatesBeforePrompt()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest("hello", "C:\\work", new Dictionary<string, string>());

        var invocation = provider.BuildInvocation(request);

        Assert.Equal(["-p", "--", "hello"], invocation.Arguments);
    }
}
