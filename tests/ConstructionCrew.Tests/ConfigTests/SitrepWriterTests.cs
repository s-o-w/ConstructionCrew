using ConstructionCrew.App;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.ConfigTests;

public class SitrepWriterTests
{
    private static string NewVault()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "cc-sitrepw-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(vaultRoot, "Notes", "Lighthouse"));
        return vaultRoot;
    }

    private static SitrepRequest Request(string vaultRoot, string body, string altitude = "summary", params string[] folders) =>
        new(vaultRoot,
            folders.Length == 0 ? ["Notes/Lighthouse", "Plans/Lighthouse"] : folders,
            altitude,
            body,
            "Foreman:Lighthouse");

    [Fact]
    public void Write_FirstOfTheDay_CreatesTheFileWithFrontmatterInsideTheCallersNotesFolder()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = new SitrepWriter().Write(Request(vaultRoot, "build is green"));

            Assert.Equal(
                Path.Combine(vaultRoot, "Notes", "Lighthouse", "Sitreps", $"{DateTimeOffset.UtcNow:yyyy-MM-dd}-summary.md"),
                path);

            var text = File.ReadAllText(path);
            Assert.StartsWith("---", text);
            Assert.Contains("type: \"[[SessionNote]]\"", text);
            Assert.Contains("touchesProject: \"[[Lighthouse]]\"", text);
            Assert.Contains("authoredBy: \"Foreman:Lighthouse\"", text);
            // The section heading carries its own author too, not just the
            // file-level frontmatter -- what a shared Jobsite's file needs.
            Assert.Contains("UTC -- Foreman:Lighthouse", text);
            Assert.Contains("build is green", text);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>Append-only: the second sitrep of the day never rewrites the first.</summary>
    [Fact]
    public void Write_SecondOfTheDay_AppendsAndKeepsExactlyOneFrontmatterBlock()
    {
        var vaultRoot = NewVault();
        try
        {
            var writer = new SitrepWriter();
            var first = writer.Write(Request(vaultRoot, "plan settled"));
            var second = writer.Write(Request(vaultRoot, "diff reviewed"));

            Assert.Equal(first, second);

            var text = File.ReadAllText(first);
            Assert.Contains("plan settled", text);
            Assert.Contains("diff reviewed", text);
            // One frontmatter block, i.e. exactly two "---" fence lines.
            Assert.Equal(2, text.ReplaceLineEndings("\n").Split('\n').Count(l => l.Trim() == "---"));
            Assert.True(text.IndexOf("plan settled", StringComparison.Ordinal) <
                        text.IndexOf("diff reviewed", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>
    /// Two Foremen sharing a Jobsite can file into the same day/altitude file --
    /// each section has to show which one wrote it, since the file-level
    /// authoredBy only ever reflects whoever created the file first.
    /// </summary>
    [Fact]
    public void Write_TwoDifferentAuthorsSameDayAndAltitude_EachSectionShowsItsOwnAuthor()
    {
        var vaultRoot = NewVault();
        try
        {
            var writer = new SitrepWriter();
            var caseyRequest = Request(vaultRoot, "Casey's update") with { AuthoredBy = "Foreman:Casey:Lighthouse" };
            var robinRequest = Request(vaultRoot, "Robin's update") with { AuthoredBy = "Foreman:Robin:Lighthouse" };

            var first = writer.Write(caseyRequest);
            var second = writer.Write(robinRequest);
            Assert.Equal(first, second);

            var text = File.ReadAllText(first);
            // File-level frontmatter still only names whoever created it first.
            Assert.Contains("authoredBy: \"Foreman:Casey:Lighthouse\"", text);

            // But each section is self-attributed, and paired with its OWN body --
            // not just that both authors and both bodies appear somewhere.
            var caseyHeadingIndex = text.IndexOf("UTC -- Foreman:Casey:Lighthouse", StringComparison.Ordinal);
            var robinHeadingIndex = text.IndexOf("UTC -- Foreman:Robin:Lighthouse", StringComparison.Ordinal);
            var caseyBodyIndex = text.IndexOf("Casey's update", StringComparison.Ordinal);
            var robinBodyIndex = text.IndexOf("Robin's update", StringComparison.Ordinal);

            Assert.True(caseyHeadingIndex >= 0 && robinHeadingIndex >= 0);
            Assert.True(caseyHeadingIndex < caseyBodyIndex && caseyBodyIndex < robinHeadingIndex);
            Assert.True(robinHeadingIndex < robinBodyIndex);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>Altitude drives the filename; kind never does.</summary>
    [Fact]
    public void Write_DifferentAltitudes_AreDifferentFiles()
    {
        var vaultRoot = NewVault();
        try
        {
            var writer = new SitrepWriter();
            var summary = writer.Write(Request(vaultRoot, "short"));
            var detail = writer.Write(Request(vaultRoot, "long", altitude: "detail"));

            Assert.NotEqual(summary, detail);
            Assert.EndsWith("-summary.md", summary);
            Assert.EndsWith("-detail.md", detail);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Write_NoNotesFolderInScope_ThrowsNamingTheCaller()
    {
        var vaultRoot = NewVault();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new SitrepWriter().Write(Request(vaultRoot, "nowhere to go", "summary", "Plans/Lighthouse")));

            Assert.Contains("Foreman:Lighthouse", ex.Message);
            Assert.Contains("Notes/", ex.Message);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>
    /// The path-escape guard. A declared folder that walks back out of the Vault
    /// with ".." must be rejected outright, never merely normalized and written.
    /// </summary>
    [Fact]
    public void Write_DeclaredFolderEscapesTheVault_ThrowsAndWritesNothing()
    {
        var vaultRoot = NewVault();
        var outside = Path.Combine(Path.GetTempPath(), "cc-sitrepw-outside-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            var escaping = "Notes/../../" + Path.GetFileName(outside);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new SitrepWriter().Write(Request(vaultRoot, "escape attempt", "summary", escaping)));

            Assert.Contains("outside", ex.Message);
            Assert.False(Directory.Exists(outside));
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>Same guard, reached through the altitude instead of the folder.</summary>
    [Fact]
    public void Write_AltitudeWalksOutOfTheSitrepsFolder_Throws()
    {
        var vaultRoot = NewVault();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new SitrepWriter().Write(Request(vaultRoot, "escape attempt", altitude: "../../../escaped")));

            Assert.Contains("outside", ex.Message);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    /// <summary>
    /// GC's own scope, end to end: FindNotesFolder takes the FIRST entry under
    /// Notes/, so "Notes/GC" leading FirstRunWizard.GcVaultFolders is what keeps GC's
    /// sitreps out of the bare "Notes" root -- and what stops file_sitrep throwing
    /// "has no vault folder under 'Notes/' in its write scope" the way a GC with no
    /// vaultFolders at all does.
    /// </summary>
    [Fact]
    public void Write_GcScope_FilesUnderNotesGc()
    {
        var vaultRoot = NewVault();
        try
        {
            var path = new SitrepWriter().Write(new SitrepRequest(
                vaultRoot,
                FirstRunWizard.GcVaultFolders,
                "status",
                "dispatched the workorder",
                "GC"));

            Assert.Equal(
                Path.Combine(vaultRoot, "Notes", "GC", "Sitreps", $"{DateTimeOffset.UtcNow:yyyy-MM-dd}-status.md"),
                path);
        }
        finally
        {
            Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void Write_NoVaultRoot_ThrowsNamingTheCaller()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new SitrepWriter().Write(new SitrepRequest("", ["Notes/Lighthouse"], "summary", "body", "Foreman:Lighthouse")));

        Assert.Contains("Foreman:Lighthouse", ex.Message);
    }
}
