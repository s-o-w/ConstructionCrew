using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// One-time migration for a roster hired before instructions templates and
/// rendered instructions files moved into the Vault (see InstructionsComposer,
/// VaultLayout). Runs as a startup self-heal (Program.cs, right after
/// foremen.yaml loads) and on demand via the /migrate command -- both call
/// <see cref="MigrateToVault"/> the same way, so there's exactly one code path
/// that decides what "already migrated" means.
///
/// Idempotent: a roster already pointing at AI/ConstructionCrew/Instructions/
/// is left untouched, file and YAML both. Never throws on a partially-migrated
/// or already-migrated roster -- a missing legacy file is skipped, not an error.
/// </summary>
public static class InstructionsMigration
{
    public sealed record Result(
        IReadOnlyList<ForemanConfig> Foremen,
        IReadOnlyList<string> MigratedForemen,
        IReadOnlyList<string> TemplatesEnsured);

    /// <summary>
    /// The two templates that ship in this repo's scaffold and must exist in
    /// every vault ConstructionCrew renders instructions from -- same "seed if
    /// absent, never overwrite" rule as every other scaffolded vault file.
    /// </summary>
    private static readonly string[] TemplateRelativePaths =
    [
        "AI/ConstructionCrew/Templates/gc-instructions.md",
        "AI/ConstructionCrew/Templates/foreman-instructions.md",
    ];

    public static Result MigrateToVault(
        string repoRoot,
        string vaultRoot,
        string foremenConfigPath,
        IReadOnlyList<ForemanConfig> foremen)
    {
        var templatesEnsured = new List<string>();
        var scaffoldSource = VaultLayout.ScaffoldSourceDirectory(repoRoot);
        foreach (var relative in TemplateRelativePaths)
        {
            if (VaultLayout.EnsureScaffoldFile(scaffoldSource, vaultRoot, relative))
            {
                templatesEnsured.Add(relative);
            }
        }

        var instructionsDir = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions");
        var migrated = new List<string>();
        var updatedForemen = new List<ForemanConfig>(foremen.Count);

        foreach (var foreman in foremen)
        {
            var oldPath = foreman.InstructionsFilePath;

            if (IsAlreadyUnderVault(oldPath, vaultRoot))
            {
                updatedForemen.Add(foreman);
                continue;
            }

            Directory.CreateDirectory(instructionsDir);
            var newPath = Path.Combine(instructionsDir, Path.GetFileName(oldPath));

            // Best-effort: the file may already be gone if something else (the
            // GC-specific fallback in FirstRunWizard.EnsureGcInstructions, which
            // runs earlier in the same startup, ahead of the roster load this
            // migration depends on) already moved it this same run.
            TryMigrateLegacyFile(oldPath, newPath);

            var oldBriefing = Path.Combine(Path.GetDirectoryName(oldPath)!, $"{foreman.Name}.briefing.md");
            TryMigrateLegacyFile(oldBriefing, InstructionsComposer.BriefingFilePath(vaultRoot, foreman.Name));

            var updated = foreman with { InstructionsFilePath = newPath };
            ForemanConfigWriter.RemoveForeman(foremenConfigPath, foreman.Name);
            ForemanConfigWriter.AppendForeman(foremenConfigPath, updated, repoRoot, vaultRoot);

            migrated.Add(foreman.Name);
            updatedForemen.Add(updated);
        }

        return new Result(updatedForemen, migrated, templatesEnsured);
    }

    /// <summary>
    /// Moves one file from its old repo-side location to its new Vault-side one,
    /// if the old one is actually there and the new one isn't already. Never
    /// throws: a missing source (already migrated, or never existed -- a
    /// briefing sidecar is optional) is simply not migrated, not an error.
    /// </summary>
    public static bool TryMigrateLegacyFile(string legacyPath, string newPath)
    {
        if (!File.Exists(legacyPath) || File.Exists(newPath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(legacyPath, newPath);
        return true;
    }

    /// <summary>
    /// Reuses ForemanConfigWriter's own vault-prefix test (via the collapse it
    /// already performs when writing foremen.yaml) rather than duplicating the
    /// boundary check here.
    /// </summary>
    private static bool IsAlreadyUnderVault(string absolutePath, string vaultRoot) =>
        ForemanConfigWriter.CollapseVaultRoot(absolutePath, vaultRoot).StartsWith("${vaultRoot}", StringComparison.Ordinal);
}
