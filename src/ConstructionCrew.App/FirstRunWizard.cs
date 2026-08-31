using System.Text.Json;
using System.Text.Json.Nodes;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using Spectre.Console;

namespace ConstructionCrew.App;

/// <summary>
/// Produces the GC when no foremen.yaml exists yet, so there is nothing for
/// the Boss to talk to.
///
/// Hooks off <c>File.Exists(settings.ForemenConfigPath)</c> in Program.cs, not
/// the "no Foreman named GC" branch: a missing file is a fresh install, while
/// a config with no GC entry is a broken roster and stays a hard fail.
///
/// Flow: resolve Vault -> recognize layout -> hire GC -> write foremen.yaml ->
/// persist Vault path. Returns the resolved Vault path so Program.cs can do
/// <c>settings = settings with { VaultRoot = resolved }</c>: AppSettings is a
/// record, so without that reassignment <c>${vaultRoot}</c> would fail to
/// expand on this same run.
/// </summary>
public static class FirstRunWizard
{
    /// <summary>
    /// GC's vault write scope. Order matters: SitrepWriter.FindNotesFolder uses
    /// the FIRST entry under Notes/, where GC's sitreps land. "Notes" and
    /// "Plans" cover GC's cross-jobsite writes: workorders under
    /// Plans/&lt;Jobsite&gt;/&lt;Feature&gt;/ and Delivery notes under
    /// Notes/&lt;Jobsite&gt;/Deliveries/.
    /// </summary>
    public static readonly IReadOnlyList<string> GcVaultFolders = ["Notes/GC", "Notes", "Plans"];

    /// <summary>Runs the first-run flow. Returns the resolved Vault path, or null if the Boss backed out.</summary>
    public static string? Run(string repoRoot, AppSettings settings, IReadOnlyList<string> availableProviderIds)
    {
        AnsiConsole.Write(new Rule("[bold yellow]first run[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            "[grey]No Foreman roster yet. Let's set up your Vault and hire the GC.[/]");
        AnsiConsole.WriteLine();

        if (availableProviderIds.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]No working CLI providers are installed -- there's nothing to hire a GC with.[/] " +
                "Install one (claude, codex, or copilot) and start again.");
            return null;
        }

        var vaultRoot = ResolveAndConfirmVault(repoRoot);
        if (vaultRoot is null)
        {
            return null;
        }

        var provider = availableProviderIds.Count == 1
            ? availableProviderIds[0]
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Engine[/] -- which CLI backs the GC?")
                    .AddChoices(availableProviderIds));

        var displayName = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Display name[/] for the GC (optional, blank to just call it 'GC'):")
                .AllowEmpty());

        var instructionsFilePath = EnsureGcInstructions(repoRoot, vaultRoot, settings.GcForemanName, availableProviderIds);

        var config = BuildGcConfig(
            settings.GcForemanName,
            provider,
            vaultRoot,
            instructionsFilePath,
            repoRoot,
            string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim());

        AnsiConsole.Write(new Panel(new Rows(
                new Markup($"[bold]Vault:[/] {Markup.Escape(vaultRoot)}"),
                new Markup($"[bold]GC name:[/] {Markup.Escape(config.Name)} (reserved)"),
                new Markup($"[bold]Display name:[/] {Markup.Escape(config.DisplayName ?? "-")}"),
                new Markup($"[bold]Engine:[/] {Markup.Escape(config.Provider)}")))
            .Header("confirm")
            .Border(BoxBorder.Rounded));

        if (!AnsiConsole.Confirm("Hire this GC?", true))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled -- nothing was written.[/]");
            return null;
        }

        ForemanConfigWriter.AppendForeman(settings.ForemenConfigPath, config, repoRoot, vaultRoot);
        PersistVaultRoot(Path.Combine(repoRoot, "appsettings.json"), vaultRoot);

        AnsiConsole.MarkupLine($"[bold green]GC hired.[/] Roster written to {Markup.Escape(settings.ForemenConfigPath)}.");
        AnsiConsole.MarkupLine($"[grey]Vault root saved to appsettings.json: {Markup.Escape(vaultRoot)}[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();

        return vaultRoot;
    }

    /// <summary>
    /// The GC's ForemanConfig, exactly as first run writes it.
    /// - <c>Name</c> is always the reserved GcForemanName; the loader rejects
    ///   any other spelling.
    /// - <c>Role</c> is always <see cref="CrewRole.GC"/>.
    /// - <c>WorkingDirectory</c> is the VAULT, not this repo: a CLI auto-loads
    ///   the CLAUDE.md/AGENTS.md in its own cwd, so GC only inherits the
    ///   vault's conventions if the vault is its cwd. The repo is added via
    ///   AddDirs instead.
    /// <c>DisplayName</c> is the one optional field: UI only, never a lookup key.
    /// </summary>
    public static ForemanConfig BuildGcConfig(
        string gcForemanName,
        string provider,
        string vaultRoot,
        string instructionsFilePath,
        string repoRoot,
        string? displayName) =>
        new(
            gcForemanName,
            CrewRole.GC,
            provider,
            vaultRoot,
            instructionsFilePath,
            new Dictionary<string, string>(ProviderDefaults.GcToolPolicy(provider)),
            JobsiteName: null,
            DisplayName: displayName,
            AddDirs: [repoRoot],
            VaultFolders: GcVaultFolders);

    /// <summary>
    /// Writes Vault:Root into appsettings.json, merging into the existing file
    /// rather than rewriting it: HomeOffice:Port lives in the same file and
    /// must survive. A malformed or missing file is replaced with a fresh document.
    /// </summary>
    public static void PersistVaultRoot(string appSettingsPath, string vaultRoot)
    {
        JsonObject root;
        try
        {
            root = File.Exists(appSettingsPath)
                ? JsonNode.Parse(File.ReadAllText(appSettingsPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        if (root["Vault"] is not JsonObject vault)
        {
            vault = new JsonObject();
            root["Vault"] = vault;
        }

        vault["Root"] = vaultRoot;

        Directory.CreateDirectory(Path.GetDirectoryName(appSettingsPath)!);
        File.WriteAllText(appSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Resolves a Vault path (existing or freshly scaffolded) and reports
    /// layout recognition. Null means the Boss backed out or scaffolding
    /// failed. Shared by <see cref="Run"/> (first hire) and
    /// <see cref="SetupVaultOnly"/> (a roster with a GC but no Vault yet, e.g.
    /// a hand-edited foremen.yaml, where the automatic first-run flow never
    /// triggers since the file exists).
    ///
    /// Does not check whether the vault's `consult-tha-graph` skill is
    /// reachable from `~/.claude/skills/`: GC and Foreman instructions call
    /// ConstructionCrew's own `build_graph`/`query_graph` MCP tools directly
    /// and never invoke that skill. It still matters for a human or external
    /// Claude Code session querying the vault directly, outside this app.
    /// </summary>
    public static string? ResolveAndConfirmVault(string repoRoot)
    {
        var vaultRoot = ResolveVaultRoot(repoRoot);
        if (vaultRoot is null)
        {
            return null;
        }

        ReportRecognition(vaultRoot);

        return vaultRoot;
    }

    /// <summary>
    /// On-demand Vault setup for a roster that already has a GC. The automatic
    /// first-run flow only triggers off a missing <c>foremen.yaml</c> (see
    /// <see cref="Run"/>), so a hand-written roster, or one predating Vault
    /// setup, has a GC but no Vault and nothing else prompts the Boss to fix
    /// it. Backs the <c>/setup-vault</c> command.
    ///
    /// Does not hire a GC. If one exists and its WorkingDirectory isn't
    /// already the Vault, it's rewritten in place (WorkingDirectory becomes
    /// the Vault, repo joins AddDirs) to match the shape a fresh
    /// <see cref="Run"/> hire produces. A GC already pointed at the Vault is
    /// left untouched.
    /// </summary>
    public static string? SetupVaultOnly(
        string repoRoot, ForemanDirectory foremen, string foremenConfigPath, string gcForemanName)
    {
        var vaultRoot = ResolveAndConfirmVault(repoRoot);
        if (vaultRoot is null)
        {
            return null;
        }

        PersistVaultRoot(Path.Combine(repoRoot, "appsettings.json"), vaultRoot);

        var gc = foremen.Find(gcForemanName);
        if (gc is not null && !string.Equals(gc.WorkingDirectory, vaultRoot, StringComparison.Ordinal))
        {
            var updated = RepointGcAtVault(gc, vaultRoot, repoRoot);

            ForemanConfigWriter.RemoveForeman(foremenConfigPath, gc.Name);
            ForemanConfigWriter.AppendForeman(foremenConfigPath, updated, repoRoot, vaultRoot);
            foremen.Add(updated);

            AnsiConsole.MarkupLine(
                $"[green]{Markup.Escape(gc.Name)}'s working directory updated to the Vault[/] " +
                "(it was pointed at this repo before -- that's the shape a config predating Vault setup has).");
        }

        return vaultRoot;
    }

    /// <summary>
    /// The pure shaping step behind <see cref="SetupVaultOnly"/>: WorkingDirectory
    /// becomes <paramref name="vaultRoot"/>, and <paramref name="repoRoot"/> joins
    /// AddDirs if not already present. Never drops an existing AddDirs entry.
    /// Separated out so it's testable without mocking console prompts.
    /// </summary>
    public static ForemanConfig RepointGcAtVault(ForemanConfig gc, string vaultRoot, string repoRoot)
    {
        var addDirs = (gc.AddDirs ?? [])
            .Append(repoRoot)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return gc with { WorkingDirectory = vaultRoot, AddDirs = addDirs };
    }

    /// <summary>Prompts for an existing Vault or scaffolds a new one. Null means the Boss backed out.</summary>
    private static string? ResolveVaultRoot(string repoRoot)
    {
        const string useExisting = "point at an existing Vault";
        const string scaffoldNew = "scaffold a new Vault";

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Vault[/] -- where does the crew's knowledge live?")
                .AddChoices(useExisting, scaffoldNew));

        if (choice == useExisting)
        {
            var existing = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Vault path[/] -- an existing directory:")
                    .Validate(p => Directory.Exists(ExpandPath(p))
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"'{p}' doesn't exist.")));

            return ExpandPath(existing);
        }

        var target = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]New Vault path[/] -- created if it isn't there yet:")
                .Validate(p => string.IsNullOrWhiteSpace(p)
                    ? ValidationResult.Error("Path can't be empty.")
                    : ValidationResult.Success()));

        var vaultRoot = ExpandPath(target);
        var scaffoldSource = VaultLayout.ScaffoldSourceDirectory(repoRoot);

        try
        {
            var written = VaultLayout.Scaffold(scaffoldSource, vaultRoot);
            AnsiConsole.MarkupLine($"[green]Scaffolded {written.Count} file(s)[/] into {Markup.Escape(vaultRoot)}.");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not scaffold the Vault:[/] {Markup.Escape(ex.Message)}");
            return null;
        }

        return vaultRoot;
    }

    /// <summary>
    /// Absolute path from what the Boss typed. Expands a leading "~" (the
    /// shape a Linux or Mac user reaches for), since Path.GetFullPath does not
    /// and would produce a literal "~" directory next to the cwd.
    /// </summary>
    internal static string ExpandPath(string input)
    {
        var trimmed = input.Trim();

        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trimmed = trimmed.Length <= 1 ? home : Path.Combine(home, trimmed[2..]);
        }

        return Path.GetFullPath(trimmed);
    }

    private static void ReportRecognition(string vaultRoot)
    {
        var missing = VaultLayout.MissingMarkers(vaultRoot);

        if (missing.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Recognized vault layout[/] -- HOME.md, CLAUDE.md, Notes/, Plans/ all present.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]Unrecognized vault layout[/] -- missing {Markup.Escape(string.Join(", ", missing))}. " +
            "That's fine; /hire will just ask you for each Foreman's vault folders instead of deriving them.");
    }

    /// <summary>
    /// GC's instructions file, rendered from
    /// AI/ConstructionCrew/Templates/gc-instructions.md through the same
    /// composer a Foreman's file goes through. An existing file is never
    /// overwritten: the Boss may have edited it. A missing template falls back
    /// to a minimal stand-in rather than failing first run.
    ///
    /// Called from two places: <see cref="Run"/> (fresh install), and
    /// Program.cs unconditionally before the roster loads, since GC.md is no
    /// longer shipped and can go missing on an existing install too.
    ///
    /// Runs before InstructionsMigration, which moves a legacy file and
    /// rewrites foremen.yaml to match. This method must only READ the legacy
    /// path, never move it: foremen.yaml may still name the OLD location here,
    /// and the loader that runs moments later hard-fails if that path stops existing.
    /// </summary>
    internal static string EnsureGcInstructions(
        string repoRoot,
        string vaultRoot,
        string gcForemanName,
        IReadOnlyList<string> availableProviderIds)
    {
        var instructionsDir = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions");
        Directory.CreateDirectory(instructionsDir);
        var path = Path.Combine(instructionsDir, "GC.md");

        if (File.Exists(path))
        {
            return path;
        }

        // Read-only here (see doc comment above); a hand-edited file stays
        // intact until InstructionsMigration moves it.
        var legacyPath = Path.Combine(repoRoot, "config", "instructions", "GC.md");
        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        string contents;
        try
        {
            contents = InstructionsComposer.Compose(
                gcForemanName,
                CrewRole.GC,
                briefing: string.Empty,
                jobsite: null,
                vaultFolders: GcVaultFolders,
                availableEngines: availableProviderIds,
                vaultRoot: vaultRoot);
        }
        catch (InvalidOperationException)
        {
            contents =
                """
                # You are the General Contractor (GC)

                You work for the Boss inside ConstructionCrew. You do not write code or
                run shell commands yourself -- your job is to understand what the Boss
                wants, turn it into a clear plan, and dispatch pieces of that plan to the
                right Foreman.

                Call `list_jobsites()` and `list_foremen()` before proposing who should do
                what; both change at runtime. If a jobsite has no assigned Foreman, tell
                the Boss to hire one (`/hire`) before you can dispatch work there.
                """;
        }

        File.WriteAllText(path, contents);
        return path;
    }
}
