using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.ProvidersTests;

/// <summary>
/// Locks in the flag mapping captured from the real codex CLI on 2026-08-30. The
/// full argv shape asserted here was also run end to end against the live binary.
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

    /// <summary>
    /// The banner is on STDERR, not stdout. Confirmed on 2026-08-31 by running
    /// a real turn with the streams captured separately: stdout held exactly
    /// the answer text, stderr held version/workdir/model/sandbox and the
    /// session id line reproduced below.
    /// </summary>
    private const string StartupBanner = """
        OpenAI Codex v0.144.6
        --------
        workdir: C:\work
        model: gpt-5.6-sol
        provider: openai
        approval: on-request
        sandbox: read-only
        reasoning effort: medium
        reasoning summaries: none
        session id: 01a059d4-fdb4-7023-91bb-d651add61b44
        --------
        """;

    [Fact]
    public void PostProcess_ReadsTheSessionIdOffStderr()
    {
        var processed = new CodexProvider()
            .PostProcess(Request(), new CliRunResult(true, "OK.", StartupBanner, 0));

        Assert.Equal("01a059d4-fdb4-7023-91bb-d651add61b44", processed.Usage!.SessionId);

        // Watch-only: nothing here claims counters or cost Codex never reported.
        Assert.Null(processed.Usage.InputTokens);
        Assert.Null(processed.Usage.CostUsd);
        // And the turn's own answer is untouched.
        Assert.Equal("OK.", processed.StandardOutput);
    }

    /// <summary>Stderr with no banner (a crash dump, an empty stream) is not an error -- there is simply no id to report.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Error: something went wrong")]
    public void PostProcess_WithoutABanner_LeavesTheResultAlone(string stderr)
    {
        var result = new CliRunResult(false, "", stderr, 1);

        Assert.Same(result, new CodexProvider().PostProcess(Request(), result));
    }

    /// <summary>
    /// Capturing the id must NOT change how Codex resumes. `codex exec resume
    /// &lt;id&gt;` is unverified, so continuity stays on `resume --last` and an
    /// id captured for a read-only watch never becomes what a conversation
    /// depends on.
    /// </summary>
    [Fact]
    public void BuildInvocation_WithASessionId_StillResumesWithLast()
    {
        var args = new CodexProvider()
            .BuildInvocation(new CliTaskRequest(
                "do the thing", "/work", new Dictionary<string, string>(),
                ContinuePreviousConversation: true,
                ResumeSessionId: "01a059d4-fdb4-7023-91bb-d651add61b44"))
            .Arguments.ToList();

        Assert.Contains("resume", args);
        Assert.Contains("--last", args);
        Assert.DoesNotContain("01a059d4-fdb4-7023-91bb-d651add61b44", args);
    }
}
