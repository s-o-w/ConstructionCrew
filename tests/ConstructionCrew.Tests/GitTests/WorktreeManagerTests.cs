using ConstructionCrew.Git;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.GitTests;

/// <summary>
/// Drives the real git binary against a scratch repo. Phase 6's gate is "two
/// Workers on the same Jobsite produce two worktrees, two branches, no file
/// clobbering" -- that is only provable against real git.
/// </summary>
public class WorktreeManagerTests
{
    private static WorktreeManager NewManager() => new(new CliProcessRunner());

    [Fact]
    public async Task OpenMergeCloseAndPrune_RoundTrips()
    {
        using var repo = await GitScratchRepo.CreateAsync();
        var manager = NewManager();
        var workerBranch = $"{repo.FeatureBranch}-worker-a1b2c3";
        var worktreePath = repo.WorktreePath("worker-a1b2c3");

        var handle = await manager.OpenAsync(
            repo.RepoPath, repo.FeatureBranch, workerBranch, worktreePath, CancellationToken.None);

        Assert.Equal(worktreePath, handle.WorktreePath);
        Assert.Equal(workerBranch, handle.WorkerBranch);
        Assert.True(Directory.Exists(worktreePath));
        // A real checkout of the feature branch, not an empty directory.
        Assert.True(File.Exists(Path.Combine(worktreePath, "README.md")));
        Assert.Equal(workerBranch, (await repo.GitOrThrow(worktreePath, "rev-parse", "--abbrev-ref", "HEAD")).Trim());

        // The Worker does its work in its own worktree and commits it there.
        File.WriteAllText(Path.Combine(worktreePath, "worker-output.txt"), "worker did the thing\n");
        await repo.CommitAllAsync(worktreePath, "worker work");

        // Nothing has reached the feature branch yet.
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "worker-output.txt")));

        var merged = await manager.MergeAsync(repo.RepoPath, repo.FeatureBranch, workerBranch, CancellationToken.None);

        Assert.True(merged);
        Assert.Equal(repo.FeatureBranch, (await repo.GitOrThrow(repo.RepoPath, "rev-parse", "--abbrev-ref", "HEAD")).Trim());
        Assert.Equal("worker did the thing\n", File.ReadAllText(Path.Combine(repo.RepoPath, "worker-output.txt")));

        await manager.CloseAsync(handle, CancellationToken.None);

        Assert.False(Directory.Exists(worktreePath));
        // The branch is gone too -- its commits survive only because they were merged.
        Assert.Equal(string.Empty, (await repo.GitOrThrow(repo.RepoPath, "branch", "--list", workerBranch)).Trim());
        Assert.DoesNotContain(worktreePath, await repo.GitOrThrow(repo.RepoPath, "worktree", "list"));

        // Prune after a clean close is a no-op that still must not throw.
        await manager.PruneAsync(repo.RepoPath, CancellationToken.None);
        Assert.Equal("worker did the thing\n", File.ReadAllText(Path.Combine(repo.RepoPath, "worker-output.txt")));
    }

    [Fact]
    public async Task MergeAsync_OnAConflict_ReturnsFalseAndLeavesTheRepoClean()
    {
        using var repo = await GitScratchRepo.CreateAsync();
        var manager = NewManager();
        var workerBranch = $"{repo.FeatureBranch}-worker-conflict";
        var worktreePath = repo.WorktreePath("worker-conflict");

        var handle = await manager.OpenAsync(
            repo.RepoPath, repo.FeatureBranch, workerBranch, worktreePath, CancellationToken.None);

        File.WriteAllText(Path.Combine(worktreePath, "README.md"), "worker's version\n");
        await repo.CommitAllAsync(worktreePath, "worker edit");

        File.WriteAllText(Path.Combine(repo.RepoPath, "README.md"), "foreman's version\n");
        await repo.CommitAllAsync(repo.RepoPath, "foreman edit");

        var merged = await manager.MergeAsync(repo.RepoPath, repo.FeatureBranch, workerBranch, CancellationToken.None);

        Assert.False(merged);
        // Aborted, not left mid-conflict: a clean tree and the Foreman's own content.
        Assert.Equal(string.Empty, (await repo.GitOrThrow(repo.RepoPath, "status", "--porcelain")).Trim());
        Assert.Equal("foreman's version\n", File.ReadAllText(Path.Combine(repo.RepoPath, "README.md")));

        await manager.CloseAsync(handle, CancellationToken.None);
    }

    /// <summary>
    /// Phase 6's gate. Not "two calls didn't crash": each Worker's file must be
    /// invisible to the other -- absent from its directory AND absent from its
    /// `git status` -- and each must be on its own branch.
    /// </summary>
    [Fact]
    public async Task TwoWorkersOnTheSameJobsite_GetIsolatedWorktrees_WithNoFileClobbering()
    {
        using var repo = await GitScratchRepo.CreateAsync();
        var manager = NewManager();

        var branchA = $"{repo.FeatureBranch}-worker-aaaaaa";
        var branchB = $"{repo.FeatureBranch}-worker-bbbbbb";
        var pathA = repo.WorktreePath("worker-aaaaaa");
        var pathB = repo.WorktreePath("worker-bbbbbb");

        var handles = await Task.WhenAll(
            manager.OpenAsync(repo.RepoPath, repo.FeatureBranch, branchA, pathA, CancellationToken.None),
            manager.OpenAsync(repo.RepoPath, repo.FeatureBranch, branchB, pathB, CancellationToken.None));

        var handleA = handles.Single(h => h.WorkerBranch == branchA);
        var handleB = handles.Single(h => h.WorkerBranch == branchB);

        Assert.NotEqual(handleA.WorktreePath, handleB.WorktreePath);
        Assert.True(Directory.Exists(handleA.WorktreePath));
        Assert.True(Directory.Exists(handleB.WorktreePath));

        // Each worktree has its own checked-out branch -- git refuses to check the
        // same branch out twice, so this is also the "two branches" half of the gate.
        Assert.Equal(branchA, (await repo.GitOrThrow(handleA.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD")).Trim());
        Assert.Equal(branchB, (await repo.GitOrThrow(handleB.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD")).Trim());

        // Both Workers write, including to THE SAME tracked file -- the case that
        // would clobber if they shared one working tree.
        File.WriteAllText(Path.Combine(handleA.WorktreePath, "only-a.txt"), "A\n");
        File.WriteAllText(Path.Combine(handleA.WorktreePath, "README.md"), "A rewrote this\n");
        File.WriteAllText(Path.Combine(handleB.WorktreePath, "only-b.txt"), "B\n");
        File.WriteAllText(Path.Combine(handleB.WorktreePath, "README.md"), "B rewrote this\n");

        // Neither Worker's new file exists in the other's tree...
        Assert.False(File.Exists(Path.Combine(handleA.WorktreePath, "only-b.txt")));
        Assert.False(File.Exists(Path.Combine(handleB.WorktreePath, "only-a.txt")));

        // ...neither overwrote the other's edit to the shared file...
        Assert.Equal("A rewrote this\n", File.ReadAllText(Path.Combine(handleA.WorktreePath, "README.md")));
        Assert.Equal("B rewrote this\n", File.ReadAllText(Path.Combine(handleB.WorktreePath, "README.md")));

        // ...and the Foreman's own working tree saw none of it.
        Assert.Equal("tracked content\n", File.ReadAllText(Path.Combine(repo.RepoPath, "README.md")));
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "only-a.txt")));
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "only-b.txt")));

        var statusA = await repo.GitOrThrow(handleA.WorktreePath, "status", "--porcelain");
        var statusB = await repo.GitOrThrow(handleB.WorktreePath, "status", "--porcelain");

        Assert.Contains("only-a.txt", statusA);
        Assert.DoesNotContain("only-b.txt", statusA);
        Assert.Contains("only-b.txt", statusB);
        Assert.DoesNotContain("only-a.txt", statusB);
        Assert.Equal(string.Empty, (await repo.GitOrThrow(repo.RepoPath, "status", "--porcelain")).Trim());

        // Both commit; each branch carries only its own commit.
        await repo.CommitAllAsync(handleA.WorktreePath, "A work");
        await repo.CommitAllAsync(handleB.WorktreePath, "B work");

        Assert.Contains("only-a.txt", await repo.GitOrThrow(repo.RepoPath, "show", "--name-only", "--format=", branchA));
        Assert.DoesNotContain("only-b.txt", await repo.GitOrThrow(repo.RepoPath, "show", "--name-only", "--format=", branchA));

        await manager.CloseAsync(handleA, CancellationToken.None);
        await manager.CloseAsync(handleB, CancellationToken.None);

        Assert.False(Directory.Exists(handleA.WorktreePath));
        Assert.False(Directory.Exists(handleB.WorktreePath));
    }

    [Fact]
    public async Task OpenAsync_WhenGitRefuses_ThrowsRatherThanFallingBackToTheForemansTree()
    {
        using var repo = await GitScratchRepo.CreateAsync();
        var manager = NewManager();

        // The feature branch is already checked out in the main worktree, so asking
        // for it again (rather than a new branch) is a guaranteed git refusal.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.OpenAsync(
                repo.RepoPath, repo.FeatureBranch, repo.FeatureBranch, repo.WorktreePath("nope"), CancellationToken.None));
    }

    [Fact]
    public async Task CloseAsync_OnAnAlreadyGoneWorktree_DoesNotThrow()
    {
        using var repo = await GitScratchRepo.CreateAsync();
        var manager = NewManager();

        await manager.CloseAsync(
            new ConstructionCrew.Core.Abstractions.WorktreeHandle(repo.WorktreePath("never-existed"), "no-such-branch"),
            CancellationToken.None);
    }

    [Fact]
    public async Task PruneAsync_OnAPathThatIsNotARepo_DoesNotThrow()
    {
        await NewManager().PruneAsync(Path.Combine(Path.GetTempPath(), "cc-not-a-repo-" + Guid.NewGuid().ToString("n")[..8]), CancellationToken.None);
    }
}
