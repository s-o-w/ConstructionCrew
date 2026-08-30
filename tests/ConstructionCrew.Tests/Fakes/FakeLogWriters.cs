using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>
/// Records every RUN-LOG.md append, optionally writing through to a real writer,
/// and optionally throwing (to prove a failing run-log write can never convert a
/// Completed job into a Failed one).
///
/// <see cref="WaitForAppends"/> exists because JobRegistry.Transition PUBLISHES the
/// job's new status BEFORE it performs any side-effect write: a test that waited on
/// the status sink alone would race the very write it is asserting on. Signalling
/// happens after the inner write and after recording, so a satisfied wait means the
/// write really finished.
/// </summary>
public sealed class FakeRunLogWriter : IRunLogWriter
{
    private readonly IRunLogWriter? _inner;
    private readonly SemaphoreSlim _appended = new(0);

    public FakeRunLogWriter(IRunLogWriter? inner = null)
    {
        _inner = inner;
    }

    public ConcurrentQueue<(string PlansFolder, JobRecord Job)> Appends { get; } = new();

    /// <summary>When set, every Append throws it -- Transition must swallow it whole.</summary>
    public Exception? ThrowOnAppend { get; set; }

    public void Append(string plansFolder, JobRecord job)
    {
        try
        {
            _inner?.Append(plansFolder, job);

            if (ThrowOnAppend is not null)
            {
                throw ThrowOnAppend;
            }
        }
        finally
        {
            Appends.Enqueue((plansFolder, job));
            _appended.Release();
        }
    }

    /// <summary>Waits until <paramref name="count"/> appends have completed, or fails the wait.</summary>
    public async Task WaitForAppends(int count, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        for (var i = 0; i < count; i++)
        {
            await _appended.WaitAsync(cts.Token);
        }
    }
}

/// <summary>
/// The state/jobs.jsonl sibling of <see cref="FakeRunLogWriter"/>. Its append is
/// the LAST statement in JobRegistry.Transition, which makes waiting on it the one
/// reliable "this transition has finished all of its side effects" barrier a test
/// has.
/// </summary>
public sealed class FakeJobsLogWriter : IJobsLogWriter
{
    private readonly IJobsLogWriter? _inner;
    private readonly SemaphoreSlim _appended = new(0);

    public FakeJobsLogWriter(IJobsLogWriter? inner = null)
    {
        _inner = inner;
    }

    public ConcurrentQueue<JobRecord> Appends { get; } = new();

    /// <summary>When set, every Append throws it -- Transition must swallow it whole.</summary>
    public Exception? ThrowOnAppend { get; set; }

    public void Append(JobRecord job)
    {
        try
        {
            _inner?.Append(job);

            if (ThrowOnAppend is not null)
            {
                throw ThrowOnAppend;
            }
        }
        finally
        {
            Appends.Enqueue(job);
            _appended.Release();
        }
    }

    /// <summary>Waits until <paramref name="count"/> appends have completed, or fails the wait.</summary>
    public async Task WaitForAppends(int count, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        for (var i = 0; i < count; i++)
        {
            await _appended.WaitAsync(cts.Token);
        }
    }
}
