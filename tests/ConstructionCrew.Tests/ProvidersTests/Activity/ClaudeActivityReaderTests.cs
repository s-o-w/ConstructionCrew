using System.Text.Json;
using ConstructionCrew.Providers.Activity;

namespace ConstructionCrew.Tests.ProvidersTests.Activity;

/// <summary>
/// Every fixture line below is the real entry shape read off a live Claude Code
/// transcript on 2026-08-31 (outer type/timestamp/sessionId/cwd, an
/// assistant message.content array of text / tool_use / thinking blocks),
/// trimmed of content but not reshaped.
/// </summary>
public class ClaudeActivityReaderTests : IDisposable
{
    private const string SessionId = "6f0d1e0e-1b7a-4c33-9a24-2f2c1f5f1a10";
    private const string Cwd = @"C:\Users\crew\PROJECTS\Lighthouse";

    private readonly string _projectsRoot =
        Path.Combine(Path.GetTempPath(), "cc-activity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_projectsRoot))
            {
                Directory.Delete(_projectsRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The colon, the backslashes, the dot inside an account name and a space
    /// inside a folder name all collapse to a dash. Each case is a real
    /// directory observed under ~/.claude/projects, not a guess.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\shawn.weekly", "C--Users-shawn-weekly")]
    [InlineData(@"C:\Users\shawn.weekly\MyObsidianVault", "C--Users-shawn-weekly-MyObsidianVault")]
    [InlineData(@"C:\Users\shawn.weekly\PROJECTS\sds-bsd", "C--Users-shawn-weekly-PROJECTS-sds-bsd")]
    [InlineData(
        @"C:\Users\shawn.weekly\PROJECTS\BSD Training Substation 385",
        "C--Users-shawn-weekly-PROJECTS-BSD-Training-Substation-385")]
    public void EncodeClaudeProjectPath_MatchesTheRealDirectoriesOnDisk(string cwd, string expected)
    {
        Assert.Equal(expected, ClaudeActivityReader.EncodeClaudeProjectPath(cwd));
    }

    /// <summary>A trailing separator is not part of the name the CLI encoded.</summary>
    [Fact]
    public void EncodeClaudeProjectPath_IgnoresATrailingSeparator()
    {
        Assert.Equal(
            "C--Users-shawn-weekly-MyObsidianVault",
            ClaudeActivityReader.EncodeClaudeProjectPath(@"C:\Users\shawn.weekly\MyObsidianVault\"));
    }

    [Fact]
    public void Read_LastEntryIsAnAnswer_ReturnsThatText()
    {
        WriteTranscript(
            AssistantText("2026-08-31T18:52:30.000Z", "Older answer."),
            AssistantToolUse("2026-08-31T18:52:37.934Z", "Bash"),
            AssistantText("2026-08-31T18:52:49.730Z", "All 239 tests pass."));

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Error);
        Assert.Equal("All 239 tests pass.", snapshot.Summary);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-31T18:52:49.730Z"),
            snapshot.At!.Value.ToUniversalTime());
    }

    /// <summary>
    /// The state the Boss most wants to see: mid-turn, nothing said yet, a tool
    /// running. A tail with no assistant text at all must still say something.
    /// </summary>
    [Fact]
    public void Read_TailIsToolActivityOnly_StillDescribesWhatIsRunning()
    {
        WriteTranscript(
            AssistantToolUse("2026-08-31T18:52:30.000Z", "Read"),
            AssistantToolUse("2026-08-31T18:52:37.934Z", "Bash"));

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.Equal("running: Bash", snapshot!.Summary);
        Assert.Null(snapshot.Error);
    }

    /// <summary>
    /// Blocks within one entry are in the order they happened, so the newest is
    /// the last: an entry that answered and then called a tool is running the
    /// tool now, not still talking.
    /// </summary>
    [Fact]
    public void Read_EntryThatAnsweredThenCalledATool_ReportsTheTool()
    {
        WriteTranscript(
            """
            {"type":"assistant","timestamp":"2026-08-31T18:52:37.934Z","sessionId":"SESSION","cwd":"CWD",
             "message":{"role":"assistant","content":[
               {"type":"thinking","thinking":"","signature":"Ev0G"},
               {"type":"text","text":"Let me check the tests."},
               {"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"dotnet test"}}]}}
            """);

        Assert.Equal("running: Bash", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    /// <summary>thinking blocks are frequently empty and never describe work; an entry holding only one is skipped for an older, useful entry.</summary>
    [Fact]
    public void Read_ThinkingOnlyEntry_FallsBackToTheLastRealActivity()
    {
        WriteTranscript(
            AssistantToolUse("2026-08-31T18:52:30.000Z", "Grep"),
            """
            {"type":"assistant","timestamp":"2026-08-31T18:52:40.000Z","sessionId":"SESSION","cwd":"CWD",
             "message":{"role":"assistant","content":[{"type":"thinking","thinking":"","signature":"Ev0G"}]}}
            """);

        Assert.Equal("running: Grep", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    /// <summary>
    /// An append can always leave a half-written last line behind, and the read
    /// window can always start mid-line. Neither is an error; both are skipped
    /// for the last entry that does parse.
    /// </summary>
    [Fact]
    public void Read_TrailingHalfWrittenLine_StillReportsTheLastGoodEntry()
    {
        var path = WriteTranscript(AssistantText("2026-08-31T18:52:49.730Z", "All 239 tests pass."));
        File.AppendAllText(path, """{"type":"assistant","timestamp":"2026-08-31T18:53:0""");

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.Equal("All 239 tests pass.", snapshot!.Summary);
        Assert.Null(snapshot.Error);
    }

    /// <summary>Best-effort by contract: this runs behind the Boss's dashboard, so a missing file reports, it never throws.</summary>
    [Fact]
    public void Read_NoTranscriptOnDisk_ReportsAnErrorRatherThanThrowing()
    {
        Directory.CreateDirectory(_projectsRoot);

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Error);
    }

    /// <summary>No id means the Foreman's first turn has not reported one yet -- there is no conversation to look up, and that is not an error.</summary>
    [Fact]
    public void Read_WithoutASessionId_ReturnsNull()
    {
        Assert.Null(NewReader().Read("", Cwd));
    }

    /// <summary>
    /// The encoding is reverse-engineered, so a path shape this machine has
    /// never produced could encode differently. A session id is a GUID, so
    /// finding the file by name cannot pick the wrong conversation.
    /// </summary>
    [Fact]
    public void Read_TranscriptUnderAnUnexpectedlyEncodedDirectory_IsStillFound()
    {
        var directory = Path.Combine(_projectsRoot, "some-other-encoding");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, SessionId + ".jsonl"),
            AssistantText("2026-08-31T18:52:49.730Z", "Found anyway.") + "\n");

        Assert.Equal("Found anyway.", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    /// <summary>The panel is 38 columns wide; a long answer must arrive already clipped, not clip the layout.</summary>
    [Fact]
    public void Read_ALongAnswer_ComesBackTruncated()
    {
        WriteTranscript(AssistantText("2026-08-31T18:52:49.730Z", new string('x', 400)));

        var summary = NewReader().Read(SessionId, Cwd)!.Summary;

        Assert.True(summary.Length <= 123, $"Expected a clipped summary, got {summary.Length} chars.");
        Assert.EndsWith("...", summary);
    }

    /// <summary>
    /// Only the tail is read, never the whole file: real transcripts run to
    /// megabytes and this refreshes behind every redraw.
    /// </summary>
    [Fact]
    public void Read_AFileLargerThanTheTailWindow_ReadsOnlyTheEndOfIt()
    {
        var filler = string.Concat(
            Enumerable.Repeat(AssistantText("2026-08-31T18:00:00.000Z", new string('f', 900)) + "\n", 400));
        var path = TranscriptPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, filler + AssistantText("2026-08-31T18:52:49.730Z", "The newest line.") + "\n");

        Assert.True(new FileInfo(path).Length > 64 * 1024, "Fixture must exceed the tail window to be meaningful.");
        Assert.Equal("The newest line.", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    /// <summary>The engine holds the file open for append the whole time a turn is in flight -- exactly when the Boss looks at it.</summary>
    [Fact]
    public void Read_WhileTheEngineHoldsTheFileOpenForWriting_StillReads()
    {
        var path = WriteTranscript(AssistantText("2026-08-31T18:52:49.730Z", "Mid-turn."));

        using var writerHoldingItOpen = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.Equal("Mid-turn.", snapshot!.Summary);
        Assert.Null(snapshot.Error);
    }

    private ClaudeActivityReader NewReader() => new(_projectsRoot);

    private string TranscriptPath() => Path.Combine(
        _projectsRoot, ClaudeActivityReader.EncodeClaudeProjectPath(Cwd), SessionId + ".jsonl");

    private string WriteTranscript(params string[] entries)
    {
        var path = TranscriptPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(entries.Select(e => Flatten(e) + "\n")));
        return path;
    }

    /// <summary>JSONL is one entry per line; the fixtures above are written multi-line only so they stay readable.</summary>
    private static string Flatten(string entry) =>
        string.Join(' ', entry.Split('\n').Select(l => l.Trim()))
            .Replace("SESSION", SessionId, StringComparison.Ordinal)
            .Replace("CWD", Cwd.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal);

    // Concatenated rather than interpolated: these entries end in a run of
    // closing braces that an interpolated raw string cannot hold as content.
    private static string AssistantText(string timestamp, string text) =>
        Entry(timestamp, """{"type":"text","text":""" + JsonSerializer.Serialize(text) + "}");

    private static string AssistantToolUse(string timestamp, string toolName) =>
        Entry(
            timestamp,
            """{"type":"tool_use","id":"toolu_01","name":""" + JsonSerializer.Serialize(toolName) +
            ""","input":{"command":"x"}}""");

    private static string Entry(string timestamp, string contentBlock) =>
        """{"type":"assistant","timestamp":""" + JsonSerializer.Serialize(timestamp) +
        ""","sessionId":"SESSION","cwd":"CWD","message":{"role":"assistant","content":[""" +
        contentBlock + "]}}";
}
