using ConstructionCrew.Git;
using ConstructionCrew.Providers.Activity;

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
/// A message from a Foreman that arrived while the Boss was doing something
/// else (a milestone sitrep) -- queued here rather than spliced into
/// <see cref="DashboardState.Transcript"/>, so it never rewrites what's
/// currently on screen. Read via <c>/inbox</c>.
/// </summary>
public sealed record InboxItem(string From, string Text, DateTimeOffset ReceivedAt, bool Read = false);

/// <summary>
/// Everything the TUI renders that is not read straight off the registries.
///
/// <para>
/// Not thread-safe, and doesn't need to be: the Boss loop is the single
/// reader of the event channel, so every write happens on the loop thread.
/// Background work reports back by writing a <see cref="BossEvent"/>, never
/// by touching this object directly.
/// </para>
/// </summary>
public sealed class DashboardState
{
    /// <summary>Per-Foreman transcripts for drive mode. GC's lives in <see cref="Transcript"/>.</summary>
    private readonly Dictionary<string, List<TranscriptLine>> _drivenTranscripts =
        new(StringComparer.OrdinalIgnoreCase);

    public TuiView View { get; set; } = TuiView.Chat;

    public string? StubLabel { get; set; }

    /// <summary>The GC conversation: what the Boss sees when not driving anyone.</summary>
    public List<TranscriptLine> Transcript { get; } = new();

    /// <summary>
    /// Messages from Foremen queued for later reading -- see <see cref="InboxItem"/>.
    /// Never touched by chat rendering; only <c>/inbox</c> reads or mutates it.
    /// </summary>
    public List<InboxItem> Inbox { get; } = new();

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
    /// An explicit <c>/watch</c> override: whose activity the side panel shows
    /// while Boss input keeps going wherever it was already going.
    ///
    /// <para>
    /// Null means "follow whoever is being driven", which is why driving
    /// implies watching but not the reverse. <c>/drive</c> and <c>/exit</c>
    /// both clear it, so the panel never ends up describing one Foreman while
    /// the Boss types to another.
    /// </para>
    /// </summary>
    public string? WatchedForeman { get; set; }

    /// <summary>
    /// Who the side panel is about: the explicit watch if there is one,
    /// otherwise whoever is being driven. Null means no panel at all.
    /// </summary>
    public string? WatchSubject => WatchedForeman ?? DrivenForeman;

    /// <summary>
    /// The passive column's last <c>git status</c>/<c>git log</c> read of the
    /// watched Foreman's worktree. Null when nothing has been read yet, or when
    /// that Foreman has no worktree.
    /// </summary>
    public GitWorkspaceSnapshot? Passive { get; set; }

    /// <summary>
    /// The watched Foreman's last known activity, read off its engine's own
    /// session transcript. Null before the first read, or when that engine
    /// keeps no readable transcript.
    /// </summary>
    public ForemanActivitySnapshot? Activity { get; set; }

    /// <summary>The transcript the output pane is currently showing.</summary>
    public List<TranscriptLine> ActiveTranscript => TranscriptFor(DrivenForeman);

    /// <summary>
    /// The transcript a line belongs in. Null, and GC's own name, both mean
    /// the main <see cref="Transcript"/>: there is exactly one GC
    /// conversation, and it must never be split across two panes.
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
