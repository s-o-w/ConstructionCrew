using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The <c>/inbox</c> command: pick through messages Foremen sent in while the
/// Boss was doing something else (milestone sitreps -- see
/// <see cref="DashboardState.Inbox"/>). Modeled directly on
/// <see cref="MemoryBrowser"/>'s own pick-loop: a SelectionPrompt with a
/// "(done)" sentinel, looping back after each read so several messages can be
/// read in one visit.
/// </summary>
public static class InboxCommand
{
    private const string Done = "(done)";

    public static void Run(DashboardState state)
    {
        if (state.Inbox.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Nothing waiting.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold yellow]inbox[/]").LeftJustified());

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]Pick a message to read:[/]")
                    .PageSize(15)
                    .AddChoices([.. state.Inbox.Select(Label), Done]));

            if (choice == Done)
            {
                return;
            }

            var index = state.Inbox.FindIndex(i => Label(i) == choice);
            if (index < 0)
            {
                continue;
            }

            var item = state.Inbox[index];
            state.Inbox[index] = item with { Read = true };

            Pager.Page($"{item.From} -- {item.ReceivedAt.ToLocalTime():g}", [new Markup(Markup.Escape(item.Text))]);
        }
    }

    internal static string Label(InboxItem item) =>
        $"{(item.Read ? " " : "*")} {item.From} -- {FirstLine(item.Text)} ({item.ReceivedAt.ToLocalTime():HH:mm})";

    /// <summary>Same one-line-preview shape as FileSitrepTool.MilestoneSummary's own first-line extraction.</summary>
    private static string FirstLine(string text)
    {
        var firstLine = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
            ?.Trim() ?? "(no detail given)";

        return firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
    }
}
