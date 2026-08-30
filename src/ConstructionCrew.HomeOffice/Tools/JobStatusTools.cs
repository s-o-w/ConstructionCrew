using System.ComponentModel;
using ConstructionCrew.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

[McpServerToolType]
public sealed class JobStatusTools
{
    private readonly JobRegistry _jobs;

    public JobStatusTools(JobRegistry jobs)
    {
        _jobs = jobs;
    }

    [McpServerTool(Name = "get_job_status")]
    [Description("Get the current status of a previously dispatched job by its job id.")]
    public JobRecord GetJobStatus([Description("The job id returned by dispatch_task.")] string jobId)
    {
        return _jobs.GetJob(jobId)
            ?? throw new McpException($"No job found with id '{jobId}'.");
    }
}
