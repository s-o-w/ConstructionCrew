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
        var args = new List<string> { "exec" };
        if (request.ContinuePreviousConversation)
        {
            args.Add("resume");
            args.Add("--last");
        }

        // -c takes key=value parsed as TOML, so the URL must be a quoted TOML
        // string. The only per-invocation MCP transport Codex has.
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
        if (request.ProviderOptions.TryGetValue("sandbox", out var sandbox) && !string.IsNullOrWhiteSpace(sandbox))
        {
            args.Add("--sandbox");
            args.Add(sandbox);
        }
        else if (request.ProviderOptions.TryGetValue("dangerouslySkipPermissions", out var skipRaw) &&
                 bool.TryParse(skipRaw, out var skip) && skip)
        {
            args.Add("--dangerously-bypass-approvals-and-sandbox");
        }

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

        // The Vault (GC's cwd) is not required to be a git repo, and `codex exec`
        // refuses to start outside one without this flag. Harmless when the cwd
        // is a repo.
        args.Add("--skip-git-repo-check");

        // Same defence as ClaudeCodeProvider: -i/--image and --add-dir are
        // multi-value, so end option parsing before the positional prompt.
        args.Add("--");
        args.Add(request.Prompt);

        return new CliInvocation(_executablePath, args, request.WorkingDirectory);
    }
}
