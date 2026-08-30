using System.Collections.Concurrent;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The Boss loop's completion-notice mechanism, in full.
///
/// <para>
/// <c>JobRegistry.StartJob</c> only ever hands back a job id; the completion data
/// (<see cref="JobRecord.Summary"/>) is written by private members, so there is no
/// public completion callback to hang a transcript append off -- and adding one
/// would widen a public surface that is deliberately narrow. Instead the loop
/// records each Boss-turn job id here at the dispatch call site, and inspects
/// every <see cref="JobRecord"/> it drains from <c>IJobStatusSink.Reader</c>
/// against this set. A drained record that is both tracked and terminal becomes a
/// transcript line.
/// </para>
///
/// <para>
/// Concurrency: the Boss loop is the only reader of the status channel, but
/// <see cref="Track"/> runs whenever a turn is dispatched and jobs the Boss never
/// started are completing on background threads the whole time. Everything here
/// goes through a <see cref="ConcurrentDictionary{TKey,TValue}"/>, and the
/// terminal check happens strictly <b>before</b> the removal so an intermediate
/// <c>Running</c> transition can never evict a still-pending id. The single
/// <c>TryRemove</c> is what decides the winner, so a record delivered twice can
/// only ever produce one transcript line.
/// </para>
/// </summary>
internal sealed class PendingBossTurns
{
    /// <summary>jobId -> the Foreman the Boss addressed (GC, or a driven Foreman).</summary>
    private readonly ConcurrentDictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>Boss turns dispatched but not yet reported back. Diagnostic/test only.</summary>
    public int Count => _pending.Count;

    public bool IsPending(string jobId) => _pending.ContainsKey(jobId);

    public void Track(string jobId, string foremanName) => _pending[jobId] = foremanName;

    /// <summary>
    /// True exactly once per tracked job, on the first terminal record drained for
    /// it. <paramref name="foremanName"/> is who the turn was addressed to, which
    /// is also which transcript the line belongs in.
    /// </summary>
    public bool TryTakeCompletion(JobRecord record, out string foremanName, out TranscriptLine line)
    {
        foremanName = string.Empty;
        line = default!;

        if (record.Status is not (JobStatus.Completed or JobStatus.Failed))
        {
            return false;
        }

        if (!_pending.TryRemove(record.JobId, out var tracked))
        {
            return false;
        }

        foremanName = tracked;
        line = Format(record, tracked);
        return true;
    }

    /// <summary>
    /// A failed turn still gets a line -- a Boss who typed something and got
    /// silence has no way to tell "still working" from "died".
    /// </summary>
    internal static TranscriptLine Format(JobRecord record, string foremanName)
    {
        var failed = record.Status == JobStatus.Failed;

        var text = string.IsNullOrWhiteSpace(record.Summary)
            ? failed ? "(failed with no output)" : "(finished with no output)"
            : record.Summary!;

        return new TranscriptLine(foremanName, text, IsError: failed);
    }
}
