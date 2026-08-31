using System.Text.Json;
using System.Text.Json.Nodes;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using Spectre.Console;

namespace ConstructionCrew.App;

/// <summary>
/// First run: there is no foremen.yaml yet, so there is no GC, so there is
/// nothing for the Boss to talk to. This wizard produces one.
///
/// It hooks off <c>File.Exists(settings.ForemenConfigPath)</c> in Program.cs,
/// deliberately NOT off the "no Foreman named GC" branch. Those are two
/// different failures: a missing file is a fresh install, while a config that
/// loads and has no GC-named entry is a broken roster, and the second stays a
/// hard fail.
///
/// Flow: point at an existing Vault or scaffold a new one -> recognize the
/// layout -> hire GC -> write foremen.yaml -> persist the Vault path to
/// appsettings.json. The resolved Vault path is RETURNED so Program.cs can do
/// <c>settings = settings with { VaultRoot = resolved }</c> on this same run --
/// AppSettings is a record and nothing reloads it from disk, so without that
/// reassignment the just-written <c>${vaultRoot}</c> would fail to expand and
/// /hire's Vault guard would still see null.
/// </summary>
public static class FirstRunWizard
{
    /// <summary>
    /// GC's vault write scope. Order matters: SitrepWriter.FindNotesFolder takes the
    /// FIRST entry under Notes/, which is where GC's own sitreps land. The bare
    /// "Notes" and "Plans" entries cover the cross-jobsite writes GC's instructions
    /// actually ask for -- workorders under Plans/&lt;Jobsite&gt;/&lt;Feature&gt;/ and Delivery
    /// notes under Notes/&lt;Jobsite&gt;/Deliveries/.
    /// </summary>
    public static readonly IReadOnlyList<string> GcVaultFolders = ["Notes/GC", "Notes", "Plans"];

    /// <summary>
    /// Runs the first-run flow. Returns the resolved absolute Vault path, or
    /// null if the Boss backed out (in which case nothing was written).
    /// </summary>
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
    /// The GC's ForemanConfig, exactly as first run writes it. Three things are
    /// invariant and not the Boss's to change here:
    /// - <c>Name</c> is always the reserved GcForemanName. The loader's
    ///   collection-level validation rejects any other spelling.
    /// - <c>Role</c> is always <see cref="CrewRole.GC"/>.
    /// - <c>WorkingDirectory</c> is the VAULT, not this repo -- a CLI auto-loads
    ///   the CLAUDE.md/AGENTS.md in its own cwd, and GC only inherits the
    ///   vault's authoring conventions if the vault is its cwd. This repo comes
    ///   along as an AddDirs entry instead.
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
    /// Writes Vault:Root into appsettings.json, merging into whatever is already
    /// there rather than rewriting the file -- HomeOffice:Port lives in the same
    /// file and must survive. A malformed or missing file is replaced with a
    /// fresh document; anything else would leave first run unable to finish.
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
    /// Resolves a Vault path (existing or freshly scaffolded), reports layout
    /// recognition, and runs the Phase 1c skill-reachability check. Null means
    /// the Boss backed out or the check failed -- nothing was written in either
    /// case. Shared by <see cref="Run"/> (first hire) and
    /// <see cref="SetupVaultOnly"/> (a roster that already has a GC but was
    /// never pointed at a Vault -- e.g. a hand-edited <c>foremen.yaml</c>, which
    /// means the automatic first-run flow at startup never triggers because the
    /// file already exists).
    ///
    /// Does NOT check whether the vault's `consult-tha-graph` Claude Code skill
    /// is reachable from `~/.claude/skills/` -- that check made sense when this
    /// was written, before ConstructionCrew's own `build_graph`/`query_graph` MCP
    /// tools existed. GC and Foreman instructions call those tools directly
    /// (see config/templates/*.md); neither ever invokes the skill, so its
    /// reachability has no bearing on ConstructionCrew's own operation. The skill
    /// still exists for a human, or an external Claude Code session, querying the
    /// vault directly outside this app -- that's a separate concern from hiring
    /// a GC here.
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
    /// first-run flow only ever triggers off a missing <c>foremen.yaml</c>
    /// (<see cref="Run"/>'s own doc comment) -- a roster written by hand, or
    /// left over from before a Vault was ever configured, has a GC but no
    /// Vault, and nothing would otherwise invite the Boss to fix that. This is
    /// the <c>/setup-vault</c> command's implementation.
    ///
    /// Does not hire a GC. If one already exists and its WorkingDirectory isn't
    /// already the Vault, it is rewritten in place (WorkingDirectory becomes the
    /// Vault, this repo joins AddDirs if it isn't there already) so GC actually
    /// gets Vault access as its cwd -- the same shape a fresh <see cref="Run"/>
    /// hire would have produced. A GC already pointed at the Vault (or no GC at
    /// all, which should not happen once <c>foremen.yaml</c> exists, but is not
    /// this method's problem to diagnose) is left untouched.
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
    /// AddDirs if it isn't already there -- the same repo-stays-reachable rule
    /// <see cref="BuildGcConfig"/> applies on a fresh hire. Never drops an existing
    /// AddDirs entry (a Boss may have added others by hand). Separated from the
    /// interactive method above so this shaping logic is directly testable without
    /// mocking a console prompt.
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
    /// Absolute path from what the Boss typed. "~/Vault" is the shape a Linux or
    /// Mac user reaches for first, and Path.GetFullPath does not expand it -- it
    /// would produce a literal "~" directory next to the cwd.
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
    /// GC's instructions file, rendered from config/templates/gc-instructions.md
    /// by the same composer a Foreman's file goes through -- the workorder
    /// handoff protocol lives in that template, so a GC hired from a hand-written
    /// stand-in would never learn it. An existing file is never overwritten: the
    /// Boss may have edited it.
    ///
    /// A missing template (a broken clone) falls back to a minimal stand-in
    /// rather than failing the whole first run -- the loader hard-fails on an
    /// instructionsFilePath that isn't there.
    /// </summary>
    private static string EnsureGcInstructions(
        string repoRoot,
        string vaultRoot,
        string gcForemanName,
        IReadOnlyList<string> availableProviderIds)
    {
        var instructionsDir = Path.Combine(repoRoot, "config", "instructions");
        Directory.CreateDirectory(instructionsDir);
        var path = Path.Combine(instructionsDir, "GC.md");

        if (File.Exists(path))
        {
            return path;
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
                repoRoot: repoRoot,
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
