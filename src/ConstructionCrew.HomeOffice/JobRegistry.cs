using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.HomeOffice;

/// <summary>
/// Tracks dispatched jobs. Every Start* method returns a job id immediately;
/// the actual run happens on a tracked background Task: dispatch_task and
/// spawn_worker must never block the caller's tool-calling turn.
/// </summary>
public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();

    /// <summary>
    /// foremanName -> jobId: is this Foreman busy, and with which job. Case-insensitive
    /// to match ForemanDirectory/LiveAgentRegistry. Every write here keys off the
    /// resolved ForemanConfig.Name, never the raw caller-supplied string.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _workorderSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// jobId -> ActiveWorkorder: which workorder a job logs against. Not cleared by
    /// ReleaseWorkorder: a pr-opened release frees the Foreman early, but the
    /// completing job still needs to know what it worked on.
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveWorkorder> _jobWorkorders = new();

    private readonly IForemanDirectory _foremen;
    private readonly IJobsiteDirectory _jobsiteDirectory;
    private readonly LiveAgentRegistry _liveAgents;
    private readonly IJobStatusSink _statusSink;
    private readonly IWorktreeManager _worktreeManager;
    private readonly JobRegistryRuntimeOptions _runtimeOptions;
    private readonly ICliProcessRunner _cliProcessRunner;
    private readonly HomeOfficeNotificationOptions _notificationOptions;
    private readonly IRunLogWriter _runLogWriter;
    private readonly IJobsLogWriter _jobsLogWriter;

    /// <summary>
    /// A TimeSpan cannot be a compile-time default parameter value, so the
    /// fallback lives here rather than on JobRegistryRuntimeOptions.AskGcTimeout.
    /// </summary>
    private static readonly TimeSpan DefaultAskGcTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Every (jobId, foreman) pair NotifyPrOpened was called with: lets a caller with no view of NotificationsCommand assert "notified exactly once".</summary>
    private readonly ConcurrentQueue<(string JobId, string ForemanName)> _prOpenedNotifications = new();

    /// <summary>
    /// <paramref name="liveAgents"/> must be the one shared instance (also used by
    /// the Boss loop), or GC ends up with two divergent conversations.
    /// <paramref name="cliProcessRunner"/> reuses the same instance already built
    /// for agentFactory and WorktreeManager, for the NotificationsCommand
    /// shell-out, rather than opening a second process-spawning seam.
    /// <paramref name="runLogWriter"/> and <paramref name="jobsLogWriter"/> live in
    /// Config, which this project doesn't reference, so only their Core interfaces
    /// are visible here.
    /// </summary>
    public JobRegistry(
        IForemanDirectory foremen,
        IJobsiteDirectory jobsiteDirectory,
        ILocalCliAgentFactory agentFactory,
        IJobStatusSink statusSink,
        LiveAgentRegistry liveAgents,
        string gcForemanName,
        IWorktreeManager worktreeManager,
        JobRegistryRuntimeOptions runtimeOptions,
        ICliProcessRunner cliProcessRunner,
        HomeOfficeNotificationOptions notificationOptions,
        IRunLogWriter runLogWriter,
        IJobsLogWriter jobsLogWriter)
    {
        _foremen = foremen;
        _jobsiteDirectory = jobsiteDirectory;
        _ = agentFactory; // Unused: every dispatch path now routes through LiveAgentRegistry.
        _statusSink = statusSink;
        _liveAgents = liveAgents;
        GcForemanName = gcForemanName;
        _worktreeManager = worktreeManager;
        _runtimeOptions = runtimeOptions;
        _cliProcessRunner = cliProcessRunner;
        _notificationOptions = notificationOptions;
        _runLogWriter = runLogWriter;
        _jobsLogWriter = jobsLogWriter;
    }

    /// <summary>The reserved name GC is hired under; AskGc resolves GC's config through it.</summary>
    public string GcForemanName { get; }

    /// <summary>
    /// Dispatches to a named, hired Foreman. Continuation-aware.
    ///
    /// <paramref name="workorder"/> null means an ordinary ad-hoc task that claims
    /// nothing and is never rejected as busy. Non-null claims the Foreman's one
    /// workorder slot and throws if already held. Does no parsing or validation --
    /// DispatchTaskTool hands in an already-validated value.
    /// </summary>
    public string StartJob(string foremanName, string task, ActiveWorkorder? workorder = null)
    {
        var config = FindForemanOrThrow(foremanName);
        // config.Name, not foremanName: keeps "Frontend" and "frontend" as one slot.
        return StartTrackedJob(
            config.Name,
            task,
            // Job id rides in the task text: the only channel a Foreman has to name
            // its own job back to ask_gc/file_sitrep. LocalCliAgent.ComposeInitialPrompt
            // renders instructions + "---" + message, so this always lands after the
            // instructions block.
            (jobId, onStarted, ct) =>
                _liveAgents.SendAsync(foremanName, config, WithJobId(jobId, task), ct, onStarted),
            workorder);
    }

    /// <summary>
    /// Spawns an ephemeral, unnamed Worker for one piece of work. Never
    /// continuation-aware: a Worker is a fresh one-shot run. Runs in the
    /// parent's engine unless overridden.
    ///
    /// The Worker gets its own git worktree cut from the parent's active
    /// workorder's feature branch, so two Workers on the same Jobsite never share
    /// a working tree. Opening it is awaited before the job id is minted: what
    /// stays backgrounded is the Worker's slow CLI turn, not the bounded
    /// `git worktree add`.
    /// </summary>
    public async Task<string> StartWorkerJob(
        string parentForemanName, string task, string? engineOverride, CancellationToken cancellationToken)
    {
        var parent = FindForemanOrThrow(parentForemanName);
        var providerId = string.IsNullOrWhiteSpace(engineOverride) ? parent.Provider : engineOverride;
        var shortId = Guid.NewGuid().ToString("n")[..6];
        var workerLabel = $"{parent.Name}/worker-{shortId}";

        // parent.Name, not parentForemanName: keyed off the canonical resolved
        // name, same as elsewhere.
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

        // WorkingDirectory is the worktree, NOT the parent's: that is the whole
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

    /// <summary>Asks a named Foreman a question mid-task. Synchronous: re-invokes the Foreman's persistent conversation and returns its answer, not a job id.</summary>
    public async Task<string> AskForeman(string foremanName, string question, CancellationToken cancellationToken)
    {
        var config = FindForemanOrThrow(foremanName);
        var result = await _liveAgents.SendAsync(foremanName, config, question, cancellationToken);
        return result.Succeeded ? result.StandardOutput : $"(Foreman '{foremanName}' errored answering: {result.StandardError})";
    }

    /// <summary>
    /// Escalates to the GC mid-job. Returns GC's answer if it arrives inside the
    /// timeout; otherwise parks <paramref name="jobId"/> and returns "parked:
    /// waiting on Boss" so the caller's turn ends instead of hanging on a human.
    ///
    /// Both ask_gc and a kind:"milestone" file_sitrep land here: one method, one
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

        // Not awaited directly: only this method's wait is bounded. A linked
        // CancellationTokenSource would cancel GC's in-flight turn on expiry and
        // break the resume path below.
        var reply = _liveAgents.SendAsync(GcForemanName, gcConfig, question, cancellationToken);

        try
        {
            return (await reply.WaitAsync(timeout, cancellationToken)).StandardOutput;
        }
        catch (TimeoutException)
        {
            var parkedAt = DateTimeOffset.UtcNow;
            Transition(jobId, JobStatus.Parked, null);

            // GC answering is the resume itself: no second timeout, no polling. If
            // GC never answers, the job stays Parked until the Boss redirects it.
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
    /// Raises the "PR is open" notification, called by FileSitrepTool's pr-opened
    /// branch right after ReleaseWorkorder(jobId). The Foreman opens the PR by
    /// shelling `gh`, which C# never observes, so this sitrep is the only way Home
    /// Office learns of it.
    ///
    /// Fires NotificationsCommand with {event} = "pr-opened"; a no-op when no
    /// command is configured.
    /// </summary>
    public void NotifyPrOpened(string jobId, string foremanName)
    {
        _prOpenedNotifications.Enqueue((jobId, foremanName));
        FireNotification("pr-opened", jobId, foremanName);
    }

    /// <summary>
    /// A milestone sitrep landing, called by FileSitrepTool's milestone branch
    /// after AskGc has already delivered it into GC's own conversation. That
    /// leaves the Boss with nothing to see -- this fires the same desktop
    /// notification pr-opened/parked already get, and publishes a synthetic
    /// JobRecord whose "milestone:" JobId prefix tells the Boss loop
    /// (Program.cs) to route it into DashboardState.Inbox rather than treat it
    /// as an ordinary tracked-or-untracked job transition.
    /// </summary>
    public void NotifyMilestone(string jobId, string foremanName, string summary)
    {
        FireNotification("milestone", jobId, foremanName);
        _statusSink.Publish(new JobRecord(
            JobId: $"milestone:{Guid.NewGuid()}",
            ForemanName: foremanName,
            Task: "milestone escalation",
            Status: JobStatus.Completed,
            CreatedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            Summary: summary));
    }

    /// <summary>
    /// Substitutes {event}/{jobId}/{foreman} into NotificationsCommand and shells
    /// it fire-and-forget through the shared ICliProcessRunner.
    ///
    /// Best-effort: a null/empty command spawns nothing (checked before any task
    /// starts), and a broken command can't block or fail the triggering
    /// transition. The inner catch is deliberately terminal, calling nothing that
    /// could itself throw unobserved on a thread-pool thread.
    /// </summary>
    private void FireNotification(string eventName, string jobId, string foremanName)
    {
        var template = _notificationOptions.NotificationsCommand;
        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        var command = template
            .Replace("{event}", eventName, StringComparison.Ordinal)
            .Replace("{jobId}", jobId, StringComparison.Ordinal)
            .Replace("{foreman}", foremanName, StringComparison.Ordinal);

        _ = Task.Run(async () =>
        {
            try
            {
                // Environment.CurrentDirectory: a housekeeping command (notify-send
                // and friends) has no need to run inside a Jobsite repo or the
                // Vault, and CliInvocation.WorkingDirectory is non-nullable.
                await _cliProcessRunner.RunAsync(
                    new CliInvocation("/bin/sh", ["-c", command], Environment.CurrentDirectory),
                    CancellationToken.None);
            }
            catch
            {
                // Deliberately empty and terminal: never worth surfacing an
                // unobserved task exception for a notification.
            }
        });
    }

    /// <summary>Every pr-opened notification raised so far. See <see cref="NotifyPrOpened"/>.</summary>
    internal IReadOnlyList<(string JobId, string ForemanName)> PrOpenedNotifications =>
        _prOpenedNotifications.ToList();

    public JobRecord? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    public IReadOnlyCollection<JobRecord> GetAllJobs() => _jobs.Values.OrderBy(j => j.CreatedAt).ToList();

    /// <summary>True if the Foreman or any Worker it spawned is running. Parked is not busy: a parked Foreman can still take a sitrep or a redirect.</summary>
    public bool IsForemanBusy(string foremanName) =>
        _jobs.Values.Any(j => j.Status is JobStatus.Pending or JobStatus.Running &&
                               (j.ForemanName.Equals(foremanName, StringComparison.OrdinalIgnoreCase) ||
                                j.ForemanName.StartsWith(foremanName + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>True if the Foreman (or a Worker) is parked waiting on the Boss. Disjoint from IsForemanBusy: parked is neither busy nor free.</summary>
    public bool IsForemanParked(string foremanName) =>
        _jobs.Values.Any(j => j.Status is JobStatus.Parked &&
                              (j.ForemanName.Equals(foremanName, StringComparison.OrdinalIgnoreCase) ||
                               j.ForemanName.StartsWith(foremanName + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Evicts a fired Foreman's cached live agent so a later re-hire under the same name starts clean.</summary>
    public void ForgetLiveAgent(string foremanName) => _liveAgents.Remove(foremanName);

    /// <summary>
    /// The engine and session id behind a name's live conversation, for the
    /// watcher. Null when nothing has been dispatched to that name yet.
    /// A pass-through so the TUI keeps talking to one registry, the same way it
    /// already asks this one <see cref="IsForemanBusy"/> rather than reaching
    /// past it into LiveAgentRegistry.
    /// </summary>
    public (string? SessionId, string Engine)? GetActivityInfo(string foremanName) =>
        _liveAgents.GetActivityInfo(foremanName);

    /// <summary>
    /// Frees the Foreman's workorder slot for <paramref name="jobId"/>, called
    /// when a PR opens, well before the job completes. Never touches
    /// _jobWorkorders: the job still needs its ActiveWorkorder when it finishes.
    ///
    /// Conditional on the slot still pointing at this job (<see
    /// cref="TryClearSlotIfOwnedBy"/>); a false result is a no-op, not an error.
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
    /// Removes the slot entry only if it's still present and still equals
    /// <paramref name="jobId"/>, atomically in one BCL call. A check-then-remove
    /// would leave a window where a stale release evicts a slot a NEXT job already
    /// claimed, since TryRemove(key) alone only checks the key exists.
    ///
    /// Shared by ReleaseWorkorder and the completion safety-net clear.
    /// </summary>
    internal bool TryClearSlotIfOwnedBy(string foremanName, string jobId) =>
        ((ICollection<KeyValuePair<string, string>>)_workorderSlots)
            .Remove(new KeyValuePair<string, string>(foremanName, jobId));

    /// <summary>
    /// The single atomic claim. GetOrAdd adds the key if absent and returns the
    /// value just added, or the already-present value: one call, no window for a
    /// second caller to observe an empty slot. A check-then-set could let two
    /// concurrent claims both see the slot empty.
    ///
    /// internal, not public: StartJob runs synchronously up to scheduling
    /// RunJobAsync, so no public path creates two genuinely concurrent claims; this
    /// has to be exercised directly (see the csproj's InternalsVisibleTo).
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

    /// <summary>Prepends the job id to the task text: the only channel a provider-agnostic CLI invocation has for the Foreman to name its own job back.</summary>
    private static string WithJobId(string jobId, string task) => $"ConstructionCrew job id: {jobId}\n\n{task}";

    /// <summary>
    /// <paramref name="run"/> receives the job id and an <c>onStarted</c> callback
    /// for LiveAgentRegistry.SendAsync, firing when the turn acquires the agent's
    /// semaphore: that's what stamps StartedAt and moves the job out of Pending.
    /// Queue time (CreatedAt -> StartedAt) is visible but never charged, which is
    /// why nothing else may transition a job to Running.
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
    /// Names the busy owner's feature for the rejection message only. The winner
    /// writes _jobWorkorders after its own GetOrAdd returns, so this lookup can
    /// (rarely) run first: that only changes the message wording, never the
    /// exclusivity GetOrAdd alone decides.
    /// </summary>
    private string DescribeInFlight(string? busyOwnerJobId) =>
        busyOwnerJobId is not null && _jobWorkorders.TryGetValue(busyOwnerJobId, out var inFlight)
            ? $"feature '{inFlight.Feature}' (job {busyOwnerJobId})"
            : $"job {busyOwnerJobId}";

    /// <summary>
    /// <paramref name="onCompleted"/> runs regardless of outcome: a Worker's
    /// live agent must be evicted on failure just as much as success.
    ///
    /// No transition to Running here: a job stays Pending until the per-Foreman
    /// semaphore is acquired inside <c>run</c>, firing the onStarted callback.
    /// Stamping Running up front would erase the queue time StartedAt exists to
    /// measure.
    /// </summary>
    private async Task RunJobAsync(string jobId, Func<CancellationToken, Task<CliRunResult>> run, Action? onCompleted)
    {
        try
        {
            var result = await run(CancellationToken.None);

            Transition(
                jobId,
                result.Succeeded ? JobStatus.Completed : JobStatus.Failed,
                result.Succeeded ? result.StandardOutput : result.StandardError,
                usage: result.Usage);
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
    /// GC's reply is the resume: folds the elapsed park interval into
    /// ParkedDuration and moves the job back to Running, only if it's still
    /// Parked. A job that reached Completed/Failed while parked is left alone --
    /// Transition itself doesn't block a terminal-status regression, so the guard
    /// lives here.
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
    /// <paramref name="startedAt"/> only lands on a transition into Running: the
    /// "dispatch began" stamp actual-hours accounting is built on; re-stamping it
    /// later would erase the queue-time signal. <paramref name="usage"/> arrives
    /// with the finished run and is never cleared by a later transition.
    ///
    /// This method's only real job is updating and publishing _jobs[jobId]. Every
    /// side-effect write below (run log, jobs.jsonl, notification) is isolated
    /// behind TryRunSideEffect: RunJobAsync's outer catch re-invokes
    /// Transition(..., Failed, ...) on ANY exception, so an unguarded I/O failure
    /// here would misreport a completed job as failed.
    /// </summary>
    private void Transition(
        string jobId, JobStatus status, string? summary, DateTimeOffset? startedAt = null, CliUsage? usage = null)
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
            Usage = usage ?? current.Usage,
        };

        _jobs[jobId] = updated;
        _statusSink.Publish(updated);

        if (status is JobStatus.Completed or JobStatus.Failed)
        {
            // _jobWorkorders, not _workorderSlots: a pr-opened release freed the
            // Foreman already, but this job still needs to know what it worked
            // on. An ad-hoc dispatch has no entry and logs nothing.
            if (_jobWorkorders.TryGetValue(jobId, out var workorder))
            {
                try
                {
                    TryRunSideEffect(() => _runLogWriter.Append(workorder.PlansFolder, updated), "RunLogWriter.Append");
                }
                finally
                {
                    _jobWorkorders.TryRemove(jobId, out _);
                }
            }

            // Safety net for a job that finished without filing a pr-opened sitrep.
            // Uses the same atomic helper as ReleaseWorkorder: a check-then-remove
            // here would reintroduce the same stale-release race.
            TryClearSlotIfOwnedBy(updated.ForemanName, jobId);
        }

        // Only fires entering Parked from elsewhere; re-parking an already-parked
        // job raises nothing.
        if (status is JobStatus.Parked && current.Status is not JobStatus.Parked)
        {
            TryRunSideEffect(() => FireNotification("parked", jobId, updated.ForemanName), "parked notification");
        }

        TryRunSideEffect(() => _jobsLogWriter.Append(updated), "state/jobs.jsonl append");
    }

    /// <summary>
    /// Runs one of Transition's side-effect writes, swallowing anything it throws.
    ///
    /// The nested try/catch is what makes this airtight: the inner catch can only
    /// run Console.Error.WriteLine, itself guarded by an empty catch. Nothing here
    /// can throw past it, so RunJobAsync's outer catch can never be triggered by a
    /// side-effect write, however badly it fails.
    /// </summary>
    private static void TryRunSideEffect(Action sideEffect, string label)
    {
        try
        {
            sideEffect();
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"ConstructionCrew: {label} failed: {ex.Message}");
            }
            catch
            {
                // Even error reporting must not propagate: the last line of defense.
            }
        }
    }
}
