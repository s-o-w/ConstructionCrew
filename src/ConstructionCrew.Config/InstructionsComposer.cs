using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Renders a crew member's instructions file from a checked-in template --
/// <c>config/templates/gc-instructions.md</c> for a GC,
/// <c>config/templates/foreman-instructions.md</c> for a Foreman. Replaces
/// HireWizard's old inline ComposeInstructions.
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

    public static string TemplatesDirectory(string repoRoot) => Path.Combine(repoRoot, "config", "templates");

    public static string TemplatePath(string repoRoot, CrewRole role) =>
        Path.Combine(TemplatesDirectory(repoRoot), role == CrewRole.GC ? "gc-instructions.md" : "foreman-instructions.md");

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

    public static string Compose(
        string name,
        CrewRole role,
        string briefing,
        JobsiteConfig? jobsite,
        IReadOnlyList<string>? vaultFolders,
        IReadOnlyList<string>? availableEngines,
        string repoRoot,
        string? vaultRoot)
    {
        var templatePath = TemplatePath(repoRoot, role);

        if (!File.Exists(templatePath))
        {
            throw new InvalidOperationException(
                $"No instructions template at '{templatePath}'. It ships with this repo -- restore it before hiring.");
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
            ["DefaultBranch"] = Fallback(jobsite?.DefaultBranch, "main (no defaultBranch configured)"),
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
