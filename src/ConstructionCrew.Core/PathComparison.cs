namespace ConstructionCrew.Core;

/// <summary>
/// Windows and macOS filesystems are (by default) case-insensitive; Linux's
/// typically isn't. A path-prefix check using the wrong comparison either
/// misses a match it should have made (harmless -- Windows/macOS) or treats
/// two genuinely different directories as the same one (Linux) -- the second
/// failure mode matters a lot for anything safety-critical, like the /fire
/// delete guard, so this isn't just a style choice.
/// </summary>
public static class PathComparison
{
    public static StringComparison ForPathPrefix =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
