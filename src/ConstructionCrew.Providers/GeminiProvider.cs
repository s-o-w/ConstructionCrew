using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>
/// Placeholder proving the provider registry accepts a fourth CLI. Gemini isn't
/// installed on the dev machine, so its flags are unverified; do not fill this in
/// from memory. Run `gemini --help` first (IMPLEMENTATION-PLAN.md Phase 3).
/// </summary>
public sealed class GeminiProvider : ICliToolProvider
{
    public string ProviderId => "gemini";

    /// <summary>
    /// Deliberately false. `gemini` is on PATH on at least one dev machine, so a
    /// pure PATH probe would offer it in the hire wizard and fail on first dispatch.
    /// The placeholder declares its own unreadiness instead of the registry
    /// hardcoding an id blocklist.
    /// </summary>
    public bool IsImplemented => false;

    public CliInvocation BuildInvocation(CliTaskRequest request) =>
        throw new NotSupportedException(
            "Gemini CLI support is not implemented -- its flags have not been verified against a real install.");
}
