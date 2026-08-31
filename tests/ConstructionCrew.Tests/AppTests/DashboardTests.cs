using ConstructionCrew.App.Tui;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.AppTests;

public class DashboardTests
{
    private static JobRecord Job(string id, JobStatus status) =>
        new(id, "Frontend", $"task {id}", status, DateTimeOffset.UtcNow, null, null);

    /// <summary>
    /// Four columns, in order, and a Parked job belongs to exactly one of them.
    /// Before Phase 7 a parked job vanished from the board entirely: it is neither
    /// Pending/Running, nor Completed, nor Failed.
    /// </summary>
    [Fact]
    public void TaskColumns_AreDoingParkedDoneFailed()
    {
        var columns = Dashboard.TaskColumns(
        [
            Job("pending", JobStatus.Pending),
            Job("running", JobStatus.Running),
            Job("parked", JobStatus.Parked),
            Job("done", JobStatus.Completed),
            Job("failed", JobStatus.Failed),
        ]);

        Assert.Equal(["doing", "parked", "done", "failed"], columns.Select(c => c.Title));

        Assert.Equal(["pending", "running"], columns[0].Jobs.Select(j => j.JobId));
        Assert.Equal(["parked"], columns[1].Jobs.Select(j => j.JobId));
        Assert.Equal(["done"], columns[2].Jobs.Select(j => j.JobId));
        Assert.Equal(["failed"], columns[3].Jobs.Select(j => j.JobId));
    }

    /// <summary>Every job lands in exactly one column -- no double-count, no dropped job.</summary>
    [Fact]
    public void TaskColumns_PartitionEveryJobExactlyOnce()
    {
        var all = new[]
        {
            Job("a", JobStatus.Pending),
            Job("b", JobStatus.Running),
            Job("c", JobStatus.Parked),
            Job("d", JobStatus.Completed),
            Job("e", JobStatus.Failed),
        };

        var placed = Dashboard.TaskColumns(all).SelectMany(c => c.Jobs).Select(j => j.JobId).ToList();

        Assert.Equal(all.Length, placed.Count);
        Assert.Equal(all.Length, placed.Distinct().Count());
    }

    /// <summary>
    /// Three roster states, not two. A parked Foreman is not busy (IsForemanBusy is
    /// false by design) but it is not free either -- it is blocked on the Boss, and
    /// rendering it as "idle" would hide the one thing the Boss has to act on.
    /// </summary>
    [Fact]
    public void StatusBadge_ParkedRendersDistinctlyFromBusyAndIdle()
    {
        var working = Dashboard.StatusBadge(busy: true, parked: false);
        var parked = Dashboard.StatusBadge(busy: false, parked: true);
        var idle = Dashboard.StatusBadge(busy: false, parked: false);

        Assert.Contains("working", working);
        Assert.Contains("parked", parked);
        Assert.Contains("idle", idle);
        Assert.NotEqual(idle, parked);
        Assert.NotEqual(working, parked);
    }

    /// <summary>
    /// /foreman was reachable only from /help, so the footer is where it gets
    /// discovered. The driving footer stays the reminder it was -- it is not a
    /// command list.
    /// </summary>
    [Fact]
    public void Footer_ListsForeman()
    {
        var footer = Dashboard.FooterFor(null);

        Assert.Contains("/foreman <Name>", footer);
        Assert.Contains("/drive <Name>", footer);
        Assert.DoesNotContain("<Foreman>", footer);

        var driving = Dashboard.FooterFor("Frontend");
        Assert.Contains("Frontend", driving);
        Assert.DoesNotContain("/foreman", driving);
    }

    /// <summary>A job that is genuinely running still wins over a stale parked flag.</summary>
    [Fact]
    public void StatusBadge_BusyWinsOverParked()
    {
        Assert.Equal(
            Dashboard.StatusBadge(busy: true, parked: false),
            Dashboard.StatusBadge(busy: true, parked: true));
    }
}
