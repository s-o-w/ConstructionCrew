using ConstructionCrew.Config;

namespace ConstructionCrew.Tests.TestSupport;

/// <summary>
/// A real, disposable scratch directory seeded with this repo's shipped
/// AI/ConstructionCrew/Templates/ masters -- the same seed
/// VaultLayout.EnsureScaffoldFile would perform on a real vault. Used wherever a
/// test needs InstructionsComposer.Compose to actually find a template, without
/// depending on any particular machine having a real vault at all.
/// </summary>
internal static class SeededVault
{
    public static string WithInstructionsTemplates()
    {
        var repoRoot = RepoPaths.FindRepoRoot(AppContext.BaseDirectory);
        var scaffoldTemplates = Path.Combine(repoRoot, "config", "scaffold", "AI", "ConstructionCrew", "Templates");

        var vaultRoot = Path.Combine(Path.GetTempPath(), "cc-seeded-vault-" + Guid.NewGuid().ToString("n")[..8]);
        var vaultTemplates = InstructionsComposer.TemplatesDirectory(vaultRoot);
        Directory.CreateDirectory(vaultTemplates);

        foreach (var source in Directory.EnumerateFiles(scaffoldTemplates, "*.md"))
        {
            File.Copy(source, Path.Combine(vaultTemplates, Path.GetFileName(source)));
        }

        // Process-lifetime temp dir, not per-test-cleaned-up. Removed on process
        // exit so a long test session doesn't litter the temp directory.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Directory.Exists(vaultRoot))
            {
                Directory.Delete(vaultRoot, recursive: true);
            }
        };

        return vaultRoot;
    }
}
