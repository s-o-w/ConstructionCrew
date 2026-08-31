using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

/// <summary>
/// Round-tripping the fields Phase 1a added, plus the two collection-level
/// invariants and the ${vaultRoot} expansion contract. Every new persisted field
/// is a three-part change (record, DTO/loader, writer) -- these tests are what
/// catch the half that silently fails to persist.
/// </summary>
public class ForemanConfigLoaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("foreman-loader-tests-").FullName;
    private readonly string _repoRoot;
    private readonly string _vaultRoot;
    private readonly string _instructions;
    private readonly string _yamlPath;

    public ForemanConfigLoaderTests()
    {
        _repoRoot = Path.Combine(_root, "repo");
        _vaultRoot = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_vaultRoot);

        _instructions = Path.Combine(_repoRoot, "GC.md");
        File.WriteAllText(_instructions, "You are the GC.");

        _yamlPath = Path.Combine(_repoRoot, "foremen.yaml");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RoundTrip_PreservesRoleDisplayNameAddDirsAndVaultFolders()
    {
        File.WriteAllText(_yamlPath, "foremen:\n");

        var config = new ForemanConfig(
            "GC",
            CrewRole.GC,
            "claude",
            _vaultRoot,
            _instructions,
            new Dictionary<string, string> { ["allowedTools"] = "Read" },
            JobsiteName: null,
            DisplayName: "Chief",
            AddDirs: [_repoRoot, _vaultRoot],
            VaultFolders: ["Notes/Thing", "Plans/Thing"]);

        ForemanConfigWriter.AppendForeman(_yamlPath, config, _repoRoot, _vaultRoot);

        var reloaded = Load();

        var gc = Assert.Single(reloaded);
        Assert.Equal(CrewRole.GC, gc.Role);
        Assert.Equal("Chief", gc.DisplayName);
        Assert.Equal([_repoRoot, _vaultRoot], gc.AddDirs);
        Assert.Equal(["Notes/Thing", "Plans/Thing"], gc.VaultFolders);
    }

    [Fact]
    public void AppendForeman_CollapsesAVaultRootedWorkingDirectoryToTheVaultRootToken()
    {
        File.WriteAllText(_yamlPath, "foremen:\n");

        ForemanConfigWriter.AppendForeman(_yamlPath, GcConfig(), _repoRoot, _vaultRoot);

        Assert.Contains("workingDirectory: '${vaultRoot}'", File.ReadAllText(_yamlPath));
        Assert.Equal(_vaultRoot, Load()[0].WorkingDirectory);
    }

    [Fact]
    public void AppendForeman_WithNullVaultRoot_FallsThroughToRepoRootCollapsing()
    {
        File.WriteAllText(_yamlPath, "foremen:\n");

        var config = GcConfig() with { WorkingDirectory = _repoRoot };
        ForemanConfigWriter.AppendForeman(_yamlPath, config, _repoRoot, vaultRoot: null);

        var text = File.ReadAllText(_yamlPath);
        Assert.Contains("workingDirectory: '${repoRoot}'", text);
        Assert.DoesNotContain("${vaultRoot}", text);
    }

    [Fact]
    public void RoleIsOptionalInYaml_AndDefaultsToForeman()
    {
        WriteYaml($"""
            foremen:
              - name: 'Backend'
                provider: 'claude'
                workingDirectory: '{_repoRoot}'
                instructionsFilePath: '{_instructions}'
            """);

        Assert.Equal(CrewRole.Foreman, Load()[0].Role);
    }

    [Fact]
    public void AnUnrecognizedRole_IsALoadErrorNamingTheFileAndTheValue()
    {
        WriteYaml($"""
            foremen:
              - name: 'Backend'
                role: 'Overlord'
                provider: 'claude'
                workingDirectory: '{_repoRoot}'
                instructionsFilePath: '{_instructions}'
            """);

        var ex = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains("Overlord", ex.Message);
        Assert.Contains(_yamlPath, ex.Message);
    }

    [Fact]
    public void TwoGcEntries_FailValidationNamingBothAndTheExpectedGcName()
    {
        WriteYaml($"""
            foremen:
              - name: 'GC'
                role: 'GC'
                provider: 'claude'
                workingDirectory: '{_repoRoot}'
                instructionsFilePath: '{_instructions}'
              - name: 'Deputy'
                role: 'GC'
                provider: 'claude'
                workingDirectory: '{_repoRoot}'
                instructionsFilePath: '{_instructions}'
            """);

        var ex = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains("GC", ex.Message);
        Assert.Contains("Deputy", ex.Message);
    }

    [Fact]
    public void AGcRoleUnderTheWrongName_FailsValidationNamingBoth()
    {
        WriteYaml($"""
            foremen:
              - name: 'Chief'
                role: 'GC'
                provider: 'claude'
                workingDirectory: '{_repoRoot}'
                instructionsFilePath: '{_instructions}'
            """);

        var ex = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains("Chief", ex.Message);
        Assert.Contains("'GC'", ex.Message);
    }

    [Fact]
    public void TheReservedGcNameWithoutTheGcRole_FailsValidation()
    {
        WriteYaml($"""
            foremen:
              - name: 'GC'
                role: 'Foreman'
                provider: 'claude'
                workingDirectory: '{_repoRoot}'
                instructionsFilePath: '{_instructions}'
            """);

        var ex = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains("role: GC", ex.Message);
    }

    [Fact]
    public void VaultRootToken_ExpandsWhenAVaultIsConfigured()
    {
        WriteYaml($$"""
            foremen:
              - name: 'GC'
                role: 'GC'
                provider: 'claude'
                workingDirectory: '${vaultRoot}'
                instructionsFilePath: '{{_instructions}}'
                addDirs:
                  - '${repoRoot}'
            """);

        var gc = Load()[0];
        Assert.Equal(_vaultRoot, gc.WorkingDirectory);
        Assert.Equal([_repoRoot], gc.AddDirs);
    }

    [Fact]
    public void VaultRootToken_WithNoVaultConfigured_IsALoadErrorNotAnEmptyExpansion()
    {
        WriteYaml($$"""
            foremen:
              - name: 'GC'
                role: 'GC'
                provider: 'claude'
                workingDirectory: '${vaultRoot}'
                instructionsFilePath: '{{_instructions}}'
            """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ForemanConfigLoader().LoadFromFile(_yamlPath, _repoRoot, vaultRoot: null, "GC"));

        Assert.Contains("GC", ex.Message);
        Assert.Contains("vaultRoot", ex.Message);
    }

    [Fact]
    public void NoVaultConfiguredAndNoTokenAnywhere_LoadsExactlyAsBefore()
    {
        WriteYaml($$"""
            foremen:
              - name: 'Backend'
                provider: 'claude'
                workingDirectory: '${repoRoot}'
                instructionsFilePath: '{{_instructions}}'
            """);

        var reloaded = new ForemanConfigLoader().LoadFromFile(_yamlPath, _repoRoot, vaultRoot: null, "GC");

        Assert.Equal(_repoRoot, Assert.Single(reloaded).WorkingDirectory);
    }

    /// <summary>
    /// Regression: a hand-edited/truncated file missing its "foremen:" key
    /// used to surface a raw, opaque YamlException from deep inside
    /// YamlDotNet. It must now name the file and say what to check.
    /// </summary>
    [Fact]
    public void LoadFromFile_MissingTopLevelKey_ThrowsAClearActionableError()
    {
        WriteYaml("""
              - name: 'Frontend'
                role: 'Foreman'
                provider: 'claude'
            """);

        var ex = Assert.Throws<InvalidOperationException>(Load);

        Assert.Contains(_yamlPath, ex.Message);
        Assert.Contains("foremen:", ex.Message);
    }

    private ForemanConfig GcConfig() => new(
        "GC",
        CrewRole.GC,
        "claude",
        _vaultRoot,
        _instructions,
        new Dictionary<string, string>());

    private void WriteYaml(string content) => File.WriteAllText(_yamlPath, content);

    private IReadOnlyList<ForemanConfig> Load() =>
        new ForemanConfigLoader().LoadFromFile(_yamlPath, _repoRoot, _vaultRoot, "GC");
}
