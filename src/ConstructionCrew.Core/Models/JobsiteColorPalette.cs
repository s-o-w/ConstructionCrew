namespace ConstructionCrew.Core.Models;

/// <summary>
/// The curated set of jobsite border colors, referenced by name so persisted
/// config (jobsites.yaml) and rendering (Spectre.Console, in the App project)
/// stay decoupled: this class has no UI dependency.
/// </summary>
public static class JobsiteColorPalette
{
    public static readonly IReadOnlyList<string> Names =
        ["red", "green", "yellow", "blue", "magenta", "cyan", "orange", "purple"];

    /// <summary>Stable per-name fallback for a jobsite that has no explicit color persisted (e.g. hired before this existed).</summary>
    public static string DeriveDeterministic(string jobsiteName)
    {
        var index = Math.Abs(jobsiteName.GetHashCode()) % Names.Count;
        return Names[index];
    }

    public static string PickRandom(Random random) => Names[random.Next(Names.Count)];
}
