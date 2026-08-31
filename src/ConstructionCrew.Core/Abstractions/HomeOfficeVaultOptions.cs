namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// The Vault-shaped settings Home Office's tools need, as a plain record so
/// HomeOffice never references the Config project (or depends on AppSettings as
/// a whole). Nullable because a first run has no Vault configured yet; every
/// consumer must guard.
/// </summary>
public sealed record HomeOfficeVaultOptions(string? VaultRoot);
