using ConstructionCrew.App.Tui;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// The /view command's testable pieces: parsing the verb (mirrors
/// ForemanDetailsCommand.TryParse's shape exactly) and resolving an argument to
/// a path that is provably inside the Vault or the repo. The paging loop itself
/// is interactive (Console.ReadLine) and is smoke-tested by hand, same as the
/// wizards.
/// </summary>
public class ViewCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-view-tests-" + Guid.NewGuid().ToString("N"));

    private string VaultRoot => Path.Combine(_root, "vault");

    private string RepoRoot => Path.Combine(_root, "repo");

    public ViewCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(VaultRoot, "Notes", "Frontend"));
        Directory.CreateDirectory(RepoRoot);
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));

        File.WriteAllText(Path.Combine(VaultRoot, "Notes", "Frontend", "Sitewalk.md"), "# Sitewalk\n");
        File.WriteAllText(Path.Combine(VaultRoot, "diagram.png"), "not really a png");
        File.WriteAllText(Path.Combine(RepoRoot, "README.md"), "# Repo\n");
        File.WriteAllText(Path.Combine(_root, "elsewhere", "secrets.md"), "nope");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp dir is not a test failure.
        }
    }

    [Theory]
    [InlineData("/view", true, "")]
    [InlineData("/view Notes/Sitewalk.md", true, "Notes/Sitewalk.md")]
    [InlineData("/view   Notes/Sitewalk.md  ", true, "Notes/Sitewalk.md")]
    [InlineData("/viewx", false, "")]
    [InlineData("/foreman Frontend", false, "")]
    public void TryParse_MatchesTheVerbAndExtractsTheTarget(string command, bool expectedMatch, string expectedTarget)
    {
        var matched = ViewCommand.TryParse(command, out var target);

        Assert.Equal(expectedMatch, matched);
        Assert.Equal(expectedTarget, target);
    }

    [Fact]
    public void Resolve_VaultRelativePath_Resolves()
    {
        var resolved = ViewCommand.Resolve(
            Path.Combine("Notes", "Frontend", "Sitewalk.md"), VaultRoot, RepoRoot, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(Path.Combine(VaultRoot, "Notes", "Frontend", "Sitewalk.md"), resolved);
    }

    /// <summary>The Vault is tried first, then the repo -- a repo-only path still resolves.</summary>
    [Fact]
    public void Resolve_RepoRelativePath_ResolvesAfterTheVaultMisses()
    {
        var resolved = ViewCommand.Resolve("README.md", VaultRoot, RepoRoot, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(Path.Combine(RepoRoot, "README.md"), resolved);
    }

    [Fact]
    public void Resolve_AbsolutePathInsideARoot_Resolves()
    {
        var absolute = Path.Combine(RepoRoot, "README.md");

        var resolved = ViewCommand.Resolve(absolute, VaultRoot, RepoRoot, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void Resolve_PathOutsideBothRoots_IsRefused()
    {
        var absolute = ViewCommand.Resolve(
            Path.Combine(_root, "elsewhere", "secrets.md"), VaultRoot, RepoRoot, out var absoluteRefusal);

        Assert.Null(absolute);
        Assert.NotNull(absoluteRefusal);
        Assert.Contains("outside", absoluteRefusal);

        // The real attack shape: a relative path that climbs out of the Vault.
        var traversal = ViewCommand.Resolve(
            Path.Combine("..", "elsewhere", "secrets.md"), VaultRoot, RepoRoot, out var traversalRefusal);

        Assert.Null(traversal);
        Assert.NotNull(traversalRefusal);
        Assert.Contains("outside", traversalRefusal);
    }

    /// <summary>A Vault-relative traversal that reaches an existing file still loses.</summary>
    [Fact]
    public void Resolve_DeepTraversalToAnExistingFile_IsRefused()
    {
        var resolved = ViewCommand.Resolve(
            Path.Combine("Notes", "..", "..", "elsewhere", "secrets.md"), VaultRoot, RepoRoot, out var refusal);

        Assert.Null(resolved);
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Resolve_MissingFile_IsRefused()
    {
        var resolved = ViewCommand.Resolve(
            Path.Combine("Notes", "Frontend", "NotThere.md"), VaultRoot, RepoRoot, out var refusal);

        Assert.Null(resolved);
        Assert.NotNull(refusal);
        Assert.Contains("No file at", refusal);
    }

    [Fact]
    public void Resolve_NonTextExtension_IsRefused()
    {
        var resolved = ViewCommand.Resolve("diagram.png", VaultRoot, RepoRoot, out var refusal);

        Assert.Null(resolved);
        Assert.NotNull(refusal);
        Assert.Contains(".png", refusal);
    }

    [Fact]
    public void Resolve_EmptyArgument_IsRefused()
    {
        Assert.Null(ViewCommand.Resolve("   ", VaultRoot, RepoRoot, out var refusal));
        Assert.NotNull(refusal);
    }

    /// <summary>An unconfigured Vault must not widen the reachable set to everything.</summary>
    [Fact]
    public void Resolve_NoVaultConfigured_StillHonorsTheRepoRoot()
    {
        Assert.Equal(
            Path.Combine(RepoRoot, "README.md"),
            ViewCommand.Resolve("README.md", null, RepoRoot, out _));

        Assert.Null(ViewCommand.Resolve(
            Path.Combine(_root, "elsewhere", "secrets.md"), null, RepoRoot, out var refusal));
        Assert.NotNull(refusal);
    }
}
