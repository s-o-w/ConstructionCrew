using System.Text.Json;
using ConstructionCrew.Providers.Activity;

namespace ConstructionCrew.Tests.ProvidersTests.Activity;

/// <summary>
/// Fixtures below are trimmed copies of real rollout entries captured on this
/// machine on 2026-08-31: every entry is {timestamp, type, payload}, tools
/// arrive as response_item/function_call or response_item/custom_tool_call with
/// the tool in payload.name, and the assistant's prose arrives as
/// event_msg/agent_message with payload.message.
/// </summary>
public class CodexActivityReaderTests : IDisposable
{
    private const string SessionId = "01a059d3-3db0-7240-8898-5335ece39750";
    private const string Cwd = @"C:\Users\crew\PROJECTS\Lighthouse";

    private readonly string _codexHome =
        Path.Combine(Path.GetTempPath(), "cc-codex-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_codexHome))
            {
                Directory.Delete(_codexHome, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Read_LastEntryIsAnAgentMessage_ReturnsThatText()
    {
        WriteRollout(SessionMeta(), AgentMessage("2026-08-31T21:56:55.000Z", "Picking up the plan first."));

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.Null(snapshot!.Error);
        Assert.Equal("Picking up the plan first.", snapshot.Summary);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-31T21:56:55.000Z"),
            snapshot.At!.Value.ToUniversalTime());
    }

    /// <summary>Codex names a shell call and a patch call the same way, in payload.name, under two different payload types.</summary>
    [Theory]
    [InlineData("function_call", "shell_command")]
    [InlineData("custom_tool_call", "apply_patch")]
    public void Read_TailIsAToolCall_DescribesWhatIsRunning(string payloadType, string toolName)
    {
        WriteRollout(SessionMeta(), ToolCall("2026-08-31T21:56:57.000Z", payloadType, toolName));

        Assert.Equal($"running: {toolName}", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    /// <summary>
    /// A rollout holding only its own header is a turn that has started but not
    /// yet done anything -- a real state, so it reports as one rather than as an
    /// error.
    /// </summary>
    [Fact]
    public void Read_SessionMetaOnly_ReportsJustStartedRatherThanAnError()
    {
        WriteRollout(SessionMeta());

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.Null(snapshot!.Error);
        Assert.Equal("just started", snapshot.Summary);
    }

    /// <summary>token_count is bookkeeping, not activity; it must not become the line the Boss reads.</summary>
    [Fact]
    public void Read_BookkeepingEntries_AreSkippedForRealActivity()
    {
        WriteRollout(
            SessionMeta(),
            ToolCall("2026-08-31T21:56:57.000Z", "function_call", "shell_command"),
            """{"timestamp":"2026-08-31T21:56:58.000Z","type":"event_msg","payload":{"type":"token_count","total":19062}}""");

        Assert.Equal("running: shell_command", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    /// <summary>The transcript is appended to while it is read, so a half-written last line is expected.</summary>
    [Fact]
    public void Read_TrailingHalfWrittenLine_StillReportsTheLastGoodEntry()
    {
        var path = WriteRollout(SessionMeta(), AgentMessage("2026-08-31T21:56:55.000Z", "Still going."));
        File.AppendAllText(path, """{"timestamp":"2026-08-31T21:57:0""");

        Assert.Equal("Still going.", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    [Fact]
    public void Read_NoRolloutOnDisk_ReportsAnErrorRatherThanThrowing()
    {
        Directory.CreateDirectory(Path.Combine(_codexHome, "sessions"));

        var snapshot = NewReader().Read(SessionId, Cwd);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Error);
    }

    /// <summary>sessions/ is the live tree, but a session old enough to have been moved is still findable.</summary>
    [Fact]
    public void Read_AnArchivedSession_IsStillFound()
    {
        var directory = Path.Combine(_codexHome, "archived_sessions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"rollout-2026-08-20T13-54-11-{SessionId}.jsonl"),
            SessionMeta() + "\n" + AgentMessage("2026-08-20T13:54:20.000Z", "From the archive.") + "\n");

        Assert.Equal("From the archive.", NewReader().Read(SessionId, Cwd)!.Summary);
    }

    /// <summary>No id means no conversation to look up yet, which is not an error.</summary>
    [Fact]
    public void Read_WithoutASessionId_ReturnsNull()
    {
        Assert.Null(NewReader().Read("", Cwd));
    }

    private CodexActivityReader NewReader() => new(_codexHome);

    private string WriteRollout(params string[] entries)
    {
        var directory = Path.Combine(_codexHome, "sessions", "2026", "08", "31");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"rollout-2026-08-31T16-56-51-{SessionId}.jsonl");
        File.WriteAllText(path, string.Concat(entries.Select(e => e.ReplaceLineEndings(" ") + "\n")));
        return path;
    }

    // Concatenated rather than interpolated: these entries end in a run of
    // closing braces that an interpolated raw string cannot hold as content.
    private static string SessionMeta() =>
        """{"timestamp":"2026-08-31T21:56:52.510Z","type":"session_meta","payload":{"session_id":""" +
        JsonSerializer.Serialize(SessionId) +
        ""","cwd":"C:\\Users\\crew\\PROJECTS\\Lighthouse","originator":"codex_exec","cli_version":"0.144.6","source":"exec"}}""";

    private static string AgentMessage(string timestamp, string message) =>
        """{"timestamp":""" + JsonSerializer.Serialize(timestamp) +
        ""","type":"event_msg","payload":{"type":"agent_message","message":""" +
        JsonSerializer.Serialize(message) + ""","phase":"commentary"}}""";

    private static string ToolCall(string timestamp, string payloadType, string toolName) =>
        """{"timestamp":""" + JsonSerializer.Serialize(timestamp) +
        ""","type":"response_item","payload":{"type":""" + JsonSerializer.Serialize(payloadType) +
        ""","id":"fc_01","name":""" + JsonSerializer.Serialize(toolName) + ""","call_id":"call_01"}}""";
}
