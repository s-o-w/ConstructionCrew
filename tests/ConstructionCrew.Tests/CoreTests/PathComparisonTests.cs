using ConstructionCrew.Core;

namespace ConstructionCrew.Tests.CoreTests;

/// <summary>
/// Regression coverage for a real cross-platform gap: a hardcoded
/// OrdinalIgnoreCase path-prefix check (used by /fire's delete guard, among
/// others) is correct on Windows/macOS but too lenient on Linux, where two
/// differently-cased paths are genuinely different directories. These tests
/// assert the *correct-for-this-platform* behavior, so they're meaningful
/// (and would catch a regression) on whichever OS actually runs them.
/// </summary>
public class PathComparisonTests
{
    private static readonly bool IsCaseInsensitivePlatform = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    [Fact]
    public void ForPathPrefix_MatchesThisPlatformsFilesystemCaseSensitivity()
    {
        var expected = IsCaseInsensitivePlatform ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        Assert.Equal(expected, PathComparison.ForPathPrefix);
    }

    [Fact]
    public void MixedCasePrefix_MatchesOnlyOnCaseInsensitivePlatforms()
    {
        var matches = "/Config/Instructions/Foo.md".StartsWith("/config/instructions", PathComparison.ForPathPrefix);

        Assert.Equal(IsCaseInsensitivePlatform, matches);
    }

    [Fact]
    public void ExactCasePrefix_AlwaysMatches()
    {
        Assert.StartsWith("/config/instructions", "/config/instructions/Foo.md", PathComparison.ForPathPrefix);
    }
}
