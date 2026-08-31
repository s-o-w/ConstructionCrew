namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Shared shape for GC and Foreman: both are just "a hired CLI agent you send a
/// message to and get a result back from." GC's brain and a Foreman's Tool are
/// the same abstraction; only the prompt composed and who calls it differ.
/// </summary>
public interface ILocalCliAgent
{
    string Name { get; }

    /// <summary>
    /// The engine's own id for this agent's conversation, once a turn has
    /// reported one. Null before the first turn, and for any engine whose
    /// result envelope carries no session id.
    ///
    /// <para>
    /// Defaulted rather than required: an agent that is not a real CLI process
    /// (a test double) genuinely has no session, and saying so costs it nothing.
    /// </para>
    /// </summary>
    string? SessionId => null;

    Task<CliRunResult> SendAsync(string message, CancellationToken cancellationToken);
}

public interface ILocalCliAgentFactory
{
    ILocalCliAgent Create(Models.ForemanConfig config);
}
