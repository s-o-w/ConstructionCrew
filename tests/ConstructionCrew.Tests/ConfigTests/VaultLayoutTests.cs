using ConstructionCrew.Config;

namespace ConstructionCrew.Tests.ConfigTests;

/// <summary>
/// The recognition predicate is what decides whether /hire derives a Foreman's
/// vault write scope or has to ask for it, so it gets pinned against a
/// deliberately incomplete vault and a fully scaffolded one. Every case here
/// builds its own temp directory; none assumes any particular machine's real
/// vault path.
/// </summary>
public class VaultLayoutTests
{
    [Fact]
    public void Recognize_OnlyHomeMd_IsNotRecognized()
    {
        var root = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "HOME.md"), "# HOME");

            Assert.Equal(VaultRecognition.Unrecognized, VaultLayout.Recognize(root));

            // The four that are actually absent, and only those -- the wizard
            // shows this list to the Boss verbatim.
            Assert.Equal(["CLAUDE.md", "Notes/", "Plans/", "AI/"], VaultLayout.MissingMarkers(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recognize_AllFiveMarkers_IsRecognized()
    {
        var root = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "HOME.md"), "# HOME");
            File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "# CLAUDE");
            Directory.CreateDirectory(Path.Combine(root, "Notes"));
            Directory.CreateDirectory(Path.Combine(root, "Plans"));
            Directory.CreateDirectory(Path.Combine(root, "AI"));

            Assert.Equal(VaultRecognition.Recognized, VaultLayout.Recognize(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recognize_MarkersAsWrongKind_IsNotRecognized()
    {
        // HOME.md as a directory and Notes as a file both have to fail: plan-Work's
        // Step 0 tests HOME.md/CLAUDE.md with `-f`, and a "Notes" file is not
        // somewhere a Foreman's Notes/<Jobsite> path can ever be written. AI/ is
        // left correct here -- this test is about the OTHER markers' wrong-kind
        // failures, not AI/'s.
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "HOME.md"));
            File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "# CLAUDE");
            File.WriteAllText(Path.Combine(root, "Notes"), "not a directory");
            Directory.CreateDirectory(Path.Combine(root, "Plans"));
            Directory.CreateDirectory(Path.Combine(root, "AI"));

            Assert.Equal(VaultRecognition.Unrecognized, VaultLayout.Recognize(root));
            Assert.Equal(["HOME.md", "Notes/"], VaultLayout.MissingMarkers(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recognize_MissingOrBlankRoot_IsNotRecognized()
    {
        Assert.Equal(VaultRecognition.Unrecognized, VaultLayout.Recognize(null));
        Assert.Equal(VaultRecognition.Unrecognized, VaultLayout.Recognize("   "));
        Assert.Equal(
            VaultRecognition.Unrecognized,
            VaultLayout.Recognize(Path.Combine(Path.GetTempPath(), "ccrew-does-not-exist-" + Guid.NewGuid().ToString("n"))));
    }

    [Fact]
    public void Scaffold_ProducesARecognizedVault()
    {
        // The scaffold's whole point: what it lays down must satisfy the very
        // predicate /hire then checks. If these two ever drift, a freshly
        // scaffolded vault would start asking the Boss for paths it should know.
        var repoRoot = FindRepoRoot();
        var root = NewTempDir();
        try
        {
            var written = VaultLayout.Scaffold(VaultLayout.ScaffoldSourceDirectory(repoRoot), root);

            Assert.Equal(VaultRecognition.Recognized, VaultLayout.Recognize(root));
            Assert.NotEmpty(written);

            // Empty directories are created even though .gitkeep is not copied.
            Assert.True(Directory.Exists(Path.Combine(root, "AI", "graph", "build")));
            Assert.False(File.Exists(Path.Combine(root, "Notes", ".gitkeep")));

            // The graph layer a build_graph run needs.
            Assert.True(File.Exists(Path.Combine(root, "AI", "graph", "context.jsonld")));
            Assert.True(File.Exists(Path.Combine(root, "AI", "graph", "Ontologies", "VaultMeta", "context.jsonld")));
            Assert.True(File.Exists(Path.Combine(root, "AI", "graph", "Ontologies", "VaultMeta", "Classes", "Project.md")));
            Assert.True(File.Exists(Path.Combine(root, "AI", "graph", "Ontologies", "VaultMeta", "Properties", "touchesProject.md")));
            Assert.True(File.Exists(Path.Combine(root, "AI", "graph", "Vocabularies", "InformationClassification", "External.md")));
            Assert.True(File.Exists(Path.Combine(root, "AI", "graph", "Vocabularies", "NoteMaturity", "Superseded.md")));

            // The instructions templates every crew member's Compose call needs.
            Assert.True(File.Exists(Path.Combine(root, "AI", "ConstructionCrew", "Templates", "gc-instructions.md")));
            Assert.True(File.Exists(Path.Combine(root, "AI", "ConstructionCrew", "Templates", "foreman-instructions.md")));

            // A scaffolded vault must not ship a Python prerequisite -- build_graph
            // is the export mechanism now, so no export_graph.sh comes along.
            Assert.Empty(Directory.EnumerateFiles(root, "export_graph.*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scaffold_NeverOverwritesAnExistingFile()
    {
        var repoRoot = FindRepoRoot();
        var root = NewTempDir();
        try
        {
            var claudeMd = Path.Combine(root, "CLAUDE.md");
            File.WriteAllText(claudeMd, "MY OWN CONVENTIONS");

            var written = VaultLayout.Scaffold(VaultLayout.ScaffoldSourceDirectory(repoRoot), root);

            Assert.Equal("MY OWN CONVENTIONS", File.ReadAllText(claudeMd));
            Assert.DoesNotContain("CLAUDE.md", written);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The crew-preferences file both instructions templates reference has to land
    /// on a vault that never went through the scaffold wizard.
    /// </summary>
    [Fact]
    public void EnsureScaffoldFile_MissingFile_WritesIt()
    {
        var repoRoot = FindRepoRoot();
        var root = NewTempDir();
        try
        {
            var wrote = VaultLayout.EnsureScaffoldFile(
                VaultLayout.ScaffoldSourceDirectory(repoRoot), root, VaultLayout.CrewPreferencesRelativePath);

            Assert.True(wrote);

            var destination = Path.Combine(root, "AI", "ConstructionCrew", "crew-preferences.md");
            Assert.True(File.Exists(destination));

            var source = Path.Combine(
                VaultLayout.ScaffoldSourceDirectory(repoRoot), "AI", "ConstructionCrew", "crew-preferences.md");
            Assert.Equal(File.ReadAllText(source), File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// This runs on every start, so the Boss's own edits have to survive it. An
    /// existing file is left byte for byte alone and the call reports it wrote nothing.
    /// </summary>
    [Fact]
    public void EnsureScaffoldFile_ExistingFile_LeavesContentAlone()
    {
        var repoRoot = FindRepoRoot();
        var root = NewTempDir();
        try
        {
            var destination = Path.Combine(root, "AI", "ConstructionCrew", "crew-preferences.md");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "MY OWN PREFERENCES");

            var wrote = VaultLayout.EnsureScaffoldFile(
                VaultLayout.ScaffoldSourceDirectory(repoRoot), root, VaultLayout.CrewPreferencesRelativePath);

            Assert.False(wrote);
            Assert.Equal("MY OWN PREFERENCES", File.ReadAllText(destination));

            // And still alone on a second pass -- the every-start case.
            Assert.False(VaultLayout.EnsureScaffoldFile(
                VaultLayout.ScaffoldSourceDirectory(repoRoot), root, VaultLayout.CrewPreferencesRelativePath));
            Assert.Equal("MY OWN PREFERENCES", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A vault with no AI/ConstructionCrew/ at all is the common case, so the copy has to
    /// build the directories on the way down rather than throwing.
    /// </summary>
    [Fact]
    public void EnsureScaffoldFile_CreatesIntermediateDirectories()
    {
        var repoRoot = FindRepoRoot();
        var root = NewTempDir();
        try
        {
            Assert.False(Directory.Exists(Path.Combine(root, "AI")));

            var wrote = VaultLayout.EnsureScaffoldFile(
                VaultLayout.ScaffoldSourceDirectory(repoRoot), root, VaultLayout.CrewPreferencesRelativePath);

            Assert.True(wrote);
            Assert.True(Directory.Exists(Path.Combine(root, "AI", "ConstructionCrew")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccrew-vault-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepoRoot() => RepoPaths.FindRepoRoot(AppContext.BaseDirectory);
}
