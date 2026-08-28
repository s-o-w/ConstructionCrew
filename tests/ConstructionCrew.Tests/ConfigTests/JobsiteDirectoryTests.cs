using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

public class JobsiteDirectoryTests
{
    [Fact]
    public void Add_MakesJobsiteFindableAndListed()
    {
        var directory = new JobsiteDirectory([]);
        var jobsite = new JobsiteConfig("XINFRA", "dir", "description");

        directory.Add(jobsite);

        Assert.Same(jobsite, directory.Find("XINFRA"));
        Assert.Contains(jobsite, directory.All());
    }

    [Fact]
    public void Remove_KnownName_RemovesIt_ReturnsTrue()
    {
        var jobsite = new JobsiteConfig("XINFRA", "dir", "description");
        var directory = new JobsiteDirectory([jobsite]);

        var removed = directory.Remove("XINFRA");

        Assert.True(removed);
        Assert.Null(directory.Find("XINFRA"));
    }
}
