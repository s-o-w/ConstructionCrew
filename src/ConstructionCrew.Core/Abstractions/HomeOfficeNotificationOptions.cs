namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// The Boss's optional external-notification hook. A plain record, not
/// DI-registered in HomeOfficeHost's container: JobRegistry is its only consumer
/// and is constructed directly by Program.cs.
///
/// <paramref name="NotificationsCommand"/> is a shell command template with
/// optional {event}/{jobId}/{foreman} placeholders, string-replaced before it
/// runs, e.g.:
///
///     notify-send "ConstructionCrew: {event} ({foreman})"
///
/// Null or empty means no notifications: no process is ever spawned.
/// </summary>
public sealed record HomeOfficeNotificationOptions(string? NotificationsCommand);
