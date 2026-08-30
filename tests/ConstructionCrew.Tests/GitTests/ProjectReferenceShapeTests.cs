using ConstructionCrew.App.Tui;
using ConstructionCrew.Git;
using ConstructionCrew.HomeOffice;

namespace ConstructionCrew.Tests.GitTests;

/// <summary>
/// Architecture §3.7's boundary rule, enforced against the compiled assemblies
/// rather than by reading the .csproj files: App is the only project allowed to
/// name ConstructionCrew.Git's concrete WorktreeManager, and HomeOffice must
/// reach IWorktreeManager through Core alone.
/// </summary>
public class ProjectReferenceShapeTests
{
    [Fact]
    public void AppAssembly_ResolvesTheNewGitReference()
    {
        var referenced = typeof(FireWizard).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        Assert.Contains("ConstructionCrew.Git", referenced);
        // Not just referenced -- actually used: Program.cs constructs it.
        Assert.NotNull(typeof(WorktreeManager).Assembly);
    }

    [Fact]
    public void HomeOfficeAssembly_CarriesNoGitReference()
    {
        var referenced = typeof(JobRegistry).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        Assert.DoesNotContain("ConstructionCrew.Git", referenced);
        Assert.Contains("ConstructionCrew.Core", referenced);
    }
}
