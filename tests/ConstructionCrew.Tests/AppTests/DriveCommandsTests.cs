using ConstructionCrew.App.Tui;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Phase 8b's routing rules: /drive switches which conversation Boss input goes
/// to, /exit switches it back, and driving a busy Foreman says so using StartedAt
/// rather than CreatedAt.
/// </summary>
public class DriveCommandsTests
{
    private static DashboardState NewState() =>
        new() { HomeOfficeAddress = "http://localhost:1/", GcForemanName = "GC" };

    private static ForemanConfig Foreman(string name) =>
        new(name, CrewRole.Foreman, "claude", "/tmp", "instructions.md", new Dictionary<string, string>());

    private static Func<string, ForemanConfig?> Roster(params string[] names)
    {
        var byName = names.ToDictionary(n => n, Foreman, StringComparer.OrdinalIgnoreCase);
        byName["GC"] = new ForemanConfig("GC", CrewRole.GC, "claude", "/tmp", "gc.md", new Dictionary<string, string>());
        return name => byName.GetValueOrDefault(name);
    }

    private static JobRecord Job(
        string foreman, JobStatus status, DateTimeOffset createdAt, DateTimeOffset? startedAt, string task = "build the thing") =>
        new("job-1", foreman, task, status, createdAt, null, null, startedAt);

    // -- parsing ---------------------------------------------------------------

    [Theory]
    [InlineData("/drive Frontend", "Frontend")]
    [InlineData("  /drive   Frontend  ", "Frontend")]
    [InlineData("/DRIVE Frontend", "Frontend")]
    [InlineData("/drive", "")]
    [InlineData("/drive   ", "")]
    public void TryParseDrive_AcceptsTheVerbAndItsArgument(string command, string expected)
    {
        Assert.True(DriveCommands.TryParseDrive(command, out var target));
        Assert.Equal(expected, target);
    }

    /// <summary>A command that merely starts with the letters is not the verb.</summary>
    [Theory]
    [InlineData("/driveby Frontend")]
    [InlineData("/tasks")]
    [InlineData("drive Frontend")]
    public void TryParseDrive_RejectsNearMisses(string command)
    {
        Assert.False(DriveCommands.TryParseDrive(command, out _));
    }

    // -- state transitions -----------------------------------------------------

    [Fact]
    public void Drive_RoutesToThatForemanAndSwitchesTheOutputPane()
    {
        var state = NewState();
        state.Transcript.Add(new TranscriptLine("Boss", "hello GC"));

        var result = DriveCommands.Apply(state, "/drive Frontend", Roster("Frontend"), []);

        Assert.Equal(BossCommandResult.Handled, result);
        Assert.Equal("Frontend", state.DrivenForeman);
        Assert.Equal(TuiView.Chat, state.View);

        // The pane is Frontend's transcript now, and GC's is untouched behind it.
        Assert.NotSame(state.Transcript, state.ActiveTranscript);
        Assert.Single(state.Transcript);
    }

    /// <summary>The canonical roster name is the transcript key, not what was typed.</summary>
    [Fact]
    public void Drive_NormalisesTheForemanName()
    {
        var state = NewState();

        DriveCommands.Apply(state, "/drive frontend", Roster("Frontend"), []);

        Assert.Equal("Frontend", state.DrivenForeman);
    }

    [Fact]
    public void Exit_WhileDriving_ReturnsToGcRatherThanQuitting()
    {
        var state = NewState();
        DriveCommands.Apply(state, "/drive Frontend", Roster("Frontend"), []);
        state.ActiveTranscript.Add(new TranscriptLine("Boss", "hello Frontend"));

        var result = DriveCommands.Apply(state, "/exit", Roster("Frontend"), []);

        Assert.Equal(BossCommandResult.Handled, result);
        Assert.Null(state.DrivenForeman);
        Assert.Null(state.Passive);
        Assert.Same(state.Transcript, state.ActiveTranscript);
    }

    [Fact]
    public void Exit_WhenNotDriving_Quits()
    {
        var state = NewState();

        Assert.Equal(BossCommandResult.Quit, DriveCommands.Apply(state, "/exit", Roster(), []));
        Assert.Equal(BossCommandResult.Quit, DriveCommands.Apply(state, "quit", Roster(), []));
        Assert.Equal(BossCommandResult.Quit, DriveCommands.Apply(state, "exit", Roster(), []));
    }

    /// <summary>Re-driving a Foreman comes back to the same transcript, not a fresh one.</summary>
    [Fact]
    public void DriveExitDrive_KeepsTheForemansTranscript()
    {
        var state = NewState();
        var roster = Roster("Frontend");

        DriveCommands.Apply(state, "/drive Frontend", roster, []);
        state.ActiveTranscript.Add(new TranscriptLine("Boss", "first"));
        var lineCount = state.ActiveTranscript.Count;

        DriveCommands.Apply(state, "/exit", roster, []);
        DriveCommands.Apply(state, "/drive Frontend", roster, []);

        // The banner line the second /drive appends is the only addition.
        Assert.Equal(lineCount + 1, state.ActiveTranscript.Count);
        Assert.Contains(state.ActiveTranscript, l => l.Text == "first");
    }

    [Fact]
    public void Drive_UnknownForeman_SaysSoAndDoesNotChangeRouting()
    {
        var state = NewState();

        var result = DriveCommands.Apply(state, "/drive Nobody", Roster("Frontend"), []);

        Assert.Equal(BossCommandResult.Handled, result);
        Assert.Null(state.DrivenForeman);
        Assert.Contains(state.Transcript, l => l.IsError && l.Text.Contains("Nobody"));
    }

    [Fact]
    public void Drive_WithNoArgument_SaysSoAndDoesNotChangeRouting()
    {
        var state = NewState();

        Assert.Equal(BossCommandResult.Handled, DriveCommands.Apply(state, "/drive", Roster("Frontend"), []));
        Assert.Null(state.DrivenForeman);
        Assert.Contains(state.Transcript, l => l.IsError);
    }

    /// <summary>
    /// Driving GC is what the Boss is already doing. A second "GC" pane would be
    /// the divergent-conversation bug in UI form, so it lands back on the one GC
    /// transcript instead.
    /// </summary>
    [Fact]
    public void Drive_Gc_ReturnsToTheOneGcTranscript()
    {
        var state = NewState();
        DriveCommands.Apply(state, "/drive Frontend", Roster("Frontend"), []);

        DriveCommands.Apply(state, "/drive GC", Roster("Frontend"), []);

        Assert.Null(state.DrivenForeman);
        Assert.Same(state.Transcript, state.ActiveTranscript);
    }

    [Fact]
    public void Apply_LeavesEveryOtherCommandAlone()
    {
        var state = NewState();

        Assert.Equal(BossCommandResult.NotHandled, DriveCommands.Apply(state, "/tasks", Roster(), []));
        Assert.Equal(BossCommandResult.NotHandled, DriveCommands.Apply(state, "hello GC", Roster(), []));
    }

    // -- queued notice ---------------------------------------------------------

    [Fact]
    public void QueuedNotice_IsNullForAFreeForeman()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(DriveCommands.QueuedNotice([], "Frontend"));
        Assert.Null(DriveCommands.QueuedNotice([Job("Frontend", JobStatus.Completed, now, now)], "Frontend"));
        Assert.Null(DriveCommands.QueuedNotice([Job("Backend", JobStatus.Running, now, now)], "Frontend"));
    }

    /// <summary>
    /// The clock is StartedAt, not CreatedAt. A job created at 09:00 that only got
    /// the Foreman's semaphore at 11:30 has been running since 11:30, and quoting
    /// 09:00 would be the pre-Phase-1a lie.
    /// </summary>
    [Fact]
    public void QueuedNotice_QuotesStartedAtNotCreatedAt()
    {
        var createdAt = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
        var startedAt = new DateTimeOffset(2026, 8, 29, 11, 30, 0, TimeSpan.Zero);

        var notice = DriveCommands.QueuedNotice(
            [Job("Frontend", JobStatus.Running, createdAt, startedAt, "Phase 3 of the plan")], "Frontend");

        Assert.NotNull(notice);
        Assert.Contains("queued behind", notice);
        Assert.Contains("Phase 3 of the plan", notice);
        Assert.Contains($"started {startedAt.ToLocalTime():HH\\:mm}", notice);
        Assert.DoesNotContain($"started {createdAt.ToLocalTime():HH\\:mm}", notice);
    }

    /// <summary>
    /// A job that has not been given the semaphore yet has no start time to quote.
    /// It says "not started yet" rather than inventing one -- which is exactly the
    /// distinction StartedAt exists to make.
    /// </summary>
    [Fact]
    public void QueuedNotice_SaysNotStartedWhenTheJobIsStillPending()
    {
        var createdAt = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

        var notice = DriveCommands.QueuedNotice(
            [Job("Frontend", JobStatus.Pending, createdAt, startedAt: null)], "Frontend");

        Assert.NotNull(notice);
        Assert.Contains("not started yet", notice);
        Assert.Contains($"queued {createdAt.ToLocalTime():HH\\:mm}", notice);
    }

    /// <summary>A Worker's job counts as its parent Foreman being busy.</summary>
    [Fact]
    public void QueuedNotice_CountsAWorkersJobAgainstItsForeman()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.NotNull(DriveCommands.QueuedNotice([Job("Frontend/worker-abc123", JobStatus.Running, now, now)], "Frontend"));
        // ...but a different Foreman whose name merely starts the same does not.
        Assert.Null(DriveCommands.QueuedNotice([Job("FrontendOps", JobStatus.Running, now, now)], "Frontend"));
    }

    [Fact]
    public void Drive_AppendsTheQueuedNoticeToThatForemansTranscript()
    {
        var state = NewState();
        var startedAt = new DateTimeOffset(2026, 8, 29, 11, 30, 0, TimeSpan.Zero);

        DriveCommands.Apply(
            state,
            "/drive Frontend",
            Roster("Frontend"),
            [Job("Frontend", JobStatus.Running, startedAt, startedAt)]);

        Assert.Contains(state.ActiveTranscript, l => l.Text.Contains("queued behind"));
        Assert.Empty(state.Transcript);
    }
}
