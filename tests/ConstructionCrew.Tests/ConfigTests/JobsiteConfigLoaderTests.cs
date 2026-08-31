using ConstructionCrew.Config;

namespace ConstructionCrew.Tests.ConfigTests;

/// <summary>
/// Regression coverage for a live bug: a jobsites.yaml missing its top-level
/// "jobsites:" key (hand-edited, or truncated) used to surface a raw, opaque
/// "No node deserializer was able to deserialize the node into type
/// ...JobsiteFileDto" straight from YamlDotNet, with no file path and no hint
/// what to check. It must now name the file and say what's likely wrong.
/// </summary>
public class JobsiteConfigLoaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("jobsite-loader-tests-").FullName;
    private readonly string _repoRoot;
    private readonly string _yamlPath;

    public JobsiteConfigLoaderTests()
    {
        _repoRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoRoot);
        _yamlPath = Path.Combine(_root, "jobsites.yaml");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void LoadFromFile_MissingTopLevelKey_ThrowsAClearActionableError()
    {
        // The exact shape that actually broke: the "jobsites:" key line (and
        // the header comment) missing, a bare indented list left behind.
        File.WriteAllText(_yamlPath, """


              - name: 'test'
                repoPath: 'C:\repo\test'
                description: 'a test jobsite'
            """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new JobsiteConfigLoader().LoadFromFile(_yamlPath, _repoRoot));

        Assert.Contains(_yamlPath, ex.Message);
        Assert.Contains("jobsites:", ex.Message);
    }

    [Fact]
    public void LoadFromFile_MissingFile_ReturnsEmptyNotAnError()
    {
        var configs = new JobsiteConfigLoader().LoadFromFile(
            Path.Combine(_root, "does-not-exist.yaml"), _repoRoot);

        Assert.Empty(configs);
    }
}
