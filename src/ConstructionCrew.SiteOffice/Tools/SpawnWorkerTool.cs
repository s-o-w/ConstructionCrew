using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ConstructionCrew.SiteOffice.Tools;

[McpServerToolType]
public sealed class SpawnWorkerTool
{
    private readonly JobRegistry _jobs;

    public SpawnWorkerTool(JobRegistry jobs)
    {
        _jobs = jobs;
    }

    [McpServerTool(Name = "spawn_worker")]
    [Description("Spawn an ephemeral Worker to do one well-defined, self-contained piece of work on your jobsite. Runs in your own engine by default; pass engine to use a different CLI if the task is fully self-contained. Returns a job id immediately. The Worker never sees your conversation -- give it everything it needs in the task description, or expect it to call ask_foreman if it gets stuck.")]
    public string SpawnWorker(
        [Description("Your own Foreman name, exactly as hired.")] string foreman,
        [Description("A clear, self-contained description of the work.")] string task,
        [Description("Optional: a different CLI engine id (e.g. 'codex') to run this Worker in instead of your own. Omit to use your own engine.")] string? engine = null)
    {
        return _jobs.StartWorkerJob(foreman, task, engine);
    }
}
