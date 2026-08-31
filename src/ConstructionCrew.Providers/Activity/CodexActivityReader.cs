using System.Text.Json;

namespace ConstructionCrew.Providers.Activity;

/// <summary>
/// Reports what a Codex CLI session is doing, by tailing the rollout transcript
/// the CLI keeps for itself at
/// <c>~/.codex/sessions/YYYY/MM/DD/rollout-&lt;timestamp&gt;-&lt;session-id&gt;.jsonl</c>.
///
/// <para>
/// WHICH of Codex's two session directories is live was the open question, and
/// it was settled by running a real <c>codex exec</c> turn on this machine on
/// 2026-08-31 while polling both. The answer: a file appears under
/// <c>sessions/</c> mid-turn and grows as the turn runs (18KB, then 60KB, then
/// 61KB, at half-second intervals, before the process exited), while
/// <c>archived_sessions/</c> did not change at all -- not during the run and
/// not after it. So <c>sessions/</c> is the live location and the one tailed
/// here; <c>archived_sessions/</c> is only checked as a fallback, for a session
/// old enough to have been moved there.
/// </para>
///
/// <para>
/// Entry shapes below were read off real rollouts the same day. Every entry is
/// <c>{timestamp, type, payload}</c> with an ISO-8601 Z timestamp. The first is
/// <c>session_meta</c>, carrying <c>payload.session_id</c> and
/// <c>payload.cwd</c>. Activity then arrives as <c>event_msg</c> entries
/// (<c>agent_message</c> holds the assistant's prose in <c>payload.message</c>;
/// <c>task_started</c>, <c>task_complete</c>, <c>token_count</c>) and
/// <c>response_item</c> entries (<c>function_call</c> and
/// <c>custom_tool_call</c> both name the tool in <c>payload.name</c>, e.g.
/// <c>shell_command</c> and <c>apply_patch</c>).
/// </para>
/// </summary>
public sealed class CodexActivityReader : IForemanActivityReader
{
    private readonly string _codexHome;

    /// <param name="codexHome">Defaults to the real <c>~/.codex</c>. Injectable so tests point at a fixture tree.</param>
    public CodexActivityReader(string? codexHome = null)
    {
        _codexHome = codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public string ProviderId => "codex";

    public ForemanActivitySnapshot? Read(string sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var path = LocateRollout(sessionId);
        if (path is null)
        {
            return new ForemanActivitySnapshot("no activity yet", null, "no transcript on disk yet");
        }

        var lines = SessionTranscriptTail.ReadLines(path, out var error);
        if (lines is null)
        {
            return new ForemanActivitySnapshot("no activity yet", null, error);
        }

        return Summarize(lines);
    }

    /// <summary>
    /// The rollout filename embeds the session id
    /// (<c>rollout-&lt;timestamp&gt;-&lt;session-id&gt;.jsonl</c>), so the file
    /// is found by name rather than by opening every rollout to read its
    /// session_meta. Live <c>sessions/</c> first, then <c>archived_sessions/</c>
    /// for one already moved.
    /// </summary>
    private string? LocateRollout(string sessionId)
    {
        var pattern = "*" + sessionId + ".jsonl";

        foreach (var (root, recurse) in new[]
        {
            (Path.Combine(_codexHome, "sessions"), SearchOption.AllDirectories),
            (Path.Combine(_codexHome, "archived_sessions"), SearchOption.TopDirectoryOnly),
        })
        {
            try
            {
                var match = Directory.EnumerateFiles(root, pattern, recurse).FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static ForemanActivitySnapshot Summarize(IReadOnlyList<string> lines)
    {
        var sawSessionMeta = false;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            JsonDocument document;

            try
            {
                // Expected, not exceptional: the CLI is appending to this file
                // while it is being read.
                document = JsonDocument.Parse(lines[i]);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var entryType = type.GetString();
                if (entryType == "session_meta")
                {
                    sawSessionMeta = true;
                    continue;
                }

                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var summary = SummarizePayload(entryType, payload);
                if (summary is not null)
                {
                    return new ForemanActivitySnapshot(summary, ReadTimestamp(root), null);
                }
            }
        }

        // A rollout holding only its own header is a turn that has started but
        // not yet done anything. That is a real state, not a failure to read.
        return sawSessionMeta
            ? new ForemanActivitySnapshot("just started", null, null)
            : new ForemanActivitySnapshot("working", null, null);
    }

    private static string? SummarizePayload(string? entryType, JsonElement payload)
    {
        if (!payload.TryGetProperty("type", out var payloadType) || payloadType.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        switch (payloadType.GetString())
        {
            // Both tool shapes name the tool the same way.
            case "function_call":
            case "custom_tool_call":
                var name = payload.TryGetProperty("name", out var toolName) && toolName.ValueKind == JsonValueKind.String
                    ? toolName.GetString()
                    : null;
                return $"running: {(string.IsNullOrWhiteSpace(name) ? "a tool" : name)}";

            case "agent_message":
                if (payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    var value = message.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return SessionTranscriptTail.OneLine(value);
                    }
                }

                return null;

            case "task_complete":
                return "turn finished";

            case "task_started":
                return "thinking";

            // Deliberately not summarized: token_count is bookkeeping, and a
            // response_item "message" is mostly the injected developer prompt,
            // not anything the Foreman did.
            default:
                return null;
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var stamp) &&
        stamp.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(stamp.GetString(), out var parsed)
            ? parsed
            : null;
}
