using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Git;

/// <summary>
/// Shells the real <c>git</c> binary through the existing
/// <see cref="ICliProcessRunner"/> seam, the choice already made against
/// LibGit2Sharp. No new process-spawning path enters the app here.
///
/// Every call runs with the repo as its working directory, so nothing depends on
/// the process-wide current directory (Workers run concurrently).
/// </summary>
public sealed class WorktreeManager : IWorktreeManager
{
    private const string GitExecutable = "git";

    private readonly ICliProcessRunner _runner;

    public WorktreeManager(ICliProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<WorktreeHandle> OpenAsync(
        string repoPath, string featureBranch, string workerBranch, string worktreePath, CancellationToken ct)
    {
        // git refuses to create a worktree in a directory that already exists and
        // is non-empty, but happily creates the leaf itself, so only the parent
        // is pre-created.
        var parent = Path.GetDirectoryName(Path.GetFullPath(worktreePath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var result = await RunAsync(
            repoPath,
            ["worktree", "add", "-b", workerBranch, worktreePath, featureBranch],
            ct);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not open a git worktree at '{worktreePath}' for branch '{workerBranch}' " +
                $"(off '{featureBranch}' in '{repoPath}'): {Describe(result)}");
        }

        return new WorktreeHandle(worktreePath, workerBranch);
    }

    /// <summary>
    /// Checks the main worktree out onto <paramref name="featureBranch"/> first: a
    /// merge lands on whatever HEAD points at, and the Foreman's own worktree is
    /// the only place the feature branch may move.
    ///
    /// A failed merge is aborted rather than left half-applied: the caller gets
    /// false and a clean repo, not a repo mid-conflict it never asked for.
    /// </summary>
    public async Task<bool> MergeAsync(string repoPath, string featureBranch, string workerBranch, CancellationToken ct)
    {
        var checkout = await RunAsync(repoPath, ["checkout", featureBranch], ct);
        if (!checkout.Succeeded)
        {
            return false;
        }

        var merge = await RunAsync(repoPath, ["merge", "--no-ff", "--no-edit", workerBranch], ct);
        if (merge.Succeeded)
        {
            return true;
        }

        // Best effort: if there was nothing to abort, git says so and we ignore it.
        await RunAsync(repoPath, ["merge", "--abort"], ct);
        return false;
    }

    /// <summary>
    /// Removes the worktree, then deletes its branch. Both best effort: a Worker
    /// whose worktree was already cleaned up by hand must not turn close_worktree
    /// into an error the Foreman has to work around.
    ///
    /// <c>--force</c> is scoped to the worker's own worktree directory, which this
    /// tool created, never a Jobsite's real working tree.
    /// </summary>
    public async Task CloseAsync(WorktreeHandle handle, CancellationToken ct)
    {
        var repoPath = await ResolveMainRepoAsync(handle.WorktreePath, ct);
        if (repoPath is null)
        {
            return;
        }

        await RunAsync(repoPath, ["worktree", "remove", "--force", handle.WorktreePath], ct);
        await RunAsync(repoPath, ["worktree", "prune"], ct);
        await RunAsync(repoPath, ["branch", "-D", handle.WorkerBranch], ct);
    }

    public async Task PruneAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return;
        }

        await RunAsync(repoPath, ["worktree", "prune"], ct);
    }

    /// <summary>
    /// A WorktreeHandle only carries the worktree path, so close must find its way
    /// back to the main repo. <c>--git-common-dir</c> points at the shared .git of
    /// the main worktree from inside any linked one.
    /// </summary>
    private async Task<string?> ResolveMainRepoAsync(string worktreePath, CancellationToken ct)
    {
        if (!Directory.Exists(worktreePath))
        {
            return null;
        }

        var result = await RunAsync(worktreePath, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        if (!result.Succeeded)
        {
            return null;
        }

        var commonDir = result.StandardOutput.Trim();
        if (commonDir.Length == 0)
        {
            return null;
        }

        // <main>/.git -> <main>. A bare repo has no parent worktree to run in;
        // fall back to the common dir itself.
        return Path.GetDirectoryName(commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
               ?? commonDir;
    }

    private Task<CliRunResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) =>
        _runner.RunAsync(new CliInvocation(GitExecutable, arguments, workingDirectory), ct);

    private static string Describe(CliRunResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(message) ? $"git exited {result.ExitCode}" : message.Trim();
    }
}
