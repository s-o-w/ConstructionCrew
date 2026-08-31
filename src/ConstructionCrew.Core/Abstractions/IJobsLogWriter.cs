using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Appends one line per JobRegistry.Transition call to <c>state/jobs.jsonl</c>:
/// append-only, machine-local crash recovery only, never read back as a resume
/// source of truth (that stays the Vault plus the ActiveWorkorder).
///
/// No plansFolder/path parameter, unlike IRunLogWriter, which writes into a
/// per-ActiveWorkorder RUN-LOG.md: this is one flat process-wide file, resolved
/// once at construction. There's nothing per-call to route to.
/// </summary>
public interface IJobsLogWriter
{
    void Append(JobRecord job);
}
