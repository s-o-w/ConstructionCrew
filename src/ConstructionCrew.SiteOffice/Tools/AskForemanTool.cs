using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ConstructionCrew.SiteOffice.Tools;

[McpServerToolType]
public sealed class AskForemanTool
{
    private readonly JobRegistry _jobs;

    public AskForemanTool(JobRegistry jobs)
    {
        _jobs = jobs;
    }

    [McpServerTool(Name = "ask_foreman")]
    [Description("Ask the named Foreman a question and wait for their answer -- for a Worker that's blocked or needs a decision only the Foreman (or Boss, via the Foreman) can make. This blocks until the Foreman replies.")]
    public async Task<string> AskForeman(
        [Description("The Foreman's name.")] string foreman,
        [Description("The question, with enough context that the Foreman can answer without seeing your work directly.")] string question,
        CancellationToken cancellationToken)
    {
        return await _jobs.AskForeman(foreman, question, cancellationToken);
    }
}
