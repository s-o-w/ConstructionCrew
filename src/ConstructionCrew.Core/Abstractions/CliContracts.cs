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
    bool ContinuePreviousConversation = false,
    IReadOnlyList<string>? AddDirs = null);

/// <summary>
/// A provider's answer to "how do I actually run this": an executable plus argv.
/// Deliberately has no CliWrap dependency — Core stays free of it.
/// </summary>
public sealed record CliInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

/// <summary>
/// Token/cost accounting for one CLI turn. Stays null for any provider whose
/// usage output has not been verified against a real run -- same discipline as
/// GeminiProvider's deliberate throw.
/// </summary>
public sealed record CliUsage(
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd,
    string? RawJson);

public sealed record CliRunResult(
    bool Succeeded,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    CliUsage? Usage = null);
