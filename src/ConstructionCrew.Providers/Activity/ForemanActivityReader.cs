namespace ConstructionCrew.Providers.Activity;

/// <summary>
/// One readable line about what a crew member is doing right now, plus when
/// that happened. <paramref name="Error"/> is set instead when the transcript
/// could not be read at all.
/// </summary>
/// <param name="Summary">Already truncated and already human-readable: the caller renders it, never parses it.</param>
/// <param name="At">The engine's own stamp for that activity, when it recorded one.</param>
/// <param name="Error">Why nothing could be read. Mutually exclusive with a useful <paramref name="Summary"/>.</param>
public sealed record ForemanActivitySnapshot(string Summary, DateTimeOffset? At, string? Error = null);

/// <summary>
/// Reads a CLI engine's own on-disk session transcript and reports the most
/// recent thing that happened in it.
///
/// <para>
/// Not a subprocess concern. Every turn this app runs is a fully buffered
/// one-shot process with stdin closed, so there is no stream to watch -- but
/// Claude Code and Codex each keep an incremental JSONL transcript of their own
/// while they work. Watching a Foreman is therefore a file-tail problem, and
/// this is the seam for it.
/// </para>
/// </summary>
public interface IForemanActivityReader
{
    /// <summary>The engine this reader understands, matched against ForemanConfig.Provider.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Best-effort by contract: a missing, locked, empty or malformed
    /// transcript comes back as a snapshot with <c>Error</c> set, never as a
    /// throw. This runs on a background refresh behind the Boss's dashboard;
    /// an unreadable file must never take that loop down.
    /// </summary>
    ForemanActivitySnapshot? Read(string sessionId, string workingDirectory);
}
