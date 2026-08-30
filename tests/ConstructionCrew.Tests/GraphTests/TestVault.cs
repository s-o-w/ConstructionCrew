namespace ConstructionCrew.Tests.GraphTests;

/// <summary>
/// Locates the real vault the graph gate runs against. CONSTRUCTIONCREW_VAULT_ROOT
/// wins; otherwise the conventional location is tried, and a miss skips the test
/// rather than failing it -- the gate needs a real vault, and CI may not have one.
/// </summary>
internal static class TestVault
{
    public static string FixturesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VaultGraph");

    public static string? Locate()
    {
        var configured = Environment.GetEnvironmentVariable("CONSTRUCTIONCREW_VAULT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && IsVault(configured))
        {
            return configured;
        }

        var conventional = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "MyObsidianVault");

        return IsVault(conventional) ? conventional : null;
    }

    private static bool IsVault(string path) =>
        File.Exists(Path.Combine(path, "AI", "graph", "context.jsonld"));
}
