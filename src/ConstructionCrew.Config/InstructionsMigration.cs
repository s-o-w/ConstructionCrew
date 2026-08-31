using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// One-time migration for a roster hired before instructions files moved into
/// the Vault (see InstructionsComposer, VaultLayout). Runs at startup and via
/// /migrate, both through <see cref="MigrateToVault"/>, so one code path
/// decides what "already migrated" means.
///
/// Idempotent: a roster already under AI/ConstructionCrew/Instructions/ is
/// left untouched. A missing legacy file is skipped, not an error.
/// </summary>
public static class InstructionsMigration
{
    public sealed record Result(
        IReadOnlyList<ForemanConfig> Foremen,
        IReadOnlyList<string> MigratedForemen,
        IReadOnlyList<string> TemplatesEnsured);

    /// <summary>The two templates every vault needs. Same "seed if absent, never overwrite" rule as other scaffolded files.</summary>
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

        // Crew preferences moved from AI/Context/ to AI/ConstructionCrew/ --
        // a vault seeded before that move has the file at the old path.
        TryMigrateLegacyFile(
            Path.Combine(vaultRoot, "AI", "Context", "crew-preferences.md"),
            Path.Combine(vaultRoot, VaultLayout.CrewPreferencesRelativePath.Replace('/', Path.DirectorySeparatorChar)));

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

            // Best-effort: FirstRunWizard.EnsureGcInstructions runs earlier in the
            // same startup and may have already moved this file.
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

    /// <summary>Moves one file to its Vault location if the source exists and the destination doesn't. A missing source (already migrated, or optional) is not an error.</summary>
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

    /// <summary>Reuses ForemanConfigWriter's vault-prefix collapse rather than duplicating the boundary check.</summary>
    private static bool IsAlreadyUnderVault(string absolutePath, string vaultRoot) =>
        ForemanConfigWriter.CollapseVaultRoot(absolutePath, vaultRoot).StartsWith("${vaultRoot}", StringComparison.Ordinal);
}
