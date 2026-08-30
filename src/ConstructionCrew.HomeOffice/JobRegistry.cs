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

    /// <summary>
    /// foremanName -> jobId. Answers "is this Foreman busy with a workorder, and
    /// which job holds it". Case-insensitive to match ForemanDirectory and
    /// LiveAgentRegistry, both of which key by Foreman name the same way; every
    /// write path here still keys off the resolved canonical ForemanConfig.Name,
    /// never the raw caller-supplied string.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _workorderSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// jobId -> ActiveWorkorder. Answers "which specific job's workorder do I log
    /// against". Deliberately NOT cleared by ReleaseWorkorder: a pr-opened release
    /// frees the Foreman to take new work long before the job itself completes,
    /// and the completing job still has to know what it was working on.
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveWorkorder> _jobWorkorders = new();

    private readonly IForemanDirectory _foremen;
    private readonly LiveAgentRegistry _liveAgents;
    private readonly ILocalCliAgentFactory _agentFactory;
    private readonly IJobStatusSink _statusSink;

    /// <summary>
    /// <paramref name="liveAgents"/> is injected, never self-constructed: exactly
    /// one LiveAgentRegistry exists per process, shared with the Boss loop, or GC
    /// ends up with two divergent conversations.
    /// </summary>
    public JobRegistry(
        IForemanDirectory foremen,
        ILocalCliAgentFactory agentFactory,
        IJobStatusSink statusSink,
        LiveAgentRegistry liveAgents,
        string gcForemanName)
    {
        _foremen = foremen;
        _agentFactory = agentFactory;
        _statusSink = statusSink;
        _liveAgents = liveAgents;
        GcForemanName = gcForemanName;
    }

    /// <summary>The reserved name GC is hired under. Phase 7's AskGc resolves GC's own config through it.</summary>
    public string GcForemanName { get; }

    /// <summary>
    /// GC (or another Foreman) dispatching to a named, hired Foreman.
    /// Continuation-aware.
    ///
    /// <paramref name="workorder"/> is null for an ordinary ad-hoc task, which
    /// claims nothing and can never be rejected as busy. A non-null workorder
    /// claims the Foreman's one workorder slot, and throws if it is already
    /// held. This method does no parsing and no path validation -- DispatchTaskTool
    /// hands it an already-validated typed value.
    /// </summary>
    public string StartJob(string foremanName, string task, ActiveWorkorder? workorder = null)
    {
        var config = FindForemanOrThrow(foremanName);
        // config.Name, not foremanName: the slot is keyed off the canonical,
        // resolved name so "Frontend" and "frontend" are one Foreman.
        return StartTrackedJob(config.Name, task, ct => _liveAgents.SendAsync(foremanName, config, task, ct), workorder);
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

    /// <summary>
    /// True if the named Foreman has a job running directly, or any worker job it
    /// spawned is running. Parked is deliberately NOT busy -- a parked Foreman is
    /// waiting on the Boss and can still take a sitrep or a redirect.
    /// </summary>
    public bool IsForemanBusy(string foremanName) =>
        _jobs.Values.Any(j => j.Status is JobStatus.Pending or JobStatus.Running &&
                               (j.ForemanName.Equals(foremanName, StringComparison.OrdinalIgnoreCase) ||
                                j.ForemanName.StartsWith(foremanName + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Evicts a fired Foreman's cached live agent so a later re-hire under the same name starts clean.</summary>
    public void ForgetLiveAgent(string foremanName) => _liveAgents.Remove(foremanName);

    /// <summary>
    /// Frees the Foreman holding <paramref name="jobId"/>'s workorder slot, so it
    /// can accept new work immediately -- called when a PR opens, well before the
    /// job itself completes. Never touches _jobWorkorders: the job still needs its
    /// ActiveWorkorder when it finishes.
    ///
    /// The clear is conditional on the slot still pointing at this exact job. The
    /// KeyValuePair overload of TryRemove is what makes that atomic: a plain
    /// read-then-remove could evict a slot a NEXT job had already claimed in
    /// between.
    /// </summary>
    public void ReleaseWorkorder(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return;
        }

        _workorderSlots.TryRemove(new KeyValuePair<string, string>(job.ForemanName, jobId));
    }

    /// <summary>
    /// The single atomic claim. GetOrAdd adds the key if absent and returns the
    /// value just added, or returns the already-present value -- one call, with no
    /// window in between for a second caller to observe an empty slot. A
    /// check-then-set (or TryAdd-then-read) would let two concurrent claims for the
    /// same Foreman both see the slot empty, or would need an unbounded retry loop
    /// to cope with the slot being vacated between the failed add and the read.
    ///
    /// internal, not public: there is no public path that creates two genuinely
    /// concurrent claims against the same in-process dictionary, because StartJob
    /// runs synchronously up to the point it schedules RunJobAsync. The claim has
    /// to be exercised directly. See ConstructionCrew.HomeOffice.csproj's
    /// InternalsVisibleTo.
    /// </summary>
    internal bool TryClaimWorkorderSlot(
        string foremanName, string jobId, ActiveWorkorder workorder, out string? busyOwnerJobId)
    {
        var ownerJobId = _workorderSlots.GetOrAdd(foremanName, jobId);

        if (ownerJobId != jobId)
        {
            busyOwnerJobId = ownerJobId;
            return false;
        }

        _jobWorkorders[jobId] = workorder;
        busyOwnerJobId = null;
        return true;
    }

    /// <summary>The ActiveWorkorder a job claimed, or null. Survives ReleaseWorkorder by design.</summary>
    internal ActiveWorkorder? GetJobWorkorder(string jobId) => _jobWorkorders.GetValueOrDefault(jobId);

    /// <summary>The job id currently holding a Foreman's workorder slot, or null when it is free.</summary>
    internal string? GetWorkorderSlotOwner(string foremanName) => _workorderSlots.GetValueOrDefault(foremanName);

    private ForemanConfig FindForemanOrThrow(string foremanName) =>
        _foremen.Find(foremanName)
            ?? throw new InvalidOperationException(
                $"No Foreman named '{foremanName}' is hired. Known Foremen: {string.Join(", ", _foremen.All().Select(f => f.Name))}.");

    private string StartTrackedJob(
        string displayName,
        string task,
        Func<CancellationToken, Task<CliRunResult>> run,
        ActiveWorkorder? workorder = null)
    {
        var jobId = Guid.NewGuid().ToString("n");

        // The claim happens before the JobRecord exists: a rejected claim must
        // leave no trace of the job at all.
        if (workorder is not null && !TryClaimWorkorderSlot(displayName, jobId, workorder, out var busyOwnerJobId))
        {
            throw new InvalidOperationException(
                $"Foreman '{displayName}' already holds a workorder: {DescribeInFlight(busyOwnerJobId)}. " +
                "Wait for it to finish (or for its PR to open) before dispatching another workorder to them.");
        }

        var job = new JobRecord(jobId, displayName, task, JobStatus.Pending, DateTimeOffset.UtcNow, null, null);
        _jobs[jobId] = job;
        _statusSink.Publish(job);

        _ = RunJobAsync(jobId, run);

        return jobId;
    }

    /// <summary>
    /// Names the feature the busy owner is on, for the rejection message only.
    /// The winner writes _jobWorkorders strictly after its own GetOrAdd returns,
    /// so this lookup can (rarely) run first -- that changes the wording of the
    /// message, never the exclusivity, which GetOrAdd alone decides.
    /// </summary>
    private string DescribeInFlight(string? busyOwnerJobId) =>
        busyOwnerJobId is not null && _jobWorkorders.TryGetValue(busyOwnerJobId, out var inFlight)
            ? $"feature '{inFlight.Feature}' (job {busyOwnerJobId})"
            : $"job {busyOwnerJobId}";

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

    /// <summary>
    /// <paramref name="startedAt"/> only ever lands on a transition into Running --
    /// it is the "agent dispatch began" stamp actual-hours accounting is built on,
    /// and re-stamping it on a later transition would erase the queue-time signal.
    /// </summary>
    private void Transition(string jobId, JobStatus status, string? summary, DateTimeOffset? startedAt = null)
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
            StartedAt = status is JobStatus.Running ? startedAt ?? current.StartedAt : current.StartedAt,
        };

        _jobs[jobId] = updated;
        _statusSink.Publish(updated);
    }
}
