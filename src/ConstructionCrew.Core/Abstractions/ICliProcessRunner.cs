namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Actually spawns a CLI invocation and captures its result. The one seam that
/// touches a real process: swap for a fake in tests, never spawn for real there.
/// </summary>
public interface ICliProcessRunner
{
    Task<CliRunResult> RunAsync(CliInvocation invocation, CancellationToken cancellationToken);
}
