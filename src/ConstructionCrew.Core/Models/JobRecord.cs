using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Core.Models;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Parked,
}

/// <summary>
/// Immutable snapshot of one dispatched job's state. JobRegistry publishes a new
/// snapshot on every transition; nothing subscribing to it ever blocks the dispatcher.
///
/// CreatedAt is when the job was enqueued; StartedAt is when the agent's dispatch
/// began -- instructions composed, about to invoke its CLI process. StartedAt is an
/// approximation of process start, not the OS spawn itself. Actual hours for a unit
/// of work are (CompletedAt - StartedAt) - ParkedDuration; queue time (StartedAt -
/// CreatedAt) is visible but never charged.
/// </summary>
public sealed record JobRecord(
    string JobId,
    string ForemanName,
    string Task,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Summary,
    DateTimeOffset? StartedAt = null,
    string? WorktreePath = null,
    CliUsage? Usage = null)
{
    /// <summary>
    /// How long this job has sat Parked, waiting on the Boss. Subtracted from
    /// (CompletedAt - StartedAt) to get actual hours.
    ///
    /// Deliberately an init-only property rather than a trailing constructor
    /// parameter with "= default". get_job_status returns a JobRecord, and the
    /// MCP layer exports a JSON schema for that return type: a struct-typed
    /// optional constructor parameter reports HasDefaultValue=true with a null
    /// boxed default, and System.Text.Json's schema exporter throws
    /// "The JSON value could not be converted to System.TimeSpan" trying to write
    /// it -- crashing the Home Office at startup. Same type, same zero default,
    /// same `with { ParkedDuration = ... }` usage; only the positional
    /// constructor slot is given up.
    /// </summary>
    public TimeSpan ParkedDuration { get; init; } = TimeSpan.Zero;
}
