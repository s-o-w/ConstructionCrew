using System.Text;
using ConstructionCrew.Core;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Appends a new Foreman block to foremen.yaml as plain text. Deliberately not
/// a deserialize-mutate-reserialize round trip through YamlDotNet -- that would
/// drop the hand-written comments at the top of the file. A hand-formatted
/// append is simple and safe for this file's shape (a flat list of Foremen).
/// </summary>
public static class ForemanConfigWriter
{
    public static void AppendForeman(string path, ForemanConfig config, string repoRoot)
    {
        var workingDirectory = CollapseRepoRoot(config.WorkingDirectory, repoRoot);
        var instructionsFilePath = CollapseRepoRoot(config.InstructionsFilePath, repoRoot);

        var block = new StringBuilder();
        block.AppendLine();
        block.AppendLine($"  - name: {Quote(config.Name)}");
        block.AppendLine($"    provider: {Quote(config.Provider)}");
        block.AppendLine($"    workingDirectory: {Quote(workingDirectory)}");
        block.AppendLine($"    instructionsFilePath: {Quote(instructionsFilePath)}");

        if (!string.IsNullOrWhiteSpace(config.JobsiteName))
        {
            block.AppendLine($"    jobsiteName: {Quote(config.JobsiteName)}");
        }

        if (config.ProviderOptions.Count > 0)
        {
            block.AppendLine("    providerOptions:");
            foreach (var (key, value) in config.ProviderOptions)
            {
                // Collapse ${repoRoot} first (e.g. mcpConfigPath is always
                // under the repo) so most values end up with no backslashes
                // at all; single-quoting whatever's left handles it safely
                // regardless.
                block.AppendLine($"      {key}: {Quote(CollapseRepoRoot(value, repoRoot))}");
            }
        }

        File.AppendAllText(path, block.ToString());
    }

    /// <summary>
    /// Removes a Foreman's entry from foremen.yaml. Only ever touches this
    /// config file -- never the Foreman's working directory or anything under
    /// it. Callers (the /fire flow) must never pass anything else to delete.
    /// </summary>
    public static bool RemoveForeman(string path, string name) => YamlListEditor.RemoveEntry(path, "foremen", name);

    private static string CollapseRepoRoot(string absolutePath, string repoRoot) =>
        absolutePath.StartsWith(repoRoot, PathComparison.ForPathPrefix)
            ? "${repoRoot}" + absolutePath[repoRoot.Length..].Replace('\\', '/')
            : absolutePath;

    /// <summary>
    /// Single-quoted YAML has no escape processing at all (except '' for a
    /// literal quote) -- unlike unquoted plain scalars, which break on things
    /// as ordinary as a Windows drive letter ("c:") or a path with backslashes,
    /// both hit for real on this file. Quote every free-form value, always.
    /// </summary>
    internal static string Quote(string value) => $"'{value.Replace("'", "''")}'";
}
