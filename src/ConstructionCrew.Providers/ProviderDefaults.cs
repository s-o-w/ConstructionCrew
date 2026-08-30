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
            // Claude Code tool names, as already used by the shipped GC config.
            "claude" => new Dictionary<string, string> { ["allowedTools"] = "Bash,Edit,Read,Write" },

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

    /// <summary>The Home Office MCP tools GC has to be able to call to do its job at all.</summary>
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
    ];

    /// <summary>
    /// GC's starting ProviderOptions, which are NOT a Foreman's. GC reads and
    /// dispatches; it never edits code or runs shell commands itself. The one
    /// thing it must have is the Home Office MCP tools -- under `claude -p` an
    /// allow-list that omits them silently denies every dispatch, which reads as
    /// a GC that talks but never delegates.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GcToolPolicy(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "claude" => new Dictionary<string, string>
            {
                ["allowedTools"] = string.Join(',', new[] { "Read", "Glob", "Grep" }.Concat(HomeOfficeTools)),
            },

            // Copilot allows a whole MCP server by bare name; no shell, no write.
            "copilot" => new Dictionary<string, string>
            {
                ["allowedTools"] = CopilotProvider.HomeOfficeServerName,
            },

            // Codex has no per-tool allow-list; read-only is the analogue of
            // "can look, cannot touch."
            "codex" => new Dictionary<string, string> { ["sandbox"] = "read-only" },

            _ => new Dictionary<string, string>(),
        };
}
