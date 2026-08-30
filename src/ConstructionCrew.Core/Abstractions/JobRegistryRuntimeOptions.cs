namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Plain settings carrier for JobRegistry -- no interface, same reasoning as
/// <see cref="HomeOfficeVaultOptions"/>: it is data the composition root already
/// resolved, not a seam anything needs to fake.
///
/// <paramref name="StateDirectory"/> is where a Worker's worktree is opened
/// (<c>&lt;StateDirectory&gt;/worktrees/&lt;Jobsite&gt;/worker-&lt;id&gt;</c>), the same
/// state/ directory tools.json already lives in.
/// <paramref name="AskGcTimeout"/> is Phase 7's ask_gc bound; unused until then.
/// </summary>
public sealed record JobRegistryRuntimeOptions(string StateDirectory, TimeSpan? AskGcTimeout = null);
