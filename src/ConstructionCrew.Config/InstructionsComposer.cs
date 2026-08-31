using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Renders a crew member's instructions file from a Vault-hosted template --
/// <c>AI/ConstructionCrew/Templates/gc-instructions.md</c> for a GC,
/// <c>AI/ConstructionCrew/Templates/foreman-instructions.md</c> for a Foreman.
/// Replaces HireWizard's old inline ComposeInstructions.
///
/// Templates live in the Vault, not this repo, so the Boss can read and edit
/// them where the rest of the second brain lives. They still ship a master copy
/// under <c>config/scaffold/AI/ConstructionCrew/Templates/</c>, seeded into a
/// vault that doesn't have its own copy yet (see VaultLayout.EnsureScaffoldFile)
/// and never overwritten after that -- an edited template is the Boss's, not
/// this tool's to clobber.
///
/// The adversarial-review workflow lives in the TEMPLATE, as literal prose, not
/// in a skill and not in C#. That is what makes it provider-agnostic: an
/// instructions file is plain text every CLI reads as its own system prompt
/// (LocalCliAgent prepends it on turn one), so Claude Code, Codex and Copilot
/// all get the same workflow without any of them needing to resolve a skill.
/// Nothing here references plan-Work, or any other vault skill, by name.
/// </summary>
public static class InstructionsComposer
{
    private const string NotConfigured = "(not configured)";

    public static string TemplatesDirectory(string vaultRoot) => Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Templates");

    public static string TemplatePath(string vaultRoot, CrewRole role) =>
        Path.Combine(TemplatesDirectory(vaultRoot), role == CrewRole.GC ? "gc-instructions.md" : "foreman-instructions.md");

    /// <summary>
    /// The authoredBy string this role stamps on every Vault note it writes.
    /// GC always yields "GC" regardless of DisplayName; a Foreman yields
    /// "Foreman:&lt;JobsiteName&gt;" (Architecture §3.1).
    /// </summary>
    public static string AuthoredBy(CrewRole role, string? jobsiteName) =>
        role == CrewRole.GC ? "GC" : $"Foreman:{jobsiteName ?? "unassigned"}";

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
    /// The briefing back out of an already-rendered Foreman instructions file, for a
    /// crew member hired before the sidecar existed. The template puts the briefing
    /// first, then a line that is exactly "---", a blank line, and a "# You are"
    /// heading (AI/ConstructionCrew/Templates/foreman-instructions.md:1-5). Returns ""
    /// when that shape is not found -- a GC file, or a hand-rewritten one.
    /// </summary>
    public static string ExtractBriefing(string renderedInstructions)
    {
        if (string.IsNullOrWhiteSpace(renderedInstructions))
        {
            return string.Empty;
        }

        // Split on any line ending; the file may have been written or hand-edited
        // on either platform.
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

            // Everything above the separator is the briefing, exactly as Compose
            // trimmed it on the way in.
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
            // Plain "main", never a parenthetical: this token is substituted into
            // a `gh pr create --base {{DefaultBranch}}` example in the template,
            // and any prose here renders as a literally broken shell command.
            ["DefaultBranch"] = Fallback(jobsite?.DefaultBranch, "main"),
            ["BuildCommand"] = Fallback(jobsite?.BuildCommand, "(no build command configured -- ask the Boss before guessing one)"),
            ["TestCommand"] = Fallback(jobsite?.TestCommand, "(no test command configured -- ask the Boss before guessing one)"),
            ["Upstream"] = RenderMap(jobsite?.Upstream),
            ["VaultRoot"] = Fallback(vaultRoot, NotConfigured),
            ["VaultFolders"] = RenderList(vaultFolders),
            ["AuthoredBy"] = AuthoredBy(role, jobsite?.Name),
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

    private static string RenderMap(IReadOnlyDictionary<string, string>? values) =>
        values is { Count: > 0 }
            ? string.Join(Environment.NewLine, values.Select(kv => $"- {kv.Key}: {kv.Value}"))
            : "- (none configured)";
}
