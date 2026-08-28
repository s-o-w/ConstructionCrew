namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// One turn to send to a CLI-backed agent: the prompt, where it should run, its
/// provider-specific options, and whether this continues a prior conversation in
/// that same working directory (used by the GC's ongoing chat with the Boss).
/// </summary>
public sealed record CliTaskRequest(
    string Prompt,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> ProviderOptions,
    bool ContinuePreviousConversation = false);

/// <summary>
/// A provider's answer to "how do I actually run this": an executable plus argv.
/// Deliberately has no CliWrap dependency — Core stays free of it.
/// </summary>
public sealed record CliInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record CliRunResult(
    bool Succeeded,
    string StandardOutput,
    string StandardError,
    int ExitCode);
