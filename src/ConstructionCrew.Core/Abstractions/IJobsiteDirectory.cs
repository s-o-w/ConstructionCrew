using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Core.Abstractions;

public interface IJobsiteDirectory
{
    JobsiteConfig? Find(string name);

    IReadOnlyCollection<JobsiteConfig> All();
}
