namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Knows one CLI tool's non-interactive flags and how to build an invocation for it.
/// Never spawns a process itself — that's <see cref="ICliProcessRunner"/>'s job.
/// </summary>
public interface ICliToolProvider
{
    /// <summary>The provider id a ForemanConfig.Provider value matches against, e.g. "claude", "codex", "copilot".</summary>
    string ProviderId { get; }

    CliInvocation BuildInvocation(CliTaskRequest request);
}
