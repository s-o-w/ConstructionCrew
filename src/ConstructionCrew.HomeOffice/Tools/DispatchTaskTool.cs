using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

/// <summary>
/// Owns parsing and validating a workorder path, not JobRegistry: this
/// keeps JobRegistry a pure state machine with no YAML dependency.
/// </summary>
[McpServerToolType]
public sealed class DispatchTaskTool
{
    private readonly JobRegistry _jobs;
    private readonly IWorkorderReader _workorderReader;
    private readonly IForemanDirectory _foremen;
    private readonly IJobsiteDirectory _jobsites;
    private readonly HomeOfficeVaultOptions _vaultOptions;

    public DispatchTaskTool(
        JobRegistry jobs,
        IWorkorderReader workorderReader,
        IForemanDirectory foremen,
        IJobsiteDirectory jobsites,
        HomeOfficeVaultOptions vaultOptions)
    {
        _jobs = jobs;
        _workorderReader = workorderReader;
        _foremen = foremen;
        _jobsites = jobsites;
        _vaultOptions = vaultOptions;
    }

    [McpServerTool(Name = "dispatch_task")]
    [Description("Dispatch a task to a named, hired Foreman. Returns a job id immediately -- the Foreman runs asynchronously. Use get_job_status to check on it later. Pass workorderPath to hand over a WORKORDER.md you wrote under Plans/<Jobsite>/<Feature>/; that claims the Foreman's one workorder slot.")]
    public string DispatchTask(
        [Description("The name of the hired Foreman to dispatch the task to.")] string foreman,
        [Description("A clear, self-contained description of the task for the Foreman to carry out.")] string task,
        [Description("Optional. The absolute path to the WORKORDER.md you wrote at <vaultRoot>/Plans/<Jobsite>/<Feature>/WORKORDER.md. Omit for an ordinary ad-hoc task.")] string? workorderPath = null)
    {
        // No workorder path never touches vaultOptions, so an unconfigured Vault
        // never blocks ad-hoc dispatch.
        if (string.IsNullOrWhiteSpace(workorderPath))
        {
            return _jobs.StartJob(foreman, task);
        }

        // Guard before calling the reader: VaultRoot is nullable, but Read's
        // vaultRoot param is non-nullable and gets Path.Combine'd.
        var vaultRoot = _vaultOptions.VaultRoot;
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            throw new InvalidOperationException(
                "dispatch_task cannot take a workorderPath: no Vault is configured. " +
                "Ask the Boss to configure a Vault root (first-run setup, or --vault-root) first. " +
                "Dispatching without a workorderPath still works.");
        }

        // Step 1: the file against itself (path segments vs. frontmatter).
        var parsed = _workorderReader.Read(workorderPath, vaultRoot);

        // Step 2: the file against the dispatch target.
        var target = _foremen.Find(foreman)
            ?? throw new InvalidOperationException(
                $"No Foreman named '{foreman}' is hired. Known Foremen: {string.Join(", ", _foremen.All().Select(f => f.Name))}.");

        if (!string.Equals(parsed.Jobsite, target.JobsiteName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workorder '{workorderPath}' is for jobsite '{parsed.Jobsite}', but Foreman '{target.Name}' is " +
                $"assigned to jobsite '{target.JobsiteName ?? "(none)"}'. Dispatch it to that jobsite's Foreman instead.");
        }

        // Step 3: SourceBranch's fallback chain lives here, not the reader: only
        // this side knows the Jobsite registry.
        var sourceBranch = parsed.SourceBranch
            ?? _jobsites.Find(parsed.Jobsite)?.DefaultBranch
            ?? "main";

        var workorder = new ActiveWorkorder(
            parsed.Feature,
            parsed.Jobsite,
            Path.Combine(vaultRoot, "Plans", parsed.Jobsite, parsed.Feature),
            sourceBranch,
            $"feature/{parsed.Feature}",
            DateTimeOffset.UtcNow);

        return _jobs.StartJob(foreman, task, workorder);
    }
}
