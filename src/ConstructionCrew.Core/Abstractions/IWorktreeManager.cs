namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Opens, merges, closes and prunes git worktrees so two Workers on the same
/// Jobsite can run concurrently without sharing one working tree.
///
/// The interface lives in Core so HomeOffice can depend on it without
/// referencing ConstructionCrew.Git (where the real git-shelling implementation
/// lives) -- see Architecture §3.7.
/// </summary>
public interface IWorktreeManager
{
    /// <summary>
    /// Creates <paramref name="workerBranch"/> off <paramref name="featureBranch"/> and
    /// checks it out into a brand-new worktree at <paramref name="worktreePath"/>.
    /// Throws if git refuses -- a Worker with no isolated working tree must never
    /// silently fall back to the Foreman's own.
    /// </summary>
    Task<WorktreeHandle> OpenAsync(string repoPath, string featureBranch, string workerBranch, string worktreePath, CancellationToken ct);

    /// <summary>
    /// Merges <paramref name="workerBranch"/> back into <paramref name="featureBranch"/>
    /// in the main worktree. Returns false (never throws) when the merge cannot be
    /// completed -- a conflict is an ordinary outcome the Foreman has to resolve,
    /// not an exceptional one.
    /// </summary>
    Task<bool> MergeAsync(string repoPath, string featureBranch, string workerBranch, CancellationToken ct);

    /// <summary>Removes the worktree directory and deletes its worker branch. Best effort.</summary>
    Task CloseAsync(WorktreeHandle handle, CancellationToken ct);

    /// <summary>
    /// <c>git worktree prune</c>: drops <c>.git/worktrees/</c> metadata for worktrees
    /// whose directory is already gone. Touches no tracked content, ever. Best effort.
    /// </summary>
    Task PruneAsync(string repoPath, CancellationToken ct);
}
