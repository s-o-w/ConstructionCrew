using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>Never spawns a real process. Records invocations and returns a canned result.</summary>
public sealed class FakeCliProcessRunner : ICliProcessRunner
{
    public List<CliInvocation> Invocations { get; } = new();
    public CliRunResult NextResult { get; set; } = new(true, "ok", "", 0);

    public Task<CliRunResult> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        Invocations.Add(invocation);
        return Task.FromResult(NextResult);
    }
}
