using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers.Activity;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// <c>/watch &lt;Name&gt;</c>: show what a crew member is actually doing,
/// without changing where the Boss's typed input goes.
///
/// <para>
/// The read-only half of "watch, then redirect". <c>/drive</c> is the other
/// half and already shows the same panel for whoever it routes to, so driving
/// implies watching; watching does not imply driving. That split is the whole
/// point: the Boss can keep talking to GC normally while a Foreman's real
/// activity ticks along beside the chat, and only spend a <c>/drive</c> once
/// there is a reason to redirect.
/// </para>
/// </summary>
internal static class WatchCommand
{
    private const string WatchVerb = "/watch";

    /// <summary>
    /// <paramref name="findForeman"/> is the roster lookup and
    /// <paramref name="readers"/> decides whether that crew member's engine
    /// keeps a transcript anyone can read.
    /// </summary>
    public static BossCommandResult Apply(
        DashboardState state,
        string command,
        Func<string, ForemanConfig?> findForeman,
        ForemanActivityReaders readers)
    {
        if (!DriveCommands.TryParseVerb(command.Trim(), WatchVerb, out var target))
        {
            return BossCommandResult.NotHandled;
        }

        state.View = TuiView.Chat;

        // A bare /watch clears the watch. Nothing to clear is worth saying so,
        // rather than silently doing nothing to a panel that is already gone.
        if (target.Length == 0)
        {
            if (state.WatchedForeman is null)
            {
                state.ActiveTranscript.Add(new TranscriptLine(
                    "home office", $"Usage: {WatchVerb} <Name>. Nobody is being watched right now."));
                return BossCommandResult.Handled;
            }

            var stopped = state.WatchedForeman;
            StopWatching(state);
            state.ActiveTranscript.Add(new TranscriptLine("home office", $"Stopped watching {stopped}."));
            return BossCommandResult.Handled;
        }

        var config = findForeman(target);
        if (config is null)
        {
            state.ActiveTranscript.Add(new TranscriptLine(
                "home office", $"No crew member named '{target}' is hired -- /hire one first.", IsError: true));
            return BossCommandResult.Handled;
        }

        // Toggle: /watch on whoever is already watched turns the panel off.
        if (config.Name.Equals(state.WatchedForeman, StringComparison.OrdinalIgnoreCase))
        {
            StopWatching(state);
            state.ActiveTranscript.Add(new TranscriptLine("home office", $"Stopped watching {config.Name}."));
            return BossCommandResult.Handled;
        }

        // Refused rather than set: a watch on an engine with no readable
        // transcript would render an empty panel forever, and the Boss would
        // reasonably read that as "this Foreman is doing nothing".
        if (readers.For(config.Provider) is null)
        {
            state.ActiveTranscript.Add(new TranscriptLine(
                "home office",
                $"{config.Name} runs on {config.Provider}, which keeps no readable session transcript -- " +
                "there is no live activity to show. The roster still shows whether it is working.",
                IsError: true));
            return BossCommandResult.Handled;
        }

        // config.Name, not the raw argument, so the panel header and the
        // registry lookup agree on one canonical spelling.
        state.WatchedForeman = config.Name;
        state.Passive = null;
        state.Activity = null;

        state.ActiveTranscript.Add(new TranscriptLine(
            "home office",
            state.DrivenForeman is null
                ? $"Watching {config.Name}. What you type still goes to GC. {WatchVerb} again to stop."
                : $"Watching {config.Name}. What you type still goes to {state.DrivenForeman}. {WatchVerb} again to stop."));

        return BossCommandResult.Handled;
    }

    /// <summary>
    /// Drops the explicit watch and the panel content with it. The panel itself
    /// only disappears if nobody is being driven either, since driving keeps
    /// showing its own.
    /// </summary>
    internal static void StopWatching(DashboardState state)
    {
        state.WatchedForeman = null;
        state.Passive = null;
        state.Activity = null;
    }
}
