using ConstructionCrew.Config;
using ConstructionCrew.Core;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The memory tab: navigate the vault locations the hired crew actually reads
/// and writes, and open a note. Roots are the union of every crew member's
/// VaultFolders -- not the whole vault -- so the Boss's unrelated notes are out
/// of scope by construction. Modal, like /view, and it renders through the same
/// MarkdownRenderer.
///
/// <para>
/// The scoping is a containment test, never a pattern match: <c>..</c> is offered
/// like any other entry and refused the moment the directory it lands on falls
/// outside every root, so there is no list of "bad" names to keep current.
/// </para>
/// </summary>
public static class MemoryBrowser
{
    private const string Up = "..";
    private const string Back = "(back)";
    private const string Done = "(done)";

    /// <summary>
    /// <paramref name="repoRoot"/> is taken to match <see cref="ViewCommand.Run"/>'s
    /// (vaultRoot, repoRoot) shape -- the browser itself never leaves the vault,
    /// so nothing here reads it.
    /// </summary>
    public static void Run(string? vaultRoot, ForemanDirectory foremen, string repoRoot)
    {
        _ = repoRoot;

        if (string.IsNullOrWhiteSpace(vaultRoot) || !Directory.Exists(vaultRoot))
        {
            Refuse("No Vault is configured -- run /settings (or set --vault-root) before browsing memory.");
            return;
        }

        var roots = Roots(vaultRoot, foremen);
        if (roots.Count == 0)
        {
            Refuse("No crew member has a vault folder that exists yet -- /hire someone, then come back.");
            return;
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold yellow]memory[/]").LeftJustified());

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]The vault folders the crew works in:[/]")
                    .PageSize(15)
                    .AddChoices([.. roots.Select(r => Relative(vaultRoot, r)), Done]));

            if (choice == Done)
            {
                return;
            }

            // Back to this same prompt when the folder walk is done, so the Boss
            // can read a note in one root and then another without retyping.
            Browse(Path.GetFullPath(Path.Combine(vaultRoot, choice)), roots);
        }
    }

    /// <summary>
    /// Every hired crew member's vault write scope, resolved against the vault
    /// root. An entry that resolves outside the vault (an absolute path, a
    /// <c>..</c> traversal) is dropped by the same containment test /view uses,
    /// and one that names a folder nobody has created yet is dropped because
    /// offering an empty prompt for it helps no one.
    /// </summary>
    internal static IReadOnlyList<string> Roots(string vaultRoot, ForemanDirectory foremen)
    {
        var fullVault = Path.GetFullPath(vaultRoot);
        var resolved = new List<string>();

        foreach (var member in foremen.All())
        {
            foreach (var entry in member.VaultFolders ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(fullVault, entry));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (!IsInsideAnyRoot(candidate, [fullVault]) || !Directory.Exists(candidate))
                {
                    continue;
                }

                resolved.Add(candidate);
            }
        }

        return resolved
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is one of the roots or sits under
    /// one. A root's own parent is deliberately outside: that is what stops
    /// <c>..</c> at the top of a crew folder instead of letting it walk up into
    /// the rest of the vault.
    /// </summary>
    internal static bool IsInsideAnyRoot(string candidate, IReadOnlyList<string> roots)
    {
        var full = Path.GetFullPath(candidate);
        var comparison = PathComparison.ForPathPrefix;

        foreach (var root in roots)
        {
            var fullRoot = Path.GetFullPath(root);

            if (full.Equals(fullRoot, comparison))
            {
                return true;
            }

            var withSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (full.StartsWith(withSeparator, comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One directory at a time: <c>..</c> first, then subdirectories, then files.
    /// A file goes straight to <see cref="ViewCommand.Page"/> -- the same paging
    /// loop /view uses, so a note reads identically whichever way it was reached.
    /// </summary>
    private static void Browse(string directory, IReadOnlyList<string> roots)
    {
        var current = directory;

        while (true)
        {
            List<string> directories;
            List<string> files;
            try
            {
                directories = [.. Directory.GetDirectories(current).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
                files = [.. Directory.GetFiles(current).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Refuse($"Could not read {current}: {ex.Message}");
                return;
            }

            var choices = new List<string> { Up };
            choices.AddRange(directories.Select(d => d + Path.DirectorySeparatorChar));
            choices.AddRange(files);
            choices.Add(Back);

            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold yellow]{Markup.Escape(current)}[/]").LeftJustified());

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey]Pick a folder to open, or a file to read:[/]")
                    .PageSize(20)
                    .AddChoices(choices));

            if (choice == Back)
            {
                return;
            }

            if (choice == Up)
            {
                var parent = Directory.GetParent(current)?.FullName;

                // Refused, not silently ignored: the Boss asked to go up, and the
                // answer is that this is the top of what the crew can see.
                if (parent is null || !IsInsideAnyRoot(parent, roots))
                {
                    Refuse("That is the top of the crew's vault folders -- /memory does not read the rest of the Vault.");
                    continue;
                }

                current = parent;
                continue;
            }

            if (choice.EndsWith(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, choice.TrimEnd(Path.DirectorySeparatorChar));
                continue;
            }

            ViewCommand.Page(Path.Combine(current, choice));
        }
    }

    /// <summary>A vault-relative label, so the prompt is readable on a deep vault path.</summary>
    private static string Relative(string vaultRoot, string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(vaultRoot), fullPath);

    private static void Refuse(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
    }
}
