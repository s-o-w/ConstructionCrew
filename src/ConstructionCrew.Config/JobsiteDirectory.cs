using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

public sealed class JobsiteDirectory : IJobsiteDirectory
{
    private readonly Dictionary<string, JobsiteConfig> _byName;

    public JobsiteDirectory(IEnumerable<JobsiteConfig> jobsites)
    {
        _byName = jobsites.ToDictionary(j => j.Name, StringComparer.OrdinalIgnoreCase);
    }

    public JobsiteConfig? Find(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyCollection<JobsiteConfig> All() => _byName.Values;

    public void Add(JobsiteConfig config) => _byName[config.Name] = config;

    public bool Remove(string name) => _byName.Remove(name);
}
