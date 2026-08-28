namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Shared shape for GC and Foreman: both are just "a hired CLI agent you send a
/// message to and get a result back from." GC's brain and a Foreman's Tool are
/// the same abstraction; only the prompt composed and who calls it differ.
/// </summary>
public interface ILocalCliAgent
{
    string Name { get; }

    Task<CliRunResult> SendAsync(string message, CancellationToken cancellationToken);
}

/// <summary>Builds an <see cref="ILocalCliAgent"/> for a given Foreman/GC configuration.</summary>
public interface ILocalCliAgentFactory
{
    ILocalCliAgent Create(Models.ForemanConfig config);
}
