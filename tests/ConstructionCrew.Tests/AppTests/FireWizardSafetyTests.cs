using ConstructionCrew.App.Tui;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Locks in the hard invariant behind /fire: it must never delete anything
/// outside this tool's own config/instructions/ directory. This is the one
/// actual File.Delete call in the whole fire flow, so it gets a direct test
/// rather than relying on code review alone.
/// </summary>
public class FireWizardSafetyTests
{
    [Fact]
    public void DeleteGeneratedInstructionsFile_InsideInstructionsDir_DeletesIt()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "ccrew-fire-test-" + Guid.NewGuid().ToString("n")[..8]);
        var instructionsDir = Path.Combine(repoRoot, "config", "instructions");
        Directory.CreateDirectory(instructionsDir);
        var path = Path.Combine(instructionsDir, "Fred.md");
        File.WriteAllText(path, "You are Fred.");

        try
        {
            FireWizard.DeleteGeneratedInstructionsFile(path, repoRoot);

            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteGeneratedInstructionsFile_OutsideInstructionsDir_NeverDeletesIt()
    {
        // The critical case: even if a ForemanConfig's InstructionsFilePath were
        // ever hand-edited to point somewhere else entirely -- e.g. a file
        // inside what would be a jobsite's repo -- this must refuse to delete
        // it. /fire must NEVER be able to reach a real repo.
        var repoRoot = Path.Combine(Path.GetTempPath(), "ccrew-fire-test-" + Guid.NewGuid().ToString("n")[..8]);
        var fakeRepoPath = Path.Combine(repoRoot, "not-config-at-all", "important-repo-file.md");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeRepoPath)!);
        File.WriteAllText(fakeRepoPath, "definitely not a generated instructions file");

        try
        {
            FireWizard.DeleteGeneratedInstructionsFile(fakeRepoPath, repoRoot);

            Assert.True(File.Exists(fakeRepoPath));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteGeneratedInstructionsFile_MissingFile_DoesNotThrow()
    {
        var repoRoot = Path.GetTempPath();
        var missing = Path.Combine(repoRoot, "config", "instructions", "does-not-exist.md");

        FireWizard.DeleteGeneratedInstructionsFile(missing, repoRoot);
    }
}
