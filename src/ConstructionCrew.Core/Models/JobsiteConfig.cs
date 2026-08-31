namespace ConstructionCrew.Core.Models;

/// <summary>
/// A project GC is responsible for: a repo clone plus enough context that an
/// assigned Foreman can "know all about" the site. More than one Foreman may be
/// assigned to the same Jobsite (added 2026-08-31); GC can dispatch
/// different workorders to each. Nothing here tracks which Foremen are assigned;
/// that's read the other way, off <c>ForemanConfig.JobsiteName</c>.
/// </summary>
public sealed record JobsiteConfig(
    string Name,
    string RepoPath,
    string Description,
    string? RepoUrl = null,
    string? ColorName = null,
    // The branch a feature branch is cut from when a workorder carries no
    // explicit sourceBranch. Null falls through to "main" at dispatch time.
    string? DefaultBranch = null,
    // How this jobsite is built and tested. Rendered verbatim into a Foreman's
    // instructions; never parsed or executed by ConstructionCrew itself.
    string? BuildCommand = null,
    string? TestCommand = null,
    // URL to this jobsite's backlog or issue tracker (a GitHub Project board, a
    // Jira board, etc). Optional; null means none configured.
    string? BacklogUrl = null,
    // Vault-relative write scope for this Jobsite: "Notes/<Jobsite>",
    // "Plans/<Jobsite>" on a recognized vault layout, or whatever the Boss
    // named on an unrecognized one. Never absolute; resolved against the
    // configured VaultRoot at use time, so it survives the Vault moving.
    IReadOnlyList<string>? VaultFolders = null);
