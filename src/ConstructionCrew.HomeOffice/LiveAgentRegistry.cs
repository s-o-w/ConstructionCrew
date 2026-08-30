using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.HomeOffice;

/// <summary>
/// Each named Foreman gets exactly one persistent ILocalCliAgent, created
/// lazily and reused for every future turn -- both dispatched tasks and
/// ask_foreman questions -- via that provider's --continue mechanism, the
/// same pattern GC already uses with the Boss. A Foreman isn't a long-running
/// process; it's a short-lived CLI invocation each turn that shares
/// conversation history with its own prior turns.
///
/// A per-name SemaphoreSlim serializes turns, since a Worker's ask_foreman
/// call could otherwise race a GC dispatch to the same Foreman and run two
/// --continue invocations against the same conversation concurrently.
/// </summary>
public sealed class LiveAgentRegistry
{
    private readonly ILocalCliAgentFactory _agentFactory;
    private readonly ConcurrentDictionary<string, (ILocalCliAgent Agent, SemaphoreSlim Lock)> _live = new(StringComparer.OrdinalIgnoreCase);

    public LiveAgentRegistry(ILocalCliAgentFactory agentFactory)
    {
        _agentFactory = agentFactory;
    }

    /// <summary>
    /// <paramref name="onStarted"/> fires the moment this turn actually gets the
    /// per-name semaphore -- i.e. "the agent's dispatch began: instructions
    /// composed, about to invoke its CLI process." That is an approximation of
    /// OS process start, not the spawn itself: LocalCliAgent still composes the
    /// prompt and builds the invocation after this point, before CliProcessRunner
    /// ever calls Cli.Wrap(...).ExecuteBufferedAsync(...). Threading the callback
    /// deeper would mean changing ICliProcessRunner's contract for a gap that is
    /// milliseconds on a continuing turn and one file read on a first turn.
    /// </summary>
    public async Task<CliRunResult> SendAsync(string name, ForemanConfig config, string message, CancellationToken cancellationToken, Action? onStarted = null)
    {
        var entry = _live.GetOrAdd(name, _ => (_agentFactory.Create(config), new SemaphoreSlim(1, 1)));

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
