using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Live, mutable registry. Hiring a Foreman at runtime adds to this directly
/// (plus a separate append to foremen.yaml for persistence across restarts --
/// this class only holds the in-memory view for the running session).
/// </summary>
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
