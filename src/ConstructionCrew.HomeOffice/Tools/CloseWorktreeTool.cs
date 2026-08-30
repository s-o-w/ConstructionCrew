using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

[McpServerToolType]
public sealed class CloseWorktreeTool
{
    private readonly IWorktreeManager _worktrees;

    public CloseWorktreeTool(IWorktreeManager worktrees)
    {
        _worktrees = worktrees;
    }

    [McpServerTool(Name = "close_worktree")]
    [Description("Remove a finished Worker's worktree directory and delete its branch. Call this AFTER merge_worker_branch -- closing first throws the Worker's commits away. Safe to call twice; an already-closed worktree is not an error.")]
    public async Task CloseWorktree(
        [Description("The worktree path returned by spawn_worker / open_worktree.")] string worktreePath,
        [Description("The Worker's branch to delete alongside it.")] string workerBranch,
        CancellationToken cancellationToken)
    {
        await _worktrees.CloseAsync(new WorktreeHandle(worktreePath, workerBranch), cancellationToken);
    }
}
