using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Core.Abstractions;

/// <summary>The registry of hired Foremen, loaded from config.</summary>
public interface IForemanDirectory
{
    ForemanConfig? Find(string name);

    IReadOnlyCollection<ForemanConfig> All();
}
