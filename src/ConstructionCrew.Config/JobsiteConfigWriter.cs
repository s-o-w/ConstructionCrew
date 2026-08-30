using System.Text;
using ConstructionCrew.Core;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>Plain-text append, same rationale as ForemanConfigWriter -- avoids a round trip that would drop comments.</summary>
public static class JobsiteConfigWriter
{
    public static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "# ConstructionCrew Jobsite registry -- projects GC is responsible for.\njobsites:\n");
        }
    }

    public static void AppendJobsite(string path, JobsiteConfig config, string repoRoot)
    {
        EnsureFileExists(path);

        var repoPath = CollapseRepoRoot(config.RepoPath, repoRoot);

        var block = new StringBuilder();
        block.AppendLine();
        block.AppendLine($"  - name: {ForemanConfigWriter.Quote(config.Name)}");
        block.AppendLine($"    repoPath: {ForemanConfigWriter.Quote(repoPath)}");
        block.AppendLine($"    description: {ForemanConfigWriter.Quote(config.Description.ReplaceLineEndings(" "))}");
        if (!string.IsNullOrWhiteSpace(config.RepoUrl))
        {
            block.AppendLine($"    repoUrl: {ForemanConfigWriter.Quote(config.RepoUrl)}");
        }

        if (!string.IsNullOrWhiteSpace(config.ColorName))
        {
            block.AppendLine($"    color: {ForemanConfigWriter.Quote(config.ColorName)}");
        }

        if (!string.IsNullOrWhiteSpace(config.DefaultBranch))
        {
            block.AppendLine($"    defaultBranch: {ForemanConfigWriter.Quote(config.DefaultBranch)}");
        }

        if (!string.IsNullOrWhiteSpace(config.BuildCommand))
        {
            block.AppendLine($"    buildCommand: {ForemanConfigWriter.Quote(config.BuildCommand.ReplaceLineEndings(" "))}");
        }

        if (!string.IsNullOrWhiteSpace(config.TestCommand))
        {
            block.AppendLine($"    testCommand: {ForemanConfigWriter.Quote(config.TestCommand.ReplaceLineEndings(" "))}");
        }

        if (config.Upstream is { Count: > 0 })
        {
            // Same shape and same CollapseRepoRoot-then-Quote ordering as
            // providerOptions in ForemanConfigWriter -- an upstream value can
            // legitimately be a repo-relative path, not just a URL.
            block.AppendLine("    upstream:");
            foreach (var (key, value) in config.Upstream)
            {
                block.AppendLine($"      {key}: {ForemanConfigWriter.Quote(CollapseRepoRoot(value, repoRoot))}");
            }
        }

        if (config.VaultFolders is { Count: > 0 })
        {
            block.AppendLine("    vaultFolders:");
            foreach (var folder in config.VaultFolders)
            {
                // Vault-relative by definition -- there is no root to collapse.
                block.AppendLine($"      - {ForemanConfigWriter.Quote(folder)}");
            }
        }

        File.AppendAllText(path, block.ToString());
    }

    /// <summary>
    /// Removes a Jobsite's entry from jobsites.yaml. NEVER deletes, touches, or
    /// even reads the jobsite's repo directory (RepoPath) -- this only edits
    /// ConstructionCrew's own config file. The /fire flow must never be given
    /// any other way to affect a jobsite's actual repo.
    /// </summary>
    public static bool RemoveJobsite(string path, string name) => YamlListEditor.RemoveEntry(path, "jobsites", name);

    private static string CollapseRepoRoot(string absolutePath, string repoRoot) =>
        absolutePath.StartsWith(repoRoot, PathComparison.ForPathPrefix)
            ? "${repoRoot}" + absolutePath[repoRoot.Length..].Replace('\\', '/')
            : absolutePath;
}
