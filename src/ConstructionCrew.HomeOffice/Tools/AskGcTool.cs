using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

/// <summary>
/// Thin MCP wrapper over <see cref="JobRegistry.AskGc"/>; the round trip,
/// timeout, and park/resume logic all live there so a kind:"milestone"
/// file_sitrep takes the same path.
///
/// No timeout parameter here on purpose: the bound is Home Office's to set,
/// not a Foreman's to argue with.
/// </summary>
[McpServerToolType]
public sealed class AskGcTool
{
    private readonly JobRegistry _jobs;

    public AskGcTool(JobRegistry jobs)
    {
        _jobs = jobs;
    }

    [McpServerTool(Name = "ask_gc")]
    [Description("Escalate to the GC (and through it, the Boss) when you are blocked on a decision only they can make. Pass YOUR OWN job id -- the one at the top of your task text. Returns the GC's answer, or 'parked: waiting on Boss' if nobody answers in time; either way your turn ends cleanly, and a parked job resumes by itself when the GC does answer.")]
    public async Task<string> AskGc(
        [Description("Your own Foreman name, exactly as hired.")] string foreman,
        [Description("Your own job id, copied from the 'ConstructionCrew job id:' line at the top of your task text.")] string jobId,
        [Description("The question, with enough context that the GC can answer without seeing your work directly.")] string question,
        CancellationToken cancellationToken)
    {
        // Hard error, never a silent fallback to "your most recent job": which
        // job is asking decides which job gets parked.
        if (string.IsNullOrWhiteSpace(jobId) || _jobs.GetJob(jobId) is null)
        {
            throw new InvalidOperationException(
                $"ask_gc needs your own job id, and '{jobId}' is not a tracked job. Copy the id from the " +
                $"'ConstructionCrew job id:' line at the top of your task text. (Caller: '{foreman}'.)");
        }

        return await _jobs.AskGc(jobId, question, cancellationToken);
    }
}
