using ConstructionCrew.App.Tui;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Phase 8a's completion-notice mechanism. JobRegistry deliberately exposes no
/// completion callback, so the Boss loop's only way to turn "the turn I dispatched
/// finished" into a transcript line is to match records drained off
/// IJobStatusSink against the ids it recorded at dispatch. These are the rules
/// that matching has to obey.
/// </summary>
public class PendingBossTurnsTests
{
    private static JobRecord Job(string id, JobStatus status, string? summary = "done", string foreman = "GC") =>
        new(id, foreman, "a task", status, DateTimeOffset.UtcNow, null, summary);

    [Fact]
    public void UntrackedJob_IsNeverAnnounced()
    {
        var pending = new PendingBossTurns();

        Assert.False(pending.TryTakeCompletion(Job("other", JobStatus.Completed), out _, out _));
    }

    /// <summary>
    /// The status check has to come before the removal. A Pending -> Running
    /// transition is published on the very same channel, and evicting the id there
    /// would silently swallow the completion notice that follows.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Parked)]
    public void NonTerminalTransition_LeavesTheJobPending(JobStatus status)
    {
        var pending = new PendingBossTurns();
        pending.Track("job-1", "GC");

        Assert.False(pending.TryTakeCompletion(Job("job-1", status), out _, out _));
        Assert.True(pending.IsPending("job-1"));
        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public void Completion_ProducesOneLineAndClearsThePendingId()
    {
        var pending = new PendingBossTurns();
        pending.Track("job-1", "GC");

        Assert.True(pending.TryTakeCompletion(Job("job-1", JobStatus.Completed, "the answer"), out var speaker, out var line));

        Assert.Equal("GC", speaker);
        Assert.Equal("GC", line.Speaker);
        Assert.Equal("the answer", line.Text);
        Assert.False(line.IsError);
        Assert.Equal(0, pending.Count);
    }

    /// <summary>A failed turn still reports: silence is indistinguishable from "still working".</summary>
    [Fact]
    public void Failure_ReportsAsAnErrorLine()
    {
        var pending = new PendingBossTurns();
        pending.Track("job-1", "GC");

        Assert.True(pending.TryTakeCompletion(Job("job-1", JobStatus.Failed, "boom"), out _, out var line));

        Assert.True(line.IsError);
        Assert.Equal("boom", line.Text);
    }

    [Fact]
    public void EmptySummary_StillSaysSomething()
    {
        var pending = new PendingBossTurns();
        pending.Track("job-1", "GC");
        pending.Track("job-2", "GC");

        Assert.True(pending.TryTakeCompletion(Job("job-1", JobStatus.Completed, ""), out _, out var completed));
        Assert.True(pending.TryTakeCompletion(Job("job-2", JobStatus.Failed, null), out _, out var failed));

        Assert.NotEmpty(completed.Text);
        Assert.NotEmpty(failed.Text);
    }

    /// <summary>The line belongs in the conversation the Boss addressed, not always GC's.</summary>
    [Fact]
    public void DrivenTurn_ReportsUnderTheDrivenForemansName()
    {
        var pending = new PendingBossTurns();
        pending.Track("job-1", "Frontend");

        Assert.True(pending.TryTakeCompletion(Job("job-1", JobStatus.Completed, "ok", "Frontend"), out var speaker, out var line));

        Assert.Equal("Frontend", speaker);
        Assert.Equal("Frontend", line.Speaker);
    }

    [Fact]
    public void SameRecordTwice_AnnouncesOnce()
    {
        var pending = new PendingBossTurns();
        pending.Track("job-1", "GC");
        var record = Job("job-1", JobStatus.Completed);

        Assert.True(pending.TryTakeCompletion(record, out _, out _));
        Assert.False(pending.TryTakeCompletion(record, out _, out _));
    }

    /// <summary>
    /// The Boss dispatches while other jobs complete on background threads, so
    /// Track and TryTakeCompletion genuinely overlap. Exactly one caller may ever
    /// win a given completion, and concurrent tracking must not lose ids.
    /// </summary>
    [Fact]
    public async Task ConcurrentCompletion_HasExactlyOneWinnerPerJob()
    {
        const int jobCount = 200;
        const int racersPerJob = 4;

        var pending = new PendingBossTurns();
        var records = Enumerable.Range(0, jobCount).Select(i => Job($"job-{i}", JobStatus.Completed)).ToList();

        foreach (var record in records)
        {
            pending.Track(record.JobId, "GC");
        }

        var barrier = new Barrier(racersPerJob + 1);
        var wins = new int[jobCount];

        // Dispatch keeps happening while completions are being drained: these ids
        // are tracked mid-race and must all survive it.
        var dispatchedDuringRace = Enumerable.Range(0, jobCount).Select(i => $"late-{i}").ToList();
        var dispatcher = Task.Run(() =>
        {
            barrier.SignalAndWait();
            foreach (var jobId in dispatchedDuringRace)
            {
                pending.Track(jobId, "GC");
            }
        });

        var racers = Enumerable.Range(0, racersPerJob).Select(racer => Task.Run(() =>
        {
            barrier.SignalAndWait();

            for (var i = 0; i < jobCount; i++)
            {
                if (pending.TryTakeCompletion(records[i], out _, out _))
                {
                    Interlocked.Increment(ref wins[i]);
                }
            }
        })).ToArray();

        await Task.WhenAll([dispatcher, .. racers]).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(wins, w => Assert.Equal(1, w));
        Assert.All(dispatchedDuringRace, id => Assert.True(pending.IsPending(id)));
        Assert.Equal(jobCount, pending.Count);
    }
}
