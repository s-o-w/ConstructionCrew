using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Renders a crew member's instructions file from a Vault-hosted template:
/// <c>AI/ConstructionCrew/Templates/gc-instructions.md</c> for a GC,
/// <c>foreman-instructions.md</c> for a Foreman.
///
/// Templates live in the Vault, not this repo, so the Boss can edit them
/// directly. A master copy ships under
/// <c>config/scaffold/AI/ConstructionCrew/Templates/</c> and seeds a vault
/// missing its own copy (see VaultLayout.EnsureScaffoldFile), but never
/// overwrites an existing, edited template.
///
/// The adversarial-review workflow lives as prose in the template, not in a
/// skill or C#, because an instructions file is plain text every CLI reads as
/// its own system prompt. That is what keeps the workflow provider-agnostic
/// across Claude Code, Codex, and Copilot.
/// </summary>
public static class InstructionsComposer
{
    private const string NotConfigured = "(not configured)";

    public static string TemplatesDirectory(string vaultRoot) => Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Templates");

    public static string TemplatePath(string vaultRoot, CrewRole role) =>
        Path.Combine(TemplatesDirectory(vaultRoot), role == CrewRole.GC ? "gc-instructions.md" : "foreman-instructions.md");

    /// <summary>
    /// The authoredBy string this role stamps on every Vault note it writes.
    /// GC always yields "GC"; a Foreman yields
    /// "Foreman:&lt;ForemanName&gt;:&lt;JobsiteName&gt;". The Foreman name matters:
    /// two Foremen can share a Jobsite, so JobsiteName alone would make their
    /// notes indistinguishable in attribution.
    /// </summary>
    public static string AuthoredBy(CrewRole role, string foremanName, string? jobsiteName) =>
        role == CrewRole.GC ? "GC" : $"Foreman:{foremanName}:{jobsiteName ?? "unassigned"}";

    /// <summary>
    /// The vault-relative path to the Boss's crew preferences. Read by agents as
    /// a tiebreaker; never parsed by C#.
    /// </summary>
    public static string CrewPreferencesPath(string? vaultRoot) =>
        string.IsNullOrWhiteSpace(vaultRoot)
            ? "AI/Context/crew-preferences.md (relative to the Vault root, once one is configured)"
            : Path.Combine(vaultRoot, "AI", "Context", "crew-preferences.md");

    /// <summary>Where a crew member's raw briefing is kept, verbatim, so its
    /// instructions can be re-rendered later without asking the Boss again.</summary>
    public static string BriefingFilePath(string vaultRoot, string name) =>
        Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions", $"{name}.briefing.md");

    /// <summary>
    /// Extracts the briefing back out of an already-rendered Foreman instructions
    /// file, for a crew member hired before the sidecar existed. The template puts
    /// the briefing first, then "---", a blank line, and a "# You are" heading.
    /// Returns "" when that shape isn't found (a GC file, or a hand-edited one).
    /// </summary>
    public static string ExtractBriefing(string renderedInstructions)
    {
        if (string.IsNullOrWhiteSpace(renderedInstructions))
        {
            return string.Empty;
        }

        // Handles either line ending; the file may be edited on either platform.
        var lines = renderedInstructions.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i + 2 < lines.Length; i++)
        {
            var isSeparator =
                lines[i] == "---" &&
                string.IsNullOrWhiteSpace(lines[i + 1]) &&
                lines[i + 2].StartsWith("# You are", StringComparison.Ordinal);

            if (!isSeparator)
            {
                continue;
            }

            // Above the separator is the briefing, trimmed the same way Compose wrote it.
            return string.Join(Environment.NewLine, lines.Take(i)).Trim();
        }

        return string.Empty;
    }

    public static string Compose(
        string name,
        CrewRole role,
        string briefing,
        JobsiteConfig? jobsite,
        IReadOnlyList<string>? vaultFolders,
        IReadOnlyList<string>? availableEngines,
        string? vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            throw new InvalidOperationException(
                "No Vault configured; cannot locate the instructions templates under AI/ConstructionCrew/Templates/.");
        }

        var templatePath = TemplatePath(vaultRoot, role);

        if (!File.Exists(templatePath))
        {
            throw new InvalidOperationException(
                $"No instructions template at '{templatePath}'. It should have been seeded from this repo's " +
                "config/scaffold/AI/ConstructionCrew/Templates/ -- restore it there before hiring.");
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = name,
            ["Briefing"] = string.IsNullOrWhiteSpace(briefing) ? $"You are {name}." : briefing.Trim(),
            ["JobsiteName"] = jobsite?.Name ?? NotConfigured,
            ["JobsitePath"] = jobsite?.RepoPath ?? NotConfigured,
            ["JobsiteDescription"] = string.IsNullOrWhiteSpace(jobsite?.Description)
                ? "(no description was given for this jobsite)"
                : jobsite!.Description.Trim(),
            // Plain "main", not a parenthetical: this substitutes into a
            // `gh pr create --base {{DefaultBranch}}` example, so prose here
            // would render as a broken shell command.
            ["DefaultBranch"] = Fallback(jobsite?.DefaultBranch, "main"),
            ["BuildCommand"] = Fallback(jobsite?.BuildCommand, "(no build command configured -- ask the Boss before guessing one)"),
            ["TestCommand"] = Fallback(jobsite?.TestCommand, "(no test command configured -- ask the Boss before guessing one)"),
            ["Backlog"] = Fallback(jobsite?.BacklogUrl, "(none configured)"),
            ["VaultRoot"] = Fallback(vaultRoot, NotConfigured),
            ["VaultFolders"] = RenderList(vaultFolders),
            ["AuthoredBy"] = AuthoredBy(role, name, jobsite?.Name),
            ["CrewPreferencesPath"] = CrewPreferencesPath(vaultRoot),
            ["AvailableEngines"] = availableEngines is { Count: > 0 }
                ? string.Join(", ", availableEngines)
                : "(unknown -- call list_foremen and use the providers you see there)",
        };

        var rendered = File.ReadAllText(templatePath);
        foreach (var (key, value) in tokens)
        {
            rendered = rendered.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string Fallback(string? value, string whenMissing) =>
        string.IsNullOrWhiteSpace(value) ? whenMissing : value.Trim();

    private static string RenderList(IReadOnlyList<string>? values) =>
        values is { Count: > 0 }
            ? string.Join(Environment.NewLine, values.Select(v => $"- {v}"))
            : "- (none configured -- ask the Boss before writing anywhere in the Vault)";
}
