using ConstructionCrew.Git;

namespace ConstructionCrew.App.Tui;

public enum TuiView
{
    Chat,
    Tasks,
    Memory,
    Monitor,
    Stub,
}

public sealed record TranscriptLine(string Speaker, string Text, bool IsError = false);

/// <summary>
/// Everything the TUI renders that is not read straight off the registries.
///
/// <para>
/// Not thread-safe, and does not need to be: the Boss loop is the single reader of
/// the event channel, so every write here happens on the loop thread. Background
/// work (an agent turn, a passive-column refresh) reports back by writing a
/// <see cref="BossEvent"/>, never by touching this object.
/// </para>
/// </summary>
public sealed class DashboardState
{
    /// <summary>Per-Foreman transcripts for drive mode. GC's lives in <see cref="Transcript"/>.</summary>
    private readonly Dictionary<string, List<TranscriptLine>> _drivenTranscripts =
        new(StringComparer.OrdinalIgnoreCase);

    public TuiView View { get; set; } = TuiView.Chat;

    public string? StubLabel { get; set; }

    /// <summary>The GC conversation -- what the Boss sees when not driving anyone.</summary>
    public List<TranscriptLine> Transcript { get; } = new();

    public required string HomeOfficeAddress { get; init; }

    /// <summary>The reserved roster name GC is hired under. Routes GC lines to <see cref="Transcript"/>.</summary>
    public string GcForemanName { get; init; } = "GC";

    /// <summary>
    /// Null when Boss input goes to GC, otherwise the canonical roster name of the
    /// Foreman it is being routed to instead. Set by <c>/drive</c>, cleared by
    /// <c>/exit</c>.
    /// </summary>
    public string? DrivenForeman { get; set; }

    /// <summary>
    /// The passive column's last <c>git status</c>/<c>git log</c> read of the
    /// driven Foreman's worktree. Null when nothing has been read yet, or when
    /// that Foreman has no worktree.
    /// </summary>
    public GitWorkspaceSnapshot? Passive { get; set; }

    /// <summary>The transcript the output pane is currently showing.</summary>
    public List<TranscriptLine> ActiveTranscript => TranscriptFor(DrivenForeman);

    /// <summary>
    /// The transcript a line belongs in. Null -- and GC's own name -- both mean the
    /// main <see cref="Transcript"/>: there is exactly one GC conversation, and it
    /// must never be split across two panes.
    /// </summary>
    public List<TranscriptLine> TranscriptFor(string? foremanName)
    {
        if (foremanName is null || foremanName.Equals(GcForemanName, StringComparison.OrdinalIgnoreCase))
        {
            return Transcript;
        }

        if (!_drivenTranscripts.TryGetValue(foremanName, out var lines))
        {
            lines = new List<TranscriptLine>();
            _drivenTranscripts[foremanName] = lines;
        }

        return lines;
    }
}
