using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

[McpServerToolType]
public sealed class MergeWorkerBranchTool
{
    private readonly IWorktreeManager _worktrees;

    public MergeWorkerBranchTool(IWorktreeManager worktrees)
    {
        _worktrees = worktrees;
    }

    [McpServerTool(Name = "merge_worker_branch")]
    [Description("Merge a finished Worker's branch back into your feature branch. Call this as each Worker's unit of work completes, then call close_worktree. Returns false if the merge could not be completed (a conflict) -- the repo is left clean and unmerged, and resolving it is your job.")]
    public async Task<bool> MergeWorkerBranch(
        [Description("Absolute path to the jobsite's repo.")] string repoPath,
        [Description("Your feature branch -- the branch the Worker's work merges into.")] string featureBranch,
        [Description("The Worker's branch, e.g. 'feature/named-graphs-worker-a1b2c3'.")] string workerBranch,
        CancellationToken cancellationToken)
    {
        return await _worktrees.MergeAsync(repoPath, featureBranch, workerBranch, cancellationToken);
    }
}
