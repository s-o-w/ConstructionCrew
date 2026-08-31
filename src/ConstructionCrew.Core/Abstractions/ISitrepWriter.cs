namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Writes a crew member's sitrep into the Vault. A seam, not a helper: the
/// Home Office calls it, ConstructionCrew.Config implements it, and neither
/// project references the other.
/// </summary>
public interface ISitrepWriter
{
    /// <summary>Appends the sitrep and returns the absolute path written to.</summary>
    string Write(SitrepRequest request);
}

/// <summary>
/// Everything the writer needs, already resolved by the MCP boundary.
/// <paramref name="VaultFolders"/> is the caller's declared write scope: the
/// sitrep lands under its first <c>Notes/</c> entry and nowhere else.
/// </summary>
public sealed record SitrepRequest(
    string VaultRoot,
    IReadOnlyList<string> VaultFolders,
    string Altitude,
    string Body,
    string AuthoredBy);
