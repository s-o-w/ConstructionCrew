using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>
/// Drives the GitHub Copilot CLI non-interactively via `copilot -p`. Every flag here
/// was read off real `copilot --help` output captured on this machine on 2026-08-30.
/// Nothing is recalled from memory.
///
/// Two things the help text states outright and this class relies on:
/// `--allow-all-tools` is "required for non-interactive mode", and
/// `--additional-mcp-config` takes "JSON string or file path (prefix with @)" --
/// hence the "@" in front of the config path.
/// </summary>
public sealed class CopilotProvider : ICliToolProvider
{
    /// <summary>Server key used in the copilot mcp-config JSON. Must match McpConfigWriter's.</summary>
    public const string HomeOfficeServerName = "home_office";

    private readonly string _executablePath;

    public CopilotProvider(string executablePath = "copilot")
    {
        _executablePath = executablePath;
    }

    public string ProviderId => "copilot";

    public string ExecutableName => _executablePath;

    public CliInvocation BuildInvocation(CliTaskRequest request)
    {
        var args = new List<string>();

        if (request.ContinuePreviousConversation)
        {
            // --continue resumes the most recent session; --resume [sessionId] is the
            // pick-one form, unusable without a session id.
            args.Add("--continue");
        }

        // --allow-tool is variadic ("[tools...]"). One flag per tool avoids it
        // swallowing the next argument, the same failure mode hit with Claude
        // Code's --allowedTools.
        if (request.ProviderOptions.TryGetValue("allowedTools", out var allowedTools) && !string.IsNullOrWhiteSpace(allowedTools))
        {
            foreach (var tool in allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                args.Add("--allow-tool");
                args.Add(tool);
            }
        }
        else if (request.ProviderOptions.TryGetValue("dangerouslySkipPermissions", out var skipRaw) &&
                 bool.TryParse(skipRaw, out var skip) && skip)
        {
            args.Add("--allow-all-tools");
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

        if (request.ProviderOptions.TryGetValue("mcpConfigPath", out var mcpConfigPath) && !string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            args.Add("--additional-mcp-config");
            args.Add("@" + mcpConfigPath);
        }

        if (request.ProviderOptions.TryGetValue("model", out var model) && !string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }

        // Copilot has no --append-system-prompt equivalent; custom instructions come
        // from AGENTS.md-style files on disk (`copilot help config`). appendSystemPrompt
        // is not mapped to any flag.

        // The prompt is a flag value here, not positional, so no end-of-options
        // terminator is needed: -p takes exactly one argument.
        args.Add("-p");
        args.Add(request.Prompt);

        return new CliInvocation(_executablePath, args, request.WorkingDirectory);
    }
}
