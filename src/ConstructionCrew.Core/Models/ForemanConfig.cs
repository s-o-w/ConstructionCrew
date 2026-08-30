namespace ConstructionCrew.Core.Models;

/// <summary>
/// Which seat on the crew a config fills. Metadata only -- Name stays the
/// canonical lookup key everywhere ("GC" is the reserved name). Role is used
/// for exactly three things: picking the instructions template, deriving the
/// authoredBy string, and load-time validation.
/// </summary>
public enum CrewRole
{
    GC,
    Foreman,
}

/// <summary>
/// A "hired" Foreman slot: which CLI backs it, where it works, and what instructions seed it.
/// </summary>
public sealed record ForemanConfig(
    string Name,                                            // canonical key. "GC" for the GC. Never localized.
    CrewRole Role,                                          // positional, after Name -- a GC silently defaulting to Foreman is worth a compiler error
    string Provider,
    string WorkingDirectory,
    string InstructionsFilePath,
    IReadOnlyDictionary<string, string> ProviderOptions,
    string? JobsiteName = null,
    string? DisplayName = null,                             // what the Boss calls it; UI only, never a lookup key
    IReadOnlyList<string>? AddDirs = null,                  // absolute dirs -> --add-dir
    IReadOnlyList<string>? VaultFolders = null);            // vault-relative write scope
