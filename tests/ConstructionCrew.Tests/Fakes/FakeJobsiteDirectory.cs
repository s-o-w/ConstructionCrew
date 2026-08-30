using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.Fakes;

public sealed class FakeJobsiteDirectory : IJobsiteDirectory
{
    private readonly Dictionary<string, JobsiteConfig> _byName;

    public FakeJobsiteDirectory(params JobsiteConfig[] jobsites) =>
        _byName = jobsites.ToDictionary(j => j.Name, StringComparer.OrdinalIgnoreCase);

    public JobsiteConfig? Find(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyCollection<JobsiteConfig> All() => _byName.Values;
}
