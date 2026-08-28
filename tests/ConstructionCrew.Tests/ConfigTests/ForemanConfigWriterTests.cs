using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

public class ForemanConfigWriterTests
{
    [Fact]
    public void AppendForeman_ThenReload_RoundTripsCorrectly()
    {
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workingDirectory = Path.Combine(repoRoot, "sandbox", "backend");
        var instructionsPath = Path.Combine(repoRoot, "config", "instructions", "Backend.md");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
        File.WriteAllText(instructionsPath, "You are Backend.");

        var yamlPath = Path.GetTempFileName();
        File.WriteAllText(yamlPath, "foremen:\n");

        try
        {
            var config = new ForemanConfig(
                "Backend",
                "claude",
                workingDirectory,
                instructionsPath,
                new Dictionary<string, string> { ["allowedTools"] = "Bash,Edit,Read,Write" });

            ForemanConfigWriter.AppendForeman(yamlPath, config, repoRoot);

            var reloaded = new ForemanConfigLoader().LoadFromFile(yamlPath, repoRoot);

            Assert.Single(reloaded);
            var backend = reloaded[0];
            Assert.Equal("Backend", backend.Name);
            Assert.Equal("claude", backend.Provider);

            // Compare via GetFullPath, not raw strings: the writer collapses
            // ${repoRoot} using forward slashes, so a round trip can come back
            // with different separator style than the original Path.Combine
            // value even though both resolve to the same directory on disk.
            Assert.Equal(Path.GetFullPath(workingDirectory), Path.GetFullPath(backend.WorkingDirectory));
            Assert.Equal(Path.GetFullPath(instructionsPath), Path.GetFullPath(backend.InstructionsFilePath));
            Assert.Equal("Bash,Edit,Read,Write", backend.ProviderOptions["allowedTools"]);
        }
        finally
        {
            File.Delete(yamlPath);
            File.Delete(instructionsPath);
        }
    }

    [Fact]
    public void AppendForeman_ProviderOptionWithWindowsPath_RoundTripsWithoutCorruptingYaml()
    {
        // Regression test for a real bug hit 2026-08-29: a providerOptions value
        // that's an absolute Windows path (e.g. an auto-stamped mcpConfigPath)
        // written as a double-quoted YAML string broke the file the first time
        // it happened for real -- "C:\Users\..." reads \U as a YAML/C-style
        // unicode escape. Single-quoted YAML has no escape processing, so this
        // must survive round-tripping intact.
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workingDirectory = Path.Combine(repoRoot, "sandbox", "fred");
        var instructionsPath = Path.Combine(repoRoot, "config", "instructions", "fred.md");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
        File.WriteAllText(instructionsPath, "You are fred.");

        var yamlPath = Path.GetTempFileName();
        File.WriteAllText(yamlPath, "foremen:\n");

        var mcpConfigPath = @"C:\Users\shawn.weekly\PROJECTS\ConstructionCrew\config\generated\claude-mcp-config.json";

        try
        {
            var config = new ForemanConfig(
                "fred",
                "claude",
                workingDirectory,
                instructionsPath,
                new Dictionary<string, string>
                {
                    ["allowedTools"] = "Bash,Edit,Read,Write",
                    ["mcpConfigPath"] = mcpConfigPath,
                });

            ForemanConfigWriter.AppendForeman(yamlPath, config, repoRoot);

            var reloaded = new ForemanConfigLoader().LoadFromFile(yamlPath, repoRoot);

            Assert.Single(reloaded);
            Assert.Equal(mcpConfigPath, reloaded[0].ProviderOptions["mcpConfigPath"]);
        }
        finally
        {
            File.Delete(yamlPath);
            File.Delete(instructionsPath);
        }
    }

    [Fact]
    public void RemoveForeman_RemovesOnlyTheNamedEntry_PreservesHeaderCommentsAndOthers()
    {
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirA = Path.Combine(repoRoot, "sandbox", "alpha");
        var dirB = Path.Combine(repoRoot, "sandbox", "beta");
        var instructionsA = Path.Combine(repoRoot, "config", "instructions", "Alpha.md");
        var instructionsB = Path.Combine(repoRoot, "config", "instructions", "Beta.md");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(Path.GetDirectoryName(instructionsA)!);
        File.WriteAllText(instructionsA, "You are Alpha.");
        File.WriteAllText(instructionsB, "You are Beta.");

        var yamlPath = Path.GetTempFileName();
        File.WriteAllText(yamlPath, "# a header comment\nforemen:\n");

        try
        {
            ForemanConfigWriter.AppendForeman(yamlPath, new ForemanConfig("Alpha", "claude", dirA, instructionsA, new Dictionary<string, string>()), repoRoot);
            ForemanConfigWriter.AppendForeman(yamlPath, new ForemanConfig("Beta", "claude", dirB, instructionsB, new Dictionary<string, string>()), repoRoot);

            var removed = ForemanConfigWriter.RemoveForeman(yamlPath, "Alpha");

            Assert.True(removed);
            Assert.Contains("# a header comment", File.ReadAllText(yamlPath));

            var reloaded = new ForemanConfigLoader().LoadFromFile(yamlPath, repoRoot);
            Assert.Single(reloaded);
            Assert.Equal("Beta", reloaded[0].Name);
        }
        finally
        {
            File.Delete(yamlPath);
            File.Delete(instructionsA);
            File.Delete(instructionsB);
        }
    }

    [Fact]
    public void RemoveForeman_UnknownName_ReturnsFalse_LeavesFileUnchanged()
    {
        var yamlPath = Path.GetTempFileName();
        File.WriteAllText(yamlPath, "foremen:\n");

        try
        {
            var before = File.ReadAllText(yamlPath);
            var removed = ForemanConfigWriter.RemoveForeman(yamlPath, "NoSuchForeman");

            Assert.False(removed);
            Assert.Equal(before, File.ReadAllText(yamlPath));
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void AppendForeman_ValueWithBareTrailingColon_RoundTripsWithoutCorruptingYaml()
    {
        // Regression test for a second real bug hit 2026-08-29, same session as
        // the one above: an unquoted plain YAML scalar ending in a bare colon
        // (e.g. a Windows drive letter like "c:", entered as a workingDirectory
        // during a real /hire flow) is the classic "while scanning a plain
        // scalar value, found invalid mapping" trap -- YAML reads the trailing
        // ":" as trying to open another mapping. Every writer must quote every
        // free-form value, not just the ones that happened to get tested.
        var repoRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var instructionsPath = Path.Combine(repoRoot, "config", "instructions", "DriveRoot.md");
        Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
        File.WriteAllText(instructionsPath, "You are DriveRoot.");

        var yamlPath = Path.GetTempFileName();
        File.WriteAllText(yamlPath, "foremen:\n");

        try
        {
            var config = new ForemanConfig("DriveRoot", "claude", "c:", instructionsPath, new Dictionary<string, string>());

            ForemanConfigWriter.AppendForeman(yamlPath, config, repoRoot);

            var reloaded = new ForemanConfigLoader().LoadFromFile(yamlPath, repoRoot);

            Assert.Single(reloaded);
            Assert.Equal("DriveRoot", reloaded[0].Name);
        }
        finally
        {
            File.Delete(yamlPath);
            File.Delete(instructionsPath);
        }
    }
}
