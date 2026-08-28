using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ConstructionCrew.SiteOffice.Tools;

[McpServerToolType]
public sealed class DispatchTaskTool
{
    private readonly JobRegistry _jobs;

    public DispatchTaskTool(JobRegistry jobs)
    {
        _jobs = jobs;
    }

    [McpServerTool(Name = "dispatch_task")]
    [Description("Dispatch a task to a named, hired Foreman. Returns a job id immediately -- the Foreman runs asynchronously. Use get_job_status to check on it later.")]
    public string DispatchTask(
        [Description("The name of the hired Foreman to dispatch the task to.")] string foreman,
        [Description("A clear, self-contained description of the task for the Foreman to carry out.")] string task)
    {
        return _jobs.StartJob(foreman, task);
    }
}
