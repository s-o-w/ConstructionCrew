using ConstructionCrew.Config;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The <c>/preferences</c> command: read or add to the crew's standing
/// preferences file (<see cref="InstructionsComposer.CrewPreferencesPath"/>),
/// without leaving the TUI to hand-edit it.
///
/// <para>
/// Deliberately two verbs only, view and add. The file's own documented shape
/// ("An empty section means no preference: use your own judgement") is
/// unstructured free text -- this does not build a structured multi-section
/// editor for it.
/// </para>
/// </summary>
public static class PreferencesCommand
{
    private const string TrailingNote = "---";

    public static void Run(string? argument, string? vaultRoot, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            AnsiConsole.MarkupLine("[red]No Vault is configured -- run /settings first.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        var path = InstructionsComposer.CrewPreferencesPath(vaultRoot);

        if (string.Equals(argument?.Trim(), "add", StringComparison.OrdinalIgnoreCase))
        {
            Add(path);
            return;
        }

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]No file at {Markup.Escape(path)} yet -- it's seeded on first launch.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Editing this directly also works: {Markup.Escape(path)}[/]");
        ViewCommand.Page(path);
    }

    private static void Add(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]No file at {Markup.Escape(path)} yet -- it's seeded on first launch.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        var line = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Add a preference[/] -- one line, appended under Conventions (blank to cancel):")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var updated = AppendUnderConventions(File.ReadAllText(path), line.Trim());
        File.WriteAllText(path, updated);

        AnsiConsole.MarkupLine("[green]Added.[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
    }

    /// <summary>
    /// Inserts <paramref name="newLine"/> as a new, uncommented line directly
    /// above the file's trailing "---" separator (the file's own documented
    /// shape -- see crew-preferences.md). Falls back to appending at the end
    /// if that separator isn't found, rather than losing the line.
    /// </summary>
    internal static string AppendUnderConventions(string content, string newLine)
    {
        var lines = content.Split('\n').ToList();

        var separatorIndex = lines.FindLastIndex(l => l.Trim() == TrailingNote);
        if (separatorIndex < 0)
        {
            var trimmed = content.TrimEnd('\n', '\r');
            return trimmed + Environment.NewLine + newLine + Environment.NewLine;
        }

        lines.Insert(separatorIndex, newLine);
        return string.Join('\n', lines);
    }
}
