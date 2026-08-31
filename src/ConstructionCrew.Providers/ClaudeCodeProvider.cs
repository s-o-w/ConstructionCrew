using System.Text.Json;
using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>
/// Drives the Claude Code CLI non-interactively. Flags verified via `claude --help`
/// and a live `claude mcp add --transport http` probe on 2026-08-28 (see
/// IMPLEMENTATION-PLAN.md): not guessed from memory.
/// </summary>
public sealed class ClaudeCodeProvider : ICliToolProvider
{
    private readonly string _executablePath;

    public ClaudeCodeProvider(string executablePath = "claude")
    {
        _executablePath = executablePath;
    }

    /// <summary>
    /// providerOptions key that puts a Foreman on `--output-format json`. Now a
    /// default for every Claude crew member (ProviderDefaults.EnsureSessionAccounting),
    /// not an opt-in: the envelope is the only place the CLI reports its own
    /// session_id, and without that there is nothing to key a resume or a
    /// transcript tail off. PostProcess unwraps `result` back to plain text, so
    /// nothing downstream sees the JSON.
    /// </summary>
    public const string OutputFormatOption = "outputFormat";

    /// <summary>
    /// providerOptions key that emits `--permission-mode &lt;value&gt;`. Per-crew-member
    /// only, set via `/foreman &lt;Name&gt;` -> provider options; no global toggle.
    /// Valid values (`claude --help`, 2026-08-30): acceptEdits, auto, bypassPermissions,
    /// manual, dontAsk, plan. Composes with an allowlist; does not replace one.
    /// </summary>
    public const string PermissionModeOption = "permissionMode";

    public string ProviderId => "claude";

    public string ExecutableName => _executablePath;

    public CliInvocation BuildInvocation(CliTaskRequest request)
    {
        var args = new List<string> { "-p" };

        if (request.ContinuePreviousConversation)
        {
            args.Add("--continue");
        }

        if (WantsJsonOutput(request))
        {
            args.Add("--output-format");
            args.Add("json");
        }

        if (request.ProviderOptions.TryGetValue("allowedTools", out var allowedTools) && !string.IsNullOrWhiteSpace(allowedTools))
        {
            args.Add("--allowedTools");
            args.Add(allowedTools);
        }
        // The three permission flags are independent. Chaining them with `else if`
        // made --dangerously-skip-permissions unreachable for any crew member with
        // an allowlist, i.e. all of them.
        if (request.ProviderOptions.TryGetValue(PermissionModeOption, out var permissionMode) && !string.IsNullOrWhiteSpace(permissionMode))
        {
            args.Add("--permission-mode");
            args.Add(permissionMode);
        }

        if (request.ProviderOptions.TryGetValue("dangerouslySkipPermissions", out var skipRaw) &&
            bool.TryParse(skipRaw, out var skip) && skip)
        {
            args.Add("--dangerously-skip-permissions");
        }

        // addDir stays as a single-value providerOptions escape hatch; AddDirs is
        // the typed list (a Vault root plus this repo, for GC) and emits one
        // --add-dir per entry.
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
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
        }

        if (request.ProviderOptions.TryGetValue("appendSystemPrompt", out var systemPrompt) && !string.IsNullOrWhiteSpace(systemPrompt))
        {
            args.Add("--append-system-prompt");
            args.Add(systemPrompt);
        }

        // "--" ends option parsing before the positional prompt. Without it,
        // variadic options like --mcp-config/--allowedTools swallow the prompt as
        // one more value in their own list instead of leaving it as the prompt.
        // Confirmed against the real CLI on 2026-08-28.
        args.Add("--");
        args.Add(request.Prompt);

        return new CliInvocation(_executablePath, args, request.WorkingDirectory);
    }

    /// <summary>
    /// On an opt-in `--output-format json` turn, stdout is one result envelope:
    ///
    ///     {"type":"result","subtype":"success","is_error":false,"result":"...",
    ///      "session_id":"...","total_cost_usd":0.0123,
    ///      "usage":{"input_tokens":4,"cache_creation_input_tokens":0,
    ///               "cache_read_input_tokens":0,"output_tokens":100}}
    ///
    /// Parsed into CliUsage, with `result` unwrapped back into StandardOutput so
    /// everything downstream (a job's Summary, a sitrep, the Boss's transcript)
    /// still sees the answer text, not a JSON blob. The raw envelope is kept
    /// verbatim in CliUsage.RawJson.
    ///
    /// Never throws and never changes Succeeded: stdout that fails to parse (a
    /// crashed CLI, a non-JSON error dump) is passed through unchanged, with Usage
    /// left null. Accounting is best-effort; the turn's own result is not.
    /// </summary>
    public CliRunResult PostProcess(CliTaskRequest request, CliRunResult result)
    {
        if (!WantsJsonOutput(request) || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            long? inputTokens = null;
            long? outputTokens = null;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                // Sum every input-side counter: a cache read is still a charged
                // input token, and input_tokens alone would understate a cached turn.
                inputTokens = Sum(
                    ReadLong(usage, "input_tokens"),
                    ReadLong(usage, "cache_creation_input_tokens"),
                    ReadLong(usage, "cache_read_input_tokens"));
                outputTokens = ReadLong(usage, "output_tokens");
            }

            decimal? costUsd = null;
            if (root.TryGetProperty("total_cost_usd", out var cost) &&
                cost.ValueKind == JsonValueKind.Number &&
                cost.TryGetDecimal(out var parsedCost))
            {
                costUsd = parsedCost;
            }

            var text = root.TryGetProperty("result", out var resultText) && resultText.ValueKind == JsonValueKind.String
                ? resultText.GetString() ?? result.StandardOutput
                : result.StandardOutput;

            // The CLI's own id for the conversation this turn belonged to. Read
            // here rather than re-parsed by a caller, because this is the only
            // place the envelope is already open.
            var sessionId = root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String
                ? session.GetString()
                : null;

            return result with
            {
                StandardOutput = text,
                Usage = new CliUsage(inputTokens, outputTokens, costUsd, result.StandardOutput, sessionId),
            };
        }
        catch (JsonException)
        {
            return result;
        }
    }

    private static bool WantsJsonOutput(CliTaskRequest request) =>
        request.ProviderOptions.TryGetValue(OutputFormatOption, out var format) &&
        format.Trim().Equals("json", StringComparison.OrdinalIgnoreCase);

    private static long? ReadLong(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    /// <summary>Null when every part is missing: an absent counter is not a zero.</summary>
    private static long? Sum(params long?[] parts) =>
        parts.Any(p => p.HasValue) ? parts.Sum(p => p ?? 0) : null;
}
