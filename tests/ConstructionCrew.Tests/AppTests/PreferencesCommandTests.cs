using ConstructionCrew.App.Tui;

namespace ConstructionCrew.Tests.AppTests;

public class PreferencesCommandTests
{
    private const string RealShape = """
        # Crew preferences

        Standing preferences for the crew. Every Foreman and the GC read this file before starting work, and use it as the tiebreaker when more than one approach would do.

        ## Reviewer engines

        Which engine reviews which kind of work. Uncomment and edit to state a preference.

        <!-- Prefer codex for reviewing C# changes; prefer claude for reviewing docs and plans. -->

        ## Conventions

        House rules the crew should follow without being told again.

        <!-- Commit messages: one short imperative subject line, no trailing period, no emoji. -->
        <!-- Ask instead of guessing when a change touches a public API, a config schema, or anything outside the jobsite's own folders. -->

        ---

        An empty section means no preference: use your own judgement.

        """;

    [Fact]
    public void AppendUnderConventions_RealShape_InsertsAboveTrailingSeparator()
    {
        var updated = PreferencesCommand.AppendUnderConventions(RealShape, "Prefer tabs over spaces.");

        var separatorIndex = updated.IndexOf("\n---", StringComparison.Ordinal);
        var insertedIndex = updated.IndexOf("Prefer tabs over spaces.", StringComparison.Ordinal);

        Assert.True(insertedIndex >= 0);
        Assert.True(insertedIndex < separatorIndex);
        Assert.Contains("An empty section means no preference", updated);
    }

    [Fact]
    public void AppendUnderConventions_NoSeparatorFound_AppendsAtTheEndRatherThanLosingIt()
    {
        var content = "# Crew preferences\n\nNo separator here.";

        var updated = PreferencesCommand.AppendUnderConventions(content, "New rule.");

        Assert.Contains("New rule.", updated);
        Assert.EndsWith("New rule." + Environment.NewLine, updated);
    }
}
