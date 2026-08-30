using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

[McpServerToolType]
public sealed class ListJobsitesTool
{
    private readonly IJobsiteDirectory _jobsites;
    private readonly IForemanDirectory _foremen;

    public ListJobsitesTool(IJobsiteDirectory jobsites, IForemanDirectory foremen)
    {
        _jobsites = jobsites;
        _foremen = foremen;
    }

    [McpServerTool(Name = "list_jobsites")]
    [Description("List every jobsite (project/repo) the Boss is responsible for, and which Foreman (if any) is assigned to each. A jobsite with no assigned Foreman needs one hired before work can be dispatched there.")]
    public IEnumerable<object> ListJobsites()
    {
        return _jobsites.All().Select(j => new
        {
            name = j.Name,
            repoPath = j.RepoPath,
            description = j.Description,
            assignedForeman = _foremen.All().FirstOrDefault(f => string.Equals(f.JobsiteName, j.Name, StringComparison.OrdinalIgnoreCase))?.Name,
        });
    }
}
