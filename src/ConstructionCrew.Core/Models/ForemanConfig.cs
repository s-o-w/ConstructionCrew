namespace ConstructionCrew.Core.Models;

/// <summary>
/// A "hired" Foreman slot: which CLI backs it, where it works, and what instructions seed it.
/// </summary>
public sealed record ForemanConfig(
    string Name,
    string Provider,
    string WorkingDirectory,
    string InstructionsFilePath,
    IReadOnlyDictionary<string, string> ProviderOptions,
    string? JobsiteName = null);
