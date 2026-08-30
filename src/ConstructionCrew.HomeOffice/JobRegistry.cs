using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.HomeOffice;

/// <summary>
/// Tracks dispatched jobs. Every Start* method returns a job id immediately;
/// the actual run happens on a tracked background Task -- dispatch_task and
/// spawn_worker must never block the caller's tool-calling turn.
/// </summary>
public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();
    private readonly IForemanDirectory _foremen;
    private readonly LiveAgentRegistry _liveAgents;
    private readonly ILocalCliAgentFactory _agentFactory;
    private readonly IJobStatusSink _statusSink;

    public JobRegistry(IForemanDirectory foremen, ILocalCliAgentFactory agentFactory, IJobStatusSink statusSink)
    {
        _foremen = foremen;
        _agentFactory = agentFactory;
        _statusSink = statusSink;
        _liveAgents = new LiveAgentRegistry(agentFactory);
    }

    /// <summary>GC (or another Foreman) dispatching to a named, hired Foreman. Continuation-aware.</summary>
    public string StartJob(string foremanName, string task)
    {
        var config = FindForemanOrThrow(foremanName);
        return StartTrackedJob(foremanName, task, ct => _liveAgents.SendAsync(foremanName, config, task, ct));
    }

    /// <summary>
    /// A Foreman spawning an ephemeral, unnamed Worker for one piece of work.
    /// Never continuation-aware -- a Worker is a fresh one-shot run, not a
    /// persistent identity. Runs in the parent's engine unless overridden.
    /// </summary>
    public string StartWorkerJob(string parentForemanName, string task, string? engineOverride)
    {
        var parent = FindForemanOrThrow(parentForemanName);
        var providerId = string.IsNullOrWhiteSpace(engineOverride) ? parent.Provider : engineOverride;
        var shortId = Guid.NewGuid().ToString("n")[..6];
        var workerLabel = $"{parentForemanName}/worker-{shortId}";
        var workerConfig = parent with { Name = workerLabel, Provider = providerId };

        return StartTrackedJob(workerLabel, task, ct => _agentFactory.Create(workerConfig).SendAsync(task, ct));
    }

    /// <summary>
    /// A Worker (or anyone) asking a named Foreman a question mid-task. Synchronous
    /// from the caller's point of view -- this re-invokes the Foreman's own
    /// persistent conversation and returns its answer directly, not a job id.
    /// </summary>
    public async Task<string> AskForeman(string foremanName, string question, CancellationToken cancellationToken)
    {
        var config = FindForemanOrThrow(foremanName);
        var result = await _liveAgents.SendAsync(foremanName, config, question, cancellationToken);
        return result.Succeeded ? result.StandardOutput : $"(Foreman '{foremanName}' errored answering: {result.StandardError})";
    }

    public JobRecord? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    public IReadOnlyCollection<JobRecord> GetAllJobs() => _jobs.Values.OrderBy(j => j.CreatedAt).ToList();

    /// <summary>True if the named Foreman has a job running directly, or any worker job it spawned is running.</summary>
    public bool IsForemanBusy(string foremanName) =>
        _jobs.Values.Any(j => j.Status is JobStatus.Pending or JobStatus.Running &&
                               (j.ForemanName.Equals(foremanName, StringComparison.OrdinalIgnoreCase) ||
                                j.ForemanName.StartsWith(foremanName + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Evicts a fired Foreman's cached live agent so a later re-hire under the same name starts clean.</summary>
    public void ForgetLiveAgent(string foremanName) => _liveAgents.Remove(foremanName);

    private ForemanConfig FindForemanOrThrow(string foremanName) =>
        _foremen.Find(foremanName)
            ?? throw new InvalidOperationException(
                $"No Foreman named '{foremanName}' is hired. Known Foremen: {string.Join(", ", _foremen.All().Select(f => f.Name))}.");

    private string StartTrackedJob(string displayName, string task, Func<CancellationToken, Task<CliRunResult>> run)
    {
        var jobId = Guid.NewGuid().ToString("n");
        var job = new JobRecord(jobId, displayName, task, JobStatus.Pending, DateTimeOffset.UtcNow, null, null);
        _jobs[jobId] = job;
        _statusSink.Publish(job);

        _ = RunJobAsync(jobId, run);

        return jobId;
    }

    private async Task RunJobAsync(string jobId, Func<CancellationToken, Task<CliRunResult>> run)
    {
        Transition(jobId, JobStatus.Running, null);

        try
        {
            var result = await run(CancellationToken.None);

            Transition(
                jobId,
                result.Succeeded ? JobStatus.Completed : JobStatus.Failed,
                result.Succeeded ? result.StandardOutput : result.StandardError);
        }
        catch (Exception ex)
        {
            Transition(jobId, JobStatus.Failed, ex.Message);
        }
    }

    private void Transition(string jobId, JobStatus status, string? summary)
    {
        if (!_jobs.TryGetValue(jobId, out var current))
        {
            return;
        }

        var updated = current with
        {
            Status = status,
            Summary = summary ?? current.Summary,
            CompletedAt = status is JobStatus.Completed or JobStatus.Failed ? DateTimeOffset.UtcNow : current.CompletedAt,
        };

        _jobs[jobId] = updated;
        _statusSink.Publish(updated);
    }
}
