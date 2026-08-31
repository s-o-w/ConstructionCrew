namespace ConstructionCrew.Config;

/// <summary>Whether a directory looks like a vault ConstructionCrew's conventions apply to.</summary>
public enum VaultRecognition
{
    /// <summary>Every marker <see cref="VaultLayout"/> looks for is present.</summary>
    Recognized,

    /// <summary>At least one marker is missing. Still usable as a Vault, just not conventionally shaped.</summary>
    Unrecognized,
}

/// <summary>
/// Recognizes the vault layout ConstructionCrew's conventions are built on.
///
/// Mirrors the `plan-Work` skill's Step 0 VAULT_ROOT discovery, so a vault
/// Recognized here is one that skill can also drive:
///
///   VAULT_ROOT="$(pwd)"
///   while [ "$VAULT_ROOT" != "/" ] &amp;&amp; { [ ! -f "$VAULT_ROOT/HOME.md" ] || [ ! -f "$VAULT_ROOT/CLAUDE.md" ]; }; do
///     VAULT_ROOT="$(dirname "$VAULT_ROOT")"
///   done
///
/// Step 0 only checks HOME.md and CLAUDE.md as files; it derives Plans/ without
/// checking it exists and never names Notes/ at all. Recognize() checks all
/// five (a strict superset) because Notes/ and Plans/ are exactly what
/// VaultFolders derivation ("Notes/&lt;Jobsite&gt;", "Plans/&lt;Jobsite&gt;") writes
/// into. Recognized here always satisfies plan-Work's test; the reverse isn't
/// guaranteed, and that asymmetry is intentional.
///
/// `AI/` is required for the same reason: instructions templates and rendered
/// files live under AI/ConstructionCrew/ (InstructionsComposer), so missing
/// AI/ blocks this tool's writes exactly like missing Notes/ or Plans/.
/// </summary>
public static class VaultLayout
{
    /// <summary>Files that must exist directly under the vault root.</summary>
    public static readonly IReadOnlyList<string> RequiredFiles = ["HOME.md", "CLAUDE.md"];

    /// <summary>Directories that must exist directly under the vault root.</summary>
    public static readonly IReadOnlyList<string> RequiredFolders = ["Notes", "Plans", "AI"];

    public static VaultRecognition Recognize(string? vaultRoot) =>
        MissingMarkers(vaultRoot).Count == 0 ? VaultRecognition.Recognized : VaultRecognition.Unrecognized;

    /// <summary>The markers that are absent, in check order, so the Boss learns why a directory wasn't recognized. A null/blank/missing root reports every marker missing.</summary>
    public static IReadOnlyList<string> MissingMarkers(string? vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot) || !Directory.Exists(vaultRoot))
        {
            return [.. RequiredFiles, .. RequiredFolders.Select(f => f + "/")];
        }

        var missing = new List<string>();

        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(vaultRoot, file)))
            {
                missing.Add(file);
            }
        }

        foreach (var folder in RequiredFolders)
        {
            if (!Directory.Exists(Path.Combine(vaultRoot, folder)))
            {
                missing.Add(folder + "/");
            }
        }

        return missing;
    }

    /// <summary>
    /// Copies the templates under <paramref name="scaffoldSourceDirectory"/> verbatim
    /// into <paramref name="vaultRoot"/>. No templating, no substitution.
    ///
    /// Two exceptions: `.gitkeep` markers are not copied (they only exist so git
    /// tracks empty scaffold directories; the directory itself is still created),
    /// and an existing file is never overwritten.
    /// </summary>
    /// <returns>The vault-relative paths actually written.</returns>
    public static IReadOnlyList<string> Scaffold(string scaffoldSourceDirectory, string vaultRoot)
    {
        if (!Directory.Exists(scaffoldSourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Scaffold templates not found at '{scaffoldSourceDirectory}'. " +
                "They ship in this repo under config/scaffold/.");
        }

        Directory.CreateDirectory(vaultRoot);

        var written = new List<string>();

        foreach (var sourceDirectory in Directory.EnumerateDirectories(scaffoldSourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(scaffoldSourceDirectory, sourceDirectory);
            Directory.CreateDirectory(Path.Combine(vaultRoot, relative));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(scaffoldSourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(sourceFile).Equals(".gitkeep", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(scaffoldSourceDirectory, sourceFile);
            var destination = Path.Combine(vaultRoot, relative);

            if (File.Exists(destination))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination);
            written.Add(relative.Replace('\\', '/'));
        }

        return written;
    }

    /// <summary>Where the scaffold templates live inside a ConstructionCrew clone.</summary>
    public static string ScaffoldSourceDirectory(string repoRoot) => Path.Combine(repoRoot, "config", "scaffold");

    /// <summary>
    /// The Boss's crew preferences path, vault-relative. Must exist on every
    /// vault, not just a scaffolded one, since both instructions templates point
    /// at it unconditionally. Kept in sync with
    /// <see cref="InstructionsComposer.CrewPreferencesPath"/>.
    /// </summary>
    public const string CrewPreferencesRelativePath = "AI/Context/crew-preferences.md";

    /// <summary>Copies one scaffold file into a vault when absent, matching Scaffold's never-overwrite rule. Returns true when it wrote one.</summary>
    public static bool EnsureScaffoldFile(string scaffoldSourceDirectory, string vaultRoot, string relativePath)
    {
        // relativePath is one literal (e.g. "AI/Context/x.md"), the same string
        // callers share with the templates; '/' is converted here for both platforms.
        var source = Path.Combine(scaffoldSourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"Scaffold file not found at '{source}'. It ships in this repo under config/scaffold/.", source);
        }

        var destination = Path.Combine(vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(destination))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        return true;
    }
}
