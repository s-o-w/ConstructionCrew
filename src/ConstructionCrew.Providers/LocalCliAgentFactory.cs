using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Providers;

public sealed class LocalCliAgentFactory : ILocalCliAgentFactory
{
    private readonly IReadOnlyDictionary<string, ICliToolProvider> _providersById;
    private readonly ICliProcessRunner _runner;

    public LocalCliAgentFactory(IEnumerable<ICliToolProvider> providers, ICliProcessRunner runner)
    {
        _providersById = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        _runner = runner;
    }

    public ILocalCliAgent Create(ForemanConfig config)
    {
        if (!_providersById.TryGetValue(config.Provider, out var provider))
        {
            throw new InvalidOperationException(
                $"No CLI provider registered for '{config.Provider}' (Foreman '{config.Name}'). " +
                $"Known providers: {string.Join(", ", _providersById.Keys)}.");
        }

        return new LocalCliAgent(config, provider, _runner);
    }
}
