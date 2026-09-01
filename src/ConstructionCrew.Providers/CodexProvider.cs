using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>
/// Drives the OpenAI Codex CLI non-interactively via `codex exec`. Every flag here
/// was read off real `codex --help` / `codex exec --help` / `codex exec resume --help`
/// output captured on this machine on 2026-08-30. Nothing is recalled from memory.
///
/// MCP wiring is the one non-obvious part: Codex has no `--mcp-config` file flag.
/// Servers live in `$CODEX_HOME/config.toml` under `[mcp_servers.&lt;name&gt;]`, which was
/// confirmed by running `codex mcp add cc_probe --url ...` and reading the file back.
/// The documented way to inject that per-invocation is `-c &lt;dotted.path&gt;=&lt;toml value&gt;`,
/// confirmed live with `codex mcp list -c 'mcp_servers.cc_probe2.url="..."'`.
/// </summary>
public sealed class CodexProvider : ICliToolProvider
{
    /// <summary>Server name used in the `mcp_servers.&lt;name&gt;` TOML path. Must match McpConfigWriter's.</summary>
    public const string HomeOfficeServerName = "home_office";

    private readonly string _executablePath;

    public CodexProvider(string executablePath = "codex")
    {
        _executablePath = executablePath;
    }

    public string ProviderId => "codex";

    public string ExecutableName => _executablePath;

    public CliInvocation BuildInvocation(CliTaskRequest request)
    {
        // `codex exec` is the non-interactive entry point. `codex exec resume --last`
        // continues the newest session for this cwd (`codex exec resume --help`:
        // "[SESSION_ID] [PROMPT]", --last picks newest).
        //
        // IMPORTANT: `codex exec resume` has a different (smaller) flag set than bare
        // `codex exec`. Verified against `codex exec resume --help` on 2026-08-31:
        // --sandbox, --approve-for-me, and --add-dir are absent from `resume` and will
        // hard-fail the process if passed. Only -c, -m, --dangerously-bypass-*,
        // --skip-git-repo-check, and --last are shared. Gate accordingly below.
        var isResume = request.ContinuePreviousConversation;
        var args = new List<string> { "exec" };
        if (isResume)
        {
            args.Add("resume");
            args.Add("--last");
        }

        // -c takes key=value parsed as TOML, so the URL must be a quoted TOML
        // string. The only per-invocation MCP transport Codex has. Supported on
        // both exec and exec resume.
        if (request.ProviderOptions.TryGetValue("mcpServerUrl", out var mcpServerUrl) && !string.IsNullOrWhiteSpace(mcpServerUrl))
        {
            args.Add("-c");
            args.Add($"mcp_servers.{HomeOfficeServerName}.url=\"{mcpServerUrl}\"");
        }

        if (request.ProviderOptions.TryGetValue("model", out var model) && !string.IsNullOrWhiteSpace(model))
        {
            args.Add("-m");
            args.Add(model);
        }

        // Codex's permission model is a sandbox policy, not a tool allowlist.
        // `allowedTools` has no Codex equivalent, so it's ignored rather than
        // mapped onto an unrelated flag.
        // --sandbox is only valid on bare `exec`, not on `exec resume`.
        if (!isResume)
        {
            if (request.ProviderOptions.TryGetValue("sandbox", out var sandbox) && !string.IsNullOrWhiteSpace(sandbox))
            {
                if (sandbox.Equals("workspace-write", StringComparison.OrdinalIgnoreCase))
                {
                    // --approve-for-me implies workspace-write sandbox AND auto-approves
                    // MCP tool-call approval requests instead of failing with
                    // "approval policy is never". Using --sandbox workspace-write alone
                    // sets approval_policy:never (all approval requests are rejected).
                    // --approve-for-me and --sandbox are mutually exclusive: verified
                    // against `codex exec --help` on 2026-08-31.
                    args.Add("--approve-for-me");
                }
                else
                {
                    // read-only or danger-full-access: no auto-approve equivalent.
                    args.Add("--sandbox");
                    args.Add(sandbox);
                }
            }
            else if (request.ProviderOptions.TryGetValue("dangerouslySkipPermissions", out var skipRaw) &&
                     bool.TryParse(skipRaw, out var skip) && skip)
            {
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
        }
        else if (request.ProviderOptions.TryGetValue("dangerouslySkipPermissions", out var skipRaw) &&
                 bool.TryParse(skipRaw, out var skip) && skip)
        {
            // --dangerously-bypass-approvals-and-sandbox IS present on exec resume.
            args.Add("--dangerously-bypass-approvals-and-sandbox");
        }

        // --add-dir is only valid on bare `exec`, not on `exec resume`.
        if (!isResume)
        {
            if (request.ProviderOptions.TryGetValue("addDir", out var addDir) && !string.IsNullOrWhiteSpace(addDir))
            {
                args.Add("--add-dir");
                args.Add(addDir);
            }

            foreach (var dir in request.AddDirs ?? [])
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                args.Add("--add-dir");
                args.Add(dir);
            }
        }

        // The Vault (GC's cwd) is not required to be a git repo, and `codex exec`
        // refuses to start outside one without this flag. Harmless when the cwd
        // is a repo. Present on both exec and exec resume.
        args.Add("--skip-git-repo-check");

        // Same defence as ClaudeCodeProvider: -i/--image and --add-dir are
        // multi-value, so end option parsing before the positional prompt.
        args.Add("--");
        args.Add(request.Prompt);

        return new CliInvocation(_executablePath, args, request.WorkingDirectory);
    }

    /// <summary>
    /// Captures the session id so a Codex Foreman can be watched, and nothing
    /// else: no counters and no cost, because `codex exec` reports neither in a
    /// machine-readable form.
    ///
    /// <para>
    /// It comes off STDERR, which is worth stating because the obvious guess is
    /// wrong. Confirmed by running a real turn on 2026-08-31 with the two
    /// streams captured separately: stdout held exactly the answer text ("OK.")
    /// and nothing else, while stderr carried the startup banner -- version,
    /// workdir, model, sandbox, and the line `session id: &lt;uuid&gt;`. So there
    /// is no need for the heavier "find the newest rollout whose cwd matches"
    /// heuristic; the CLI states the id outright.
    /// </para>
    ///
    /// <para>
    /// This deliberately does NOT change how Codex resumes. BuildInvocation
    /// still uses `resume --last` and ignores ResumeSessionId: whether `codex
    /// exec resume &lt;id&gt;` behaves identically has not been verified, and an
    /// id captured for a read-only watch must not quietly become the mechanism
    /// a conversation's continuity depends on.
    /// </para>
    /// </summary>
    public CliRunResult PostProcess(CliTaskRequest request, CliRunResult result)
    {
        if (string.IsNullOrEmpty(result.StandardError))
        {
            return result;
        }

        var match = SessionIdBanner.Match(result.StandardError);
        if (!match.Success)
        {
            return result;
        }

        return result with
        {
            Usage = (result.Usage ?? new CliUsage(null, null, null, null)) with
            {
                SessionId = match.Groups["id"].Value,
            },
        };
    }

    /// <summary>Matches the banner line `session id: 01a059d4-fdb4-7023-91bb-d651add61b44`, copied from real stderr.</summary>
    private static readonly System.Text.RegularExpressions.Regex SessionIdBanner = new(
        @"^\s*session id:\s*(?<id>[0-9a-fA-F][0-9a-fA-F-]{7,})\s*$",
        System.Text.RegularExpressions.RegexOptions.Multiline |
        System.Text.RegularExpressions.RegexOptions.Compiled);
}
