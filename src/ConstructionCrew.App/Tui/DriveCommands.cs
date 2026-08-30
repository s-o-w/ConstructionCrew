using System.Globalization;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.App.Tui;

/// <summary>What the Boss loop should do with a line after drive routing has had a look at it.</summary>
internal enum BossCommandResult
{
    /// <summary>Not a drive command -- carry on down the rest of the command table.</summary>
    NotHandled,

    /// <summary>Fully dealt with; re-render and take the next line.</summary>
    Handled,

    /// <summary>The Boss asked to leave, and was not driving anyone.</summary>
    Quit,
}

/// <summary>
/// <c>/drive &lt;Foreman&gt;</c> and <c>/exit</c>, as one testable unit the Boss
/// loop calls rather than a rule the loop re-implements inline.
///
/// <para>
/// Driving is a routing change only: subsequent Boss input goes to that Foreman's
/// own persistent conversation instead of GC's, and the output pane shows that
/// Foreman's transcript. There is no PTY and no live terminal attach -- it stays
/// an in/out message relay through the one shared LiveAgentRegistry every other
/// dispatch already goes through.
/// </para>
/// </summary>
internal static class DriveCommands
{
    private const string DriveVerb = "/drive";

    /// <summary>
    /// <paramref name="findForeman"/> is the roster lookup (Program hands it
    /// <c>ForemanDirectory.Find</c>) and <paramref name="jobs"/> is the live job
    /// list the queued notice is computed from.
    /// </summary>
    public static BossCommandResult Apply(
        DashboardState state,
        string command,
        Func<string, ForemanConfig?> findForeman,
        IReadOnlyCollection<JobRecord> jobs)
    {
        var trimmed = command.Trim();

        if (IsExitVerb(trimmed))
        {
            // /exit means "leave drive mode" while driving and "quit" otherwise.
            // The Boss can always get out of a Foreman without leaving the app.
            if (state.DrivenForeman is null)
            {
                return BossCommandResult.Quit;
            }

            StopDriving(state);
            return BossCommandResult.Handled;
        }

        if (!TryParseDrive(trimmed, out var target))
        {
            return BossCommandResult.NotHandled;
        }

        state.View = TuiView.Chat;

        if (target.Length == 0)
        {
            state.ActiveTranscript.Add(new TranscriptLine("home office", $"Usage: {DriveVerb} <Foreman>.", IsError: true));
            return BossCommandResult.Handled;
        }

        var config = findForeman(target);
        if (config is null)
        {
            state.ActiveTranscript.Add(new TranscriptLine(
                "home office", $"No Foreman named '{target}' is hired -- /hire one first.", IsError: true));
            return BossCommandResult.Handled;
        }

        // Driving GC is what the Boss is already doing. Silently landing in a
        // second, parallel "GC" pane would be the divergent-conversation bug in
        // UI form, so this returns to the one GC transcript instead.
        if (config.Name.Equals(state.GcForemanName, StringComparison.OrdinalIgnoreCase))
        {
            StopDriving(state);
            state.Transcript.Add(new TranscriptLine(
                "home office", "GC is who the Boss already talks to -- back to the main chat."));
            return BossCommandResult.Handled;
        }

        // config.Name, not the raw argument: the transcript key has to be the
        // canonical roster name so "frontend" and "Frontend" are one pane.
        state.DrivenForeman = config.Name;
        state.Passive = null;

        var notice = QueuedNotice(jobs, config.Name);
        state.ActiveTranscript.Add(new TranscriptLine(
            "home office",
            notice is null
                ? $"Driving {config.Name}. /exit returns to GC."
                : $"Driving {config.Name}. {notice} /exit returns to GC."));

        return BossCommandResult.Handled;
    }

    /// <summary>
    /// Clears the drive target and the passive column with it -- a stale
    /// <c>git status</c> from the Foreman you just left is worse than none.
    /// </summary>
    internal static void StopDriving(DashboardState state)
    {
        state.DrivenForeman = null;
        state.Passive = null;
        state.View = TuiView.Chat;
    }

    /// <summary>
    /// Parses <c>/drive Frontend</c>. <paramref name="target"/> comes back empty
    /// for a bare <c>/drive</c>; <c>/driveby</c> is not a drive command at all.
    /// </summary>
    internal static bool TryParseDrive(string command, out string target)
    {
        target = string.Empty;

        var trimmed = command.Trim();
        if (!trimmed.StartsWith(DriveVerb, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[DriveVerb.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
        {
            return false;
        }

        target = rest.Trim();
        return true;
    }

    internal static bool IsExitVerb(string command)
    {
        var trimmed = command.Trim();
        return trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "queued behind ..., started HH:mm" for a Foreman that already has a turn in
    /// flight, or null when it is free.
    ///
    /// <para>
    /// The clock comes from <see cref="JobRecord.StartedAt"/>, never
    /// <see cref="JobRecord.CreatedAt"/>: StartedAt is stamped when the turn
    /// actually acquires that Foreman's semaphore, so it is the only one of the two
    /// that answers "how long has this really been running". A job that has not
    /// started yet says so rather than quoting a start time it does not have --
    /// that distinction is the whole point of tracking both stamps.
    /// </para>
    /// </summary>
    internal static string? QueuedNotice(IReadOnlyCollection<JobRecord> jobs, string foremanName)
    {
        var inFlight = jobs
            .Where(j => j.Status is JobStatus.Pending or JobStatus.Running && BelongsTo(foremanName, j.ForemanName))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefault();

        if (inFlight is null)
        {
            return null;
        }

        var label = Summarize(inFlight.Task);

        return inFlight.StartedAt is { } startedAt
            ? $"queued behind \"{label}\", started {Clock(startedAt)}."
            : $"queued behind \"{label}\", not started yet (queued {Clock(inFlight.CreatedAt)}).";
    }

    /// <summary>
    /// A job belongs to a Foreman if it is theirs or one of the Workers they
    /// spawned (<c>&lt;Foreman&gt;/worker-abc123</c>) -- the same ownership rule
    /// <c>JobRegistry.IsForemanBusy</c> uses.
    /// </summary>
    internal static bool BelongsTo(string foremanName, string jobForemanName) =>
        jobForemanName.Equals(foremanName, StringComparison.OrdinalIgnoreCase) ||
        jobForemanName.StartsWith(foremanName + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stamps are stored UTC; the Boss reads a wall clock.</summary>
    private static string Clock(DateTimeOffset stamp) =>
        stamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Summarize(string task)
    {
        var oneLine = task.ReplaceLineEndings(" ").Trim();
        return oneLine.Length > 40 ? oneLine[..40] + "..." : oneLine;
    }
}
