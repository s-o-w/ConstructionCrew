using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Memory;

/// <summary>Phase 1 placeholder -- MemPalace isn't stood up yet (see IMPLEMENTATION-PLAN.md Phase 3).</summary>
public sealed class NullMemoryClient : IMemoryClient
{
    public Task RecordAsync(string note, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
}
