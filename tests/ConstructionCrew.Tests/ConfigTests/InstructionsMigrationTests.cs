using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

/// <summary>
/// Real temp directories, a real foremen.yaml, real File.Move -- same
/// convention as HireWizardVaultFoldersTests' git-backed tests. This is the one
/// code path that decides "already migrated," so it gets exercised directly
/// rather than trusted from code review alone.
///
/// repoRoot is always the REAL repo (RepoPaths.FindRepoRoot) -- MigrateToVault
/// seeds templates from its real config/scaffold/, so a fake repoRoot would
/// fail on that step regardless of what's under test here. Everything else
/// (the "legacy" file location, the vault, foremen.yaml) is a scratch temp dir
/// unrelated to the real checkout.
/// </summary>
public class InstructionsMigrationTests
{
    private static readonly string RepoRoot = RepoPaths.FindRepoRoot(AppContext.BaseDirectory);

    [Fact]
    public void MigrateToVault_LegacyRoster_MovesFilesAndRewritesTheYaml()
    {
        var root = NewTempDir();
        try
        {
            var vaultRoot = Path.Combine(root, "vault");
            var legacyDir = Path.Combine(root, "legacy-instructions");
            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(vaultRoot);

            var legacyPath = Path.Combine(legacyDir, "Frontend.md");
            File.WriteAllText(legacyPath, "You are Frontend, hand-edited by the Boss.");
            var legacyBriefing = Path.Combine(legacyDir, "Frontend.briefing.md");
            File.WriteAllText(legacyBriefing, "You are the Frontend Foreman.");

            var foremenYamlPath = Path.Combine(root, "foremen.yaml");
            var foreman = new ForemanConfig(
                "Frontend", CrewRole.Foreman, "claude", root, legacyPath, new Dictionary<string, string>());
            ForemanConfigWriter.AppendForeman(foremenYamlPath, foreman, RepoRoot, vaultRoot);

            var result = InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, [foreman]);

            Assert.Equal(["Frontend"], result.MigratedForemen);
            Assert.False(File.Exists(legacyPath));
            Assert.False(File.Exists(legacyBriefing));

            var newPath = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions", "Frontend.md");
            Assert.True(File.Exists(newPath));
            Assert.Equal("You are Frontend, hand-edited by the Boss.", File.ReadAllText(newPath));

            var newBriefing = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions", "Frontend.briefing.md");
            Assert.True(File.Exists(newBriefing));

            Assert.Equal(newPath, Assert.Single(result.Foremen).InstructionsFilePath);

            // The rewritten YAML round-trips back to the new path -- not just the
            // in-memory result, the file on disk too.
            var reloaded = new ForemanConfigLoader().LoadFromFile(foremenYamlPath, RepoRoot, vaultRoot, "GC");
            Assert.Equal(newPath, Assert.Single(reloaded).InstructionsFilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateToVault_AlreadyOnVaultPaths_TouchesNoFileAndRewritesNothing()
    {
        var root = NewTempDir();
        try
        {
            var vaultRoot = Path.Combine(root, "vault");
            var vaultInstructionsDir = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions");
            Directory.CreateDirectory(vaultInstructionsDir);

            var currentPath = Path.Combine(vaultInstructionsDir, "Frontend.md");
            File.WriteAllText(currentPath, "You are Frontend.");
            var writeTimeUtc = File.GetLastWriteTimeUtc(currentPath);

            var foremenYamlPath = Path.Combine(root, "foremen.yaml");
            var foreman = new ForemanConfig(
                "Frontend", CrewRole.Foreman, "claude", root, currentPath, new Dictionary<string, string>());
            ForemanConfigWriter.AppendForeman(foremenYamlPath, foreman, RepoRoot, vaultRoot);
            var yamlBefore = File.ReadAllText(foremenYamlPath);

            var result = InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, [foreman]);

            Assert.Empty(result.MigratedForemen);
            Assert.Equal(currentPath, Assert.Single(result.Foremen).InstructionsFilePath);
            Assert.Equal(writeTimeUtc, File.GetLastWriteTimeUtc(currentPath));
            Assert.Equal(yamlBefore, File.ReadAllText(foremenYamlPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateToVault_NoBriefingSidecar_MigratesTheInstructionsFileAnyway()
    {
        var root = NewTempDir();
        try
        {
            var vaultRoot = Path.Combine(root, "vault");
            var legacyDir = Path.Combine(root, "legacy-instructions");
            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(vaultRoot);

            var legacyPath = Path.Combine(legacyDir, "GC.md");
            File.WriteAllText(legacyPath, "You are the General Contractor.");
            // Deliberately no GC.briefing.md sidecar -- GC never gets one.

            var foremenYamlPath = Path.Combine(root, "foremen.yaml");
            var gc = new ForemanConfig(
                "GC", CrewRole.GC, "claude", vaultRoot, legacyPath, new Dictionary<string, string>());
            ForemanConfigWriter.AppendForeman(foremenYamlPath, gc, RepoRoot, vaultRoot);

            var result = InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, [gc]);

            Assert.Equal(["GC"], result.MigratedForemen);
            var newPath = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions", "GC.md");
            Assert.True(File.Exists(newPath));
            Assert.False(File.Exists(Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions", "GC.briefing.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// FirstRunWizard.EnsureGcInstructions runs before the roster loads and has
    /// its own legacy-file fallback for exactly this reason -- by the time this
    /// migration runs, GC's file may already be sitting at the NEW path even
    /// though foremen.yaml still names the OLD one. The YAML still needs
    /// rewriting; the file must not be touched a second time.
    /// </summary>
    [Fact]
    public void MigrateToVault_FileAlreadyMovedByGcFallback_StillRewritesTheYaml()
    {
        var root = NewTempDir();
        try
        {
            var vaultRoot = Path.Combine(root, "vault");
            var vaultInstructionsDir = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions");
            Directory.CreateDirectory(vaultInstructionsDir);

            var newPath = Path.Combine(vaultInstructionsDir, "GC.md");
            File.WriteAllText(newPath, "You are the General Contractor.");
            // No file at the legacy path at all -- already moved.

            var foremenYamlPath = Path.Combine(root, "foremen.yaml");
            var legacyPath = Path.Combine(root, "legacy-instructions", "GC.md");
            var gc = new ForemanConfig(
                "GC", CrewRole.GC, "claude", vaultRoot, legacyPath, new Dictionary<string, string>());
            ForemanConfigWriter.AppendForeman(foremenYamlPath, gc, RepoRoot, vaultRoot);

            var result = InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, [gc]);

            Assert.Equal(["GC"], result.MigratedForemen);
            Assert.Equal(newPath, Assert.Single(result.Foremen).InstructionsFilePath);
            Assert.Equal("You are the General Contractor.", File.ReadAllText(newPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateToVault_SeedsMissingTemplatesFromTheRepoScaffold()
    {
        var root = NewTempDir();
        try
        {
            var vaultRoot = Path.Combine(root, "vault");
            Directory.CreateDirectory(vaultRoot);
            var foremenYamlPath = Path.Combine(root, "foremen.yaml");

            var result = InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, []);

            Assert.Equal(2, result.TemplatesEnsured.Count);
            Assert.True(File.Exists(Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Templates", "gc-instructions.md")));
            Assert.True(File.Exists(Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Templates", "foreman-instructions.md")));

            // Idempotent: a second pass ensures nothing further and never touches
            // what's already there.
            File.WriteAllText(
                Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Templates", "gc-instructions.md"), "MY OWN EDIT");
            var second = InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, []);

            Assert.Empty(second.TemplatesEnsured);
            Assert.Equal(
                "MY OWN EDIT",
                File.ReadAllText(Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Templates", "gc-instructions.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateToVault_LegacyCrewPreferences_MovesToConstructionCrewFolder()
    {
        var root = NewTempDir();
        try
        {
            var vaultRoot = Path.Combine(root, "vault");
            var legacyDir = Path.Combine(vaultRoot, "AI", "Context");
            Directory.CreateDirectory(legacyDir);
            var legacyPath = Path.Combine(legacyDir, "crew-preferences.md");
            File.WriteAllText(legacyPath, "# Crew preferences\n\nPrefer codex for review.");

            var foremenYamlPath = Path.Combine(root, "foremen.yaml");

            InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, []);

            Assert.False(File.Exists(legacyPath));
            var newPath = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "crew-preferences.md");
            Assert.True(File.Exists(newPath));
            Assert.Equal("# Crew preferences\n\nPrefer codex for review.", File.ReadAllText(newPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateToVault_NoLegacyCrewPreferences_DoesNothing()
    {
        var root = NewTempDir();
        try
        {
            var vaultRoot = Path.Combine(root, "vault");
            Directory.CreateDirectory(vaultRoot);
            var foremenYamlPath = Path.Combine(root, "foremen.yaml");

            InstructionsMigration.MigrateToVault(RepoRoot, vaultRoot, foremenYamlPath, []);

            Assert.False(File.Exists(Path.Combine(vaultRoot, "AI", "ConstructionCrew", "crew-preferences.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccrew-migration-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
