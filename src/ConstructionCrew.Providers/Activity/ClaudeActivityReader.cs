using System.Text.Json;

namespace ConstructionCrew.Providers.Activity;

/// <summary>
/// Reports what a Claude Code session is doing, by tailing the transcript the
/// CLI keeps for itself at
/// <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session-id&gt;.jsonl</c>.
///
/// <para>
/// Every shape below was read off real transcripts on this machine on
/// 2026-08-31, not recalled: outer entries carry <c>type</c>, <c>timestamp</c>
/// (ISO-8601 Z), <c>sessionId</c> and <c>cwd</c>; an <c>assistant</c> entry's
/// <c>message.content</c> is an array of blocks typed <c>text</c>
/// (<c>{"type":"text","text":...}</c>), <c>tool_use</c>
/// (<c>{"type":"tool_use","name":"Bash","input":{...}}</c>) or <c>thinking</c>.
/// A <c>user</c> entry's content is either a bare string or an array holding
/// <c>tool_result</c> blocks.
/// </para>
/// </summary>
public sealed class ClaudeActivityReader : IForemanActivityReader
{
    private readonly string _projectsRoot;

    /// <param name="projectsRoot">
    /// Defaults to the real <c>~/.claude/projects</c>. Injectable so tests point
    /// at a fixture directory instead of the developer's own sessions.
    /// </param>
    public ClaudeActivityReader(string? projectsRoot = null)
    {
        _projectsRoot = projectsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    }

    public string ProviderId => "claude";

    /// <summary>
    /// Claude Code names a session's directory after its working directory, with
    /// every character that is not a letter, digit or dash replaced by a dash.
    ///
    /// <para>
    /// Reverse-engineered from real directories on this machine rather than
    /// assumed, and every observed separator agrees:
    /// <c>C:\Users\shawn.weekly</c> becomes <c>C--Users-shawn-weekly</c> (the
    /// drive colon, the backslashes AND the dot in the account name), and
    /// <c>...\PROJECTS\BSD Training Substation 385</c> becomes
    /// <c>...-PROJECTS-BSD-Training-Substation-385</c> (spaces too).
    /// </para>
    /// </summary>
    internal static string EncodeClaudeProjectPath(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-';
            }
        });
    }

    /// <summary>
    /// Finds the most-recently-written transcript in the project directory for
    /// <paramref name="cwd"/>. Used while a turn is in-flight and the session ID
    /// has not yet been extracted (that only happens after the process exits in
    /// buffered mode). All <c>.jsonl</c> files in a project directory belong to
    /// the same working directory by construction, so the most-recently-modified
    /// file is the active session without needing a CWD check inside the file.
    /// </summary>
    public ForemanActivitySnapshot? TryReadForCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var projectDir = Path.Combine(_projectsRoot, EncodeClaudeProjectPath(cwd));

        string[] files;
        try
        {
            files = Directory.GetFiles(projectDir, "*.jsonl");
        }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (files.Length == 0)
        {
            return null;
        }

        // Most-recently-modified = the active session (being written to right now).
        var mostRecent = files
            .Select(f => (Path: f, Modified: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(x => x.Modified)
            .First()
            .Path;

        var lines = SessionTranscriptTail.ReadLines(mostRecent, out var error);
        if (lines is null)
        {
            return new ForemanActivitySnapshot("no activity yet", null, error);
        }

        return Summarize(lines) ?? new ForemanActivitySnapshot("starting up", null, null);
    }

    public ForemanActivitySnapshot? Read(string sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var path = LocateTranscript(sessionId, workingDirectory);
        if (path is null)
        {
            return new ForemanActivitySnapshot("no activity yet", null, "no transcript on disk yet");
        }

        var lines = SessionTranscriptTail.ReadLines(path, out var error);
        if (lines is null)
        {
            return new ForemanActivitySnapshot("no activity yet", null, error);
        }

        return Summarize(lines) ?? new ForemanActivitySnapshot("working", null, null);
    }

    /// <summary>
    /// The encoded directory first, then a scan of every project directory for
    /// the session's own file.
    ///
    /// <para>
    /// The fallback earns its keep: the encoding above is reverse-engineered, so
    /// a path shape this machine has never produced could still encode
    /// differently. A session id is a GUID, so a filename match is unambiguous
    /// and the scan cannot pick the wrong conversation -- which is the whole
    /// reason this feature keys off a real id instead of "most recent here".
    /// </para>
    /// </summary>
    private string? LocateTranscript(string sessionId, string workingDirectory)
    {
        var fileName = sessionId + ".jsonl";

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var direct = Path.Combine(_projectsRoot, EncodeClaudeProjectPath(workingDirectory), fileName);
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_projectsRoot))
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private const int MaxActivityLines = 8;

    /// <summary>
    /// Walks the tail newest-first and collects up to <see cref="MaxActivityLines"/>
    /// events, then returns them in chronological order so the watch panel renders
    /// as a transcript tail (oldest at top, newest at bottom).
    ///
    /// <para>
    /// Within one entry the blocks are in the order they happened, so they are
    /// scanned in reverse too: an entry that thought, answered, then called a
    /// tool is doing the tool call now. <c>thinking</c> blocks are skipped
    /// entirely -- they are frequently empty and never a description of work.
    /// </para>
    /// </summary>
    private static ForemanActivitySnapshot? Summarize(IReadOnlyList<string> lines)
    {
        var collected = new List<(string Text, DateTimeOffset? At)>();

        for (var i = lines.Count - 1; i >= 0 && collected.Count < MaxActivityLines; i--)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(lines[i]);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var summary = SummarizeEntry(root);
                if (summary is not null)
                {
                    collected.Add((summary, ReadTimestamp(root)));
                }
            }
        }

        if (collected.Count == 0)
        {
            return null;
        }

        // collected is newest-first; reverse to chronological for display.
        collected.Reverse();
        var lineTexts = collected.Select(c => c.Text).ToList();
        return new ForemanActivitySnapshot(lineTexts[^1], collected[^1].At, null, lineTexts);
    }

    private static string? SummarizeEntry(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var entryType = type.GetString();
        if (entryType != "assistant" && entryType != "user")
        {
            return null;
        }

        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        // A user turn's content can be a bare string -- that is the Boss's own
        // prompt, and "waiting" is a truer summary of it than echoing it back.
        if (content.ValueKind == JsonValueKind.String)
        {
            return entryType == "user" ? "reading the task" : null;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var blocks = content.EnumerateArray().ToList();
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var block = blocks[i];
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var blockType) ||
                blockType.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (blockType.GetString())
            {
                case "tool_use":
                    var name = block.TryGetProperty("name", out var toolName) && toolName.ValueKind == JsonValueKind.String
                        ? toolName.GetString()
                        : null;
                    return $"running: {(string.IsNullOrWhiteSpace(name) ? "a tool" : name)}";

                case "text":
                    if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var value = text.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return SessionTranscriptTail.OneLine(value);
                        }
                    }

                    break;

                case "tool_result":
                    return "reading a tool result";
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var stamp) &&
        stamp.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(stamp.GetString(), out var parsed)
            ? parsed
            : null;
}
