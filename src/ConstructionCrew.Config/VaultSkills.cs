namespace ConstructionCrew.Config;

/// <summary>
/// Result of probing whether a vault skill is discoverable from the user's
/// global skills directory.
/// </summary>
/// <param name="Reachable">True when the global link exists and resolves to the vault's own copy.</param>
/// <param name="LinkPath">Where the global skills directory expects the skill to live.</param>
/// <param name="ExpectedTarget">The vault path the link should point at.</param>
/// <param name="ActualTarget">What the link resolves to today, or null when there is no link at all.</param>
/// <param name="SuggestedCommand">The exact `ln -s` (or `mklink`) command that would fix it.</param>
public sealed record VaultSkillProbe(
    bool Reachable,
    string LinkPath,
    string ExpectedTarget,
    string? ActualTarget,
    string SuggestedCommand);

/// <summary>
/// Reachability check for the vault skills ConstructionCrew's roles depend on.
///
/// Only consult-tha-graph today: it lives in the vault's AI/shared-skills/ tree,
/// while ~/.claude/skills/ carries links into AI/claude-home/skills/ -- so it is
/// routinely NOT discoverable without an explicit link. Never repaired silently:
/// a symlink into the Boss's home directory is his to create, not ours.
/// </summary>
public static class VaultSkills
{
    public const string ConsultThaGraph = "consult-tha-graph";

    public static VaultSkillProbe Probe(string vaultRoot) => Probe(vaultRoot, ConsultThaGraph);

    public static VaultSkillProbe Probe(string vaultRoot, string skillName)
    {
        var expectedTarget = Path.Combine(vaultRoot, "AI", "shared-skills", skillName);
        var linkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "skills",
            skillName);

        var suggestedCommand = OperatingSystem.IsWindows()
            ? $"mklink /D \"{linkPath}\" \"{expectedTarget}\""
            : $"ln -s \"{expectedTarget}\" \"{linkPath}\"";

        var actualTarget = ResolveTarget(linkPath);
        var reachable = actualTarget is not null
                        && Directory.Exists(actualTarget)
                        && File.Exists(Path.Combine(actualTarget, "SKILL.md"));

        return new VaultSkillProbe(reachable, linkPath, expectedTarget, actualTarget, suggestedCommand);
    }

    /// <summary>
    /// Prints the miss and the exact command to fix it, then asks whether to
    /// continue anyway. Returns true to continue. Never creates the link itself.
    /// </summary>
    public static bool ConfirmContinueOnMiss(VaultSkillProbe probe, TextWriter output, TextReader input)
    {
        if (probe.Reachable)
        {
            return true;
        }

        output.WriteLine($"Skill '{Path.GetFileName(probe.ExpectedTarget)}' is not reachable from {probe.LinkPath}.");
        output.WriteLine(probe.ActualTarget is null
            ? "  No link is present there."
            : $"  The link there resolves to: {probe.ActualTarget}");
        output.WriteLine($"  Expected it to resolve to: {probe.ExpectedTarget}");
        output.WriteLine("  Fix it with:");
        output.WriteLine($"    {probe.SuggestedCommand}");
        output.Write("Continue anyway? [y/N] ");

        var answer = input.ReadLine();
        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The final target of a link, or the path itself when it is a real
    /// directory, or null when nothing is there.
    /// </summary>
    private static string? ResolveTarget(string linkPath)
    {
        if (!Directory.Exists(linkPath) && !File.Exists(linkPath))
        {
            return null;
        }

        var info = new DirectoryInfo(linkPath);
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return resolved?.FullName ?? info.FullName;
    }
}
