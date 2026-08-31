namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// One turn to a CLI-backed agent: the prompt, where it runs, its
/// provider-specific options, and how it rejoins a prior conversation (used by
/// GC's ongoing chat with the Boss).
/// </summary>
/// <param name="ContinuePreviousConversation">
/// "Rejoin whatever ran here last." A directory-scoped guess, and the weaker of
/// the two: only correct while nothing else runs the same CLI in the same
/// working directory.
/// </param>
/// <param name="ResumeSessionId">
/// The exact conversation to rejoin. Preferred over
/// <paramref name="ContinuePreviousConversation"/> whenever it is known,
/// because it names one session instead of describing a place.
/// </param>
public sealed record CliTaskRequest(
    string Prompt,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> ProviderOptions,
    bool ContinuePreviousConversation = false,
    IReadOnlyList<string>? AddDirs = null,
    string? ResumeSessionId = null);

/// <summary>
/// A provider's answer to "how do I run this": an executable plus argv.
/// Deliberately has no CliWrap dependency; Core stays free of it.
/// </summary>
public sealed record CliInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

/// <summary>
/// Token/cost accounting for one CLI turn, plus the CLI's own id for the
/// conversation that turn belonged to. Stays null for any provider whose usage
/// output hasn't been verified against a real run, same discipline as
/// GeminiProvider's deliberate throw.
/// </summary>
/// <param name="SessionId">
/// The engine's own session identifier, when its result envelope carries one.
/// A first-class field rather than something callers re-parse out of
/// <paramref name="RawJson"/>: it is what keys a resume to one exact
/// conversation, and what locates that conversation's transcript on disk.
/// </param>
public sealed record CliUsage(
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd,
    string? RawJson,
    string? SessionId = null);

public sealed record CliRunResult(
    bool Succeeded,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    CliUsage? Usage = null);
