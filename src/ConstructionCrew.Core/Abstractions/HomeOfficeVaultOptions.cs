namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// The Vault-shaped settings the Home Office's tools need, carried as a plain
/// record so HomeOffice never has to reference the Config project (or take a
/// dependency on AppSettings as a whole). Nullable because first run
/// legitimately has no Vault configured yet -- every consumer must guard.
/// </summary>
public sealed record HomeOfficeVaultOptions(string? VaultRoot);
