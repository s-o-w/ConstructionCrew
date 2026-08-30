using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.ConfigTests;

public class RunLogWriterTests
{
    private static string NewPlansFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cc-runlog-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static JobRecord Job(
        DateTimeOffset created,
        DateTimeOffset? started,
        DateTimeOffset? completed,
        TimeSpan parked = default,
        CliUsage? usage = null,
        string summary = "done") =>
        new(
            "job-1", "Frontend", "the task", JobStatus.Completed, created, completed, summary,
            StartedAt: started, Usage: usage)
        {
            ParkedDuration = parked,
        };

    /// <summary>Actual hours are (CompletedAt - StartedAt) - ParkedDuration. Queue time is beside them, never inside them.</summary>
    [Fact]
    public void Append_WritesOneEntry_WithParkedTimeExcludedFromActualHours()
    {
        var plansFolder = NewPlansFolder();
        try
        {
            var created = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
            var job = Job(
                created,
                started: created.AddMinutes(30),   // 0.50h queued
                completed: created.AddMinutes(210), // 3.00h wall clock after start
                parked: TimeSpan.FromMinutes(60),   // ...of which one hour was waiting on the Boss
                usage: new CliUsage(1200, 340, 0.1234m, "{}"));

            new RunLogWriter().Append(plansFolder, job);

            var lines = File.ReadAllLines(Path.Combine(plansFolder, "RUN-LOG.md"));
            var entry = Assert.Single(lines, l => l.StartsWith("- ", StringComparison.Ordinal));

            Assert.Contains("job job-1", entry);
            Assert.Contains("foreman Frontend", entry);
            Assert.Contains("actual 2.00h", entry);
            Assert.Contains("parked 1.00h", entry);
            Assert.Contains("queued 0.50h", entry);
            Assert.Contains("tokens in/out 1200/340", entry);
            Assert.Contains("cost $0.1234", entry);
            Assert.Contains("summary: done", entry);
        }
        finally
        {
            Directory.Delete(plansFolder, recursive: true);
        }
    }

    /// <summary>A number the run never produced is written as "unavailable" -- never as a zero.</summary>
    [Fact]
    public void Append_JobThatNeverStarted_LabelsMissingValuesUnavailable()
    {
        var plansFolder = NewPlansFolder();
        try
        {
            var created = DateTimeOffset.UtcNow;
            new RunLogWriter().Append(plansFolder, Job(created, started: null, completed: created.AddMinutes(1)));

            var entry = File.ReadAllLines(Path.Combine(plansFolder, "RUN-LOG.md"))
                .Single(l => l.StartsWith("- ", StringComparison.Ordinal));

            Assert.Contains("actual unavailable", entry);
            Assert.Contains("queued unavailable", entry);
            Assert.Contains("tokens in/out unavailable/unavailable", entry);
            Assert.Contains("cost unavailable", entry);
        }
        finally
        {
            Directory.Delete(plansFolder, recursive: true);
        }
    }

    /// <summary>Two completions against one Plans folder accumulate; the header is written once.</summary>
    [Fact]
    public void Append_TwiceToTheSameFolder_AccumulatesUnderOneHeader()
    {
        var plansFolder = NewPlansFolder();
        try
        {
            var writer = new RunLogWriter();
            var created = DateTimeOffset.UtcNow;
            writer.Append(plansFolder, Job(created, created, created.AddHours(1), summary: "first"));
            writer.Append(plansFolder, Job(created, created, created.AddHours(1), summary: "second"));

            var lines = File.ReadAllLines(Path.Combine(plansFolder, "RUN-LOG.md"));

            Assert.Single(lines, l => l == "# RUN-LOG");
            Assert.Equal(2, lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(plansFolder, recursive: true);
        }
    }

    /// <summary>A multi-line summary is flattened, so one entry is always exactly one line.</summary>
    [Fact]
    public void Append_MultiLineSummary_StaysOneLine()
    {
        var plansFolder = NewPlansFolder();
        try
        {
            var created = DateTimeOffset.UtcNow;
            new RunLogWriter().Append(
                plansFolder, Job(created, created, created.AddHours(1), summary: "line one\nline two\nline three"));

            var lines = File.ReadAllLines(Path.Combine(plansFolder, "RUN-LOG.md"));
            var entry = Assert.Single(lines, l => l.StartsWith("- ", StringComparison.Ordinal));

            Assert.Contains("summary: line one line two line three", entry);
        }
        finally
        {
            Directory.Delete(plansFolder, recursive: true);
        }
    }

    /// <summary>
    /// Deterministic proof that the second caller really did contend for the SAME
    /// lock object -- not a scheduling guess. onContended fires if and only if
    /// Monitor.TryEnter(fileLock, 0) returned false, and TryEnter's attempt and its
    /// result are one atomic operation, so there is no window in which this could
    /// fire without first genuinely holding the lock.
    /// </summary>
    [Fact]
    public async Task AppendWithLockForTesting_SecondCaller_GenuinelyContendsForTheHeldLock()
    {
        var plansFolder = NewPlansFolder();
        try
        {
            var writer = new RunLogWriter();
            var path = Path.Combine(plansFolder, "RUN-LOG.md");

            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = Task.Run(() => writer.AppendWithLockForTesting(path, criticalSection: () =>
            {
                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                File.AppendAllText(path, "first\n");
            }));

            try
            {
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // first now holds the lock, blocked inside the critical section

                var second = Task.Run(() => writer.AppendWithLockForTesting(
                    path,
                    criticalSection: () => File.AppendAllText(path, "second\n"),
                    onContended: () => secondContended.TrySetResult()));

                await secondContended.Task.WaitAsync(TimeSpan.FromSeconds(5)); // second's own TryEnter(0) genuinely failed -- proof of real contention, not a scheduling guess

                releaseFirst.TrySetResult();
                await Task.WhenAll(first, second);

                Assert.Equal(new[] { "first", "second" }, File.ReadAllLines(path));
            }
            finally
            {
                releaseFirst.TrySetResult(); // always release first -- whether either wait or an assertion above threw or timed out -- so its background task completes
            }
        }
        finally
        {
            Directory.Delete(plansFolder, recursive: true);
        }
    }

    /// <summary>
    /// The comparer, specifically: on a case-insensitive filesystem two spellings of
    /// one physical path must resolve to ONE lock object, which contention proves
    /// directly. Deliberately a no-op on Linux, where PathComparison.PathComparer is
    /// Ordinal and the two really are different keys.
    /// </summary>
    [Fact]
    public async Task AppendWithLockForTesting_DifferentlyCasedPaths_ContendForSameLock()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return; // PathComparison.PathComparer is Ordinal on Linux -- two differently-cased paths are genuinely different keys there, so no contention is the correct expectation
        }

        var writer = new RunLogWriter();
        var lowerPath = "/vault/plans/featurex/run-log.md";
        var upperPath = "/VAULT/PLANS/FEATUREX/RUN-LOG.MD";

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(() => writer.AppendWithLockForTesting(lowerPath, criticalSection: () =>
        {
            firstEntered.TrySetResult();
            releaseFirst.Task.GetAwaiter().GetResult();
        }));

        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var second = Task.Run(() => writer.AppendWithLockForTesting(
                upperPath,
                criticalSection: () => { },
                onContended: () => secondContended.TrySetResult()));

            await secondContended.Task.WaitAsync(TimeSpan.FromSeconds(5)); // proves upperPath's claim genuinely contended against lowerPath's held lock -- only possible if the comparer treats them as the same key

            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second);
        }
        finally
        {
            releaseFirst.TrySetResult(); // always release first -- whether an await above threw or timed out -- so its background task can complete and the test process exits cleanly
        }
    }

    /// <summary>
    /// Coarse end-to-end sanity check at the writer itself: many concurrent appends
    /// to one file produce exactly that many whole lines, none partial or
    /// interleaved. Useful, but NOT on its own proof that the lock is what makes it
    /// pass -- that is the deterministic test above.
    /// </summary>
    [Fact]
    public async Task Append_ManyConcurrentAppendsToOneFile_ProducesOneWholeLineEach()
    {
        var plansFolder = NewPlansFolder();
        try
        {
            var writer = new RunLogWriter();
            var created = DateTimeOffset.UtcNow;

            await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
                writer.Append(plansFolder, Job(created, created, created.AddHours(1), summary: $"token-{i}")))));

            var entries = File.ReadAllLines(Path.Combine(plansFolder, "RUN-LOG.md"))
                .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(24, entries.Count);
            foreach (var i in Enumerable.Range(0, 24))
            {
                Assert.Single(entries, e => e.EndsWith($"summary: token-{i}", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(plansFolder, recursive: true);
        }
    }

    /// <summary>
    /// Canonicalization resolves a symlinked ANCESTOR directory, not just the leaf:
    /// two strings reaching one physical file must normalize to one key, or they
    /// would take two different lock objects.
    /// </summary>
    [Fact]
    public void CanonicalizePath_ThroughASymlinkedAncestor_ResolvesToTheRealPath()
    {
        var root = NewPlansFolder();
        try
        {
            var real = Path.Combine(root, "real");
            Directory.CreateDirectory(Path.Combine(real, "feature"));
            var link = Path.Combine(root, "link");

            try
            {
                Directory.CreateSymbolicLink(link, real);
            }
            catch (IOException)
            {
                return; // no symlink privilege on this machine (unelevated Windows); nothing to prove here
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            var viaLink = RunLogWriter.CanonicalizePath(Path.Combine(link, "feature", "RUN-LOG.md"));
            var direct = RunLogWriter.CanonicalizePath(Path.Combine(real, "feature", "RUN-LOG.md"));

            Assert.Equal(direct, viaLink);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
