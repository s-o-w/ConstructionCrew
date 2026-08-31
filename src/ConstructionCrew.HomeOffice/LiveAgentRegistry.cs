using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.HomeOffice;

/// <summary>
/// Each named Foreman gets one persistent ILocalCliAgent, created lazily and
/// reused via the provider's --continue mechanism, the same pattern GC uses
/// with the Boss. A Foreman is a short-lived CLI invocation per turn that
/// shares conversation history with its prior turns, not a long-running
/// process.
///
/// A per-name SemaphoreSlim serializes turns: a Worker's ask_foreman could
/// otherwise race a GC dispatch to the same Foreman and run two --continue
/// invocations against the same conversation concurrently.
/// </summary>
public sealed class LiveAgentRegistry
{
    private readonly ILocalCliAgentFactory _agentFactory;
    private readonly ConcurrentDictionary<string, (ILocalCliAgent Agent, SemaphoreSlim Lock, string Engine)> _live = new(StringComparer.OrdinalIgnoreCase);

    public LiveAgentRegistry(ILocalCliAgentFactory agentFactory)
    {
        _agentFactory = agentFactory;
    }

    /// <summary>
    /// <paramref name="onStarted"/> fires when this turn gets the per-name
    /// semaphore: an approximation of OS process start, not the spawn itself:
    /// LocalCliAgent still composes the prompt after this, before
    /// CliProcessRunner calls Cli.Wrap(...).ExecuteBufferedAsync(...). Threading
    /// the callback deeper would mean changing ICliProcessRunner's contract for a
    /// gap of milliseconds.
    /// </summary>
    public async Task<CliRunResult> SendAsync(string name, ForemanConfig config, string message, CancellationToken cancellationToken, Action? onStarted = null)
    {
        var entry = _live.GetOrAdd(name, _ => (_agentFactory.Create(config), new SemaphoreSlim(1, 1), config.Provider));

        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            onStarted?.Invoke();
            return await entry.Agent.SendAsync(message, cancellationToken);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>
    /// What is needed to find a name's live activity on disk: the engine driving
    /// it, and that engine's own session id once a turn has reported one.
    ///
    /// <para>
    /// Null when the name has never been dispatched to, which is the honest
    /// answer: there is no conversation to watch yet. A non-null result with a
    /// null SessionId means "hired and cached, but its first turn has not
    /// reported an id" -- a real state the watcher renders differently from
    /// "never started".
    /// </para>
    ///
    /// <para>
    /// The engine comes from the ForemanConfig this name's agent was created
    /// with, not from the agent: it is already known at Create time, and asking
    /// the agent would push a naming concern into ILocalCliAgent for nothing.
    /// </para>
    /// </summary>
    public (string? SessionId, string Engine)? GetActivityInfo(string name) =>
        _live.TryGetValue(name, out var entry) ? (entry.Agent.SessionId, entry.Engine) : null;

    /// <summary>
    /// Evicts a name's cached agent so a Foreman later re-hired under the same
    /// name never silently continues a fired Foreman's old conversation.
    /// </summary>
    public void Remove(string name)
    {
        if (_live.TryRemove(name, out var entry))
        {
            entry.Lock.Dispose();
        }
    }
}
