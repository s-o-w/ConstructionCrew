using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>
/// A TaskCompletionSource-gated ILocalCliAgent: each armed call signals when it
/// STARTS and then blocks until it is explicitly released.
///
/// Deliberately not a Task.Delay-based "slow" fake. A fixed delay is a guess at
/// how long something takes; this is a real signal, so a test can assert on the
/// exact moment dispatch began without polling and without a timing race.
/// </summary>
public sealed class GatedFakeCliAgent : ILocalCliAgent
{
    private readonly Queue<(TaskCompletionSource Started, TaskCompletionSource Release)> _pending = new();
    private readonly object _lock = new();

    public GatedFakeCliAgent(string name = "gated-fake")
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Every message this agent was sent, in order.</summary>
    public ConcurrentQueue<string> Messages { get; } = new();

    public (Task Started, Action Release) ArmNextCall()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) { _pending.Enqueue((started, release)); }
        return (started.Task, () => release.TrySetResult());
    }

    public async Task<CliRunResult> SendAsync(string message, CancellationToken cancellationToken)
    {
        Messages.Enqueue(message);

        TaskCompletionSource started, release;
        lock (_lock) { (started, release) = _pending.Dequeue(); }
        started.TrySetResult();
        await release.Task;
        return new CliRunResult(true, "done", "", 0);
    }
}

/// <summary>
/// Always hands back the SAME agent, whatever config it is asked for -- matching
/// LiveAgentRegistry's real per-name caching, so two jobs dispatched to one
/// Foreman name really do contend for one agent (and one semaphore).
/// </summary>
public sealed class SingleAgentFactory : ILocalCliAgentFactory
{
    private readonly ILocalCliAgent _agent;

    public SingleAgentFactory(ILocalCliAgent agent)
    {
        _agent = agent;
    }

    public ILocalCliAgent Create(ForemanConfig config) => _agent;
}

/// <summary>
/// One gated agent per Foreman name, created on demand. Lets a test gate a
/// Foreman's turn and the GC's turn independently -- which is what ask_gc's
/// park/resume path needs.
/// </summary>
public sealed class PerNameGatedAgentFactory : ILocalCliAgentFactory
{
    private readonly ConcurrentDictionary<string, GatedFakeCliAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every Create() call, in order -- a second entry for one name means a divergent conversation.</summary>
    public ConcurrentQueue<string> CreateCalls { get; } = new();

    /// <summary>Gets (creating if needed) the agent for a name, so a test can arm it before dispatch.</summary>
    public GatedFakeCliAgent For(string name) => _agents.GetOrAdd(name, n => new GatedFakeCliAgent(n));

    public ILocalCliAgent Create(ForemanConfig config)
    {
        CreateCalls.Enqueue(config.Name);
        return For(config.Name);
    }
}

/// <summary>
/// Returns immediately with a canned result, recording every message per name.
/// The non-gated sibling of <see cref="PerNameGatedAgentFactory"/>, for tests
/// that care about WHICH conversation was used rather than about timing.
/// </summary>
public sealed class RecordingAgentFactory : ILocalCliAgentFactory
{
    private readonly ConcurrentDictionary<string, RecordingAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> CreateCalls { get; } = new();

    public RecordingAgent For(string name) => _agents.GetOrAdd(name, n => new RecordingAgent(n));

    /// <summary>Null when nothing ever dispatched to that name.</summary>
    public RecordingAgent? Existing(string name) => _agents.GetValueOrDefault(name);

    public ILocalCliAgent Create(ForemanConfig config)
    {
        CreateCalls.Enqueue(config.Name);
        return For(config.Name);
    }

    public sealed class RecordingAgent : ILocalCliAgent
    {
        public RecordingAgent(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public ConcurrentQueue<string> Messages { get; } = new();

        public string Reply { get; set; } = "ok";

        public Task<CliRunResult> SendAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Enqueue(message);
            return Task.FromResult(new CliRunResult(true, Reply, "", 0));
        }
    }
}
