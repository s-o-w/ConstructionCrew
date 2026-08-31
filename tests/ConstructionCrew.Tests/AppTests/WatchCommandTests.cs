using ConstructionCrew.App.Tui;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers.Activity;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// /watch is the read-only half of "watch, then redirect": it moves what the
/// side panel is about without moving where the Boss's typing goes. Mirrors
/// DriveCommandsTests' shape, since the two commands share a parser and have to
/// agree about the same panel.
/// </summary>
public class WatchCommandTests
{
    private static DashboardState NewState() =>
        new() { HomeOfficeAddress = "http://localhost:1/", GcForemanName = "GC" };

    private static Func<string, ForemanConfig?> Roster(params (string Name, string Provider)[] members)
    {
        var byName = members.ToDictionary(
            m => m.Name,
            m => new ForemanConfig(m.Name, CrewRole.Foreman, m.Provider, "/tmp", "instructions.md", new Dictionary<string, string>()),
            StringComparer.OrdinalIgnoreCase);
        byName["GC"] = new ForemanConfig("GC", CrewRole.GC, "claude", "/tmp", "gc.md", new Dictionary<string, string>());
        return name => byName.GetValueOrDefault(name);
    }

    private static ForemanActivityReaders Readers() => ForemanActivityReaders.Default();

    // -- parsing ---------------------------------------------------------------

    [Theory]
    [InlineData("/watch Casey", "Casey")]
    [InlineData("  /watch   Casey  ", "Casey")]
    [InlineData("/WATCH Casey", "Casey")]
    [InlineData("/watch", "")]
    public void Apply_AcceptsTheVerbAndItsArgument(string command, string expectedTarget)
    {
        var state = NewState();

        Assert.Equal(BossCommandResult.Handled, WatchCommand.Apply(state, command, Roster(("Casey", "claude")), Readers()));

        // A bare verb clears rather than targets, so the state proves the parse.
        Assert.Equal(expectedTarget.Length == 0 ? null : expectedTarget, state.WatchedForeman);
    }

    /// <summary>A command that merely starts with the letters is not the verb.</summary>
    [Theory]
    [InlineData("/watchdog Casey")]
    [InlineData("/tasks")]
    [InlineData("watch Casey")]
    public void Apply_RejectsNearMisses(string command)
    {
        Assert.Equal(
            BossCommandResult.NotHandled,
            WatchCommand.Apply(NewState(), command, Roster(("Casey", "claude")), Readers()));
    }

    // -- state transitions -----------------------------------------------------

    /// <summary>
    /// The defining difference from /drive: the panel changes, the routing does
    /// not. The Boss keeps talking to GC while Casey's activity ticks alongside.
    /// </summary>
    [Fact]
    public void Watch_ShowsTheirActivityWithoutRedirectingTypedInput()
    {
        var state = NewState();

        WatchCommand.Apply(state, "/watch Casey", Roster(("Casey", "claude")), Readers());

        Assert.Equal("Casey", state.WatchedForeman);
        Assert.Equal("Casey", state.WatchSubject);

        // Still talking to GC: nothing about the drive target moved.
        Assert.Null(state.DrivenForeman);
        Assert.Same(state.Transcript, state.ActiveTranscript);
    }

    /// <summary>The canonical roster spelling, so the panel header and the registry lookup agree.</summary>
    [Fact]
    public void Watch_NormalizesToTheRosterName()
    {
        var state = NewState();

        WatchCommand.Apply(state, "/watch casey", Roster(("Casey", "claude")), Readers());

        Assert.Equal("Casey", state.WatchedForeman);
    }

    [Fact]
    public void Watch_OnTheAlreadyWatchedName_TogglesItOff()
    {
        var state = NewState();
        var roster = Roster(("Casey", "claude"));

        WatchCommand.Apply(state, "/watch Casey", roster, Readers());
        WatchCommand.Apply(state, "/watch Casey", roster, Readers());

        Assert.Null(state.WatchedForeman);
        Assert.Null(state.WatchSubject);
    }

    [Fact]
    public void BareWatch_ClearsAnActiveWatch()
    {
        var state = NewState();
        var roster = Roster(("Casey", "claude"));

        WatchCommand.Apply(state, "/watch Casey", roster, Readers());
        WatchCommand.Apply(state, "/watch", roster, Readers());

        Assert.Null(state.WatchedForeman);
    }

    /// <summary>
    /// Stale panel content is worse than none: the previous subject's git status
    /// must not sit under the new subject's name until the next refresh lands.
    /// </summary>
    [Fact]
    public void Watch_ClearsThePreviousSubjectsPanelContent()
    {
        var state = NewState();
        state.Activity = new ForemanActivitySnapshot("running: Bash", DateTimeOffset.UtcNow);

        WatchCommand.Apply(state, "/watch Casey", Roster(("Casey", "claude")), Readers());

        Assert.Null(state.Activity);
        Assert.Null(state.Passive);
    }

    [Fact]
    public void Watch_UnknownName_SaysSoAndSetsNoWatch()
    {
        var state = NewState();

        WatchCommand.Apply(state, "/watch Nobody", Roster(("Casey", "claude")), Readers());

        Assert.Null(state.WatchedForeman);
        Assert.True(state.Transcript[^1].IsError);
    }

    /// <summary>
    /// Copilot keeps its state in SQLite, not a readable JSONL transcript. The
    /// watch is refused rather than set: an always-empty panel would read as
    /// "this Foreman is doing nothing", which is a lie.
    /// </summary>
    [Fact]
    public void Watch_AnEngineWithNoReadableTranscript_IsRefusedRatherThanSetToBlank()
    {
        var state = NewState();

        WatchCommand.Apply(state, "/watch Dana", Roster(("Dana", "copilot")), Readers());

        Assert.Null(state.WatchedForeman);
        Assert.True(state.Transcript[^1].IsError);
        Assert.Contains("copilot", state.Transcript[^1].Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Driving implies watching: with no explicit watch, the panel follows
    /// whoever the Boss is driving, and an explicit watch overrides that.
    /// </summary>
    [Fact]
    public void WatchSubject_FallsBackToTheDrivenForeman()
    {
        var state = NewState();
        state.DrivenForeman = "Casey";

        Assert.Equal("Casey", state.WatchSubject);

        WatchCommand.Apply(state, "/watch Dana", Roster(("Casey", "claude"), ("Dana", "claude")), Readers());

        Assert.Equal("Dana", state.WatchSubject);
        // The override moved the panel, not the conversation.
        Assert.Equal("Casey", state.DrivenForeman);
    }

    /// <summary>
    /// Redirecting takes the panel with it. Leaving an earlier watch in place
    /// would describe one Foreman while the Boss types to another.
    /// </summary>
    [Fact]
    public void Drive_AfterAWatch_TakesThePanelOver()
    {
        var state = NewState();
        var roster = Roster(("Casey", "claude"), ("Dana", "claude"));

        WatchCommand.Apply(state, "/watch Dana", roster, Readers());
        DriveCommands.Apply(state, "/drive Casey", roster, []);

        Assert.Null(state.WatchedForeman);
        Assert.Equal("Casey", state.WatchSubject);
    }

    /// <summary>Leaving drive mode drops the panel entirely, watch override included.</summary>
    [Fact]
    public void Exit_ClearsBothTheDriveAndTheWatch()
    {
        var state = NewState();
        var roster = Roster(("Casey", "claude"), ("Dana", "claude"));

        DriveCommands.Apply(state, "/drive Casey", roster, []);
        WatchCommand.Apply(state, "/watch Dana", roster, Readers());
        DriveCommands.Apply(state, "/exit", roster, []);

        Assert.Null(state.DrivenForeman);
        Assert.Null(state.WatchedForeman);
        Assert.Null(state.WatchSubject);
    }
}
