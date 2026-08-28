namespace ConstructionCrew.Core.Models;

/// <summary>
/// A project GC is responsible for -- a repo clone plus enough context that a
/// Foreman assigned to it can "know all about" the site. Strictly one Foreman
/// per Jobsite by design (Shawn's call, 2026-08-28).
/// </summary>
public sealed record JobsiteConfig(
    string Name,
    string RepoPath,
    string Description,
    string? RepoUrl = null,
    string? ColorName = null);
