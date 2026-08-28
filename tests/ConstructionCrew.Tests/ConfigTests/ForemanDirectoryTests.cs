using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

public class ForemanDirectoryTests
{
    [Fact]
    public void Add_MakesForemanFindableAndListed()
    {
        var directory = new ForemanDirectory([]);
        var config = new ForemanConfig("Backend", "claude", "dir", "instructions.md", new Dictionary<string, string>());

        directory.Add(config);

        Assert.Same(config, directory.Find("Backend"));
        Assert.Contains(config, directory.All());
    }

    [Fact]
    public void Remove_KnownName_RemovesIt_ReturnsTrue()
    {
        var config = new ForemanConfig("Backend", "claude", "dir", "instructions.md", new Dictionary<string, string>());
        var directory = new ForemanDirectory([config]);

        var removed = directory.Remove("Backend");

        Assert.True(removed);
        Assert.Null(directory.Find("Backend"));
        Assert.Empty(directory.All());
    }

    [Fact]
    public void Remove_UnknownName_ReturnsFalse()
    {
        var directory = new ForemanDirectory([]);

        Assert.False(directory.Remove("NoSuchForeman"));
    }

    [Fact]
    public void Add_SameNameTwice_Upserts()
    {
        var directory = new ForemanDirectory([]);
        directory.Add(new ForemanConfig("Backend", "claude", "dir1", "a.md", new Dictionary<string, string>()));
        directory.Add(new ForemanConfig("Backend", "codex", "dir2", "b.md", new Dictionary<string, string>()));

        Assert.Single(directory.All());
        Assert.Equal("codex", directory.Find("Backend")!.Provider);
    }
}
