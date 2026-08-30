namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Knows one CLI tool's non-interactive flags and how to build an invocation for it.
/// Never spawns a process itself — that's <see cref="ICliProcessRunner"/>'s job.
/// </summary>
public interface ICliToolProvider
{
    /// <summary>The provider id a ForemanConfig.Provider value matches against, e.g. "claude", "codex", "copilot".</summary>
    string ProviderId { get; }

    /// <summary>
    /// The executable ProviderRegistry probes for on PATH. Defaults to the provider
    /// id because every CLI wired so far ships a binary of exactly that name; a
    /// provider configured with an explicit path overrides this.
    /// </summary>
    string ExecutableName => ProviderId;

    /// <summary>
    /// False for a placeholder whose flags have never been verified against a real
    /// install -- GeminiProvider's deliberate throw is the reference case. The registry
    /// reads this instead of hard-coding an id blocklist, so a placeholder stays
    /// unavailable even on a machine where its binary happens to be on PATH.
    /// </summary>
    bool IsImplemented => true;

    CliInvocation BuildInvocation(CliTaskRequest request);
}
