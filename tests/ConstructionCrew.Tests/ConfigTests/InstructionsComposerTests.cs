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
}
