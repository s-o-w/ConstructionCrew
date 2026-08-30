namespace ConstructionCrew.Config;

public sealed record AppSettings(
    string ForemenConfigPath,
    string JobsitesConfigPath,
    string StateDirectory,
    string GeneratedConfigDirectory,
    int HomeOfficePort,
    string GcForemanName)
{
    public static AppSettings ForRepoRoot(string repoRoot) => new(
        ForemenConfigPath: Path.Combine(repoRoot, "config", "foremen.yaml"),
        JobsitesConfigPath: Path.Combine(repoRoot, "config", "jobsites.yaml"),
        StateDirectory: Path.Combine(repoRoot, "state"),
        GeneratedConfigDirectory: Path.Combine(repoRoot, "config", "generated"),
        HomeOfficePort: 5199,
        GcForemanName: "GC");
}
