namespace ConstructionCrew.Providers.Activity;

/// <summary>
/// Reports what a Codex CLI session is doing. Stub: registered so the resolver
/// and every caller are already wired, but not yet implemented.
///
/// <para>
/// Deliberately not written from the shape a plan described. Codex keeps
/// per-session JSONL in two places (<c>~/.codex/sessions/YYYY/MM/DD/</c> and
/// <c>~/.codex/archived_sessions/</c>), and WHICH of them is appended to while
/// a turn is still running has to be confirmed against a real <c>codex exec</c>
/// run before anything here reads one. Same discipline CodexProvider holds
/// itself to about its own flags.
/// </para>
/// </summary>
public sealed class CodexActivityReader : IForemanActivityReader
{
    public string ProviderId => "codex";

    public ForemanActivitySnapshot? Read(string sessionId, string workingDirectory) =>
        new("no activity yet", null, "live activity for codex is not implemented yet");
}
