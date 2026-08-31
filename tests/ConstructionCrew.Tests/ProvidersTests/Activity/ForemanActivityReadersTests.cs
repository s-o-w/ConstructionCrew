using ConstructionCrew.Providers.Activity;

namespace ConstructionCrew.Tests.ProvidersTests.Activity;

public class ForemanActivityReadersTests
{
    [Fact]
    public void For_Claude_ResolvesTheClaudeReader()
    {
        Assert.IsType<ClaudeActivityReader>(ForemanActivityReaders.Default().For("claude"));
    }

    [Fact]
    public void For_Codex_ResolvesTheCodexReader()
    {
        Assert.IsType<CodexActivityReader>(ForemanActivityReaders.Default().For("codex"));
    }

    /// <summary>
    /// Copilot keeps its state in a SQLite data.db, not a flat JSONL file: a
    /// different mechanism, not a missing case. Null is what lets the TUI say so
    /// instead of setting a watch that would only ever render blank.
    /// </summary>
    [Theory]
    [InlineData("copilot")]
    [InlineData("gemini")]
    [InlineData("something-else")]
    [InlineData("")]
    [InlineData(null)]
    public void For_AnEngineWithNoTranscriptReader_IsNull(string? providerId)
    {
        Assert.Null(ForemanActivityReaders.Default().For(providerId));
    }

    /// <summary>Provider ids are matched the same case-insensitive way the roster matches them everywhere else.</summary>
    [Fact]
    public void For_MatchesTheProviderIdCaseInsensitively()
    {
        Assert.NotNull(ForemanActivityReaders.Default().For("Claude"));
    }
}
