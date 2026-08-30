using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>
/// Placeholder proving the provider registry is open to a fourth CLI. Gemini
/// CLI isn't installed on the dev machine, so its non-interactive flags have
/// never been verified -- do not fill this in from memory. Run `gemini --help`
/// for real first (IMPLEMENTATION-PLAN.md Phase 3).
/// </summary>
public sealed class GeminiProvider : ICliToolProvider
{
    public string ProviderId => "gemini";

    /// <summary>
    /// Deliberately false. `gemini` IS on PATH on at least one dev machine, so a
    /// pure PATH probe would happily offer it in the hire wizard and then blow up
    /// on the first dispatch. The placeholder declares its own unreadiness; the
    /// registry never carries a hand-written "id != gemini" filter.
    /// </summary>
    public bool IsImplemented => false;

    public CliInvocation BuildInvocation(CliTaskRequest request) =>
        throw new NotSupportedException(
            "Gemini CLI support is not implemented -- its flags have not been verified against a real install.");
}
