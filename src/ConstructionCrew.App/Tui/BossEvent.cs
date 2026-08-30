using ConstructionCrew.Core.Models;
using ConstructionCrew.Git;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Everything the Boss loop can wake up for, funnelled into one channel.
///
/// <para>
/// Three producers write it -- the input thread's pump, the
/// <c>IJobStatusSink.Reader</c> pump, and the passive-column refresh -- and the
/// loop is the only reader. That is deliberate: with a single reader, every
/// mutation of <see cref="DashboardState"/> happens on the loop thread, so the
/// transcript lists need no locking. Waiting on several channels at once instead
/// would mean abandoning pending waiters on each pass, which quietly accumulates
/// them on a channel that stays idle.
/// </para>
/// </summary>
internal abstract record BossEvent
{
    /// <summary>A line the Boss typed.</summary>
    internal sealed record InputLine(string Text) : BossEvent;

    /// <summary>stdin reached EOF -- the old loop's <c>input is null</c> break.</summary>
    internal sealed record InputClosed : BossEvent;

    /// <summary>One job status transition, drained from <c>IJobStatusSink.Reader</c>.</summary>
    internal sealed record JobTransition(JobRecord Record) : BossEvent;

    /// <summary>A finished passive-column refresh. Null when the driven Foreman has no worktree.</summary>
    internal sealed record PassiveRefreshed(GitWorkspaceSnapshot? Snapshot) : BossEvent;
}
