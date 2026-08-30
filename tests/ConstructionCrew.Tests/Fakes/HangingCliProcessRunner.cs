using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>
/// An ICliProcessRunner whose runs never finish until the test says so -- so a
/// dispatched job stays genuinely in flight while the test asserts on the state it
/// claimed (its workorder slot, its ActiveWorkorder).
///
/// This exists because a job's completion is not inert: it consumes the job's
/// ActiveWorkorder and clears the Foreman's busy slot. A fake that returns
/// instantly would race every such assertion. Nothing here spawns a process.
/// </summary>
public sealed class HangingCliProcessRunner : ICliProcessRunner
{
    private readonly TaskCompletionSource<CliRunResult> _hung = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private readonly List<CliInvocation> _invocations = new();

    /// <summary>Which invocations hang. Default: all of them.</summary>
    public Func<CliInvocation, bool> ShouldHang { get; set; } = _ => true;

    /// <summary>What a non-hanging invocation (and a released hung one) returns.</summary>
    public CliRunResult NextResult { get; set; } = new(true, "done", "", 0);

    public IReadOnlyList<CliInvocation> Invocations
    {
        get { lock (_lock) { return _invocations.ToList(); } }
    }

    public Task<CliRunResult> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _invocations.Add(invocation);
        }

        return ShouldHang(invocation) ? _hung.Task : Task.FromResult(NextResult);
    }

    /// <summary>Lets every hung run finish, so no background job is left dangling.</summary>
    public void Release() => _hung.TrySetResult(NextResult);
}
