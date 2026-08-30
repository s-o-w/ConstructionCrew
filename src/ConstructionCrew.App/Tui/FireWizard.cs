using ConstructionCrew.Config;
using ConstructionCrew.Core;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.HomeOffice;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Removes a Foreman (and its strictly-one-to-one Jobsite) from
/// ConstructionCrew's own config and live state.
///
/// HARD INVARIANT, never to be relaxed: this only ever edits/deletes files
/// this tool itself generated (foremen.yaml, jobsites.yaml, a Foreman's
/// config/instructions/<name>.md). It NEVER touches tracked working-tree
/// content in a Jobsite repo, with exactly one exception:
/// <c>git worktree remove</c>/<c>prune</c>, which only ever rewrite
/// <c>.git/worktrees/</c> bookkeeping for worktrees ConstructionCrew itself
/// opened. Those repos are the Boss's, not this tool's to touch.
/// </summary>
public static class FireWizard
{
    public static async Task Run(
        ForemanDirectory foremen,
        JobsiteDirectory jobsites,
        JobRegistry jobs,
        string repoRoot,
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
                    : $"[bold]Jobsite (also removed):[/] {Markup.Escape(jobsite.Name)}"),
                new Markup("[grey]This only removes config in this tool. The jobsite's repo on disk is never touched.[/]")))
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

        DeleteGeneratedInstructionsFile(foreman.InstructionsFilePath, repoRoot);

        if (jobsite is not null)
        {
            var jobsitesYamlPath = Path.Combine(repoRoot, "config", "jobsites.yaml");
            JobsiteConfigWriter.RemoveJobsite(jobsitesYamlPath, jobsite.Name);
            jobsites.Remove(jobsite.Name);
        }

        // NEVER repoRoot -- that is ConstructionCrew's own checkout, not the
        // jobsite's. A Foreman with no Jobsite (GC, or one never assigned one) has
        // no worktrees to clean up: skipping the whole prune is the correct
        // outcome there, not an error.
        if (!string.IsNullOrWhiteSpace(jobsite?.RepoPath))
        {
            await worktreeManager.PruneAsync(jobsite.RepoPath, cancellationToken);
        }

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(name)} is fired.[/] Config removed. The jobsite's repo was not touched.");
    }

    /// <summary>Public so the "never deletes outside config/instructions" invariant can be tested directly, not just code-reviewed.</summary>
    public static void DeleteGeneratedInstructionsFile(string instructionsFilePath, string repoRoot)
    {
        // Only ever delete a file that's actually inside this tool's own
        // generated-config directory -- never trust the path blindly, even
        // though it should always point there for a Foreman hired via /hire.
        var instructionsDir = Path.GetFullPath(Path.Combine(repoRoot, "config", "instructions"));
        var fullPath = Path.GetFullPath(instructionsFilePath);

        if (File.Exists(fullPath) && fullPath.StartsWith(instructionsDir, PathComparison.ForPathPrefix))
        {
            File.Delete(fullPath);
        }
    }
}
