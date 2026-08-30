using System.Text.Json.Nodes;
using ConstructionCrew.App;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

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

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccrew-firstrun-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
