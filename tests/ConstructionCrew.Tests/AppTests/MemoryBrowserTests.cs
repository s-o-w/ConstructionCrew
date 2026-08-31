using ConstructionCrew.App.Tui;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// The memory browser's testable pieces: which vault folders it will ever offer,
/// and the containment test that stops <c>..</c> at the top of them. The prompt
/// loop itself is interactive (SelectionPrompt/Console.ReadLine) and is
/// smoke-tested by hand, same as /view's pager and the wizards.
/// </summary>
public class MemoryBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-memory-tests-" + Guid.NewGuid().ToString("N"));

    private string VaultRoot => Path.Combine(_root, "vault");

    public MemoryBrowserTests()
    {
        Directory.CreateDirectory(Path.Combine(VaultRoot, "Notes", "Frontend"));
        Directory.CreateDirectory(Path.Combine(VaultRoot, "Notes", "GC"));
        Directory.CreateDirectory(Path.Combine(VaultRoot, "Plans", "Frontend"));
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));

        File.WriteAllText(Path.Combine(VaultRoot, "Notes", "Frontend", "Sitewalk.md"), "# Sitewalk\n");
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

    private static ForemanConfig Crew(string name, CrewRole role, params string[] vaultFolders) =>
        new(name, role, "fake", "dir", "instructions.md", new Dictionary<string, string>(), VaultFolders: vaultFolders);

    /// <summary>
    /// Every hired crew member contributes its own folders, and a folder two of
    /// them share is offered once. Sorted, so the prompt does not reshuffle
    /// between sessions.
    /// </summary>
    [Fact]
    public void Roots_UnionsEveryCrewMembersVaultFolders()
    {
        var foremen = new ForemanDirectory([
            Crew("GC", CrewRole.GC, "Notes/GC"),
            Crew("Frontend", CrewRole.Foreman, "Notes/Frontend", "Plans/Frontend"),
            // Deliberately overlaps Frontend: two Foremen reading one folder is a
            // union, not a duplicate row in the prompt.
            Crew("Backend", CrewRole.Foreman, "Notes/Frontend"),
        ]);

        var roots = MemoryBrowser.Roots(VaultRoot, foremen);

        Assert.Equal(
            [
                Path.Combine(VaultRoot, "Notes", "Frontend"),
                Path.Combine(VaultRoot, "Notes", "GC"),
                Path.Combine(VaultRoot, "Plans", "Frontend"),
            ],
            roots);
    }

    /// <summary>
    /// A configured folder nobody has created yet is not a root: offering an empty
    /// prompt for it helps no one, and a hire can name a folder before it exists.
    /// </summary>
    [Fact]
    public void Roots_SkipsFoldersThatDoNotExist()
    {
        var foremen = new ForemanDirectory([Crew("Frontend", CrewRole.Foreman, "Notes/Frontend", "Notes/NeverCreated")]);

        var roots = MemoryBrowser.Roots(VaultRoot, foremen);

        Assert.Equal([Path.Combine(VaultRoot, "Notes", "Frontend")], roots);
    }

    /// <summary>
    /// The scoping is containment, not pattern matching: a traversal and an
    /// absolute path both resolve first and are then refused for landing outside
    /// the Vault -- even though both targets genuinely exist on disk.
    /// </summary>
    [Fact]
    public void Roots_SkipsAnEntryThatEscapesTheVault()
    {
        var foremen = new ForemanDirectory([
            Crew("Frontend", CrewRole.Foreman,
                "Notes/Frontend",
                Path.Combine("..", "elsewhere"),
                Path.Combine("Notes", "..", "..", "elsewhere"),
                Path.Combine(_root, "elsewhere")),
        ]);

        var roots = MemoryBrowser.Roots(VaultRoot, foremen);

        Assert.Equal([Path.Combine(VaultRoot, "Notes", "Frontend")], roots);
        Assert.DoesNotContain(roots, r => r.Contains("elsewhere", StringComparison.Ordinal));
    }

    /// <summary>
    /// A root's own parent is outside. This is the whole <c>..</c> guard: from the
    /// top of a crew folder, up leads into the rest of the Vault, and the browser
    /// refuses rather than following.
    /// </summary>
    [Fact]
    public void IsInsideAnyRoot_ParentOfARoot_IsOutside()
    {
        var root = Path.Combine(VaultRoot, "Notes", "Frontend");
        IReadOnlyList<string> roots = [root];

        Assert.False(MemoryBrowser.IsInsideAnyRoot(Path.Combine(VaultRoot, "Notes"), roots));
        Assert.False(MemoryBrowser.IsInsideAnyRoot(VaultRoot, roots));
        Assert.False(MemoryBrowser.IsInsideAnyRoot(Path.Combine(VaultRoot, "Plans", "Frontend"), roots));

        // The root itself and anything under it stay reachable, or the browser
        // could not offer its own contents.
        Assert.True(MemoryBrowser.IsInsideAnyRoot(root, roots));
        Assert.True(MemoryBrowser.IsInsideAnyRoot(Path.Combine(root, "Sub", "deep.md"), roots));

        // Sibling-prefix, not a child: "Frontend-old" must not pass a naive
        // StartsWith without the separator.
        Assert.False(MemoryBrowser.IsInsideAnyRoot(root + "-old", roots));

        // An unnormalized traversal, exactly as a repeated ".." would build it --
        // it is resolved first and then refused, which is why there is no list of
        // bad names to keep current.
        Assert.False(MemoryBrowser.IsInsideAnyRoot(Path.Combine(root, "..", "..", "..", "elsewhere"), roots));
        Assert.True(MemoryBrowser.IsInsideAnyRoot(Path.Combine(root, "Sub", ".."), roots));
    }
}
