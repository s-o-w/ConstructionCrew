namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// The Boss's optional external-notification hook. A plain record, same reasoning
/// as HomeOfficeVaultOptions -- but never registered in HomeOfficeHost's DI
/// container either, since JobRegistry is its only consumer and is
/// plain-constructed by Program.cs.
///
/// <paramref name="NotificationsCommand"/> is a shell command TEMPLATE, with
/// optional {event}/{jobId}/{foreman} placeholders string-replaced immediately
/// before it runs, e.g.:
///
///     notify-send "ConstructionCrew: {event} ({foreman})"
///
/// Null or empty means no notifications at all -- no process is ever spawned.
/// </summary>
public sealed record HomeOfficeNotificationOptions(string? NotificationsCommand);
