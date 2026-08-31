using Spectre.Console;
using Spectre.Console.Rendering;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The full-screen, paged "-- more (enter) --" rendering loop shared by
/// /view (<see cref="ViewCommand"/>) and /inbox (<see cref="InboxCommand"/>).
/// Extracted out of ViewCommand.Page so a second reader of a long block of
/// text doesn't reimplement the same loop.
/// </summary>
internal static class Pager
{
    internal static void Page(string title, IEnumerable<IRenderable> blocks)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold yellow]{Markup.Escape(title)}[/]").LeftJustified());

        // -4: the rule, the "-- more --" line, and slack either side, so a
        // page never scrolls its own prompt off the top.
        var pageHeight = Math.Max(4, AnsiConsole.Profile.Height - 4);
        var used = 0;

        foreach (var block in blocks)
        {
            AnsiConsole.Write(block);
            used += EstimateLines(block);

            if (used < pageHeight)
            {
                continue;
            }

            AnsiConsole.Markup("[grey]-- more (enter) --[/]");
            Console.ReadLine();
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold yellow]{Markup.Escape(title)}[/]").LeftJustified());
            used = 0;
        }

        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
    }

    /// <summary>
    /// How many console rows a block will occupy, including wrapping and
    /// borders. Renders it once into a throwaway plain-text console rather
    /// than guessing per renderable type.
    /// </summary>
    internal static int EstimateLines(IRenderable block)
    {
        try
        {
            var writer = new StringWriter();
            var probe = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(writer),
            });

            probe.Profile.Width = Math.Max(20, AnsiConsole.Profile.Width);
            probe.Write(block);

            return Math.Max(1, writer.ToString().Count(c => c == '\n'));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return 1;
        }
    }
}
