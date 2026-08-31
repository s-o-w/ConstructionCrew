namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Knows one CLI tool's non-interactive flags and how to build an invocation for it.
/// Never spawns a process itself; that's <see cref="ICliProcessRunner"/>'s job.
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
    /// install (GeminiProvider's deliberate throw is the reference case). The
    /// registry reads this instead of hardcoding an id blocklist, so a placeholder
    /// stays unavailable even when its binary is on PATH.
    /// </summary>
    bool IsImplemented => true;

    CliInvocation BuildInvocation(CliTaskRequest request);

    /// <summary>
    /// A chance for a provider to read its own structured output off a finished
    /// run: token/cost accounting, and unwrapping a machine-readable envelope back
    /// to the plain answer text. Called by LocalCliAgent with the same request
    /// BuildInvocation received, so a provider can tell whether it asked for
    /// structured output this turn.
    ///
    /// Default hands the result back unchanged. A provider whose usage output has
    /// never been verified against a real run leaves Usage null rather than
    /// guessing at a shape, the same discipline as GeminiProvider's deliberate throw.
    /// </summary>
    CliRunResult PostProcess(CliTaskRequest request, CliRunResult result) => result;
}
