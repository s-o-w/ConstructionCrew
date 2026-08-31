using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>In-memory Foreman registry. Hiring at runtime adds here directly; a separate append to foremen.yaml persists it across restarts.</summary>
public sealed class ForemanDirectory : IForemanDirectory
{
    private readonly Dictionary<string, ForemanConfig> _byName;

    public ForemanDirectory(IEnumerable<ForemanConfig> foremen)
    {
        _byName = foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;

    public void Add(ForemanConfig config) => _byName[config.Name] = config;

    public bool Remove(string name) => _byName.Remove(name);
}
