using ConstructionCrew.App.Tui;

namespace ConstructionCrew.Tests.AppTests;

public class InboxCommandTests
{
    [Fact]
    public void Label_Unread_MarksItWithAnAsterisk()
    {
        var item = new InboxItem("Frontend", "Sitewalk complete", new DateTimeOffset(2026, 8, 31, 14, 5, 0, TimeSpan.Zero));

        var label = InboxCommand.Label(item);

        Assert.StartsWith("*", label);
        Assert.Contains("Frontend", label);
        Assert.Contains("Sitewalk complete", label);
    }

    [Fact]
    public void Label_Read_HasNoAsterisk()
    {
        var item = new InboxItem("Frontend", "Sitewalk complete", DateTimeOffset.UtcNow, Read: true);

        var label = InboxCommand.Label(item);

        Assert.DoesNotContain("*", label);
    }

    [Fact]
    public void Label_LongBody_TruncatesTheFirstLinePreview()
    {
        var longText = new string('x', 200);
        var item = new InboxItem("Frontend", longText, DateTimeOffset.UtcNow);

        var label = InboxCommand.Label(item);

        Assert.Contains("...", label);
    }
}
