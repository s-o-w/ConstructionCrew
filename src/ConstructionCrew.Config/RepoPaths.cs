namespace ConstructionCrew.Config;

/// <summary>Finds the repo root by walking up from a start directory to the solution file.</summary>
public static class RepoPaths
{
    public static string FindRepoRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("ConstructionCrew.slnx").Any() || dir.EnumerateFiles("ConstructionCrew.sln").Any())
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate the ConstructionCrew repo root above '{startDirectory}'.");
    }
}
