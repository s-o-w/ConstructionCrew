using ConstructionCrew.Core;
using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Config;

/// <summary>
/// Append-only markdown sitreps, one file per day per altitude, inside the
/// caller's own Notes/ folder.
///
/// The composed path is prefix-checked against both the Vault root and the
/// caller's folder, using the same <see cref="PathComparison.ForPathPrefix"/>
/// the /fire delete guard uses, so a crafted VaultFolders entry or altitude
/// can't walk out with "..".
/// </summary>
public sealed class SitrepWriter : ISitrepWriter
{
    private const string SitrepsFolderName = "Sitreps";

    public string Write(SitrepRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VaultRoot))
        {
            throw new InvalidOperationException(
                $"No Vault is configured, so '{request.AuthoredBy}' has nowhere to file a sitrep.");
        }

        var notesFolder = FindNotesFolder(request.VaultFolders)
            ?? throw new InvalidOperationException(
                $"'{request.AuthoredBy}' has no vault folder under 'Notes/' in its write scope, so it cannot file a " +
                "sitrep. Ask the Boss to give it one (a Foreman hired against a recognized vault gets 'Notes/<Jobsite>').");

        var vaultRootFull = Path.GetFullPath(request.VaultRoot);
        var folderFull = Path.GetFullPath(Path.Combine(vaultRootFull, notesFolder));
        RequireInside(folderFull, vaultRootFull, request.AuthoredBy);

        var sitrepsDirectory = Path.GetFullPath(Path.Combine(folderFull, SitrepsFolderName));
        RequireInside(sitrepsDirectory, folderFull, request.AuthoredBy);

        var fileName = $"{DateTimeOffset.UtcNow:yyyy-MM-dd}-{request.Altitude}.md";
        var filePath = Path.GetFullPath(Path.Combine(sitrepsDirectory, fileName));
        // Checked innermost first: Sitreps folder, then caller's folder, then Vault root.
        RequireInside(filePath, sitrepsDirectory, request.AuthoredBy);
        RequireInside(filePath, folderFull, request.AuthoredBy);
        RequireInside(filePath, vaultRootFull, request.AuthoredBy);

        Directory.CreateDirectory(sitrepsDirectory);

        // The heading repeats the author because a shared jobsite's file can carry
        // sections from more than one Foreman; frontmatter only reflects who created it.
        var section = $"## {DateTimeOffset.UtcNow:HH:mm:ss} UTC -- {request.AuthoredBy}{Environment.NewLine}{Environment.NewLine}" +
                      $"{request.Body.Trim()}{Environment.NewLine}";

        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, Frontmatter(request, notesFolder) + Environment.NewLine + section);
        }
        else
        {
            File.AppendAllText(filePath, Environment.NewLine + section);
        }

        return filePath;
    }

    /// <summary>
    /// The first declared folder under Notes/. A Foreman's scope also carries
    /// Plans/&lt;Jobsite&gt;; a sitrep is a note, so only the Notes/ side is eligible.
    /// </summary>
    private static string? FindNotesFolder(IReadOnlyList<string>? vaultFolders) =>
        vaultFolders?.FirstOrDefault(f =>
            !string.IsNullOrWhiteSpace(f) &&
            Normalize(f).StartsWith("Notes/", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string folder) => folder.Replace('\\', '/').TrimStart('/');

    private static void RequireInside(string candidate, string root, string authoredBy)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, PathComparison.ForPathPrefix))
        {
            throw new InvalidOperationException(
                $"'{authoredBy}' tried to file a sitrep at '{candidate}', which is outside '{root}'. " +
                "A sitrep is only ever written inside the caller's own Notes/ folder.");
        }
    }

    /// <summary>
    /// touchesProject is derived from the folder itself (Notes/&lt;Jobsite&gt;), the
    /// only project signal a SitrepRequest carries. Written once, on the day's
    /// first sitrep; every later one appends a section below it.
    /// </summary>
    private static string Frontmatter(SitrepRequest request, string notesFolder)
    {
        var segments = Normalize(notesFolder).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var project = segments.Length > 1 ? segments[1] : null;

        var lines = new List<string>
        {
            "---",
            "type: \"[[SessionNote]]\"",
            $"date: {DateTimeOffset.UtcNow:yyyy-MM-dd}",
        };

        if (!string.IsNullOrWhiteSpace(project))
        {
            lines.Add($"touchesProject: \"[[{project}]]\"");
        }

        lines.Add($"authoredBy: \"{request.AuthoredBy}\"");
        lines.Add("---");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
