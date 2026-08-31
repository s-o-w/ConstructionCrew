using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// A folder/file picker built on Spectre's SelectionPrompt (arrow keys +
/// Enter), not raw keystroke capture -- SelectionPrompt already gives
/// arrow-navigation and Enter-to-choose for free, matching every other picker
/// in this app (HireWizard, FireWizard, MemoryBrowser, ForemanDetailsCommand).
/// Expand state persists in a HashSet across loop iterations, so more than
/// one branch of the tree can be open at once.
/// </summary>
public static class DirectoryPicker
{
    private const string Cancel = "(cancel)";
    private const string PickThis = "(pick this folder)";

    /// <summary>Null on cancel, or when startingDirectory doesn't exist (caller falls back to typing).</summary>
    public static string? Pick(string startingDirectory, bool allowFiles = false)
    {
        var root = Path.GetFullPath(startingDirectory);
        if (!Directory.Exists(root))
        {
            return null;
        }

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };

        while (true)
        {
            var rows = new List<(string Label, string? Path, bool IsFolder)> { (PickThis, root, true) };
            AppendChildren(rows, root, expanded, depth: 1, allowFiles);
            rows.Add((Cancel, null, false));

            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold yellow]{Markup.Escape(root)}[/]").LeftJustified());

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<(string Label, string? Path, bool IsFolder)>()
                    .Title("[grey]Enter a folder to expand or collapse it; pick a file or \"(pick this folder)\" to choose:[/]")
                    .PageSize(20)
                    .UseConverter(r => r.Label)
                    .AddChoices(rows));

            if (choice.Label == Cancel)
            {
                return null;
            }

            if (choice.Label == PickThis)
            {
                return choice.Path;
            }

            if (choice.IsFolder)
            {
                if (!expanded.Remove(choice.Path!))
                {
                    expanded.Add(choice.Path!);
                }

                continue;
            }

            return choice.Path;
        }
    }

    /// <summary>
    /// Flattens one level of <paramref name="directory"/>'s children into
    /// <paramref name="rows"/>, recursing into any child already in
    /// <paramref name="expanded"/>. Pure with respect to the filesystem it
    /// reads -- no console I/O -- so it's directly testable.
    /// </summary>
    internal static void AppendChildren(
        List<(string Label, string? Path, bool IsFolder)> rows,
        string directory,
        HashSet<string> expanded,
        int depth,
        bool allowFiles)
    {
        var indent = new string(' ', depth * 2);

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var dir in dirs)
        {
            var isExpanded = expanded.Contains(dir);
            rows.Add(($"{indent}{(isExpanded ? "\U0001F4C2" : "\U0001F4C1")} {Path.GetFileName(dir)}", dir, true));
            if (isExpanded)
            {
                AppendChildren(rows, dir, expanded, depth + 1, allowFiles);
            }
        }

        if (!allowFiles)
        {
            return;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            rows.Add(($"{indent}\U0001F4C4 {Path.GetFileName(file)}", file, false));
        }
    }
}
