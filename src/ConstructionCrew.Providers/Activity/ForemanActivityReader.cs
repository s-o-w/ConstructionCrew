namespace ConstructionCrew.Providers.Activity;

/// <summary>
/// A short tail of what a crew member has been doing, plus the timestamp of the
/// most recent event. <paramref name="Error"/> is set instead when the transcript
/// could not be read at all.
/// </summary>
/// <param name="Summary">Most recent event, one line. Equals <c>Lines.Last()</c> when Lines is set.</param>
/// <param name="At">The engine's own stamp for the most recent event, when it recorded one.</param>
/// <param name="Error">Why nothing could be read. Mutually exclusive with Lines/Summary.</param>
/// <param name="Lines">
/// Last N activity events in chronological order (oldest first, newest last).
/// Null for sentinel states ("starting up", "no turns yet"). When set, the
/// dashboard renders the whole list as a mini-transcript tail.
/// </param>
public sealed record ForemanActivitySnapshot(
    string Summary,
    DateTimeOffset? At,
    string? Error = null,
    IReadOnlyList<string>? Lines = null);

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

    /// <summary>
    /// Finds the most recent in-flight session for a given working directory,
    /// without knowing the session ID yet. Used to show activity while the
    /// foreman's first turn is still running. Default returns null (not
    /// supported by this provider).
    /// </summary>
    ForemanActivitySnapshot? TryReadForCwd(string cwd) => null;
}
