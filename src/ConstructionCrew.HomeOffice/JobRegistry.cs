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
    private readonly IJobsiteDirectory _jobsiteDirectory;
    private readonly LiveAgentRegistry _liveAgents;
    private readonly IJobStatusSink _statusSink;
    private readonly IWorktreeManager _worktreeManager;
    private readonly JobRegistryRuntimeOptions _runtimeOptions;

    /// <summary>
    /// A TimeSpan cannot be a compile-time default parameter value, so the
    /// fallback lives here rather than on JobRegistryRuntimeOptions.AskGcTimeout.
    /// </summary>
    private static readonly TimeSpan DefaultAskGcTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Every (jobId, foreman) NotifyPrOpened was called with. Phase 10 replaces the
    /// body with the NotificationsCommand hook; the record stays, because that is
    /// what makes "notified exactly once" testable without an extra seam.
    /// </summary>
    private readonly ConcurrentQueue<(string JobId, string ForemanName)> _prOpenedNotifications = new();

    /// <summary>
    /// <paramref name="liveAgents"/> is injected, never self-constructed: exactly
    /// one LiveAgentRegistry exists per process, shared with the Boss loop, or GC
    /// ends up with two divergent conversations. <paramref name="worktreeManager"/>
    /// is the same shared instance HomeOfficeHost registers -- one instance, two
    /// consumers.
    ///
    /// <paramref name="agentFactory"/> is still taken (and still the factory the
    /// composition root builds LiveAgentRegistry from) even though every dispatch
    /// path now routes through LiveAgentRegistry: its position anchors the
    /// parameter order later phases append to.
    /// </summary>
    public JobRegistry(
        IForemanDirectory foremen,
        IJobsiteDirectory jobsiteDirectory,
        ILocalCliAgentFactory agentFactory,
        IJobStatusSink statusSink,
        LiveAgentRegistry liveAgents,
        string gcForemanName,
        IWorktreeManager worktreeManager,
        JobRegistryRuntimeOptions runtimeOptions)
    {
        _foremen = foremen;
        _jobsiteDirectory = jobsiteDirectory;
        _ = agentFactory;
        _statusSink = statusSink;
        _liveAgents = liveAgents;
        GcForemanName = gcForemanName;
        _worktreeManager = worktreeManager;
        _runtimeOptions = runtimeOptions;
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
        return StartTrackedJob(
            config.Name,
            task,
            // The job id rides in on the task text, not in a separate channel: it is
            // the only way the Foreman can name its own job back to ask_gc /
            // file_sitrep. LocalCliAgent.ComposeInitialPrompt renders
            // instructions + "---" + message, so this line always lands after the
            // instructions block, never ahead of it.
            (jobId, onStarted, ct) =>
                _liveAgents.SendAsync(foremanName, config, WithJobId(jobId, task), ct, onStarted),
            workorder);
    }

    /// <summary>
    /// A Foreman spawning an ephemeral, unnamed Worker for one piece of work.
    /// Never continuation-aware -- a Worker is a fresh one-shot run, not a
    /// persistent identity. Runs in the parent's engine unless overridden.
    ///
    /// The Worker gets its OWN git worktree, cut from the parent's active
    /// workorder's feature branch, so two Workers on the same Jobsite never share
    /// a working tree. Opening it is awaited before the job id is minted -- a
    /// deliberate, narrow exception to "return immediately, track in background":
    /// what stays backgrounded is the slow part (the Worker's CLI turn), not
    /// `git worktree add`, a bounded metadata operation.
    /// </summary>
    public async Task<string> StartWorkerJob(
        string parentForemanName, string task, string? engineOverride, CancellationToken cancellationToken)
    {
        var parent = FindForemanOrThrow(parentForemanName);
        var providerId = string.IsNullOrWhiteSpace(engineOverride) ? parent.Provider : engineOverride;
        var shortId = Guid.NewGuid().ToString("n")[..6];
        var workerLabel = $"{parent.Name}/worker-{shortId}";

        // parent.Name, not parentForemanName: the slot is keyed off the canonical
        // resolved name everywhere else too.
        if (!_workorderSlots.TryGetValue(parent.Name, out var parentJobId) ||
            !_jobWorkorders.TryGetValue(parentJobId, out var activeWorkorder))
        {
            throw new InvalidOperationException(
                $"Foreman '{parent.Name}' has no active workorder, so there is no Feature or feature " +
                "branch to cut a Worker's worktree from. Dispatch a workorder to them first, then spawn Workers.");
        }

        var repoPath = _jobsiteDirectory.Find(activeWorkorder.Jobsite)?.RepoPath;
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            throw new InvalidOperationException(
                $"Foreman '{parent.Name}' holds a workorder for jobsite '{activeWorkorder.Jobsite}', " +
                "but no such jobsite is configured (or it has no repo path) -- a Worker's worktree needs one.");
        }

        var workerBranch = $"{activeWorkorder.FeatureBranch}-worker-{shortId}";
        var worktreePath = Path.Combine(
            _runtimeOptions.StateDirectory, "worktrees", activeWorkorder.Jobsite, $"worker-{shortId}");

        var handle = await _worktreeManager.OpenAsync(
            repoPath, activeWorkorder.FeatureBranch, workerBranch, worktreePath, cancellationToken);

        // WorkingDirectory is the worktree, NOT the parent's -- that is the whole
        // point of the isolation.
        var workerConfig = parent with
        {
            Name = workerLabel,
            Provider = providerId,
            WorkingDirectory = handle.WorktreePath,
        };

        return StartTrackedJob(
            workerLabel,
            task,
            (jobId, onStarted, ct) =>
                _liveAgents.SendAsync(workerLabel, workerConfig, WithJobId(jobId, task), ct, onStarted),
            worktreePath: handle.WorktreePath,
            // A Worker is one-shot: its cached agent must never be continued by a
            // later Worker that happens to draw the same label.
            onCompleted: () => _liveAgents.Remove(workerLabel));
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

    /// <summary>
    /// A Foreman or Worker escalating to the GC (and through it, the Boss) mid-job.
    /// Returns GC's answer if it comes back inside the timeout; otherwise parks
    /// <paramref name="jobId"/> and returns "parked: waiting on Boss" so the caller's
    /// turn ends cleanly instead of hanging on a human.
    ///
    /// Both ask_gc and a kind:"milestone" file_sitrep land here -- one method, one
    /// GC conversation.
    /// </summary>
    public async Task<string> AskGc(string jobId, string question, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !_jobs.ContainsKey(jobId))
        {
            throw new InvalidOperationException(
                $"No job '{jobId}' is tracked. Pass the job id you were given at the top of your task text; " +
                "there is deliberately no fallback to 'your most recent job'.");
        }

        var gcConfig = FindForemanOrThrow(GcForemanName);
        var timeout = _runtimeOptions.AskGcTimeout ?? DefaultAskGcTimeout;

        // Started, but deliberately NOT awaited directly: only this method's own
        // wait is bounded. A linked CancellationTokenSource would cancel GC's
        // in-flight turn on expiry and destroy the resume path below.
        var reply = _liveAgents.SendAsync(GcForemanName, gcConfig, question, cancellationToken);

        try
        {
            return (await reply.WaitAsync(timeout, cancellationToken)).StandardOutput;
        }
        catch (TimeoutException)
        {
            var parkedAt = DateTimeOffset.UtcNow;
            Transition(jobId, JobStatus.Parked, null);

            // GC answering IS the resume -- no second timeout, no polling. If GC
            // never answers, the job stays Parked until the Boss redirects the
            // Foreman or the job's own turn ends.
            _ = reply.ContinueWith(
                antecedent =>
                {
                    // Observe a faulted send so it never surfaces as an unobserved
                    // task exception; a failed GC turn still resumes the job.
                    _ = antecedent.Exception;
                    ResumeFromPark(jobId, parkedAt);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return "parked: waiting on Boss";
        }
    }

    /// <summary>
    /// Phase 10 fills this in (it shells the configured NotificationsCommand). It
    /// exists now because file_sitrep's pr-opened path calls it, and the call site
    /// is what Phase 7 is specifying. Recording the call is the whole body for now.
    /// </summary>
    public void NotifyPrOpened(string jobId, string foremanName) =>
        _prOpenedNotifications.Enqueue((jobId, foremanName));

    /// <summary>Every pr-opened notification raised so far. See <see cref="NotifyPrOpened"/>.</summary>
    internal IReadOnlyList<(string JobId, string ForemanName)> PrOpenedNotifications =>
        _prOpenedNotifications.ToList();

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

    /// <summary>
    /// True if the named Foreman (or one of its Workers) is parked waiting on the
    /// Boss. Deliberately disjoint from IsForemanBusy: a parked Foreman is not
    /// busy, but it is not free either, and the roster has to show the difference.
    /// </summary>
    public bool IsForemanParked(string foremanName) =>
        _jobs.Values.Any(j => j.Status is JobStatus.Parked &&
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
    /// The clear is conditional on the slot still pointing at this exact job (see
    /// <see cref="TryClearSlotIfOwnedBy"/>). A false result is a no-op, not an
    /// error: the slot had already moved on.
    /// </summary>
    public void ReleaseWorkorder(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return;
        }

        TryClearSlotIfOwnedBy(job.ForemanName, jobId);
    }

    /// <summary>
    /// Removes the slot entry if, and only if, the key is still present AND its
    /// current value still equals <paramref name="jobId"/> -- atomically, in one
    /// BCL call. A check-then-remove (read the slot, compare, TryRemove by key)
    /// leaves a window in which a stale release can evict a slot a NEXT job has
    /// already claimed, because TryRemove(key) only checks the key exists.
    ///
    /// Shared by ReleaseWorkorder and (Phase 10) the completion safety-net clear.
    /// </summary>
    internal bool TryClearSlotIfOwnedBy(string foremanName, string jobId) =>
        ((ICollection<KeyValuePair<string, string>>)_workorderSlots)
            .Remove(new KeyValuePair<string, string>(foremanName, jobId));

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

    /// <summary>
    /// Prepends the job id to a task's text. The Foreman has to be able to name its
    /// own job back to ask_gc / file_sitrep, and the task text is the only channel
    /// a provider-agnostic CLI invocation has.
    /// </summary>
    private static string WithJobId(string jobId, string task) => $"ConstructionCrew job id: {jobId}\n\n{task}";

    /// <summary>
    /// <paramref name="run"/> receives the job id (to stamp into the message it
    /// sends) and an <c>onStarted</c> callback to hand to
    /// LiveAgentRegistry.SendAsync: it fires when the turn actually acquires that
    /// agent's semaphore, which is what stamps StartedAt and moves the job out of
    /// Pending. Queue time (CreatedAt -> StartedAt) is visible but never charged --
    /// which is exactly why nothing else may transition a job to Running.
    /// </summary>
    private string StartTrackedJob(
        string displayName,
        string task,
        Func<string, Action, CancellationToken, Task<CliRunResult>> run,
        ActiveWorkorder? workorder = null,
        string? worktreePath = null,
        Action? onCompleted = null)
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

        var job = new JobRecord(
            jobId, displayName, task, JobStatus.Pending, DateTimeOffset.UtcNow, null, null,
            WorktreePath: worktreePath);
        _jobs[jobId] = job;
        _statusSink.Publish(job);

        _ = RunJobAsync(
            jobId,
            ct => run(jobId, () => Transition(jobId, JobStatus.Running, null, startedAt: DateTimeOffset.UtcNow), ct),
            onCompleted);

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

    /// <summary>
    /// <paramref name="onCompleted"/> runs however the job ends -- a Worker's live
    /// agent has to be evicted on a failure just as much as on a success.
    ///
    /// There is deliberately NO transition to Running here. A job stays Pending
    /// until agent dispatch actually begins -- i.e. until the per-Foreman semaphore
    /// is acquired inside <c>run</c>, which is what fires the onStarted callback
    /// StartTrackedJob threaded in. Stamping Running up front would erase the queue
    /// time the whole StartedAt design exists to measure.
    /// </summary>
    private async Task RunJobAsync(string jobId, Func<CancellationToken, Task<CliRunResult>> run, Action? onCompleted)
    {
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
        finally
        {
            onCompleted?.Invoke();
        }
    }

    /// <summary>
    /// GC's reply landing is the resume. Folds the elapsed park interval into
    /// ParkedDuration and puts the job back to Running -- but only if it is STILL
    /// Parked. A job that reached Completed or Failed while parked is left exactly
    /// as it is: no transition, no throw, no error log. Transition itself does not
    /// block a terminal-status regression, so the guard has to live here.
    /// </summary>
    private void ResumeFromPark(string jobId, DateTimeOffset parkedAt)
    {
        if (!_jobs.TryGetValue(jobId, out var current) || current.Status is not JobStatus.Parked)
        {
            return;
        }

        _jobs[jobId] = current with { ParkedDuration = current.ParkedDuration + (DateTimeOffset.UtcNow - parkedAt) };
        Transition(jobId, JobStatus.Running, null);
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
