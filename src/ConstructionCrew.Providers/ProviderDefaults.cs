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
}
