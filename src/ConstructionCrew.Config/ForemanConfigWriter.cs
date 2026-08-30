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
    /// <summary>
    /// Creates foremen.yaml with its header comment if it is not there yet, so
    /// the first-run flow has something to append to. Mirrors
    /// <see cref="JobsiteConfigWriter.EnsureFileExists"/>. Never touches an
    /// existing file.
    /// </summary>
    public static void EnsureFileExists(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            """
            # ConstructionCrew Foreman registry -- your personal roster (git-ignored).
            #
            # "GC" is a reserved name: whichever entry is named GC plays the General
            # Contractor role the Boss talks to directly, and that entry must also
            # carry `role: GC`. Every other entry is `role: Foreman`. Add Foremen via
            # the /hire wizard; hand-editing this file also works.
            #
            # ${repoRoot} expands to this repo's root at load time and ${vaultRoot} to
            # the configured Vault root, so this file stays portable if either moves.

            foremen:

            """);
    }

    public static void AppendForeman(string path, ForemanConfig config, string repoRoot, string? vaultRoot)
    {
        EnsureFileExists(path);

        var workingDirectory = CollapseRoots(config.WorkingDirectory, vaultRoot, repoRoot);
        var instructionsFilePath = CollapseRoots(config.InstructionsFilePath, vaultRoot, repoRoot);

        var block = new StringBuilder();
        block.AppendLine();
        block.AppendLine($"  - name: {Quote(config.Name)}");
        block.AppendLine($"    role: {config.Role}");
        block.AppendLine($"    provider: {Quote(config.Provider)}");
        block.AppendLine($"    workingDirectory: {Quote(workingDirectory)}");
        block.AppendLine($"    instructionsFilePath: {Quote(instructionsFilePath)}");

        if (!string.IsNullOrWhiteSpace(config.DisplayName))
        {
            block.AppendLine($"    displayName: {Quote(config.DisplayName)}");
        }

        if (!string.IsNullOrWhiteSpace(config.JobsiteName))
        {
            block.AppendLine($"    jobsiteName: {Quote(config.JobsiteName)}");
        }

        if (config.AddDirs is { Count: > 0 })
        {
            block.AppendLine("    addDirs:");
            foreach (var dir in config.AddDirs)
            {
                block.AppendLine($"      - {Quote(CollapseRoots(dir, vaultRoot, repoRoot))}");
            }
        }

        if (config.VaultFolders is { Count: > 0 })
        {
            block.AppendLine("    vaultFolders:");
            foreach (var folder in config.VaultFolders)
            {
                // Vault-relative by definition -- no root collapsing to do.
                block.AppendLine($"      - {Quote(folder)}");
            }
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

    /// <summary>
    /// Vault prefix first, then repo prefix -- the safe ordering if the two
    /// roots were ever nested (a Vault inside the repo, or the reverse).
    /// </summary>
    private static string CollapseRoots(string absolutePath, string? vaultRoot, string repoRoot) =>
        IsUnderVaultRoot(absolutePath, vaultRoot)
            ? CollapseVaultRoot(absolutePath, vaultRoot)
            : CollapseRepoRoot(absolutePath, repoRoot);

    private static bool IsUnderVaultRoot(string absolutePath, string? vaultRoot) =>
        !string.IsNullOrWhiteSpace(vaultRoot) && absolutePath.StartsWith(vaultRoot, PathComparison.ForPathPrefix);

    internal static string CollapseVaultRoot(string absolutePath, string? vaultRoot) =>
        IsUnderVaultRoot(absolutePath, vaultRoot)
            ? "${vaultRoot}" + absolutePath[vaultRoot!.Length..].Replace('\\', '/')
            : absolutePath;

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
