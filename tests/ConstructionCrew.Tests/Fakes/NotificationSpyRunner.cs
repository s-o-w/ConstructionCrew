using System.Collections.Concurrent;
using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>
/// Stands in for the NotificationsCommand shell-out. Records every invocation and
/// signals the first one, so a test can await a fire-and-forget notification
/// instead of sleeping on it. Never spawns a process.
/// </summary>
public sealed class NotificationSpyRunner : ICliProcessRunner
{
    private readonly ConcurrentQueue<CliInvocation> _invocations = new();
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<CliInvocation> Invocations => _invocations.ToList();

    /// <summary>Completes as soon as one invocation has been recorded.</summary>
    public Task FirstInvocation => _first.Task;

    public Task<CliRunResult> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        _invocations.Enqueue(invocation);
        _first.TrySetResult();
        return Task.FromResult(new CliRunResult(true, "", "", 0));
    }
}
