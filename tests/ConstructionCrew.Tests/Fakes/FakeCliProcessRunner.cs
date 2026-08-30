using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>Never spawns a real process. Records invocations and returns a canned result.</summary>
public sealed class FakeCliProcessRunner : ICliProcessRunner
{
    public List<CliInvocation> Invocations { get; } = new();
    public CliRunResult NextResult { get; set; } = new(true, "ok", "", 0);

    /// <summary>
    /// Per-invocation result, for a caller that shells more than one command in
    /// one operation (GitWorkspaceInspector runs status and log). Null falls back
    /// to <see cref="NextResult"/>, so every existing use is unchanged.
    /// </summary>
    public Func<CliInvocation, CliRunResult>? Handler { get; set; }

    public Task<CliRunResult> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        Invocations.Add(invocation);
        return Task.FromResult(Handler?.Invoke(invocation) ?? NextResult);
    }
}
