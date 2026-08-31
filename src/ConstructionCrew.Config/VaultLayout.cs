namespace ConstructionCrew.Config;

/// <summary>Whether a directory looks like a vault ConstructionCrew's conventions apply to.</summary>
public enum VaultRecognition
{
    /// <summary>Every marker <see cref="VaultLayout"/> looks for is present.</summary>
    Recognized,

    /// <summary>At least one marker is missing. Still usable as a Vault -- just not conventionally shaped.</summary>
    Unrecognized,
}

/// <summary>
/// Recognizes the vault layout ConstructionCrew's conventions are built on.
///
/// The predicate is deliberately the same one the `plan-Work` skill's Step 0 uses
/// to resolve VAULT_ROOT, so a vault this returns Recognized for is a vault that
/// skill can also drive. Step 0's discovery block is:
///
///   VAULT_ROOT="$(pwd)"
///   while [ "$VAULT_ROOT" != "/" ] &amp;&amp; { [ ! -f "$VAULT_ROOT/HOME.md" ] || [ ! -f "$VAULT_ROOT/CLAUDE.md" ]; }; do
///     VAULT_ROOT="$(dirname "$VAULT_ROOT")"
///   done
///   PLANS_ROOT="$VAULT_ROOT/Plans"
///
/// Note what that literally tests: `HOME.md` and `CLAUDE.md` as *files*. `Plans/`
/// it derives without an existence check, and `Notes/` it never names at all.
/// Recognize() checks all four -- a strict superset -- because Notes/ and Plans/
/// are what the VaultFolders derivation ("Notes/&lt;Jobsite&gt;", "Plans/&lt;Jobsite&gt;")
/// actually writes into, and deriving a write path into a directory that isn't
/// there is the failure this check exists to prevent. Anything Recognized here
/// therefore also satisfies plan-Work's own two-file test; the reverse is not
/// guaranteed, and that asymmetry is intentional.
/// </summary>
public static class VaultLayout
{
    /// <summary>Files that must exist directly under the vault root.</summary>
    public static readonly IReadOnlyList<string> RequiredFiles = ["HOME.md", "CLAUDE.md"];

    /// <summary>Directories that must exist directly under the vault root.</summary>
    public static readonly IReadOnlyList<string> RequiredFolders = ["Notes", "Plans"];

    public static VaultRecognition Recognize(string? vaultRoot) =>
        MissingMarkers(vaultRoot).Count == 0 ? VaultRecognition.Recognized : VaultRecognition.Unrecognized;

    /// <summary>
    /// The markers that are absent, in the order they are checked -- for telling the
    /// Boss exactly why a directory was not recognized, rather than just that it wasn't.
    /// A null/blank/missing root reports every marker as missing.
    /// </summary>
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
    /// Copies the templates under <paramref name="scaffoldSourceDirectory"/>
    /// (config/scaffold/ in this repo) verbatim into <paramref name="vaultRoot"/>.
    /// No templating engine, no substitution -- a file is copied byte for byte or
    /// not at all.
    ///
    /// Two deliberate exceptions to "verbatim":
    /// - `.gitkeep` markers are not copied. They exist only so git tracks the
    ///   scaffold's intentionally-empty directories; the directory itself is still
    ///   created here, which is the actual intent.
    /// - An existing file is never overwritten. Scaffolding into a directory that
    ///   already holds content adds what is missing and leaves the rest alone.
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
    /// The Boss's standing crew preferences, vault-relative. Both instructions
    /// templates point the crew at this path unconditionally, so it has to exist on
    /// every vault -- not just a scaffolded one. Kept in sync with
    /// <see cref="InstructionsComposer.CrewPreferencesPath"/>.
    /// </summary>
    public const string CrewPreferencesRelativePath = "AI/Context/crew-preferences.md";

    /// <summary>
    /// Copies exactly one scaffold file into a vault when it is absent, for a file
    /// both instructions templates reference unconditionally. Existing files are
    /// never touched, matching Scaffold's own rule. Returns true when it wrote one.
    /// </summary>
    public static bool EnsureScaffoldFile(string scaffoldSourceDirectory, string vaultRoot, string relativePath)
    {
        // Path.Combine on a relative path holding '/' works on both platforms; the
        // separators are normalized by the OS. Kept as one segment string so callers
        // pass the same literal the templates use.
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
