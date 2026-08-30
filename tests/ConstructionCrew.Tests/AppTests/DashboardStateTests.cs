using ConstructionCrew.App.Tui;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Drive mode's transcript routing. The invariant that matters: there is exactly
/// one GC conversation, and driving a Foreman must never fork it.
/// </summary>
public class DashboardStateTests
{
    private static DashboardState NewState() =>
        new() { HomeOfficeAddress = "http://localhost:1/", GcForemanName = "GC" };

    [Fact]
    public void NotDriving_ActiveTranscriptIsTheGcTranscript()
    {
        var state = NewState();

        Assert.Null(state.DrivenForeman);
        Assert.Same(state.Transcript, state.ActiveTranscript);
    }

    /// <summary>
    /// GC's name and null both resolve to the one GC list. A completion notice for
    /// a GC job and a line the Boss typed to GC have to land in the same place, or
    /// the pane shows half a conversation.
    /// </summary>
    [Fact]
    public void GcName_ResolvesToTheSameListAsNull()
    {
        var state = NewState();

        Assert.Same(state.Transcript, state.TranscriptFor(null));
        Assert.Same(state.Transcript, state.TranscriptFor("GC"));
        Assert.Same(state.Transcript, state.TranscriptFor("gc"));
    }

    [Fact]
    public void EachForemanKeepsItsOwnTranscript()
    {
        var state = NewState();

        state.TranscriptFor("Frontend").Add(new TranscriptLine("Boss", "hello"));
        state.TranscriptFor("Backend").Add(new TranscriptLine("Boss", "hi"));

        Assert.Single(state.TranscriptFor("Frontend"));
        Assert.Single(state.TranscriptFor("Backend"));
        Assert.Empty(state.Transcript);
        Assert.NotSame(state.TranscriptFor("Frontend"), state.TranscriptFor("Backend"));
    }

    /// <summary>Roster lookups are case-insensitive everywhere else; so is this.</summary>
    [Fact]
    public void ForemanTranscriptLookupIsCaseInsensitive()
    {
        var state = NewState();

        Assert.Same(state.TranscriptFor("Frontend"), state.TranscriptFor("frontend"));
    }

    [Fact]
    public void DrivenForeman_SwitchesWhichTranscriptIsActive()
    {
        var state = NewState();
        state.Transcript.Add(new TranscriptLine("Boss", "for GC"));

        state.DrivenForeman = "Frontend";
        state.ActiveTranscript.Add(new TranscriptLine("Boss", "for Frontend"));

        Assert.Single(state.ActiveTranscript);
        Assert.Single(state.Transcript);

        state.DrivenForeman = null;
        Assert.Same(state.Transcript, state.ActiveTranscript);
    }
}
