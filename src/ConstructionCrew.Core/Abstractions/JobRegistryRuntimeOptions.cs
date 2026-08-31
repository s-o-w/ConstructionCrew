namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Plain settings carrier for JobRegistry, same reasoning as
/// <see cref="HomeOfficeVaultOptions"/>: data the composition root already
/// resolved, not a seam anything needs to fake.
///
/// <paramref name="StateDirectory"/> is where a Worker's worktree opens
/// (<c>&lt;StateDirectory&gt;/worktrees/&lt;Jobsite&gt;/worker-&lt;id&gt;</c>), the same
/// state/ directory tools.json lives in. <paramref name="AskGcTimeout"/> is
/// Phase 7's ask_gc bound; unused until then.
/// </summary>
public sealed record JobRegistryRuntimeOptions(string StateDirectory, TimeSpan? AskGcTimeout = null);
