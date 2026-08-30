using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.CoreTests;

/// <summary>
/// Proves Phase 1a's JobRecord change compiles and behaves, through the public
/// constructor only. It proves nothing about JobRegistry.Transition -- that stays
/// private at this phase, and RunJobAsync still transitions to Running with no
/// StartedAt. The end-to-end claim is Phase 7's.
/// </summary>
public class JobRecordTests
{
    [Fact]
    public void StartedAt_RoundTripsThroughTheConstructor()
    {
        var started = DateTimeOffset.UtcNow;

        var job = new JobRecord(
            "job-1",
            "Frontend",
            "build the thing",
            JobStatus.Running,
            DateTimeOffset.UtcNow,
            null,
            null,
            StartedAt: started);

        Assert.Equal(started, job.StartedAt);
    }

    [Fact]
    public void OmittingStartedAt_LeavesItNull()
    {
        var job = new JobRecord("job-1", "Frontend", "task", JobStatus.Pending, DateTimeOffset.UtcNow, null, null);

        Assert.Null(job.StartedAt);
        Assert.Equal(TimeSpan.Zero, job.ParkedDuration);
        Assert.Null(job.WorktreePath);
        Assert.Null(job.Usage);
    }

    [Fact]
    public void WithExpression_SetsStartedAtWithoutTouchingAnythingElse()
    {
        var job = new JobRecord("job-1", "Frontend", "task", JobStatus.Pending, DateTimeOffset.UtcNow, null, null);
        var started = DateTimeOffset.UtcNow;

        var running = job with { Status = JobStatus.Running, StartedAt = started };

        Assert.Equal(started, running.StartedAt);
        Assert.Equal(job.CreatedAt, running.CreatedAt);
        Assert.Null(job.StartedAt);
    }

    [Fact]
    public void ParkedIsARealStatus()
    {
        var job = new JobRecord("job-1", "Frontend", "task", JobStatus.Pending, DateTimeOffset.UtcNow, null, null);

        var parked = job with { Status = JobStatus.Parked, ParkedDuration = TimeSpan.FromMinutes(3) };

        Assert.Equal(JobStatus.Parked, parked.Status);
        Assert.Equal(TimeSpan.FromMinutes(3), parked.ParkedDuration);
    }
}
