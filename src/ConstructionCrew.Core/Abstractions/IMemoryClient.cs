namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Generic shared-memory abstraction. Phase 1 ships a no-op implementation;
/// Phase 3 wires MemPalace's MCP server behind it. Fallback path only: GC/Foremen
/// primarily touch memory through their own hired CLI's native MCP client hitting
/// MemPalace directly, not this interface.
/// </summary>
public interface IMemoryClient
{
    Task RecordAsync(string note, CancellationToken cancellationToken);

    Task<string> SearchAsync(string query, CancellationToken cancellationToken);
}
