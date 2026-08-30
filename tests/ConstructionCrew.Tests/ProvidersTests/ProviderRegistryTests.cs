using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Providers;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.ProvidersTests;

public class ProviderRegistryTests
{
    /// <summary>A fake PATH: only the names handed in here "resolve".</summary>
    private static Func<string, string?> PathWith(params string[] onPath)
    {
        var set = new HashSet<string>(onPath, StringComparer.OrdinalIgnoreCase);
        return name => set.Contains(name) ? "/usr/bin/" + name : null;
    }

    [Fact]
    public void Available_KeepsRegisteredProvidersThatResolveOnPath()
    {
        var registry = new ProviderRegistry(
            [new FakeCliToolProvider("claude"), new FakeCliToolProvider("codex")],
            PathWith("claude", "codex"));

        Assert.Equal(["claude", "codex"], registry.AvailableIds());
    }

    [Fact]
    public void Available_DropsRegisteredProviderThatIsMissingFromPath()
    {
        // The whole point of the registry: hiring must not offer a CLI this machine
        // doesn't have installed.
        var registry = new ProviderRegistry(
            [new FakeCliToolProvider("claude"), new FakeCliToolProvider("codex")],
            PathWith("claude"));

        Assert.Equal(["claude"], registry.AvailableIds());

        var codexProbe = registry.Probes().Single(p => p.ProviderId == "codex");
        Assert.True(codexProbe.Implemented);
        Assert.Null(codexProbe.ResolvedPath);
        Assert.False(codexProbe.Available);
    }

    [Fact]
    public void Available_DropsGemini_EvenWhenItsBinaryIsOnPath()
    {
        // Phase 2 step 6: the "gemini isn't wired yet" exclusion must come from
        // GeminiProvider declaring IsImplemented == false, NOT from a hand-written
        // `.Where(id => id != "gemini")` filter. `gemini` really is on PATH on at
        // least one dev machine, so a pure PATH probe would wrongly offer it.
        var registry = new ProviderRegistry(
            ProviderRegistry.DefaultProviders(),
            PathWith("claude", "codex", "copilot", "gemini"));

        Assert.DoesNotContain("gemini", registry.AvailableIds());
        Assert.Equal(["claude", "codex", "copilot"], registry.AvailableIds());

        var gemini = registry.Probes().Single(p => p.ProviderId == "gemini");
        Assert.False(gemini.Implemented);
        Assert.False(gemini.Available);

        // ...and it stays registered, so a foremen.yaml naming it still resolves to a
        // provider that throws a clear message rather than "no provider registered".
        Assert.Contains(registry.Registered, p => p.ProviderId == "gemini");
    }

    [Fact]
    public void GeminiProvider_StillThrows()
    {
        // Architecture section 7: explicitly out of scope, stays a throwing placeholder.
        var gemini = new GeminiProvider();
        Assert.Throws<NotSupportedException>(() =>
            gemini.BuildInvocation(new CliTaskRequest("hi", "/work", new Dictionary<string, string>())));
    }

    [Fact]
    public void Refresh_WritesToolsJsonCache_ThatReadsBack()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "cc-tools-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(stateDirectory, "tools.json");

        try
        {
            var registry = new ProviderRegistry(
                [new FakeCliToolProvider("claude"), new FakeCliToolProvider("codex"), new FakeCliToolProvider("gemini", isImplemented: false)],
                PathWith("claude"),
                cachePath);

            registry.Refresh();

            Assert.True(File.Exists(cachePath));

            var cache = ProviderRegistry.ReadCache(cachePath);
            Assert.NotNull(cache);
            Assert.Equal(3, cache!.Tools.Count);
            Assert.Equal("/usr/bin/claude", cache.Tools.Single(t => t.ProviderId == "claude").ResolvedPath);
            Assert.Null(cache.Tools.Single(t => t.ProviderId == "codex").ResolvedPath);
            Assert.False(cache.Tools.Single(t => t.ProviderId == "gemini").Implemented);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Refresh_PicksUpAToolInstalledAfterTheFirstProbe()
    {
        // /settings re-runs discovery; a CLI installed mid-session must show up.
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude" };
        var registry = new ProviderRegistry(
            [new FakeCliToolProvider("claude"), new FakeCliToolProvider("codex")],
            name => installed.Contains(name) ? "/usr/bin/" + name : null);

        Assert.Equal(["claude"], registry.AvailableIds());

        installed.Add("codex");
        registry.Refresh();

        Assert.Equal(["claude", "codex"], registry.AvailableIds());
    }

    [Fact]
    public void ResolveOnPath_FindsARealFileByExplicitPath_AndNullsAMissingOne()
    {
        var file = Path.Combine(Path.GetTempPath(), "cc-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, string.Empty);

        try
        {
            Assert.Equal(Path.GetFullPath(file), ProviderRegistry.ResolveOnPath(file));
            Assert.Null(ProviderRegistry.ResolveOnPath(file + "-nope"));
            Assert.Null(ProviderRegistry.ResolveOnPath("definitely-not-a-real-cli-" + Guid.NewGuid().ToString("N")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ResolveOnPath_SearchesPathDirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executableName = "cc-fake-cli";
        // On Windows a bare name is only found via PATHEXT, so give it a listed one.
        var fileName = OperatingSystem.IsWindows() ? executableName + ".EXE" : executableName;
        File.WriteAllText(Path.Combine(directory, fileName), string.Empty);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", originalPath + Path.PathSeparator + directory);
            Assert.NotNull(ProviderRegistry.ResolveOnPath(executableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }
    }
}
