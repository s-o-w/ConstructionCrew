using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Tests.TestSupport;

namespace ConstructionCrew.Tests.ConfigTests;

/// <summary>
/// Runs against the templates that actually ship in this repo, not a synthetic
/// fixture -- an unreplaced token or a lost section in the real file is exactly
/// the failure worth catching. They now live under a Vault's
/// AI/ConstructionCrew/Templates/, so <see cref="RealVaultRoot"/> is a real temp
/// directory seeded once from this repo's config/scaffold/ copy -- not the vault
/// this tool was built against, and not a hand-written fixture.
/// </summary>
public class InstructionsComposerTests
{
    private static readonly string RealVaultRoot = SeededVault.WithInstructionsTemplates();

    private static JobsiteConfig Jobsite() =>
        new(
            "Lighthouse",
            "/home/boss/code/lighthouse",
            "The semantic data platform.",
            RepoUrl: null,
            ColorName: "purple",
            DefaultBranch: "develop",
            BuildCommand: "dotnet build",
            TestCommand: "dotnet test",
            BacklogUrl: "https://github.com/orgs/example-org/projects/73",
            VaultFolders: ["Notes/Lighthouse", "Plans/Lighthouse"]);

    [Fact]
    public void Compose_Foreman_RendersEveryTokenAndTheReviewWorkflow()
    {
        var rendered = InstructionsComposer.Compose(
            "Frontend",
            CrewRole.Foreman,
            "You are the Frontend Foreman.",
            Jobsite(),
            ["Notes/Lighthouse", "Plans/Lighthouse"],
            ["claude", "codex"],
            RealVaultRoot);

        Assert.DoesNotContain("{{", rendered);
        Assert.Contains("You are the Frontend Foreman.", rendered);
        Assert.Contains("Lighthouse", rendered);
        Assert.Contains("dotnet build", rendered);
        Assert.Contains("dotnet test", rendered);
        Assert.Contains("develop", rendered);
        Assert.Contains("https://github.com/orgs/example-org/projects/73", rendered);
        Assert.Contains("Notes/Lighthouse", rendered);
        Assert.Contains("Foreman:Frontend:Lighthouse", rendered);
        Assert.Contains(Path.Combine(RealVaultRoot, "AI", "ConstructionCrew", "crew-preferences.md"), rendered);
        Assert.Contains("claude, codex", rendered);
        // The adversarial-review workflow is template text, and it must be
        // provider-agnostic -- no vault skill is ever named.
        Assert.Contains("adversarial", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spawn_worker", rendered);
        Assert.DoesNotContain("plan-Work", rendered);
    }

    /// <summary>
    /// Phase 4: the sitewalk brief is TEMPLATE text, rendered once here, never
    /// hand-written into HireWizard. It must carry all four load-bearing parts --
    /// read-only, a note in Notes/&lt;Jobsite&gt;/, the kind="milestone" sitrep that
    /// is what actually notifies the GC, and build_graph as a non-blocking closing
    /// step.
    /// </summary>
    [Fact]
    public void Compose_Foreman_RendersTheSitewalkBrief()
    {
        var rendered = InstructionsComposer.Compose(
            "Frontend",
            CrewRole.Foreman,
            "You are the Frontend Foreman.",
            Jobsite(),
            ["Notes/Lighthouse", "Plans/Lighthouse"],
            ["claude", "codex"],
            RealVaultRoot);

        Assert.Contains("sitewalk", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("READ-ONLY", rendered);
        Assert.Contains("Notes/Lighthouse/Sitewalk.md", rendered);
        // The completion path, spelled out: milestone, not status.
        Assert.Contains("kind=\"milestone\"", rendered);
        Assert.Contains("a file appearing in the", rendered);
        // The closing step, and its clean degrade.
        Assert.Contains("build_graph()", rendered);
        Assert.Contains("sitewalk recorded; graph export failed", rendered);
    }

    /// <summary>
    /// Phase 8: a Foreman dispatched against an empty directory has to stand the
    /// repo up before it cuts a branch. That is template prose, and every token
    /// in it ({{JobsitePath}}, {{DefaultBranch}}, {{Name}}) must actually
    /// substitute -- an unreplaced token there renders as a broken git command.
    /// </summary>
    [Fact]
    public void Compose_ForemanTemplate_CarriesTheRepoBootstrapStep()
    {
        var rendered = InstructionsComposer.Compose(
            "Frontend",
            CrewRole.Foreman,
            "You are the Frontend Foreman.",
            Jobsite(),
            ["Notes/Lighthouse", "Plans/Lighthouse"],
            ["claude", "codex"],
            RealVaultRoot);

        // HEAD, not just --git-dir, is the real gate: hiring against a new
        // jobsite already runs a bare `git init`, so a .git directory existing
        // proves nothing about whether this project has an actual commit yet.
        Assert.Contains("rev-parse HEAD", rendered);
        Assert.Contains("rev-parse --git-dir", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    /// <summary>
    /// Multiple Foremen per Jobsite means Sitewalk.md is no longer one
    /// Foreman's alone. The template has to tell every Foreman to check for an
    /// existing sitewalk before writing and append rather than overwrite it.
    /// </summary>
    [Fact]
    public void Compose_ForemanTemplate_CarriesTheSitewalkAppendNotOverwriteRule()
    {
        var rendered = InstructionsComposer.Compose(
            "Frontend",
            CrewRole.Foreman,
            "You are the Frontend Foreman.",
            Jobsite(),
            ["Notes/Lighthouse", "Plans/Lighthouse"],
            ["claude", "codex"],
            RealVaultRoot);

        Assert.Contains("already exists", rendered);
        Assert.Contains("never overwrite it", rendered);
        Assert.Contains("Append a new section", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    [Fact]
    public void Compose_Gc_RendersTheWorkorderHandoff()
    {
        var rendered = InstructionsComposer.Compose(
            "GC",
            CrewRole.GC,
            briefing: string.Empty,
            jobsite: null,
            vaultFolders: null,
            availableEngines: ["claude"],
            vaultRoot: RealVaultRoot);

        Assert.DoesNotContain("{{", rendered);
        Assert.Contains("WORKORDER.md", rendered);
        Assert.Contains("Plans/<Jobsite>/<Feature>", rendered);
        Assert.Contains("workorderPath", rendered);
        Assert.Contains("dispatch_task", rendered);
    }

    [Fact]
    public void AuthoredBy_IsGcForAGcAndForemanNameJobsiteForAForeman()
    {
        Assert.Equal("GC", InstructionsComposer.AuthoredBy(CrewRole.GC, "GC", "Lighthouse"));
        Assert.Equal("Foreman:Frontend:Lighthouse", InstructionsComposer.AuthoredBy(CrewRole.Foreman, "Frontend", "Lighthouse"));

        // The whole point: two Foremen sharing a Jobsite must not collide.
        Assert.NotEqual(
            InstructionsComposer.AuthoredBy(CrewRole.Foreman, "Frontend", "Lighthouse"),
            InstructionsComposer.AuthoredBy(CrewRole.Foreman, "Backend", "Lighthouse"));
    }

    /// <summary>
    /// The DefaultBranch token is substituted into a `gh pr create --base ...`
    /// example, so its fallback has to be a branch name and nothing else. Prose
    /// there renders as a broken shell command a Foreman would run verbatim.
    /// </summary>
    [Fact]
    public void Compose_NoDefaultBranch_RendersBareMainInsideThePrCommand()
    {
        var rendered = InstructionsComposer.Compose(
            "Frontend",
            CrewRole.Foreman,
            "You are the Frontend Foreman.",
            Jobsite() with { DefaultBranch = null },
            ["Notes/Lighthouse"],
            ["claude"],
            RealVaultRoot);

        Assert.Contains("--base main", rendered);
        Assert.DoesNotContain("(no defaultBranch configured)", rendered);
    }

    [Fact]
    public void Compose_MissingTemplate_ThrowsNamingThePath()
    {
        var emptyVaultRoot = Path.Combine(Path.GetTempPath(), "cc-no-templates-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(emptyVaultRoot);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => InstructionsComposer.Compose(
                "Frontend", CrewRole.Foreman, "b", null, null, null, emptyVaultRoot));

            Assert.Contains("foreman-instructions.md", ex.Message);
        }
        finally
        {
            Directory.Delete(emptyVaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Compose_NoVaultConfigured_ThrowsRatherThanLookingAnywhereElse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InstructionsComposer.Compose(
            "Frontend", CrewRole.Foreman, "b", null, null, null, vaultRoot: null));

        Assert.Contains("No Vault configured", ex.Message);
    }

    [Fact]
    public void ExtractBriefing_RoundTripsAComposedForemanFile()
    {
        var briefing = string.Join(
            Environment.NewLine,
            "You are the Frontend Foreman.",
            "You care about the TUI more than anyone else on the crew.");

        var rendered = InstructionsComposer.Compose(
            "Frontend",
            CrewRole.Foreman,
            briefing,
            Jobsite(),
            ["Notes/Lighthouse"],
            ["claude"],
            RealVaultRoot);

        Assert.Equal(briefing, InstructionsComposer.ExtractBriefing(rendered));
    }

    [Fact]
    public void ExtractBriefing_GcFile_ReturnsEmpty()
    {
        var rendered = InstructionsComposer.Compose(
            "GC",
            CrewRole.GC,
            briefing: string.Empty,
            jobsite: null,
            vaultFolders: ["AI/Context"],
            availableEngines: ["claude"],
            vaultRoot: RealVaultRoot);

        Assert.Equal(string.Empty, InstructionsComposer.ExtractBriefing(rendered));
    }

    [Fact]
    public void ExtractBriefing_UnrecognizedShape_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, InstructionsComposer.ExtractBriefing(string.Empty));
        Assert.Equal(
            string.Empty,
            InstructionsComposer.ExtractBriefing("Some briefing\n\n# You are Frontend, a Foreman\n\nbody"));
        Assert.Equal(
            string.Empty,
            InstructionsComposer.ExtractBriefing("Some briefing\n\n---\n\n# Something else entirely\n"));
    }

    [Fact]
    public void BriefingFilePath_SitsUnderConstructionCrewInstructions()
    {
        var briefingPath = InstructionsComposer.BriefingFilePath("/vault", "Frontend");

        Assert.Equal(
            Path.Combine("/vault", "AI", "ConstructionCrew", "Instructions", "Frontend.briefing.md"),
            briefingPath);
        Assert.Equal(
            Path.Combine("/vault", "AI", "ConstructionCrew", "Instructions"),
            Path.GetDirectoryName(briefingPath));
    }
}
