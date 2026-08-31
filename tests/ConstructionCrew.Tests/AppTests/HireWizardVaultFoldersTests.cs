using ConstructionCrew.App.Tui;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// /hire derives a Foreman's vault write scope from the Jobsite name only when
/// the vault layout is recognized. On an unrecognized layout it must return null
/// -- "ask the Boss" -- rather than inventing Notes/&lt;Jobsite&gt; paths under
/// directories that aren't there.
/// </summary>
public class HireWizardVaultFoldersTests
{
    [Fact]
    public void DeriveVaultFolders_RecognizedLayout_DerivesNotesAndPlans()
    {
        var vaultRoot = NewRecognizedVault();
        try
        {
            Assert.Equal(
                ["Notes/Frontend", "Plans/Frontend"],
                HireWizard.DeriveVaultFolders(vaultRoot, "Frontend"));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void DeriveVaultFolders_UnrecognizedLayout_ReturnsNullSoTheWizardAsks()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "ccrew-hire-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(vaultRoot);
        try
        {
            File.WriteAllText(Path.Combine(vaultRoot, "HOME.md"), "# HOME");

            // Null, not empty: an empty list would read as "this Foreman writes
            // nowhere," which is a different (and wrong) answer.
            Assert.Null(HireWizard.DeriveVaultFolders(vaultRoot, "Frontend"));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void DerivedVaultFolders_SurviveTheForemanYamlRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccrew-hire-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var repoRoot = Path.Combine(root, "repo");
            var vaultRoot = NewRecognizedVault(Path.Combine(root, "vault"));
            var workingDirectory = Path.Combine(root, "jobsite-repo");
            Directory.CreateDirectory(workingDirectory);
            var instructionsPath = Path.Combine(repoRoot, "config", "instructions", "Frontend.md");
            Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
            File.WriteAllText(instructionsPath, "You are Frontend.");

            var config = new ForemanConfig(
                "Frontend",
                CrewRole.Foreman,
                "claude",
                workingDirectory,
                instructionsPath,
                new Dictionary<string, string>(),
                JobsiteName: "Frontend",
                DisplayName: null,
                AddDirs: [vaultRoot],
                VaultFolders: HireWizard.DeriveVaultFolders(vaultRoot, "Frontend"));

            var yamlPath = Path.Combine(repoRoot, "config", "foremen.yaml");
            ForemanConfigWriter.AppendForeman(yamlPath, config, repoRoot, vaultRoot);

            var reloaded = new ForemanConfigLoader().LoadFromFile(yamlPath, repoRoot, vaultRoot, "GC");

            Assert.Equal(["Notes/Frontend", "Plans/Frontend"], Assert.Single(reloaded).VaultFolders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NormalizeBranch_Blank_IsMain()
    {
        // Never the fallback prose: this value lands inside `gh pr create --base ...`.
        Assert.Equal("main", HireWizard.NormalizeBranch(null));
        Assert.Equal("main", HireWizard.NormalizeBranch(""));
        Assert.Equal("main", HireWizard.NormalizeBranch("   "));
    }

    [Fact]
    public void NormalizeBranch_TrimsInput()
    {
        Assert.Equal("develop", HireWizard.NormalizeBranch("  develop  "));
    }

    [Fact]
    public void NormalizeOptionalCommand_Blank_IsNull()
    {
        // Null, so InstructionsComposer's "ask the Boss" prose renders instead.
        Assert.Null(HireWizard.NormalizeOptionalCommand(null));
        Assert.Null(HireWizard.NormalizeOptionalCommand("   "));
        Assert.Equal("dotnet build", HireWizard.NormalizeOptionalCommand("  dotnet build  "));
    }

    [Fact]
    public void EnsureVaultFolders_CreatesMissingFolders()
    {
        var vaultRoot = NewRecognizedVault();
        try
        {
            var rejected = HireWizard.EnsureVaultFolders(vaultRoot, ["Notes/Frontend", "Plans/Frontend"]);

            Assert.Empty(rejected);
            Assert.True(Directory.Exists(Path.Combine(vaultRoot, "Notes", "Frontend")));
            Assert.True(Directory.Exists(Path.Combine(vaultRoot, "Plans", "Frontend")));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void EnsureVaultFolders_ExistingFolder_IsLeftAlone()
    {
        var vaultRoot = NewRecognizedVault();
        try
        {
            var existing = Path.Combine(vaultRoot, "Notes", "Frontend");
            Directory.CreateDirectory(existing);
            var note = Path.Combine(existing, "Sitewalk.md");
            File.WriteAllText(note, "# Sitewalk");

            var rejected = HireWizard.EnsureVaultFolders(vaultRoot, ["Notes/Frontend"]);

            Assert.Empty(rejected);
            Assert.True(File.Exists(note));
            Assert.Equal("# Sitewalk", File.ReadAllText(note));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void EnsureVaultFolders_EscapingEntry_IsRejectedAndReported()
    {
        var vaultRoot = NewRecognizedVault();
        var outside = Path.GetFullPath(Path.Combine(vaultRoot, "..", "outside"));
        try
        {
            var rejected = HireWizard.EnsureVaultFolders(vaultRoot, ["../outside", "Notes/Frontend"]);

            // Reported back, and nothing created outside the vault.
            Assert.Equal(["../outside"], rejected);
            Assert.False(Directory.Exists(outside));

            // The good entry beside it is still created.
            Assert.True(Directory.Exists(Path.Combine(vaultRoot, "Notes", "Frontend")));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    private static string NewRecognizedVault(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), "ccrew-hire-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "HOME.md"), "# HOME");
        File.WriteAllText(Path.Combine(path, "CLAUDE.md"), "# CLAUDE");
        Directory.CreateDirectory(Path.Combine(path, "Notes"));
        Directory.CreateDirectory(Path.Combine(path, "Plans"));
        return path;
    }
}
