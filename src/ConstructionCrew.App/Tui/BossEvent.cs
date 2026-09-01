using ConstructionCrew.Core.Models;
using ConstructionCrew.Git;
using ConstructionCrew.Providers.Activity;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Everything the Boss loop can wake up for, funnelled into one channel.
///
/// <para>
/// Three producers write it (input pump, <c>IJobStatusSink.Reader</c> pump,
/// passive-column refresh), and the loop is the only reader. Single reader
/// means every <see cref="DashboardState"/> mutation happens on the loop
/// thread, so the transcript lists need no locking.
/// </para>
/// </summary>
internal abstract record BossEvent
{
    /// <summary>A line the Boss typed.</summary>
    internal sealed record InputLine(string Text) : BossEvent;

    /// <summary>stdin reached EOF.</summary>
    internal sealed record InputClosed : BossEvent;

    /// <summary>One job status transition, drained from <c>IJobStatusSink.Reader</c>.</summary>
    internal sealed record JobTransition(JobRecord Record) : BossEvent;

    /// <summary>A finished passive-column refresh. Null when the watched Foreman has no worktree.</summary>
    internal sealed record PassiveRefreshed(GitWorkspaceSnapshot? Snapshot) : BossEvent;

    /// <summary>
    /// A finished activity read of one Foreman's session transcript.
    /// <paramref name="ForemanName"/> distinguishes GC's read (always running
    /// when GC is busy, shown in the main pane) from the watch subject's read
    /// (shown in the side panel). Separate from <see cref="PassiveRefreshed"/>:
    /// the two read different things at different speeds.
    /// </summary>
    internal sealed record ActivityRefreshed(string ForemanName, ForemanActivitySnapshot? Snapshot) : BossEvent;

    /// <summary>
    /// Periodic tick fired while any agent is busy. Drives continuous activity
    /// reads for GC (main pane) and the watch subject (side panel) without
    /// needing a job-transition event to trigger them.
    /// </summary>
    internal sealed record ActivityHeartbeat : BossEvent;
}
