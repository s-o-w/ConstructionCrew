using System.Collections.Concurrent;
using System.Globalization;
using ConstructionCrew.Core;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Appends one line per completed unit of work to
/// <c>&lt;plansFolder&gt;/RUN-LOG.md</c>: when the work started and finished, how
/// long it sat parked, how long it queued, and what the run cost.
///
/// Actual hours are (CompletedAt - StartedAt) - ParkedDuration. Queue time
/// (StartedAt - CreatedAt) is recorded beside it but never charged against an
/// estimate. A value that is genuinely unknown is written as "unavailable" rather
/// than omitted, so a missing number never reads as a zero.
/// </summary>
public sealed class RunLogWriter : IRunLogWriter
{
    private const string FileName = "RUN-LOG.md";
    private const string Unavailable = "unavailable";

    /// <summary>
    /// One lock object per RUN-LOG.md this process writes, keyed by a canonicalized
    /// path. Two Features under two different Jobsites can legitimately complete at
    /// the same wall-clock moment; only writes to the SAME file need to serialize.
    ///
    /// The comparer is the codebase's one OS-aware path policy (OrdinalIgnoreCase
    /// on Windows/macOS, Ordinal on Linux) -- canonicalization resolves symlinks,
    /// not casing, so without it two spellings of one physical file on a
    /// case-insensitive filesystem would land two different lock objects.
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
    /// The real lock-acquisition path -- Append itself calls this, passing the real
    /// file-write logic as <paramref name="criticalSection"/>. This is production
    /// code with one added observation point, not a parallel reimplementation:
    /// `lock (x) { body }` is exactly Monitor.Enter/try/finally Monitor.Exit.
    ///
    /// <paramref name="onContended"/> fires if, and only if, a non-blocking
    /// Monitor.TryEnter(fileLock, 0) genuinely failed because another caller
    /// already holds the lock. TryEnter is a single atomic BCL operation -- the
    /// attempt happening and its result being known are the same call, so there is
    /// no scheduling window in which a test could mistake luck for contention.
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
            onContended?.Invoke(); // TryEnter's own atomic result says so -- no window to misjudge
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
    /// Physical-path canonicalization: Path.GetFullPath resolves "."/".." but NOT a
    /// symlinked ANCESTOR directory, so a Vault (or a Jobsite's Plans folder)
    /// reached through a symlink would otherwise give two in-process strings for
    /// one real file -- and therefore two different lock objects.
    ///
    /// Walks the path from its root, resolving any segment that is itself a
    /// reparse point to its final target. Pathological cases (symlink loops,
    /// targets containing further relative segments) get no special guard beyond
    /// what ResolveLinkTarget itself does: this is a defensible fix for the
    /// realistic case (one symlink hop), not a hardened realpath.
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

    /// <summary>
    /// One line per entry, on purpose: concurrent appends from two completing jobs
    /// can then never interleave into a half-written multi-line record, and the log
    /// stays greppable.
    /// </summary>
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

    /// <summary>
    /// Actual hours = (CompletedAt - StartedAt) - ParkedDuration. Null when the job
    /// never reached one of those stamps -- a job that failed before dispatch began
    /// has no actual hours, and reporting zero would be a lie.
    /// </summary>
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

    /// <summary>
    /// A summary is whatever the agent last said, newlines and all. Flattened, so
    /// one entry is always exactly one line.
    /// </summary>
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
