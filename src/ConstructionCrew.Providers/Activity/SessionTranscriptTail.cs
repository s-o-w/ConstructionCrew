using System.Text;

namespace ConstructionCrew.Providers.Activity;

/// <summary>
/// Reads the last complete lines of an append-only JSONL transcript that
/// another process currently has open for writing.
///
/// <para>
/// Shared by every engine's reader, because the three hard parts are the same
/// whoever wrote the file: do not lock the writer out, do not read a megabyte
/// to answer a one-line question, and do not choke on the half-written last
/// line an append is always allowed to leave behind.
/// </para>
/// </summary>
internal static class SessionTranscriptTail
{
    /// <summary>
    /// How far back from the end to read. Sized off real transcripts on disk,
    /// not picked round: in a 5,589-line Claude session, 10% of lines were over
    /// 4KB, the 99th percentile was 25KB, and the longest single line was 4.8MB.
    /// A 4KB window would routinely contain no complete line at all, so it is
    /// 64KB -- still bounded, still nothing next to a multi-megabyte file.
    /// </summary>
    internal const int TailBytes = 64 * 1024;

    /// <summary>
    /// The last complete lines of <paramref name="path"/>, oldest first, or null
    /// with <paramref name="error"/> set.
    ///
    /// <para>
    /// FileShare.ReadWrite is not optional: the engine holds the file open for
    /// append the entire time a turn is in flight, which is exactly when the
    /// Boss wants to look at it. Anything stricter fails with a sharing
    /// violation precisely when the answer matters.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string>? ReadLines(string path, out string? error)
    {
        error = null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            if (length == 0)
            {
                error = "transcript is empty";
                return null;
            }

            var take = (int)Math.Min(length, TailBytes);
            stream.Seek(length - take, SeekOrigin.Begin);

            var buffer = new byte[take];
            var read = stream.ReadAtLeast(buffer, take, throwOnEndOfStream: false);

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');

            // Two deliberate drops. The first element is whatever remained of the
            // line the seek landed mid-way through, unless the read covered the
            // whole file. The last is either empty (the file ended on a newline)
            // or a line still being appended -- a real, expected state, not an
            // error, so it is skipped rather than reported.
            var start = take < length ? 1 : 0;
            var complete = new List<string>(lines.Length);
            for (var i = start; i < lines.Length - 1; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length > 0)
                {
                    complete.Add(line);
                }
            }

            if (complete.Count == 0)
            {
                error = "no complete entry yet";
                return null;
            }

            return complete;
        }
        catch (FileNotFoundException)
        {
            error = "no transcript on disk yet";
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            error = "no transcript on disk yet";
            return null;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>One line, clipped to <paramref name="max"/> with an ellipsis, newlines flattened so it cannot break the panel.</summary>
    internal static string OneLine(string text, int max = 120)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }

        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "...";
    }
}
