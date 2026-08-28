namespace ConstructionCrew.Core.Models;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Immutable snapshot of one dispatched job's state. JobRegistry publishes a new
/// snapshot on every transition; nothing subscribing to it ever blocks the dispatcher.
/// </summary>
public sealed record JobRecord(
    string JobId,
    string ForemanName,
    string Task,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Summary);
