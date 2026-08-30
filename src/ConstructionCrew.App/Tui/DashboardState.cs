namespace ConstructionCrew.App.Tui;

public enum TuiView
{
    Chat,
    Tasks,
    Stub,
}

public sealed record TranscriptLine(string Speaker, string Text, bool IsError = false);

public sealed class DashboardState
{
    public TuiView View { get; set; } = TuiView.Chat;

    public string? StubLabel { get; set; }

    public List<TranscriptLine> Transcript { get; } = new();

    public required string HomeOfficeAddress { get; init; }
}
