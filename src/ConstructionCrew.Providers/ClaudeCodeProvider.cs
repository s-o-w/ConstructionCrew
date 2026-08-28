using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>
/// Drives the Claude Code CLI non-interactively. Flags verified via `claude --help`
/// and a live `claude mcp add --transport http` probe on 2026-08-28 (see
/// IMPLEMENTATION-PLAN.md) -- not guessed from memory.
/// </summary>
public sealed class ClaudeCodeProvider : ICliToolProvider
{
    private readonly string _executablePath;

    public ClaudeCodeProvider(string executablePath = "claude")
    {
        _executablePath = executablePath;
    }

    public string ProviderId => "claude";

    public CliInvocation BuildInvocation(CliTaskRequest request)
    {
        var args = new List<string> { "-p" };

        if (request.ContinuePreviousConversation)
        {
            args.Add("--continue");
        }

        if (request.ProviderOptions.TryGetValue("allowedTools", out var allowedTools) && !string.IsNullOrWhiteSpace(allowedTools))
        {
            args.Add("--allowedTools");
            args.Add(allowedTools);
        }
        else if (request.ProviderOptions.TryGetValue("dangerouslySkipPermissions", out var skipRaw) &&
                 bool.TryParse(skipRaw, out var skip) && skip)
        {
            args.Add("--dangerously-skip-permissions");
        }

        if (request.ProviderOptions.TryGetValue("addDir", out var addDir) && !string.IsNullOrWhiteSpace(addDir))
        {
            args.Add("--add-dir");
            args.Add(addDir);
        }

        if (request.ProviderOptions.TryGetValue("mcpConfigPath", out var mcpConfigPath) && !string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
        }

        if (request.ProviderOptions.TryGetValue("appendSystemPrompt", out var systemPrompt) && !string.IsNullOrWhiteSpace(systemPrompt))
        {
            args.Add("--append-system-prompt");
            args.Add(systemPrompt);
        }

        // "--" ends option parsing before the positional prompt. Without it,
        // variadic options like --mcp-config/--allowedTools (which accept a
        // space-separated list) greedily swallow the prompt text as one more
        // value in their own list instead of leaving it as the prompt -- confirmed
        // by direct repro against the real CLI on 2026-08-28.
        args.Add("--");
        args.Add(request.Prompt);

        return new CliInvocation(_executablePath, args, request.WorkingDirectory);
    }
}
