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

    /// <summary>
    /// The three permission flags are independent. An allowlist no longer suppresses
    /// a permission mode, and "--" still ends option parsing before the prompt.
    /// </summary>
    [Fact]
    public void BuildInvocation_PermissionMode_EmitsFlagAlongsideAllowedTools()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "do the thing",
            "/work",
            new Dictionary<string, string>
            {
                ["allowedTools"] = "Read mcp__home_office",
                [ClaudeCodeProvider.PermissionModeOption] = "acceptEdits",
            });

        var args = provider.BuildInvocation(request).Arguments.ToList();

        var allowedIndex = args.IndexOf("--allowedTools");
        Assert.True(allowedIndex >= 0, "Expected --allowedTools.");
        Assert.Equal("Read mcp__home_office", args[allowedIndex + 1]);

        var modeIndex = args.IndexOf("--permission-mode");
        Assert.True(modeIndex >= 0, "Expected --permission-mode alongside the allowlist.");
        Assert.Equal("acceptEdits", args[modeIndex + 1]);

        var separatorIndex = args.IndexOf("--");
        Assert.True(allowedIndex < separatorIndex, "--allowedTools must precede the \"--\" terminator.");
        Assert.True(modeIndex < separatorIndex, "--permission-mode must precede the \"--\" terminator.");
        Assert.Equal("do the thing", args[separatorIndex + 1]);
        Assert.Equal(args.Count - 1, separatorIndex + 1);
    }

    /// <summary>
    /// Regression: dangerouslySkipPermissions used to sit in the `else if` branch of
    /// allowedTools, so any crew member with an allowlist (all of them) could never
    /// reach it.
    /// </summary>
    [Fact]
    public void BuildInvocation_DangerouslySkipPermissions_NoLongerShadowedByAllowedTools()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "do the thing",
            "/work",
            new Dictionary<string, string>
            {
                ["allowedTools"] = "Read",
                ["dangerouslySkipPermissions"] = "true",
            });

        var args = provider.BuildInvocation(request).Arguments.ToList();

        Assert.Contains("--allowedTools", args);
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.True(
            args.IndexOf("--dangerously-skip-permissions") < args.IndexOf("--"),
            "--dangerously-skip-permissions must precede the \"--\" terminator.");
        Assert.Equal("do the thing", args[^1]);
    }

    /// <summary>
    /// A real `claude -p --output-format json` result envelope, captured from the
    /// CLI's documented output shape -- not invented. `usage` counts every
    /// input-side bucket separately; a cached turn reports almost all of its input
    /// under cache_read_input_tokens.
    /// </summary>
    private const string JsonResultSample = """
        {"type":"result","subtype":"success","is_error":false,"duration_ms":6421,
         "duration_api_ms":6002,"num_turns":1,"result":"All 239 tests pass.",
         "session_id":"6f0d1e0e-1b7a-4c33-9a24-2f2c1f5f1a10","total_cost_usd":0.0731,
         "usage":{"input_tokens":12,"cache_creation_input_tokens":4210,
                  "cache_read_input_tokens":18332,"output_tokens":516}}
        """;

    [Fact]
    public void BuildInvocation_OutputFormatJson_EmitsTheFlagAheadOfTheTerminator()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "hello", "/work", new Dictionary<string, string> { ["outputFormat"] = "json" });

        var args = provider.BuildInvocation(request).Arguments.ToList();

        var flagIndex = args.IndexOf("--output-format");
        Assert.True(flagIndex >= 0, "Expected --output-format json when opted in.");
        Assert.Equal("json", args[flagIndex + 1]);
        Assert.True(flagIndex < args.IndexOf("--"), "--output-format must precede the \"--\" terminator.");
    }

    /// <summary>Opt-in, not default: an ordinary Foreman's invocation is untouched.</summary>
    [Fact]
    public void BuildInvocation_WithoutTheOption_NeverAsksForJson()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest("hello", "/work", new Dictionary<string, string>());

        Assert.DoesNotContain("--output-format", provider.BuildInvocation(request).Arguments);
    }

    [Fact]
    public void PostProcess_JsonRun_FillsUsageAndUnwrapsTheAnswerText()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "hello", "/work", new Dictionary<string, string> { ["outputFormat"] = "json" });

        var processed = provider.PostProcess(request, new CliRunResult(true, JsonResultSample, "", 0));

        Assert.Equal("All 239 tests pass.", processed.StandardOutput);
        Assert.NotNull(processed.Usage);
        // Every input-side bucket summed: 12 + 4210 + 18332.
        Assert.Equal(22554, processed.Usage!.InputTokens);
        Assert.Equal(516, processed.Usage.OutputTokens);
        Assert.Equal(0.0731m, processed.Usage.CostUsd);
        Assert.Equal(JsonResultSample, processed.Usage.RawJson);
        // The run's own outcome is untouched by accounting.
        Assert.True(processed.Succeeded);
        Assert.Equal(0, processed.ExitCode);
    }

    /// <summary>
    /// session_id is the field the whole watch/resume path is built on: it names
    /// one exact conversation, where --continue only ever means "whatever ran in
    /// this directory last". Read off the same envelope as the counters, so no
    /// caller has to parse the JSON a second time.
    /// </summary>
    [Fact]
    public void PostProcess_JsonRun_CapturesTheSessionId()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "hello", "/work", new Dictionary<string, string> { ["outputFormat"] = "json" });

        var processed = provider.PostProcess(request, new CliRunResult(true, JsonResultSample, "", 0));

        Assert.Equal("6f0d1e0e-1b7a-4c33-9a24-2f2c1f5f1a10", processed.Usage!.SessionId);
    }

    /// <summary>An envelope with no session_id is not an error: accounting still lands, the id just stays unavailable.</summary>
    [Fact]
    public void PostProcess_EnvelopeWithoutASessionId_LeavesItNull()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "hello", "/work", new Dictionary<string, string> { ["outputFormat"] = "json" });

        var processed = provider.PostProcess(
            request, new CliRunResult(true, """{"type":"result","result":"done"}""", "", 0));

        Assert.Null(processed.Usage!.SessionId);
    }

    /// <summary>Without the opt-in there is no envelope to read, so nothing is parsed and Usage stays null.</summary>
    [Fact]
    public void PostProcess_WithoutTheOption_LeavesTheResultExactlyAsItCame()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest("hello", "/work", new Dictionary<string, string>());
        var result = new CliRunResult(true, JsonResultSample, "", 0);

        Assert.Same(result, provider.PostProcess(request, result));
    }

    /// <summary>A crashed CLI dumps text, not JSON. Accounting is best-effort; the turn's result is not.</summary>
    [Fact]
    public void PostProcess_JsonRunThatDidNotProduceJson_IsHandedBackUnchanged()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "hello", "/work", new Dictionary<string, string> { ["outputFormat"] = "json" });
        var result = new CliRunResult(false, "Error: credit balance too low", "", 1);

        var processed = provider.PostProcess(request, result);

        Assert.Same(result, processed);
        Assert.Null(processed.Usage);
    }

    /// <summary>An envelope with no usage block still parses -- the counters just stay unavailable.</summary>
    [Fact]
    public void PostProcess_EnvelopeWithoutUsage_LeavesTheCountersNull()
    {
        var provider = new ClaudeCodeProvider();
        var request = new CliTaskRequest(
            "hello", "/work", new Dictionary<string, string> { ["outputFormat"] = "json" });

        var processed = provider.PostProcess(
            request, new CliRunResult(true, """{"type":"result","result":"done"}""", "", 0));

        Assert.Equal("done", processed.StandardOutput);
        Assert.NotNull(processed.Usage);
        Assert.Null(processed.Usage!.InputTokens);
        Assert.Null(processed.Usage.OutputTokens);
        Assert.Null(processed.Usage.CostUsd);
    }
}
