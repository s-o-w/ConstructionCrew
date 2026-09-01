using ConstructionCrew.Core.Models;
using ConstructionCrew.HomeOffice;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The <c>/job</c> command: browse all jobs and page through any job's full
/// details — task text, summary/error, timestamps, cost. Modeled on
/// <see cref="InboxCommand"/>: SelectionPrompt loop, then a Pager for the body.
/// </summary>
public static class JobDetailCommand
{
    private const string Done = "(done)";

    public static void Run(JobRegistry jobs)
    {
        var all = jobs.GetAllJobs()
            .OrderByDescending(j => j.CreatedAt)
            .ToList();

        if (all.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No jobs yet.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold yellow]jobs[/]").LeftJustified());

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]Pick a job to view details:[/]")
                    .PageSize(20)
                    .AddChoices([.. all.Select(Label), Done]));

            if (choice == Done)
            {
                return;
            }

            var index = all.FindIndex(j => Label(j) == choice);
            if (index < 0)
            {
                continue;
            }

            ShowDetail(all[index]);
        }
    }

    private static void ShowDetail(JobRecord j)
    {
        var statusColor = j.Status switch
        {
            JobStatus.Failed => "red",
            JobStatus.Completed => "green",
            JobStatus.Parked => "magenta",
            JobStatus.Running or JobStatus.Pending => "yellow",
            _ => "grey",
        };

        var title = $"{j.ForemanName} — [{statusColor}]{j.Status}[/]";

        var blocks = new List<IRenderable>();

        // IDs and status
        blocks.Add(new Markup($"[grey]job id  [/] {Markup.Escape(j.JobId)}"));
        blocks.Add(new Markup($"[grey]foreman [/] [bold]{Markup.Escape(j.ForemanName)}[/]"));
        blocks.Add(new Markup($"[grey]status  [/] [{statusColor}]{j.Status}[/]"));
        blocks.Add(Text.Empty);

        // Timestamps and elapsed
        blocks.Add(new Markup($"[grey]created [/] {j.CreatedAt.ToLocalTime():g}"));
        if (j.StartedAt is { } startedAt)
        {
            blocks.Add(new Markup($"[grey]started [/] {startedAt.ToLocalTime():g}"));
        }
        if (j.CompletedAt is { } completedAt)
        {
            blocks.Add(new Markup($"[grey]finished[/] {completedAt.ToLocalTime():g}"));
            if (j.StartedAt is { } s)
            {
                var wall = completedAt - s;
                var actual = wall - j.ParkedDuration;
                blocks.Add(new Markup($"[grey]elapsed [/] {FormatSpan(actual)} wall, {FormatSpan(j.ParkedDuration)} parked"));
            }
        }

        // Cost
        if (j.Usage is { CostUsd: { } cost })
        {
            blocks.Add(new Markup($"[grey]cost    [/] ${cost:F4}"));
        }

        blocks.Add(Text.Empty);

        // Full task
        blocks.Add(new Markup("[bold]task[/]"));
        blocks.Add(new Markup(Markup.Escape(j.Task)));
        blocks.Add(Text.Empty);

        // Summary / error
        if (!string.IsNullOrWhiteSpace(j.Summary))
        {
            var summaryLabel = j.Status == JobStatus.Failed ? "[bold red]error[/]" : "[bold]result[/]";
            blocks.Add(new Markup(summaryLabel));
            blocks.Add(new Markup(Markup.Escape(j.Summary)));
        }

        Pager.Page(title, blocks);
    }

    internal static string Label(JobRecord j)
    {
        var statusTag = j.Status switch
        {
            JobStatus.Failed => "[red]FAIL[/]",
            JobStatus.Completed => "[green]DONE[/]",
            JobStatus.Parked => "[magenta]PARK[/]",
            JobStatus.Running => "[yellow]RUN [/]",
            JobStatus.Pending => "[yellow]WAIT[/]",
            _ => "[grey]?   [/]",
        };

        var preview = j.Task.Length > 50 ? j.Task[..50] + "…" : j.Task;
        var time = j.CreatedAt.ToLocalTime().ToString("HH:mm");
        return $"{statusTag} {Markup.Escape(j.ForemanName),-8} {Markup.Escape(preview)} ({time})";
    }

    private static string FormatSpan(TimeSpan t)
    {
        var clamped = t < TimeSpan.Zero ? TimeSpan.Zero : t;
        return clamped.TotalHours >= 1
            ? $"{(int)clamped.TotalHours}h{clamped.Minutes:00}m"
            : $"{(int)clamped.TotalMinutes}m{clamped.Seconds:00}s";
    }
}
