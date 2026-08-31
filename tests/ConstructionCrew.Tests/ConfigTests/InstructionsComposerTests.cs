using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

/// <summary>
/// Runs against the templates that actually ship in this repo, not a synthetic
/// fixture -- an unreplaced token or a lost section in the real file is exactly
/// the failure worth catching.
/// </summary>
public class InstructionsComposerTests
{
    private static string RepoRoot => RepoPaths.FindRepoRoot(AppContext.BaseDirectory);

    private static JobsiteConfig Jobsite() =>
        new(
            "XINFRA",
            "/home/shawn/PROJECTS/XINFRA",
            "The semantic data platform.",
            RepoUrl: null,
            ColorName: "purple",
            DefaultBranch: "develop",
            BuildCommand: "dotnet build",
            TestCommand: "dotnet test",
            Upstream: new Dictionary<string, string> { ["board"] = "https://github.com/orgs/spatialbiz/projects/73" },
            VaultFolders: ["Notes/XINFRA", "Plans/XINFRA"]);

    [Fact]
    public void Compose_Foreman_RendersEveryTokenAndTheReviewWorkflow()
    {
        var rendered = InstructionsComposer.Compose(
            "Frontend",
            CrewRole.Foreman,
            "You are the Frontend Foreman.",
            Jobsite(),
            ["Notes/XINFRA", "Plans/XINFRA"],
            ["claude", "codex"],
            RepoRoot,
            "/home/shawn/Vault");

        Assert.DoesNotContain("{{", rendered);
        Assert.Contains("You are the Frontend Foreman.", rendered);
        Assert.Contains("XINFRA", rendered);
        Assert.Contains("dotnet build", rendered);
        Assert.Contains("dotnet test", rendered);
        Assert.Contains("develop", rendered);
        Assert.Contains("https://github.com/orgs/spatialbiz/projects/73", rendered);
        Assert.Contains("Notes/XINFRA", rendered);
        Assert.Contains("Foreman:XINFRA", rendered);
        Assert.Contains(Path.Combine("/home/shawn/Vault", "AI", "Context", "crew-preferences.md"), rendered);
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
            ["Notes/XINFRA", "Plans/XINFRA"],
            ["claude", "codex"],
            RepoRoot,
            "/home/shawn/Vault");

        Assert.Contains("sitewalk", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("READ-ONLY", rendered);
        Assert.Contains("Notes/XINFRA/Sitewalk.md", rendered);
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
            ["Notes/XINFRA", "Plans/XINFRA"],
            ["claude", "codex"],
            RepoRoot,
            "/home/shawn/Vault");

        Assert.Contains("rev-parse --git-dir", rendered);
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
            repoRoot: RepoRoot,
            vaultRoot: "/home/shawn/Vault");

        Assert.DoesNotContain("{{", rendered);
        Assert.Contains("WORKORDER.md", rendered);
        Assert.Contains("Plans/<Jobsite>/<Feature>", rendered);
        Assert.Contains("workorderPath", rendered);
        Assert.Contains("dispatch_task", rendered);
    }

    [Fact]
    public void AuthoredBy_IsGcForAGcAndForemanJobsiteForAForeman()
    {
        Assert.Equal("GC", InstructionsComposer.AuthoredBy(CrewRole.GC, "XINFRA"));
        Assert.Equal("Foreman:XINFRA", InstructionsComposer.AuthoredBy(CrewRole.Foreman, "XINFRA"));
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
            ["Notes/XINFRA"],
            ["claude"],
            RepoRoot,
            "/home/shawn/Vault");

        Assert.Contains("--base main", rendered);
        Assert.DoesNotContain("(no defaultBranch configured)", rendered);
    }

    [Fact]
    public void Compose_MissingTemplate_ThrowsNamingThePath()
    {
        var emptyRepoRoot = Path.Combine(Path.GetTempPath(), "cc-no-templates-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(emptyRepoRoot);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => InstructionsComposer.Compose(
                "Frontend", CrewRole.Foreman, "b", null, null, null, emptyRepoRoot, null));

            Assert.Contains("foreman-instructions.md", ex.Message);
        }
        finally
        {
            Directory.Delete(emptyRepoRoot, recursive: true);
        }
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
            ["Notes/XINFRA"],
            ["claude"],
            RepoRoot,
            "/home/shawn/Vault");

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
            repoRoot: RepoRoot,
            vaultRoot: "/home/shawn/Vault");

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
    public void BriefingFilePath_SitsBesideTheInstructionsFile()
    {
        var briefingPath = InstructionsComposer.BriefingFilePath("/repo", "Frontend");

        Assert.Equal(Path.Combine("/repo", "config", "instructions", "Frontend.briefing.md"), briefingPath);
        Assert.Equal(
            Path.Combine("/repo", "config", "instructions"),
            Path.GetDirectoryName(briefingPath));
    }
}
