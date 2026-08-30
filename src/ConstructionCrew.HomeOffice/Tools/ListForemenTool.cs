using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

[McpServerToolType]
public sealed class ListForemenTool
{
    private readonly IForemanDirectory _foremen;
    private readonly JobRegistry _jobs;

    public ListForemenTool(IForemanDirectory foremen, JobRegistry jobs)
    {
        _foremen = foremen;
        _jobs = jobs;
    }

    [McpServerTool(Name = "list_foremen")]
    [Description("List every currently hired Foreman by name, provider, and whether they're busy right now. Call this instead of assuming a static roster -- Foremen can be hired mid-session.")]
    public IEnumerable<object> ListForemen()
    {
        return _foremen.All()
            .Where(f => !f.Name.Equals("GC", StringComparison.OrdinalIgnoreCase))
            .Select(f => new
            {
                name = f.Name,
                provider = f.Provider,
                jobsite = f.JobsiteName,
                workingDirectory = f.WorkingDirectory,
                busy = _jobs.IsForemanBusy(f.Name),
            });
    }
}
