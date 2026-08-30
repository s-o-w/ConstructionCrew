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
    private static bool IsCaseInsensitiveFileSystem =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparison ForPathPrefix =>
        IsCaseInsensitiveFileSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The same policy as <see cref="ForPathPrefix"/>, as a StringComparer -- for
    /// anything keying a dictionary by path (RunLogWriter's per-file lock map).
    /// Derived from one OS check, so the two can never drift apart.
    /// </summary>
    public static StringComparer PathComparer =>
        IsCaseInsensitiveFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
