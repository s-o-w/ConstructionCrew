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
    /// A finished activity read of the watched Foreman's session transcript.
    /// Separate from <see cref="PassiveRefreshed"/> on purpose: the two read
    /// different things at different speeds, and one must never make the
    /// other wait.
    /// </summary>
    internal sealed record ActivityRefreshed(ForemanActivitySnapshot? Snapshot) : BossEvent;
}
