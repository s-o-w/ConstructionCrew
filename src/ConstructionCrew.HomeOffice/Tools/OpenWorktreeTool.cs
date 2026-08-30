using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

/// <summary>
/// Depends on <see cref="IWorktreeManager"/> only -- HomeOffice never names the
/// ConstructionCrew.Git concrete type (Architecture §3.7).
/// </summary>
[McpServerToolType]
public sealed class OpenWorktreeTool
{
    private readonly IWorktreeManager _worktrees;

    public OpenWorktreeTool(IWorktreeManager worktrees)
    {
        _worktrees = worktrees;
    }

    [McpServerTool(Name = "open_worktree")]
    [Description("Open an isolated git worktree on a new branch cut from a feature branch. spawn_worker already does this for you -- use this tool only when you need a worktree of your own, outside a Worker. Returns the worktree path and the branch checked out in it.")]
    public async Task<WorktreeHandle> OpenWorktree(
        [Description("Absolute path to the jobsite's repo.")] string repoPath,
        [Description("The branch the new worktree's branch is cut from, e.g. 'feature/named-graphs'.")] string featureBranch,
        [Description("The new branch to create and check out in the worktree.")] string workerBranch,
        [Description("Absolute path of the directory to create the worktree in. Must not already exist.")] string worktreePath,
        CancellationToken cancellationToken)
    {
        return await _worktrees.OpenAsync(repoPath, featureBranch, workerBranch, worktreePath, cancellationToken);
    }
}
