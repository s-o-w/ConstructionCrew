using ConstructionCrew.App.Tui;
using ConstructionCrew.Git;
using ConstructionCrew.Providers;
using ConstructionCrew.Tests.GitTests;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Locks in the hard invariant behind /fire: it must never delete anything
/// outside this tool's own AI/ConstructionCrew/Instructions/ directory in the
/// Vault. This is the one actual File.Delete call in the whole fire flow, so
/// it gets a direct test rather than relying on code review alone.
/// </summary>
public class FireWizardSafetyTests
{
    [Fact]
    public void DeleteGeneratedInstructionsFile_InsideInstructionsDir_DeletesIt()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "ccrew-fire-test-" + Guid.NewGuid().ToString("n")[..8]);
        var instructionsDir = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions");
        Directory.CreateDirectory(instructionsDir);
        var path = Path.Combine(instructionsDir, "Fred.md");
        File.WriteAllText(path, "You are Fred.");

        try
        {
            FireWizard.DeleteGeneratedInstructionsFile(path, vaultRoot);

            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteGeneratedInstructionsFile_OutsideInstructionsDir_NeverDeletesIt()
    {
        // The critical case: even if a ForemanConfig's InstructionsFilePath were
        // ever hand-edited to point somewhere else entirely -- e.g. a file
        // inside what would be a jobsite's repo -- this must refuse to delete
        // it. /fire must NEVER be able to reach a real repo.
        var vaultRoot = Path.Combine(Path.GetTempPath(), "ccrew-fire-test-" + Guid.NewGuid().ToString("n")[..8]);
        var fakeRepoPath = Path.Combine(vaultRoot, "not-ai-at-all", "important-repo-file.md");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeRepoPath)!);
        File.WriteAllText(fakeRepoPath, "definitely not a generated instructions file");

        try
        {
            FireWizard.DeleteGeneratedInstructionsFile(fakeRepoPath, vaultRoot);

            Assert.True(File.Exists(fakeRepoPath));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteGeneratedInstructionsFile_MissingFile_DoesNotThrow()
    {
        var vaultRoot = Path.GetTempPath();
        var missing = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions", "does-not-exist.md");

        FireWizard.DeleteGeneratedInstructionsFile(missing, vaultRoot);
    }

    [Fact]
    public void DeleteGeneratedInstructionsFile_NoVaultConfigured_NeverThrowsOrDeletes()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccrew-fire-test-" + Guid.NewGuid().ToString("n")[..8] + ".md");
        File.WriteAllText(path, "You are Fred.");

        try
        {
            FireWizard.DeleteGeneratedInstructionsFile(path, vaultRoot: null);

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Phase 6 relaxed the /fire invariant from "never writes to a Jobsite's repo
    /// path" to "never touches TRACKED WORKING-TREE CONTENT in a Jobsite repo,
    /// except git worktree remove/prune". This pins the new invariant: the prune
    /// step /fire runs must change nothing but .git/worktrees/ bookkeeping --
    /// tracked files, dirty edits and untracked files all survive byte-for-byte.
    /// </summary>
    [Fact]
    public async Task Prune_TouchesOnlyGitWorktreesMetadata_NeverTrackedContent()
    {
        using var repo = await GitScratchRepo.CreateAsync();
        var manager = new WorktreeManager(new CliProcessRunner());

        // A worktree whose directory is gone -- exactly what /fire prunes.
        var stale = repo.WorktreePath("stale");
        await repo.GitOrThrow(repo.RepoPath, "worktree", "add", "-b", "feature/thing-worker-stale", stale, repo.FeatureBranch);
        Directory.Delete(stale, recursive: true);

        var metadataDir = Path.Combine(repo.RepoPath, ".git", "worktrees", "stale");
        Assert.True(Directory.Exists(metadataDir), "setup failed: git left no worktree metadata to prune");

        // Uncommitted state that MUST survive: a dirty tracked file and an
        // untracked one. Both are the Boss's real work.
        File.WriteAllText(Path.Combine(repo.RepoPath, "README.md"), "the Boss was editing this\n");
        File.WriteAllText(Path.Combine(repo.RepoPath, "scratch.txt"), "untracked, unsaved, precious\n");

        var before = repo.SnapshotWorkingTree();
        var statusBefore = await repo.GitOrThrow(repo.RepoPath, "status", "--porcelain");
        var headBefore = await repo.GitOrThrow(repo.RepoPath, "rev-parse", "HEAD");

        await manager.PruneAsync(repo.RepoPath, CancellationToken.None);

        // The one thing prune is allowed to change.
        Assert.False(Directory.Exists(metadataDir));

        // ...and nothing else. Every file outside .git, byte-for-byte.
        Assert.Equal(before, repo.SnapshotWorkingTree());
        Assert.Equal(statusBefore, await repo.GitOrThrow(repo.RepoPath, "status", "--porcelain"));
        Assert.Equal(headBefore, await repo.GitOrThrow(repo.RepoPath, "rev-parse", "HEAD"));
    }
}
