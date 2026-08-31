using System.Collections.Concurrent;
using System.Globalization;
using ConstructionCrew.Core;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Appends one line per completed unit of work to
/// <c>&lt;plansFolder&gt;/RUN-LOG.md</c>: start/finish time, parked time, queue
/// time, and cost.
///
/// Actual hours = (CompletedAt - StartedAt) - ParkedDuration. Queue time
/// (StartedAt - CreatedAt) is recorded but never charged against an estimate.
/// An unknown value is written as "unavailable", never omitted, so it never
/// reads as zero.
/// </summary>
public sealed class RunLogWriter : IRunLogWriter
{
    private const string FileName = "RUN-LOG.md";
    private const string Unavailable = "unavailable";

    /// <summary>
    /// One lock per RUN-LOG.md path, keyed by canonicalized path. Only writes to
    /// the SAME file need to serialize.
    ///
    /// The comparer is OrdinalIgnoreCase on Windows/macOS, Ordinal on Linux.
    /// Canonicalization resolves symlinks, not casing, so without this comparer
    /// two spellings of one file on a case-insensitive filesystem would get two
    /// different lock objects.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _fileLocks = new(PathComparison.PathComparer);

    public void Append(string plansFolder, JobRecord job)
    {
        var path = Path.Combine(plansFolder, FileName);
        var entry = FormatEntry(job);

        AppendWithLockForTesting(CanonicalizePath(path), () =>
        {
            Directory.CreateDirectory(plansFolder);

            if (!File.Exists(path))
            {
                File.AppendAllText(path, $"# RUN-LOG{Environment.NewLine}{Environment.NewLine}");
            }

            File.AppendAllText(path, entry + Environment.NewLine);
        });
    }

    /// <summary>
    /// The real lock-acquisition path; Append calls this with the real write logic
    /// as <paramref name="criticalSection"/>. `lock (x) { body }` is exactly
    /// Monitor.Enter/try/finally Monitor.Exit, so this is production code with one
    /// added observation hook, not a reimplementation.
    ///
    /// <paramref name="onContended"/> fires only when a non-blocking
    /// Monitor.TryEnter(fileLock, 0) genuinely failed because another caller holds
    /// the lock: TryEnter is one atomic call, so a test can't mistake luck for
    /// contention.
    /// </summary>
    internal void AppendWithLockForTesting(
        string normalizedPath,
        Action criticalSection,
        Action? beforeAcquire = null,
        Action? onContended = null)
    {
        beforeAcquire?.Invoke();
        var fileLock = _fileLocks.GetOrAdd(normalizedPath, _ => new object());

        if (!Monitor.TryEnter(fileLock, 0))
        {
            onContended?.Invoke(); // TryEnter already confirmed contention atomically.
            Monitor.Enter(fileLock);
        }

        try
        {
            criticalSection();
        }
        finally
        {
            Monitor.Exit(fileLock);
        }
    }

    /// <summary>
    /// Path.GetFullPath resolves "."/".." but not a symlinked ancestor directory,
    /// so a path reached through a symlink could otherwise get two lock objects
    /// for one real file. Walks the path from root, resolving any segment that is
    /// a reparse point. Handles one symlink hop; not a hardened realpath.
    /// </summary>
    internal static string CanonicalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var resolved = root;
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(resolved, segment);
            var isReparsePoint = (File.Exists(candidate) || Directory.Exists(candidate))
                && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0;

            resolved = isReparsePoint
                ? new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate
                : candidate;
        }

        return resolved;
    }

    /// <summary>One line per entry: concurrent appends can never interleave into a half-written record, and the log stays greppable.</summary>
    internal static string FormatEntry(JobRecord job)
    {
        var actual = ActualDuration(job);
        var queued = QueueDuration(job);

        return string.Join(" | ", new[]
        {
            $"- {Stamp(job.CompletedAt ?? DateTimeOffset.UtcNow)}",
            $"job {job.JobId}",
            $"foreman {job.ForemanName}",
            $"{job.Status.ToString().ToLowerInvariant()}",
            $"started {Stamp(job.StartedAt)}",
            $"actual {Hours(actual)}",
            $"parked {Hours(job.ParkedDuration)}",
            $"queued {Hours(queued)}",
            $"tokens in/out {Tokens(job.Usage?.InputTokens)}/{Tokens(job.Usage?.OutputTokens)}",
            $"cost {Cost(job.Usage?.CostUsd)}",
            $"summary: {OneLine(job.Summary)}",
        });
    }

    /// <summary>Actual hours = (CompletedAt - StartedAt) - ParkedDuration. Null (not zero) when the job never reached those stamps.</summary>
    internal static TimeSpan? ActualDuration(JobRecord job) =>
        job.CompletedAt is { } completedAt && job.StartedAt is { } startedAt
            ? completedAt - startedAt - job.ParkedDuration
            : null;

    /// <summary>Queue time: recorded, visible, and never charged against an estimate.</summary>
    internal static TimeSpan? QueueDuration(JobRecord job) =>
        job.StartedAt is { } startedAt ? startedAt - job.CreatedAt : null;

    private static string Hours(TimeSpan? value) =>
        value is { } span
            ? span.TotalHours.ToString("0.00", CultureInfo.InvariantCulture) + "h"
            : Unavailable;

    private static string Stamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? Unavailable;

    private static string Tokens(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? Unavailable;

    private static string Cost(decimal? value) =>
        value is { } cost ? "$" + cost.ToString("0.####", CultureInfo.InvariantCulture) : Unavailable;

    /// <summary>Flattens the agent's summary (whatever it said, newlines and all) into exactly one line.</summary>
    private static string OneLine(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "(none)";
        }

        var flattened = string.Join(
            " ",
            summary.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

        return flattened.Length > 300 ? flattened[..300] + "..." : flattened;
    }
}
