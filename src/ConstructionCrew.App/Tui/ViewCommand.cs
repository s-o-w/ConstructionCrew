using ConstructionCrew.Core;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The <c>/view &lt;path&gt;</c> command: render a crew-authored Markdown file
/// into the console through <see cref="MarkdownRenderer"/>, paging on
/// anything longer than the window.
///
/// <para>
/// Modal and full-width, not a side panel: the dashboard's main column
/// already shares width with a 38-column passive column in drive mode, and a
/// workorder rendered into what's left is unreadable.
/// </para>
///
/// <para>
/// The reachable set is closed: a resolved path must sit under the Vault
/// root or the repo root, must exist, and must carry a text extension.
/// <c>/view ../../.ssh/id_rsa</c> is refused by the containment test, not by
/// pattern-matching what it looks like.
/// </para>
/// </summary>
public static class ViewCommand
{
    private const string Verb = "/view";

    private static readonly string[] ViewableExtensions = [".md", ".txt", ".yaml", ".yml"];

    /// <summary>
    /// Parses <c>/view Notes/Frontend/Sitewalk.md</c>. <paramref name="target"/>
    /// comes back empty for a bare <c>/view</c> (prints usage); <c>/viewx</c>
    /// is not this command at all.
    /// </summary>
    internal static bool TryParse(string command, out string target)
    {
        target = string.Empty;

        var trimmed = command.Trim();
        if (!trimmed.StartsWith(Verb, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[Verb.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
        {
            return false;
        }

        target = rest.Trim();
        return true;
    }

    public static void Run(string argument, string? vaultRoot, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            AnsiConsole.MarkupLine("[grey]/view <path> -- a .md, .txt, .yaml or .yml file under the Vault or the repo.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        var resolved = Resolve(argument, vaultRoot, repoRoot, out var refusal);
        if (resolved is null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(refusal ?? "Can't view that.")}[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        Page(resolved);
    }

    /// <summary>
    /// Reads a resolved, already-authorized path and pages it through the
    /// renderer. Split out from <see cref="Run"/> because the memory browser
    /// navigates to a file its own way and then needs exactly this loop.
    /// </summary>
    internal static void Page(string path)
    {
        string markdown;
        try
        {
            markdown = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Could not read {Markup.Escape(path)}:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return;
        }

        Pager.Page(path, MarkdownRenderer.RenderBlocks(markdown));
    }

    /// <summary>
    /// An absolute path is taken as given; anything else resolves against the
    /// Vault root first, then the repo root. The result must pass three
    /// checks in order: sits under one of those two roots, exists, is a text
    /// file. Containment is checked first so a traversal out of the Vault is
    /// refused as a traversal, not by leaking whether the target exists.
    /// </summary>
    internal static string? Resolve(string argument, string? vaultRoot, string repoRoot, out string? refusal)
    {
        refusal = null;

        var trimmed = (argument ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            refusal = "Usage: /view <path>";
            return null;
        }

        string candidate;
        if (Path.IsPathRooted(trimmed))
        {
            candidate = Path.GetFullPath(trimmed);
        }
        else
        {
            candidate = string.Empty;

            if (!string.IsNullOrWhiteSpace(vaultRoot))
            {
                var underVault = Path.GetFullPath(Path.Combine(vaultRoot, trimmed));
                if (File.Exists(underVault))
                {
                    candidate = underVault;
                }
            }

            if (candidate.Length == 0)
            {
                candidate = Path.GetFullPath(Path.Combine(repoRoot, trimmed));
            }
        }

        if (!IsUnder(candidate, vaultRoot) && !IsUnder(candidate, repoRoot))
        {
            refusal = $"'{trimmed}' is outside both the Vault and the repo -- /view only reads files under those two roots.";
            return null;
        }

        if (!File.Exists(candidate))
        {
            refusal = $"No file at {candidate}.";
            return null;
        }

        var extension = Path.GetExtension(candidate);
        if (!ViewableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            refusal = $"'{extension}' isn't viewable -- /view reads {string.Join(", ", ViewableExtensions)}.";
            return null;
        }

        return candidate;
    }

    private static bool IsUnder(string fullPath, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root);
        var comparison = PathComparison.ForPathPrefix;

        if (fullPath.Equals(fullRoot, comparison))
        {
            return true;
        }

        var withSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(withSeparator, comparison);
    }
}
