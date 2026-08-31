using System.Text.Json.Nodes;
using ConstructionCrew.App;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Tests.TestSupport;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// The invariant first run has to hold: whatever else the Boss chooses, the GC
/// entry it writes carries role GC and the canonical reserved name. Break either
/// and the roster loads into a Program.cs that can't find a GC at all -- the one
/// branch Phase 3 deliberately left as a hard fail.
/// </summary>
public class FirstRunWizardTests
{
    [Fact]
    public void BuildGcConfig_AlwaysUsesRoleGcAndTheCanonicalName()
    {
        var settings = AppSettings.ForRepoRoot("/repo");

        var config = FirstRunWizard.BuildGcConfig(
            settings.GcForemanName,
            "claude",
            "/vault",
            "/repo/config/instructions/GC.md",
            "/repo",
            displayName: "Chief");

        Assert.Equal(settings.GcForemanName, config.Name);
        Assert.Equal(CrewRole.GC, config.Role);

        // DisplayName is UI only -- it must never displace the lookup key.
        Assert.Equal("Chief", config.DisplayName);
        Assert.NotEqual(config.DisplayName, config.Name);

        // GC's cwd is the Vault, not the repo; the repo rides along on --add-dir.
        Assert.Equal("/vault", config.WorkingDirectory);
        Assert.Equal(["/repo"], config.AddDirs);
    }

    [Fact]
    public void ExpandPath_StripsWrappingQuotes_SameResultAsUnquoted()
    {
        var quoted = FirstRunWizard.ExpandPath("\"C:\\foo\\bar\"");
        var unquoted = FirstRunWizard.ExpandPath("C:\\foo\\bar");

        Assert.Equal(unquoted, quoted);
    }

    [Fact]
    public void BuildGcConfig_WithNoDisplayName_LeavesItNull()
    {
        var config = FirstRunWizard.BuildGcConfig("GC", "claude", "/vault", "/repo/GC.md", "/repo", displayName: null);

        Assert.Null(config.DisplayName);
        Assert.Equal("GC", config.Name);
        Assert.Equal(CrewRole.GC, config.Role);
    }

    [Fact]
    public void BuildGcConfig_GivesGcTheHomeOfficeTools()
    {
        // Under `claude -p`, an allow-list that omits the Home Office MCP tools
        // silently denies every dispatch -- a GC that talks but never delegates.
        var config = FirstRunWizard.BuildGcConfig("GC", "claude", "/vault", "/repo/GC.md", "/repo", null);

        Assert.Contains("mcp__home_office__dispatch_task", config.ProviderOptions["allowedTools"]);
    }

    /// <summary>
    /// Without a vault write scope SitrepWriter.FindNotesFolder returns null and
    /// file_sitrep throws. The first entry has to be the one under Notes/, because
    /// that is the one FindNotesFolder takes.
    /// </summary>
    [Fact]
    public void BuildGcConfig_SetsGcVaultFolders()
    {
        var config = FirstRunWizard.BuildGcConfig("GC", "claude", "/vault", "/repo/GC.md", "/repo", null);

        Assert.NotNull(config.VaultFolders);
        Assert.Equal("Notes/GC", config.VaultFolders![0]);
        Assert.Equal(FirstRunWizard.GcVaultFolders, config.VaultFolders);
    }

    /// <summary>
    /// Regression test for a real bug hit live (2026-08-30): GC.md is no longer
    /// shipped (Phase 4 -- it's per-install state, rendered fresh). First run's
    /// own call to EnsureGcInstructions only fires when foremen.yaml doesn't
    /// exist yet -- an EXISTING roster (the common case: the Boss already ran
    /// the app before, or foremen.yaml survives a git pull that deletes the
    /// tracked GC.md alongside it) never re-triggers first run, so nothing
    /// regenerated the file. ForemanConfigLoader.LoadFromFile hard-fails on a
    /// missing instructionsFilePath, so the app refused to start at all. Fixed
    /// by calling EnsureGcInstructions unconditionally in Program.cs, right
    /// before the roster loads -- this test pins that it's actually callable
    /// (internal, not private) and produces a real file for a missing path.
    /// </summary>
    [Fact]
    public void EnsureGcInstructions_MissingFile_RendersOneEvenOutsideFirstRun()
    {
        var root = NewTempDir();
        try
        {
            var repoRoot = Path.Combine(root, "repo");
            var vaultRoot = Path.Combine(root, "vault");
            Directory.CreateDirectory(vaultRoot);

            var path = FirstRunWizard.EnsureGcInstructions(repoRoot, vaultRoot, "GC", ["claude"]);

            Assert.True(File.Exists(path));
            Assert.Contains("General Contractor", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WrittenGcConfig_RoundTripsThroughTheLoadersValidation()
    {
        // The real end-to-end shape: what BuildGcConfig produces, written by the
        // writer, has to survive the loader's collection-level one-GC /
        // name-matches-GcForemanName validation -- the check that would reject a
        // wizard writing role Foreman or a non-canonical name.
        var root = NewTempDir();
        try
        {
            var repoRoot = Path.Combine(root, "repo");
            var vaultRoot = Path.Combine(root, "vault");
            Directory.CreateDirectory(vaultRoot);
            var instructionsPath = Path.Combine(repoRoot, "config", "instructions", "GC.md");
            Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
            File.WriteAllText(instructionsPath, "You are the GC.");

            var yamlPath = Path.Combine(repoRoot, "config", "foremen.yaml");
            var config = FirstRunWizard.BuildGcConfig("GC", "claude", vaultRoot, instructionsPath, repoRoot, null);

            // No pre-existing file: AppendForeman must create it with its header.
            ForemanConfigWriter.AppendForeman(yamlPath, config, repoRoot, vaultRoot);

            Assert.Contains("${vaultRoot}", File.ReadAllText(yamlPath));

            var reloaded = new ForemanConfigLoader().LoadFromFile(yamlPath, repoRoot, vaultRoot, "GC");

            var gc = Assert.Single(reloaded);
            Assert.Equal("GC", gc.Name);
            Assert.Equal(CrewRole.GC, gc.Role);
            Assert.Equal(Path.GetFullPath(vaultRoot), Path.GetFullPath(gc.WorkingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PersistVaultRoot_MergesIntoExistingSettingsWithoutLosingThem()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "appsettings.json");
            File.WriteAllText(path, """{ "HomeOffice": { "Port": 5199 } }""");

            FirstRunWizard.PersistVaultRoot(path, "/home/boss/Vault");

            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal("/home/boss/Vault", (string?)json["Vault"]!["Root"]);

            // The port has to survive -- it lives in the same file.
            Assert.Equal(5199, (int?)json["HomeOffice"]!["Port"]);

            // And it has to be what AppSettingsLoader actually reads back.
            Assert.Equal("/home/boss/Vault", AppSettingsLoader.Load(root, []).VaultRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PersistVaultRoot_WithNoFileYet_CreatesOne()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "appsettings.json");

            FirstRunWizard.PersistVaultRoot(path, "/home/boss/Vault");

            Assert.Equal("/home/boss/Vault", AppSettingsLoader.Load(root, []).VaultRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RepointGcAtVault_PointsWorkingDirectoryAtVaultAndAddsRepoToAddDirs()
    {
        // The exact shape a foremen.yaml predating Vault setup has: GC's cwd is
        // the repo (there was no Vault to point it at yet), and no AddDirs at
        // all, since BuildGcConfig's own repo-in-AddDirs rule never ran.
        var gc = new ForemanConfig(
            "GC", CrewRole.GC, "claude", "/repo", "/repo/config/instructions/GC.md",
            new Dictionary<string, string>());

        var updated = FirstRunWizard.RepointGcAtVault(gc, "/vault", "/repo");

        Assert.Equal("/vault", updated.WorkingDirectory);
        Assert.Equal(["/repo"], updated.AddDirs);

        // Everything else about GC is untouched.
        Assert.Equal("GC", updated.Name);
        Assert.Equal(CrewRole.GC, updated.Role);
    }

    [Fact]
    public void RepointGcAtVault_WithRepoAlreadyInAddDirs_DoesNotDuplicateIt()
    {
        var gc = new ForemanConfig(
            "GC", CrewRole.GC, "claude", "/repo", "/repo/config/instructions/GC.md",
            new Dictionary<string, string>(), AddDirs: ["/repo", "/somewhere/else"]);

        var updated = FirstRunWizard.RepointGcAtVault(gc, "/vault", "/repo");

        Assert.Equal(["/repo", "/somewhere/else"], updated.AddDirs);
    }

    [Fact]
    public void RepointGcAtVault_KeepsAnyExistingAddDirsEntriesTheBossAddedByHand()
    {
        var gc = new ForemanConfig(
            "GC", CrewRole.GC, "claude", "/repo", "/repo/config/instructions/GC.md",
            new Dictionary<string, string>(), AddDirs: ["/somewhere/else"]);

        var updated = FirstRunWizard.RepointGcAtVault(gc, "/vault", "/repo");

        Assert.Equal(["/somewhere/else", "/repo"], updated.AddDirs);
    }

    /// <summary>
    /// EnsureGcInstructions is private and returns early when the file already
    /// exists, so the thing worth pinning is what it would render: the CURRENT
    /// gc-instructions.md template, not the stale copy this repo used to ship.
    /// Both of these were missing from that deleted file.
    /// </summary>
    [Fact]
    public void ComposedGcInstructions_CarryTheSectionsTheStaleShippedFileLacked()
    {
        var rendered = InstructionsComposer.Compose(
            "GC",
            CrewRole.GC,
            briefing: string.Empty,
            jobsite: null,
            vaultFolders: ["AI/Context"],
            availableEngines: ["claude"],
            vaultRoot: SeededVault.WithInstructionsTemplates());

        Assert.Contains("Closing out a Feature", rendered);
        Assert.Contains("file_sitrep", rendered);
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccrew-firstrun-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
