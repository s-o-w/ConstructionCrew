using ConstructionCrew.App.Tui;

namespace ConstructionCrew.Tests.AppTests;

public class DirectoryPickerTests
{
    private static string NewTempDirTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccrew-picker-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "top.txt"), "top level file");
        File.WriteAllText(Path.Combine(root, "sub", "nested.txt"), "nested file");
        return root;
    }

    [Fact]
    public void AppendChildren_FreshlyStarted_ShowsOnlyImmediateChildrenCollapsed()
    {
        var root = NewTempDirTree();
        try
        {
            var rows = new List<(string Label, string? Path, bool IsFolder)>();
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DirectoryPicker.AppendChildren(rows, root, expanded, depth: 1, allowFiles: false);

            var row = Assert.Single(rows);
            Assert.Equal(Path.Combine(root, "sub"), row.Path);
            Assert.True(row.IsFolder);
            Assert.Contains("\U0001F4C1", row.Label); // closed folder icon
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppendChildren_SubfolderExpanded_ShowsItsChildrenIndentedOneLevelDeeper()
    {
        var root = NewTempDirTree();
        try
        {
            var sub = Path.Combine(root, "sub");
            var rows = new List<(string Label, string? Path, bool IsFolder)>();
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sub };

            DirectoryPicker.AppendChildren(rows, root, expanded, depth: 1, allowFiles: true);

            var subRow = rows.Single(r => r.Path == sub);
            Assert.Contains("\U0001F4C2", subRow.Label); // open folder icon

            var nestedFile = Path.Combine(sub, "nested.txt");
            var nestedRow = rows.Single(r => r.Path == nestedFile);
            Assert.False(nestedRow.IsFolder);
            // Indented one level deeper than the "sub" row itself.
            var subIndent = subRow.Label.Length - subRow.Label.TrimStart(' ').Length;
            var nestedIndent = nestedRow.Label.Length - nestedRow.Label.TrimStart(' ').Length;
            Assert.True(nestedIndent > subIndent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppendChildren_CollapsedAgain_HidesItsChildren()
    {
        var root = NewTempDirTree();
        try
        {
            var rows = new List<(string Label, string? Path, bool IsFolder)>();
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DirectoryPicker.AppendChildren(rows, root, expanded, depth: 1, allowFiles: true);

            Assert.DoesNotContain(rows, r => r.Path == Path.Combine(root, "sub", "nested.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppendChildren_AllowFilesFalse_NeverEmitsAFileRow()
    {
        var root = NewTempDirTree();
        try
        {
            var rows = new List<(string Label, string? Path, bool IsFolder)>();
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DirectoryPicker.AppendChildren(rows, root, expanded, depth: 1, allowFiles: false);

            Assert.DoesNotContain(rows, r => !r.IsFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Pick_StartingDirectoryDoesNotExist_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ccrew-picker-missing-" + Guid.NewGuid().ToString("n")[..8]);

        Assert.Null(DirectoryPicker.Pick(missing));
    }
}
