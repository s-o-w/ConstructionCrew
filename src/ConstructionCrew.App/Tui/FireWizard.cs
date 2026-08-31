using ConstructionCrew.Config;
using ConstructionCrew.Core;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.HomeOffice;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Removes a Foreman from ConstructionCrew's own config and live state. NEVER
/// removes its Jobsite: other Foremen may be assigned to it, and even a solo
/// Foreman's Jobsite config (and its Vault content) is work the Boss may
/// still want.
///
/// HARD INVARIANT: this only ever edits/deletes files it itself generated
/// (foremen.yaml, a Foreman's own AI/ConstructionCrew/Instructions/&lt;name&gt;.md
/// in the Vault). It NEVER touches jobsites.yaml, tracked working-tree
/// content in a Jobsite repo, or anything in the Vault, except <c>git
/// worktree remove</c>/<c>prune</c>, which only rewrite
/// <c>.git/worktrees/</c> bookkeeping for worktrees ConstructionCrew opened.
/// </summary>
public static class FireWizard
{
    public static async Task Run(
        ForemanDirectory foremen,
        JobsiteDirectory jobsites,
        JobRegistry jobs,
        string repoRoot,
        string? vaultRoot,
        IWorktreeManager worktreeManager,
        CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule("[bold red]fire a foreman[/]").LeftJustified());

        var candidates = foremen.All()
            .Where(f => !f.Name.Equals("GC", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .ToList();

        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Nobody to fire -- only GC is hired.[/]");
            return;
        }

        var name = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Who[/] are you firing?")
                .AddChoices(candidates));

        var foreman = foremen.Find(name);
        if (foreman is null)
        {
            return;
        }

        if (jobs.IsForemanBusy(name))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(name)} has a job running right now.[/]");
            if (!AnsiConsole.Confirm("Fire anyway?", false))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return;
            }
        }

        var jobsite = foreman.JobsiteName is null ? null : jobsites.Find(foreman.JobsiteName);

        AnsiConsole.Write(new Panel(new Rows(
                new Markup($"[bold]Name:[/] {Markup.Escape(foreman.Name)}"),
                new Markup(jobsite is null
                    ? "[bold]Jobsite:[/] [grey]none[/]"
                    : $"[bold]Jobsite:[/] {Markup.Escape(jobsite.Name)} [grey](kept -- not removed, even if this was its only Foreman)[/]"),
                new Markup("[grey]This only removes this Foreman's own config in this tool. The jobsite's repo, its Vault content, and any other Foreman assigned to it are never touched.[/]")))
            .Header("confirm")
            .Border(BoxBorder.Rounded));

        if (!AnsiConsole.Confirm($"Fire {Markup.Escape(name)}?", false))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return;
        }

        var foremenYamlPath = Path.Combine(repoRoot, "config", "foremen.yaml");
        ForemanConfigWriter.RemoveForeman(foremenYamlPath, foreman.Name);
        foremen.Remove(foreman.Name);
        jobs.ForgetLiveAgent(foreman.Name);

        DeleteGeneratedInstructionsFile(foreman.InstructionsFilePath, vaultRoot);

        // The Jobsite is never removed here (see class doc comment). Prune is
        // safe regardless: it only clears stale .git/worktrees/ bookkeeping
        // for worktrees whose directory is already gone.
        //
        // NEVER repoRoot: that is ConstructionCrew's own checkout, not the
        // jobsite's. A Foreman with no Jobsite has no worktrees to clean up;
        // skipping the prune there is correct, not an error.
        if (!string.IsNullOrWhiteSpace(jobsite?.RepoPath))
        {
            await worktreeManager.PruneAsync(jobsite.RepoPath, cancellationToken);
        }

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(name)} is fired.[/] Config removed. The jobsite (its config and everything under it in the Vault) was not touched.");
    }

    /// <summary>Public so the "never deletes outside AI/ConstructionCrew/Instructions" invariant can be tested directly.</summary>
    public static void DeleteGeneratedInstructionsFile(string instructionsFilePath, string? vaultRoot)
    {
        // No Vault, no boundary to check against: never delete.
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            return;
        }

        // Only delete a file actually inside this tool's own
        // generated-instructions directory; never trust the path blindly.
        var instructionsDir = Path.GetFullPath(Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions"));
        var fullPath = Path.GetFullPath(instructionsFilePath);

        if (File.Exists(fullPath) && fullPath.StartsWith(instructionsDir, PathComparison.ForPathPrefix))
        {
            File.Delete(fullPath);
        }

        // The paired briefing sidecar (InstructionsComposer.BriefingFilePath)
        // is written alongside every rendered instructions file so it can be
        // re-rendered later without re-asking the Boss -- but nothing else
        // removes it, so it was left behind forever after a fire. Same
        // boundary check as above; the name comes from the file we just
        // deleted, not a new parameter, so every existing call site is
        // unaffected.
        var foremanName = Path.GetFileNameWithoutExtension(fullPath);
        var briefingPath = Path.GetFullPath(InstructionsComposer.BriefingFilePath(vaultRoot, foremanName));
        if (File.Exists(briefingPath) && briefingPath.StartsWith(instructionsDir, PathComparison.ForPathPrefix))
        {
            File.Delete(briefingPath);
        }
    }
}
