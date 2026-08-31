using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Providers;

/// <summary>
/// The starting ProviderOptions a newly hired Foreman gets, per provider. These are
/// NOT interchangeable: each CLI has its own permission vocabulary, so stamping Claude
/// Code's "Bash,Edit,Read,Write" onto a Copilot Foreman would silently grant it
/// nothing. Every value below comes from each CLI's own `--help` output, not
/// guessed by analogy to another provider's.
/// </summary>
public static class ProviderDefaults
{
    /// <summary>
    /// A working-but-not-unrestricted default: the Foreman can run shell commands and
    /// write files inside its own Jobsite, which is the whole job.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToolPolicy(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            // Claude Code tool names already used by the shipped GC config, plus
            // the Home Office tools: under `claude -p`, an allowlist missing them
            // silently denies every ask_gc and file_sitrep, so the Foreman works
            // but never reports (same failure mode GcToolPolicy guards against for
            // dispatch).
            "claude" => new Dictionary<string, string>
            {
                ["allowedTools"] = string.Join(
                    ',',
                    new[] { "Bash", "Edit", "Read", "Write" }.Concat(ForemanHomeOfficeTools)),
            },

            // Copilot's permission patterns are kind(argument): bare "shell" allows all
            // shell commands, "write" allows file create/modify, and a bare MCP server
            // name allows all of that server's tools (`copilot help permissions`).
            "copilot" => new Dictionary<string, string>
            {
                ["allowedTools"] = $"shell,write,{CopilotProvider.HomeOfficeServerName}",
            },

            // Codex has no per-tool allowlist; permissions are a sandbox policy.
            // workspace-write is the analogue of "can edit its own repo".
            "codex" => new Dictionary<string, string> { ["sandbox"] = "workspace-write" },

            _ => new Dictionary<string, string>(),
        };

    /// <summary>
    /// The Home Office MCP tools a Foreman must be able to call: report
    /// (file_sitrep), escalate (ask_gc), delegate (spawn_worker plus the two
    /// worktree tools that close a Worker out), and refresh the graph after a
    /// sitewalk. No dispatch_task: a Foreman never dispatches to other Foremen.
    /// </summary>
    private static readonly string[] ForemanHomeOfficeTools =
    [
        "mcp__home_office__file_sitrep",
        "mcp__home_office__ask_gc",
        "mcp__home_office__spawn_worker",
        "mcp__home_office__merge_worker_branch",
        "mcp__home_office__close_worktree",
        "mcp__home_office__get_job_status",
        "mcp__home_office__list_foremen",
        "mcp__home_office__list_jobsites",
        "mcp__home_office__build_graph",
        "mcp__home_office__query_graph",
    ];

    /// <summary>
    /// The Home Office MCP tools GC must be able to call. Deliberately not the
    /// bare server name `mcp__home_office` (used by the copilot branch): for
    /// claude, that form would also hand GC `ask_gc`, `merge_worker_branch`, and
    /// `close_worktree`. This explicit list denies those by omission. `ask_gc`
    /// stays out because GC never calls it (gc-instructions.md); granting it would
    /// let GC escalate to itself.
    /// </summary>
    private static readonly string[] HomeOfficeTools =
    [
        "mcp__home_office__list_foremen",
        "mcp__home_office__list_jobsites",
        "mcp__home_office__dispatch_task",
        "mcp__home_office__spawn_worker",
        "mcp__home_office__ask_foreman",
        "mcp__home_office__get_job_status",
        "mcp__home_office__build_graph",
        "mcp__home_office__query_graph",
        "mcp__home_office__file_sitrep",
    ];

    /// <summary>
    /// GC's starting ProviderOptions, distinct from a Foreman's: GC dispatches and
    /// authors, never running shell commands itself. It needs the Home Office MCP
    /// tools (an allowlist missing them silently denies every dispatch, so GC
    /// talks but never delegates) and Write/Edit, since writing the workorder is
    /// step one of the work loop.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GcToolPolicy(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "claude" => new Dictionary<string, string>
            {
                ["allowedTools"] = string.Join(
                    ',',
                    new[] { "Read", "Glob", "Grep", "Write", "Edit" }.Concat(HomeOfficeTools)),
            },

            // Copilot allows a whole MCP server by bare name. `write` is Copilot's
            // permission kind for "allow all file editing". No `shell`: GC runs no
            // commands.
            "copilot" => new Dictionary<string, string>
            {
                ["allowedTools"] = $"write,{CopilotProvider.HomeOfficeServerName}",
            },

            // Codex has no per-tool allowlist; the sandbox policy is the only lever.
            // GC's WorkingDirectory is the Vault, so workspace-write scopes GC's
            // writes to the Vault and nothing else.
            "codex" => new Dictionary<string, string> { ["sandbox"] = "workspace-write" },

            _ => new Dictionary<string, string>(),
        };

    /// <summary>
    /// A crew member's full ProviderOptions for <paramref name="provider"/>: that
    /// CLI's tool policy plus its Home Office wiring. Tool policies are not
    /// portable across CLIs, so a provider switch resets the policy, but wiring is
    /// re-applied, not dropped. Hand-tuning done via provider options is lost on a
    /// provider switch, the same as re-hiring under a new provider.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ComposeProviderOptions(
        CrewRole role,
        string provider,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> mcpOptionsByProvider)
    {
        var composed = new Dictionary<string, string>(
            role == CrewRole.GC ? GcToolPolicy(provider) : ToolPolicy(provider));

        if (mcpOptionsByProvider.TryGetValue(provider, out var mcpOptions))
        {
            foreach (var option in mcpOptions)
            {
                composed[option.Key] = option.Value;
            }
        }

        return composed;
    }

    /// <summary>
    /// The GC options a roster MUST end up with, merged over whatever foremen.yaml
    /// supplied. Union, not replacement: a Boss who added an option by hand keeps it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnsureGcToolPolicy(
        string providerId,
        IReadOnlyDictionary<string, string> current)
    {
        var required = GcToolPolicy(providerId);
        if (required.Count == 0)
        {
            return current;
        }

        // Codex: the sandbox policy is the whole permission model, so "upgrade" means
        // replacing an absent or explicitly read-only value. Anything else the Boss
        // chose (danger-full-access, say) is left alone.
        if (required.TryGetValue("sandbox", out var requiredSandbox))
        {
            var hasSandbox = current.TryGetValue("sandbox", out var currentSandbox);
            var needsUpgrade = !hasSandbox
                || string.IsNullOrWhiteSpace(currentSandbox)
                || string.Equals(currentSandbox!.Trim(), "read-only", StringComparison.OrdinalIgnoreCase);

            if (!needsUpgrade)
            {
                return current;
            }

            var upgraded = new Dictionary<string, string>(current) { ["sandbox"] = requiredSandbox };
            return upgraded;
        }

        if (!required.TryGetValue("allowedTools", out var requiredTools))
        {
            return current;
        }

        current.TryGetValue("allowedTools", out var currentTools);

        var merged = Split(currentTools).ToList();
        var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
        var added = false;

        foreach (var tool in Split(requiredTools))
        {
            if (seen.Add(tool))
            {
                merged.Add(tool);
                added = true;
            }
        }

        if (!added)
        {
            return current;
        }

        return new Dictionary<string, string>(current) { ["allowedTools"] = string.Join(',', merged) };
    }

    private static IEnumerable<string> Split(string? list) =>
        (list ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
