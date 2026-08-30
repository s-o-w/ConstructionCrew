namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Carries exactly the two values that round-trip from <see cref="IWorktreeManager.OpenAsync"/>
/// to <see cref="IWorktreeManager.CloseAsync"/>: the worktree path to
/// <c>git worktree remove</c>, and the worker branch to delete alongside it.
/// </summary>
public sealed record WorktreeHandle(string WorktreePath, string WorkerBranch);
