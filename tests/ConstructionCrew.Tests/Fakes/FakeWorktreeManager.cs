using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>
/// Never shells git. Records every call and hands back the handle it was asked
/// for, so JobRegistry-level tests can assert on worktree wiring without a repo.
/// </summary>
public sealed class FakeWorktreeManager : IWorktreeManager
{
    public ConcurrentBag<(string RepoPath, string FeatureBranch, string WorkerBranch, string WorktreePath)> Opened { get; } = new();
    public ConcurrentBag<string> Pruned { get; } = new();
    public ConcurrentBag<WorktreeHandle> Closed { get; } = new();
    public ConcurrentBag<(string RepoPath, string FeatureBranch, string WorkerBranch)> Merged { get; } = new();

    public Task<WorktreeHandle> OpenAsync(string repoPath, string featureBranch, string workerBranch, string worktreePath, CancellationToken ct)
    {
        Opened.Add((repoPath, featureBranch, workerBranch, worktreePath));
        return Task.FromResult(new WorktreeHandle(worktreePath, workerBranch));
    }

    public Task<bool> MergeAsync(string repoPath, string featureBranch, string workerBranch, CancellationToken ct)
    {
        Merged.Add((repoPath, featureBranch, workerBranch));
        return Task.FromResult(true);
    }

    public Task CloseAsync(WorktreeHandle handle, CancellationToken ct)
    {
        Closed.Add(handle);
        return Task.CompletedTask;
    }

    public Task PruneAsync(string repoPath, CancellationToken ct)
    {
        Pruned.Add(repoPath);
        return Task.CompletedTask;
    }
}
