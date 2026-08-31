using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Providers;

/// <summary>
/// The starting ProviderOptions a newly hired Foreman gets, per provider. These are
/// NOT interchangeable: each CLI has its own permission vocabulary, so stamping Claude
/// Code's "Bash,Edit,Read,Write" onto a Copilot Foreman would silently grant it
/// nothing. Every value below comes from the captured help in docs/provider-flags/.
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
            // Claude Code tool names, as already used by the shipped GC config,
            // PLUS the Home Office tools -- under `claude -p` an allow-list that
            // omits them silently denies every ask_gc and file_sitrep, which reads
            // as a Foreman that works but never reports (the same failure mode
            // GcToolPolicy below already guards against for dispatch).
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

            // Codex has no per-tool allowlist at all -- permissions are a sandbox
            // policy. workspace-write is the analogue of "can edit its own repo".
            "codex" => new Dictionary<string, string> { ["sandbox"] = "workspace-write" },

            _ => new Dictionary<string, string>(),
        };

    /// <summary>
    /// The Home Office MCP tools a Foreman has to be able to call to do its job at
    /// all: report (file_sitrep), escalate (ask_gc), delegate (spawn_worker and the
    /// two worktree tools that close a Worker out), and refresh the graph after a
    /// sitewalk. No dispatch_task -- a Foreman does not dispatch to other Foremen.
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
    /// The Home Office MCP tools GC has to be able to call to do its job at all.
    /// Deliberately NOT the bare server name `mcp__home_office`: that form works,
    /// and is what the copilot branch relies on, but for claude it would also hand
    /// GC `ask_gc`, `merge_worker_branch` and `close_worktree`. The explicit list is
    /// the deny-by-omission that keeps those away. `ask_gc` in particular stays out:
    /// GC never calls it (gc-instructions.md), and granting it would let GC escalate
    /// to itself.
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
    /// GC's starting ProviderOptions, which are NOT a Foreman's. GC dispatches and
    /// authors; it never runs shell commands itself. Two things it must have:
    /// the Home Office MCP tools -- under `claude -p` an allow-list that omits them
    /// silently denies every dispatch, which reads as a GC that talks but never
    /// delegates -- and Write/Edit, because writing the workorder is step one of
    /// the work loop.
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
            // own permission kind for "allow all file editing". No `shell`: GC still
            // runs no commands.
            "copilot" => new Dictionary<string, string>
            {
                ["allowedTools"] = $"write,{CopilotProvider.HomeOfficeServerName}",
            },

            // Codex has no per-tool allow-list; the sandbox policy is the only lever.
            // GC's WorkingDirectory is the Vault, so workspace-write scopes GC's
            // writes to the Vault and nothing else.
            "codex" => new Dictionary<string, string> { ["sandbox"] = "workspace-write" },

            _ => new Dictionary<string, string>(),
        };

    /// <summary>
    /// A crew member's full ProviderOptions for <paramref name="provider"/>: that
    /// CLI's own tool policy, plus the Home Office wiring for it. Tool policies are
    /// NOT portable across CLIs (see ToolPolicy's own comment), so a provider switch
    /// resets the policy -- but the wiring has to be re-applied, not dropped.
    /// Any hand-tuning done via "provider options" is lost on a provider switch,
    /// same as it would be re-hiring under the new provider.
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
        // chose (danger-full-access, say) is theirs and is left alone.
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
